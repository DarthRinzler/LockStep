using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LockstepBase {

    public enum SC_Message {
        SynAck = 1,
        StartGame = 2,
        GameStep = 3,
        GameStepCollection = 4,
        NewPlayer = 5
    }

    public enum CS_Message {
        Syn = 1,
        SynAckAck = 2,
        JoinGame = 3,
        StartReady = 4,
        PlayerInput = 5,
    }

    // [MessageType][NumGameSteps][GS1][GS2]..[GSN]
    public struct GameStepCollection {
        public List<GameStep> GameSteps { get; set; }

        public GameStepCollection(int expectedCount) {
            GameSteps = new List<GameStep>(expectedCount);
        }

        public GameStepCollection(BinaryReader br) {
            if ((SC_Message)br.ReadByte() != SC_Message.GameStepCollection) {
                throw new Exception("Expecte Message type SC_GameStepCollection");
            }

            var numGameSteps = br.ReadByte();

            GameSteps = new List<GameStep>();
            for (int i = 0; i < numGameSteps; i++) {
                var gameStep = new GameStep(br);
                GameSteps.Add(gameStep);
            }
        }

        public void SerializeToBuff(BinaryWriter bw) {
            bw.Write((byte)SC_Message.GameStepCollection);
            bw.Write((byte)GameSteps.Count);

            foreach (var gs in GameSteps.OrderByDescending(gs => gs.StepNumber)) {
                gs.SerializeToBuff(bw);
            }
        }
    }

    // [StepNumber][NumPlayers][PlayerStep1][PlayerStep2]..[PlayerStepN]
    public struct GameStep {
        public UInt16 StepNumber { get; private set; }
        public byte EllapsedMs { get; private set; }
        public Dictionary<byte, PlayerInputs> PlayerInputs { get; set; }

        public GameStep(BinaryReader br) {
            StepNumber = br.ReadUInt16();
            EllapsedMs = br.ReadByte();
            var numPlayers = br.ReadByte();

            PlayerInputs = new Dictionary<byte, PlayerInputs>(numPlayers);
            for (int i = 0; i < numPlayers; i++) {
                var ps = new PlayerInputs(br);
                PlayerInputs.Add(ps.PlayerId, ps);
            }
        }

        public GameStep(UInt16 stepNumber, byte ellapsedMs) {
            StepNumber = stepNumber;
            EllapsedMs = ellapsedMs;
            PlayerInputs = new Dictionary<byte, PlayerInputs>();
        }

        public void SerializeToBuff(BinaryWriter writer) {
            writer.Write(StepNumber);
            writer.Write(EllapsedMs);
            writer.Write((byte)PlayerInputs.Count);

            foreach (var ps in PlayerInputs.Values.OrderBy(p => p.PlayerId)) {
                ps.SerializeToBuff(writer);
            }
        }
    }

    // [PlayerId][KeyCount][Key1Id][Key1Val][Key2Id][Key2Val]..[KeyNId][KeyNVal]
    public struct PlayerInputs {
        public byte PlayerId { get; set; }

        public Dictionary<ConsoleKey, bool> Inputs { get; set; }

        public PlayerInputs(byte playerId) {
            PlayerId = playerId;
            Inputs = new Dictionary<ConsoleKey, bool>();
        }

        public PlayerInputs(BinaryReader reader) {
            PlayerId = reader.ReadByte();
            var inputCount = reader.ReadByte();

            Inputs = new Dictionary<ConsoleKey, bool>();

            for (int i = 0; i < inputCount; i++) {
                var key = reader.ReadByte();
                var val = reader.ReadByte();
                Inputs.Add((ConsoleKey)key, val == 1);
            }
        }

        public void SerializeToBuff(BinaryWriter writer) {
            writer.Write(PlayerId);
            writer.Write((byte)Inputs.Count);

            foreach (var input in Inputs) {
                writer.Write((byte)input.Key);
                writer.Write((byte)(input.Value ? 1 : 0));
            }
        }
    }

    public struct BitVector {
        public UInt64 Data;

        public BitVector(byte[] data) {
            Data = 0;
            for (int i = 0; i < data.Length; i++) {
                Data |= ((UInt64)(data[i]) << (i * 8));
            }
        }

        public BitVector(BitVector other) {
            Data = other.Data;
        }

        public void SetBit(int idx, int val) {
            if (idx > 63) throw new IndexOutOfRangeException("Index cannot be greater than 63");

            if (val == 1) {
                UInt64 mask = (UInt64)1 << idx;
                Data |= mask;
            }
            else {
                UInt64 mask = ~((UInt64)1 << idx);
                Data &= mask;
            }
        }

        public int GetBit(int idx) {
            if (idx > 63) throw new IndexOutOfRangeException("Index cannot be greater than 63");

            UInt64 mask = (UInt64)1 << idx;
            return (Data & mask) > 0 ? 1 : 0;
        }

        public byte[] Serialize(int length) {
            return BitConverter.GetBytes(Data).Take(length).ToArray();
        }

        public IEnumerable<bool> ToEnumerable() {
            for (int i = 0; i < 64; i++) {
                yield return GetBit(i) == 1;
            }
        }
    }

    public class GameHistory {

        public int Count { get { return _gameSteps.Count; } }
        private UInt16 _min;
        private int _size;

        private Dictionary<UInt16, GameStep> _gameSteps;

        public GameHistory(int size) {
            _min = 0;
            _size = size;
            _gameSteps = new Dictionary<ushort, GameStep>();
        }

        public void Add(GameStep step) {
            _gameSteps[step.StepNumber] = step;

            if (_gameSteps.Count > _size) {
                while (true) {
                    if (_gameSteps.ContainsKey(_min)) {
                        _gameSteps.Remove(_min++);
                        break;
                    }
                    else {
                        _min++;
                    }
                }
            }
        }

        public bool TryGet(UInt16 stepNumber, out GameStep ret) {
            return _gameSteps.TryGetValue(stepNumber, out ret);
        }
    }

    public class CircularBuff<T> {
        private T[] _buff;
        private int _curIdx;

        public int Count { get { return _curIdx + 1; } }

        public CircularBuff(int size) {
            _buff = new T[size];
            _curIdx = -1;
        }

        public void Add(T toAdd) {
            _curIdx++;
            _buff[CurIdx()] = toAdd;
        }

        public int CurIdx() {
            return _curIdx % _buff.Length;
        }

        public T GetTop() {
            return _buff[CurIdx()];
        }

        public T GetLast(int n) {
            int idx = (_curIdx - n) % _buff.Length;
            return _buff[idx];
        }

        public bool IsEmpty() {
            return _curIdx == -1;
        }
    }
}
