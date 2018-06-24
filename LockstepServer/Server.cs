using LockstepBase;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LockstepServer {
    public class Server {

        private const int _minPlayerCount = 1;

        private IPEndPoint PlayEndPoint = new IPEndPoint(IPAddress.Any, 5725);
        private const int GameStepIntervalMs = (int)((1/60.0) * 1000); // 63hz
        //private const int GameStepIntervalMs = 25;

        private DateTime _lastStepBroadcast;
        private PlayerData[] _playersById;
        private Dictionary<IPEndPoint, PlayerData> _playersByIp;
        private Dictionary<byte, PlayerInputs> _curPlayerInputs;
        private GameHistory _gameHistory;

        private volatile UInt16 _stepNumber;
        private object _gameLock;

        static void Main(string[] args) {
            new Server(_minPlayerCount);
        }

        public Server(int numPlayers) {
            _gameLock = new object();
            _gameHistory = new GameHistory(120);
            _curPlayerInputs = new Dictionary<byte, PlayerInputs>();
            Start(numPlayers).Wait();
        }

        public async Task Start(int numPlayers) {
            while (true) {
                try {
                    _playersById = new PlayerData[numPlayers];
                    _playersByIp = new Dictionary<IPEndPoint, PlayerData>();

                    using (UdpClient listener = new UdpClient(PlayEndPoint)) {
                        await AcceptPlayerConnections(listener);
                        StartReceivePlayerInputdata(listener);
                        SendPlayerUpdates();
                    }
                }
                catch(Exception e) {
                    Console.WriteLine(e);
                }
            }
        }

        private Task AcceptPlayerConnections(UdpClient listener) {
            Console.WriteLine("Accepting Player Connections");
            Func<UdpReceiveResult, CS_Message> Message = (recv) => (CS_Message)recv.Buffer[0];

            return Task.Run(async () => {
                try {
                    while (true) {
                        // Get Connection
                        var packet = await listener.ReceiveAsync();

                        // If Syn Packet, New player wants to connect, Create new PlayerData object
                        if (Message(packet) == CS_Message.Syn) {
                            Console.WriteLine("Syn From: " + packet.RemoteEndPoint);
                            PlayerData playerData = new PlayerData() {
                                RemoteEndPoint = packet.RemoteEndPoint,
                                State = PlayerState.SynAck,
                                UdpClient = listener
                            };

                            lock (_gameLock) {
                                _playersByIp.Add(packet.RemoteEndPoint, playerData);
                            }

                            // Respond with SynAck
                            Console.WriteLine("Responding SynAck");
                            await listener.SendAsync(new byte[] { (byte)SC_Message.SynAck }, 1, playerData.RemoteEndPoint);
                        }
                        // If SynAckAck Packet, New player is connected
                        else if (Message(packet) == CS_Message.SynAckAck) {
                            Console.WriteLine("SynAck from: " + packet.RemoteEndPoint);

                            PlayerData PlayerData;
                            lock (_gameLock) {
                                // Verify user already exists
                                if (!_playersByIp.TryGetValue(packet.RemoteEndPoint, out PlayerData)) {
                                    Console.WriteLine("Invalid request from {0}. Expected Syn from Unknown user, got SynAckAck", packet.RemoteEndPoint);
                                    continue;
                                }

                                // Verify user is in State SynAck    
                                if (PlayerData.State != PlayerState.SynAck) {
                                    Console.WriteLine("Invalid request from {0}. Expected User to be in State SynAck before sending SynAckAck, actual state: {1}", packet.RemoteEndPoint, PlayerData.State);
                                    continue;
                                }

                                PlayerData.State = PlayerState.SynAckAck;
                            }

                        }
                        // If JoinGame Message
                        else if (Message(packet) == CS_Message.JoinGame) {
                            Console.WriteLine("JoinGame from {0}", packet.RemoteEndPoint);

                            PlayerData playerData;
                            lock (_gameLock) {
                                // Verify user already exists
                                if (!_playersByIp.TryGetValue(packet.RemoteEndPoint, out playerData)) {
                                    Console.WriteLine("Invalid request from {0}. Expected Syn from Unknown user, got JoinGame", packet.RemoteEndPoint);
                                    continue;
                                }

                                // Verify user is in State SynAckAck    
                                if (playerData.State != PlayerState.SynAckAck) {
                                    Console.WriteLine("Invalid request from {0}. Expected User to be in State SynAckAck before sending JoinGame, actual state: {1}", packet.RemoteEndPoint, playerData.State);
                                    continue;
                                }

                                // Set Player Name from Packet
                                playerData.Name = Encoding.UTF8.GetString(packet.Buffer.Skip(1).ToArray());
                                playerData.JoinTime = DateTime.UtcNow;
                                playerData.State = PlayerState.JoinGame;

                                // find first null slot in players array, idx is id
                                for (int id = 0; id < _playersById.Length; id++) {
                                    if (_playersById[id] == null) {
                                        playerData.Id = (byte)id;
                                        _playersById[id] = playerData;
                                        break;
                                    }
                                }
                            }
                        }
                        // If StartReady Message
                        else if (Message(packet) == CS_Message.StartReady) {
                            Console.WriteLine("StartReady from {0}", packet.RemoteEndPoint);

                            PlayerData playerData;
                            lock (_gameLock) {
                                // Verify user already exists
                                if (!_playersByIp.TryGetValue(packet.RemoteEndPoint, out playerData)) {
                                    Console.WriteLine("Invalid request from {0}. Expected Syn from Unknown user, got StartReady", packet.RemoteEndPoint);
                                    continue;
                                }

                                // Verify user is in State JoinGame 
                                if (playerData.State != PlayerState.JoinGame) {
                                    Console.WriteLine("Invalid request from {0}. Expected User to be in State JoinGame before sending StartReady, actual state: {1}", packet.RemoteEndPoint, playerData.State);
                                    continue;
                                }

                                playerData.State = PlayerState.StartGame;

                                // If all users in State StartGame, then StartGame!
                                if (_playersById.Length >= _minPlayerCount && _playersById.All(p => p.State == PlayerState.StartGame)) {
                                    Console.WriteLine("All players Ready To Start");
                                    break;
                                }
                            }
                        }
                        // Invalid Message
                        else {
                            Console.WriteLine("Received invalid message type {0} from {1}, Ignoring", Message(packet), packet.RemoteEndPoint);
                        }
                    }
                }
                catch(Exception e) {
                    Console.Write(e);
                }
            });
        }

        private void StartReceivePlayerInputdata(UdpClient listener) {
            Task.Run(async () => {
                try {
                    Console.WriteLine("Receiving Player Input");
                    while (true) {
                        var packet = await listener.ReceiveAsync();

                        byte playerId = _playersByIp[packet.RemoteEndPoint].Id;
                        CS_Message msgType = (CS_Message)packet.Buffer[0];

                        switch (msgType) {
                            case CS_Message.PlayerInput:
                                HandlePlayerInput(playerId, packet.Buffer);
                                break;
                        }
                    }
                }
                catch(Exception e) {
                    Console.Write(e);
                }
            });
        }

        private void HandlePlayerInput(byte playerId, byte[] data) {
            if (data.Length != 3) return; 

            lock(_gameLock) {
                PlayerInputs playerInputs;

                // If no PlayerStep exists for this player in the CurrentGameStep yet, create one
                if (!_curPlayerInputs.TryGetValue(playerId, out playerInputs)) {
                    playerInputs = new PlayerInputs(playerId);
                    _curPlayerInputs.Add(playerId, playerInputs);
                }

                // Add Input to PlayerStep
                ConsoleKey input = (ConsoleKey)data[1];
                bool newVal = data[2] == 0 ? false : true;
                bool curVal;

                // If Input doesn't exist for player (expected) then add it
                if (!playerInputs.Inputs.TryGetValue(input, out curVal)) {
                    playerInputs.Inputs.Add(input, newVal);
                }
                // If Input does exist already and the values do not match, then it will need to be held until the next GameStep
                else if (curVal != newVal) { 
                    //TODO                    
                    throw new NotImplementedException("Need to implement this");
                }
            }
        }

        private void SendPlayerUpdates() {
            Console.WriteLine("Starting Broadcasts");
            _lastStepBroadcast = DateTime.UtcNow;

            DateTime start = DateTime.UtcNow;
            int sleepIntervalMs = GameStepIntervalMs;

            try {
                while (true) {
                    if (sleepIntervalMs > 0) {
                        Thread.Sleep(sleepIntervalMs);
                    }

                    start = DateTime.UtcNow;

                    GameStep gameStep;
                    lock (_gameLock) {
                        byte ellapsed = (byte)Math.Min((DateTime.UtcNow - _lastStepBroadcast).TotalMilliseconds, byte.MaxValue);

                        // Create new GameStep to broadcast
                        gameStep = new GameStep(_stepNumber, ellapsed);

                        GameStep lastGameStep;
                        // If previous GameSteps exit
                        if (_gameHistory.Count > 0 && _gameHistory.TryGet((UInt16)(_stepNumber-1), out lastGameStep)) { 
                            foreach (var currentInput in _curPlayerInputs) {

                                // If Player input exists from last game update, take XOR of last player input and current player input
                                PlayerInputs lastInput;
                                if (lastGameStep.PlayerInputs.TryGetValue(currentInput.Key, out lastInput)) {
                                    var changed = currentInput.Value.Inputs
                                        .Where(i => !lastInput.Inputs.ContainsKey(i.Key) || lastInput.Inputs[i.Key] != i.Value)
                                        .ToDictionary(k => k.Key, k => k.Value);

                                    PlayerInputs pi = new PlayerInputs() {
                                        PlayerId = currentInput.Key,
                                        Inputs = changed
                                    };

                                    gameStep.PlayerInputs.Add(pi.PlayerId, pi);
                                }
                                // If no last game update for this player, just send current inputs
                                else {
                                    gameStep.PlayerInputs.Add(currentInput.Key, currentInput.Value);
                                }
                            }

                            _curPlayerInputs.Clear();
                        }
                        // If no previous GameSteps exist
                        else {
                            gameStep.PlayerInputs = _curPlayerInputs;
                        }

                        _gameHistory.Add(gameStep);
                    }

                    GameStepCollection gsc = new GameStepCollection(3);
                    gsc.GameSteps.Add(gameStep);

                    // Send Redundant Step #1
                    if (_stepNumber >= 1) {
                        UInt16 idx = (UInt16)Math.Max(_stepNumber - 2, 0);
                        GameStep redundant;
                        if (_gameHistory.TryGet(idx, out redundant)) {
                            gsc.GameSteps.Add(redundant);
                        }
                    }

                    // Send Redundant Step #2
                    if (_stepNumber >= 3) {
                        UInt16 idx = (UInt16)Math.Max(_stepNumber - 8, 0);
                        GameStep redundant;
                        if (_gameHistory.TryGet(idx, out redundant)) {
                            gsc.GameSteps.Add(redundant);
                        }
                    }

                    BroadcastGameStepCollection(gsc);

                    // Calculate next Sleep Interval
                    _lastStepBroadcast = DateTime.UtcNow;
                    _stepNumber++;
                    TimeSpan broadcastDuration = (_lastStepBroadcast - start);
                    sleepIntervalMs = Math.Max(GameStepIntervalMs - (int)broadcastDuration.TotalMilliseconds, 0);
                }
            }
            catch(Exception e) {
                Console.Write(e);
            }
        }

        private static byte[] tempBuff = new byte[4096];
        private static MemoryStream ms = new MemoryStream(tempBuff);
        private static BinaryWriter bw = new BinaryWriter(ms);

        private void BroadcastGameStepCollection(GameStepCollection gsc) {
            bw.Seek(0, SeekOrigin.Begin);
            gsc.SerializeToBuff(bw);
            bw.Flush();

            var tasks = _playersById.Select(async player => {
                try {
                    //if (new Random().Next(0, 100) <= 6) return; // 7% packet loss
                    await player.UdpClient.SendAsync(tempBuff, (int)ms.Position, player.RemoteEndPoint);
                }
                catch (Exception e) {
                    Console.WriteLine(e);
                }
            })
            .AsParallel()
            .ToArray();

            Task.WaitAll(tasks);
        }
    }

    public class PlayerData {
        public PlayerState State { get; set; }
        public byte Id { get; set; }
        public string Name { get; set; }
        public IPEndPoint RemoteEndPoint { get; set; }
        public UdpClient UdpClient { get; set; }
        public DateTime JoinTime { get; set; }
    }

    public enum PlayerState {
        SynAck, SynAckAck, JoinGame, StartGame, InGame, ExitGame
    }
}













