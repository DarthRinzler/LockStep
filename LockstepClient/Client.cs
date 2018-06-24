using LockstepBase;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Galactica {

    public class Client : IDisposable {
        public UdpClient UdpClient { get; private set; }

        private IPEndPoint _playEndpoint = new IPEndPoint(IPAddress.Loopback, 5725);
        private GameHistory _gameHistory;
        private Thread _getGameUpdatesThread;
        private volatile UInt16 _highestGameStepNum;
        private volatile UInt16 _unityGameStepNum;
        private volatile bool _shutdown;
        private string _name;

        public static void Main(String[] args) {
            Client client = new Client("A");
            client.ConnectToServer();
            client.JoinGame();
            client.StartReady();
            client.StartGetGameUpdatesThread();

            while (true) {
                var step = client.GetNextStep();
                Thread.Sleep(15);
            }
        }

        public Client(string name) {
            _name = name;
            _gameHistory = new GameHistory(2048);
            _unityGameStepNum = 0;
            _highestGameStepNum = 0;
        }

        public void StartGetGameUpdatesThread() {
            Log("Listen for Game Updates");
            _getGameUpdatesThread = new Thread(new ThreadStart(() => {
                GetGameUpdates(UdpClient);
            }));
            _getGameUpdatesThread.Start();
        }

        public bool ConnectToServer() {
            try {
                UdpClient = new UdpClient();

                Log("Connecting");
                UdpClient.Connect(_playEndpoint);

                Log("Syn");
                UdpClient.Send(new byte[] { (byte)CS_Message.Syn }, 1);

                Log("Wait for SynAck..");
                IPEndPoint endPoint = null;
                byte[] msg = UdpClient.Receive(ref endPoint);

                if ((SC_Message)msg[0] != SC_Message.SynAck) {
                    Log("Expected SynAck from Server, got: " + msg[0]);
                    return false;
                }
                Log("Received SynAck..");

                Log("Send SynAckAck");
                UdpClient.Send(new byte[] { (byte)CS_Message.SynAckAck }, 1);
            }
            catch (Exception e) {
                Log(e.ToString());
                return false;
            }

            return true;
        }

        public bool JoinGame() {
            try {
                Log("Joining Game");

                var data = Encoding.UTF8.GetBytes(_name);
                var buff = new byte[data.Length + 1];
                Buffer.BlockCopy(data, 0, buff, 1, data.Length);
                buff[0] = (byte)CS_Message.JoinGame;
                UdpClient.Send(buff, buff.Length);
            }
            catch (Exception e) {
                Log(e.ToString());
                return false;
            }
            return true;
        }

        public bool StartReady() {
            try {
                Log("StartReady Game");
                UdpClient.Send(new byte[] { (byte)CS_Message.StartReady }, 1);
            }
            catch (Exception e) {
                Log(e.ToString());
                return false;
            }
            return true;
        }

        public GameStep? GetNextStep() {
            lock (_gameHistory) {
                GameStep nextStep = new GameStep();

                // If new Game Step is found
                if (_gameHistory.TryGet(_unityGameStepNum, out nextStep)) {
                    if (_unityGameStepNum != nextStep.StepNumber) {
                        Log("Exception!");
                        throw new InvalidProgramException("Next step should be last step + 1!");
                    }
                    _unityGameStepNum++;
                    return nextStep;
                }
                // If no game step is found
                else {
                    var lag = _highestGameStepNum - _unityGameStepNum;
                    return null;
                }
            }
        }

        public void SendPlayerInput(KeyCode k, bool on) {
            byte[] data = new byte[3];
            data[0] = (byte)CS_Message.PlayerInput;
            data[1] = (byte)MapKey(k);
            data[2] = (byte)(on ? 1 : 0);
            UdpClient.BeginSend(data, data.Length, null, null);
        }

        private void GetGameUpdates(UdpClient client) {
            Log("Receiving GameSteps");

            try {
                while (!_shutdown) {
                    IPEndPoint endPoint = null;
                    var msg = client.Receive(ref endPoint);

                    using (var br = new BinaryReader(new MemoryStream(msg))) {
                        if ((SC_Message)msg[0] == SC_Message.GameStepCollection) {
                            GameStepCollection gsc = new GameStepCollection(br);

                            foreach (var gs in gsc.GameSteps) {
                                if (gs.StepNumber > _highestGameStepNum + 8) {
                                    Log("Disconnected!");
                                    throw new Exception("Disconnected!");
                                }

                                _highestGameStepNum = Math.Max(gs.StepNumber, _highestGameStepNum);

                                lock (_gameHistory) {
                                    _gameHistory.Add(gs);
                                }

                                if (gs.PlayerInputs.Any()) {
                                    var msgs = gs.PlayerInputs
                                        .Select(pi =>
                                            pi.Key +
                                            ":" +
                                            String.Join("|", pi.Value.Inputs.Select(k => k.Key + ":" + k.Value).ToArray()))
                                        .Aggregate((a, e) => a + "," + e);

                                    //Log("[" + gs.StepNumber + "] " + msgs);
                                }
                            }
                        }
                        else {
                            Log("Invalid Message!");
                        }
                    }
                }
            }
            catch (Exception e) {
                Log(e.ToString());
            }

            Log("GetGameSteps Exit");
        }

        private ConsoleKey MapKey(KeyCode k) {
            switch (k) {
                case KeyCode.RightArrow: return ConsoleKey.RightArrow;
                case KeyCode.LeftArrow: return ConsoleKey.LeftArrow;
                case KeyCode.DownArrow: return ConsoleKey.DownArrow;
                case KeyCode.UpArrow: return ConsoleKey.UpArrow;
                default:
                    throw new ArgumentException("Unsupported Key code: " + k.ToString());
            }
        }

        public void Dispose() {
            Log("Disposing Client");
            _shutdown = true;
            try {
                UdpClient.Close();
            }
            catch (Exception e) {
                Log(e.ToString());
            }
        }

        private void Log(string msg) {
            Console.WriteLine(msg);
            //Debug.Log(msg);
        }
    }
}
