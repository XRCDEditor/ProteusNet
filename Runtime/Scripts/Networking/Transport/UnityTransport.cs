using jKnepel.SimpleUnityNetworking.Logging;
using jKnepel.SimpleUnityNetworking.Networking;
using jKnepel.SimpleUnityNetworking.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Error;
using Unity.Networking.Transport.Relay;
using Unity.Networking.Transport.Utilities;

namespace jKnepel.SimpleUnityNetworking.Transporting
{
    public sealed partial class UnityTransport : Transport
    {
        #region fields
        
        private const int LOCAL_HOST_RTT = 50;
        
        private bool _disposed;
        
        private int _maxNumberOfClients;
        
        private NetworkDriver _driver;
        private NetworkSettings _networkSettings;
        
        private RelayServerData _relayServerData;
        
        private readonly Dictionary<SendTarget, SendQueue> _outgoingMessages = new();

        private NetworkPipeline _unreliablePipeline;
        private NetworkPipeline _unreliableSequencedPipeline;
        private NetworkPipeline _reliablePipeline;
        private NetworkPipeline _reliableSequencedPipeline;

        private Dictionary<uint, NetworkConnection> _clientIDToConnection;
        private Dictionary<NetworkConnection, uint> _connectionToClientID;
        private uint _clientIDs;
        
        private NetworkConnection _serverConnection;
        
        private uint _hostClientID; // client ID that the hosting server assigns its local client

        private ELocalConnectionState _serverState = ELocalConnectionState.Stopped;
        private ELocalConnectionState _clientState = ELocalConnectionState.Stopped;
        
        public override ELocalConnectionState LocalServerState => _serverState;
        public override ELocalConnectionState LocalClientState => _clientState;

        public override bool IsServer => LocalServerState == ELocalConnectionState.Started;
        public override bool IsClient => LocalClientState == ELocalConnectionState.Started;
        public override bool IsOnline => IsServer || IsClient;
        public override bool IsHost => IsServer && IsClient;

        public override event Action<ServerReceivedData> OnServerReceivedData;
        public override event Action<ClientReceivedData> OnClientReceivedData;
        public override event Action<ELocalConnectionState> OnServerStateUpdated;
        public override event Action<ELocalConnectionState> OnClientStateUpdated;
        public override event Action<uint, ERemoteConnectionState> OnConnectionUpdated;

        public override event Action<string, EMessageSeverity> OnTransportLogAdded;

        #endregion
        
        #region lifecycle
        
        public UnityTransport(TransportSettings settings)
            : base(settings) {}
        
        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            
            if (disposing)
            {
                _serverState = ELocalConnectionState.Stopped;
                _clientState = ELocalConnectionState.Stopped;
                _clientIDToConnection = null;
                _connectionToClientID = null;
                _serverConnection = default;
                _clientIDs = 0;
                _hostClientID = 0;
            }

            foreach (var queue in _outgoingMessages.Values)
                queue.Dispose();
            // TODO : clean/flush outgoing messages
            DisposeInternals();

            _disposed = true;
        }

        public override void StartServer()
        {
            if (LocalServerState is ELocalConnectionState.Starting or ELocalConnectionState.Started)
            {
                OnTransportLogAdded?.Invoke("Failed to start the server, there already exists a local server.", EMessageSeverity.Error);
                return;
            }

            SetLocalServerState(ELocalConnectionState.Starting);
            
            InitialiseSettings();

            NetworkEndpoint endpoint = default;
            switch (Settings.ProtocolType)
            {
                case EProtocolType.UnityTransport:
                    var port = Settings.Port == 0 ? NetworkUtilities.FindNextAvailablePort() : Settings.Port;
                    if (!string.IsNullOrEmpty(Settings.ServerListenAddress))
                    {
                        if (!NetworkEndpoint.TryParse(Settings.Address, port, out endpoint))
                            NetworkEndpoint.TryParse(Settings.Address, port, out endpoint, NetworkFamily.Ipv6);
                    }
                    else
                    {
                        endpoint = NetworkEndpoint.LoopbackIpv4.WithPort(port);
                    }
                    break;
                case EProtocolType.UnityRelayTransport:
                    if (_relayServerData.Equals(default(RelayServerData)))
                    {
                        SetLocalServerState(ELocalConnectionState.Stopping);
                        DisposeInternals();
                        OnTransportLogAdded?.Invoke("The relay server data needs to be set before a server can be started using the relay protocol.", EMessageSeverity.Error);
                        SetLocalServerState(ELocalConnectionState.Stopped);
                        return;
                    }

                    _networkSettings.WithRelayParameters(ref _relayServerData, Settings.HeartbeatTimeoutMS);
                    endpoint = NetworkEndpoint.AnyIpv4;
                    break;
            }
            
            if (endpoint.Family == NetworkFamily.Invalid)
            {
                SetLocalServerState(ELocalConnectionState.Stopping);
                OnTransportLogAdded?.Invoke("The given local or remote address uses an invalid IP family.", EMessageSeverity.Error);
                SetLocalServerState(ELocalConnectionState.Stopped);
                return;
            }
            
            InitialiseDrivers();
            
            if (_driver.Bind(endpoint) != 0)
            {
                SetLocalServerState(ELocalConnectionState.Stopping);
                DisposeInternals();
                OnTransportLogAdded?.Invoke($"Failed to bind server to local address {endpoint.Address} and port {endpoint.Port}.", EMessageSeverity.Error);
                SetLocalServerState(ELocalConnectionState.Stopped);
                return;
            }

            _maxNumberOfClients = Settings.MaxNumberOfClients;
            _clientIDToConnection = new();
            _connectionToClientID = new();
            _driver.Listen();
            SetLocalServerState(ELocalConnectionState.Started);
        }

        public override void StopServer()
        {
            if (LocalServerState is ELocalConnectionState.Stopping or ELocalConnectionState.Stopped) return;
            if (LocalClientState is ELocalConnectionState.Starting or ELocalConnectionState.Started) 
                StopHostClient();
            
            SetLocalServerState(ELocalConnectionState.Stopping);

            var conns = _clientIDToConnection.Values.ToArray();
            foreach (var conn in conns)
            {
                if (conn.GetState(_driver) == NetworkConnection.State.Disconnected) continue;
                // TODO : flush send queue
                CleanOutgoingMessages(conn);
                _driver.Disconnect(conn);
                while (_driver.PopEventForConnection(conn, out _) != NetworkEvent.Type.Empty) {}
            }

            _driver.ScheduleUpdate().Complete();
            _clientIDToConnection = null;
            _connectionToClientID = null;
            _clientIDs = 0;
            DisposeInternals();

            SetLocalServerState(ELocalConnectionState.Stopped);
        }

        public override void StartClient()
        {
            if (LocalClientState is ELocalConnectionState.Starting or ELocalConnectionState.Started)
            {
                OnTransportLogAdded?.Invoke("Failed to start the client, there already exists a local client.", EMessageSeverity.Error);
                return;
            }

            if (LocalServerState is ELocalConnectionState.Starting or ELocalConnectionState.Started)
            {
                StartHostClient();
                return;
            }
            
            SetLocalClientState(ELocalConnectionState.Starting);

            InitialiseSettings();
            
            NetworkEndpoint serverEndpoint = default;
            switch (Settings.ProtocolType)
            {
                case EProtocolType.UnityTransport:
                    if (!NetworkEndpoint.TryParse(Settings.Address, Settings.Port, out serverEndpoint))
                        NetworkEndpoint.TryParse(Settings.Address, Settings.Port, out serverEndpoint, NetworkFamily.Ipv6);
                    break;
                case EProtocolType.UnityRelayTransport:
                    if (_relayServerData.Equals(default(RelayServerData)))
                    {
                        SetLocalServerState(ELocalConnectionState.Stopping);
                        DisposeInternals();
                        OnTransportLogAdded?.Invoke("The relay server data needs to be set before a client can be started using the relay protocol.", EMessageSeverity.Error);
                        SetLocalServerState(ELocalConnectionState.Stopped);
                        return;
                    }

                    _networkSettings.WithRelayParameters(ref _relayServerData, Settings.HeartbeatTimeoutMS);
                    serverEndpoint = _relayServerData.Endpoint;
                    break;
            }
            
            if (serverEndpoint.Family == NetworkFamily.Invalid)
            {
                SetLocalClientState(ELocalConnectionState.Stopping);
                OnTransportLogAdded?.Invoke("The server address is invalid.", EMessageSeverity.Error);
                SetLocalClientState(ELocalConnectionState.Stopped);
                return;
            }

            InitialiseDrivers();
            
            var localEndpoint = serverEndpoint.Family == NetworkFamily.Ipv4 ? NetworkEndpoint.AnyIpv4 : NetworkEndpoint.AnyIpv6;
            if (_driver.Bind(localEndpoint) != 0)
            {
                SetLocalClientState(ELocalConnectionState.Stopping);
                DisposeInternals();
                OnTransportLogAdded?.Invoke($"Failed to bind client to local address {localEndpoint.Address} and port {localEndpoint.Port}", EMessageSeverity.Error);
                SetLocalClientState(ELocalConnectionState.Stopped);
                return;
            }

            _serverConnection = _driver.Connect(serverEndpoint);
        }

        public override void StopClient()
        {
            if (LocalClientState is ELocalConnectionState.Stopping or ELocalConnectionState.Stopped) return;
            if (LocalServerState is ELocalConnectionState.Starting or ELocalConnectionState.Started)
            {
                StopHostClient();
                return;
            }

            SetLocalClientState(ELocalConnectionState.Stopping);
            
            // TODO : flush send queue
            CleanOutgoingMessages(_serverConnection);
            if (_driver.Disconnect(_serverConnection) == 0)
            {
            }
            
            _driver.ScheduleUpdate().Complete();
            _serverConnection = default;
            DisposeInternals();

            SetLocalClientState(ELocalConnectionState.Stopped);
        }

        public override void StopNetwork()
        {
            StopClient();
            StopServer();
        }

        private void StartHostClient()
        {
            SetLocalClientState(ELocalConnectionState.Starting);
            
            if (_clientIDToConnection.Count >= _maxNumberOfClients)
            {
                SetLocalClientState(ELocalConnectionState.Stopping);
                OnTransportLogAdded?.Invoke("Maximum number of clients reached. Server cannot accept the connection.", EMessageSeverity.Error);
                SetLocalClientState(ELocalConnectionState.Stopped);
                return;
            }
            
            _hostClientID = _clientIDs++;
            SetLocalClientState(ELocalConnectionState.Started);
            OnConnectionUpdated?.Invoke(_hostClientID, ERemoteConnectionState.Connected);
        }

        private void StopHostClient()
        {
            SetLocalClientState(ELocalConnectionState.Stopping);
            _hostClientID = 0;
            OnConnectionUpdated?.Invoke(_hostClientID, ERemoteConnectionState.Disconnected);
            SetLocalClientState(ELocalConnectionState.Stopped);
        }

        public override void DisconnectClient(uint clientID)
        {
            if (!IsServer)
            {
                OnTransportLogAdded?.Invoke("The server has to be started to disconnect a client.", EMessageSeverity.Error);
                return;
            }

            if (IsHost && clientID == _hostClientID)
            {
                _hostClientID = 0;
                OnConnectionUpdated?.Invoke(_hostClientID, ERemoteConnectionState.Disconnected);
                SetLocalClientState(ELocalConnectionState.Stopping);
                SetLocalClientState(ELocalConnectionState.Stopped);
                return;
            }

            if (!_clientIDToConnection.TryGetValue(clientID, out var conn))
            {
                OnTransportLogAdded?.Invoke($"The client with the ID {clientID} does not exist", EMessageSeverity.Error);
                return;
            }
            
            if (_driver.GetConnectionState(conn) != NetworkConnection.State.Disconnected)
            {
                // TODO : flush send queues
                CleanOutgoingMessages(conn);
                _driver.Disconnect(conn);
                while (_driver.PopEventForConnection(conn, out _) != NetworkEvent.Type.Empty) {}
            }
            
            OnConnectionUpdated?.Invoke(_hostClientID, ERemoteConnectionState.Disconnected);
        }

        public void SetRelayServerData(RelayServerData relayServerData)
        {
            if (LocalServerState is ELocalConnectionState.Starting or ELocalConnectionState.Started)
            {
                OnTransportLogAdded?.Invoke(
                    "Relay server data should not be set while a local connection is already active!" +
                    "If you are setting the relay data on the client side of a host, you can ignore this warning, " +
                    "but setting the relay data again as client is unnecessary."
                    , EMessageSeverity.Warning);
            }
            
            _relayServerData = relayServerData;
            Settings.ProtocolType = EProtocolType.UnityRelayTransport;
        }
        
        #endregion
        
        #region incoming

        public override void IterateIncoming()
        {
            if (!_driver.IsCreated) return;

            _driver.ScheduleUpdate().Complete();

            while (_driver.IsCreated && AcceptConnection()) {}
            while (_driver.IsCreated && ProcessEvent()) {}
        }

        private bool AcceptConnection()
        {
            var conn = _driver.Accept();
            if (conn == default) return false;

            var numberOfConnectedClients = _clientIDToConnection.Count;
            if (IsHost) numberOfConnectedClients++;
            if (numberOfConnectedClients >= _maxNumberOfClients)
            {
                _driver.Disconnect(conn);
                while (_driver.PopEventForConnection(conn, out _) != NetworkEvent.Type.Empty) {}
                return false;
            }
            
            var clientID = _clientIDs++;
            _clientIDToConnection[clientID] = conn;
            _connectionToClientID[conn] = clientID;
            OnConnectionUpdated?.Invoke(clientID, ERemoteConnectionState.Connected);
            
            return true;
        }

        private bool ProcessEvent()
        {
            var eventType = _driver.PopEvent(out var conn, out var reader, out var pipe);

            switch (eventType)
            {
                case NetworkEvent.Type.Data:
                    HandleData(conn, reader, pipe);
                    return true;
                case NetworkEvent.Type.Connect:
                    SetLocalClientState(ELocalConnectionState.Started);
                    return true;
                case NetworkEvent.Type.Disconnect:
                    if (LocalClientState is ELocalConnectionState.Starting or ELocalConnectionState.Started
                        && conn.Equals(_serverConnection))
                    {
                        SetLocalClientState(ELocalConnectionState.Stopping);
                        // TODO : flush send queues
                        CleanOutgoingMessages(_serverConnection);
                        DisposeInternals();
                        SetLocalClientState(ELocalConnectionState.Stopped);
                    }
                    else if (LocalServerState is ELocalConnectionState.Starting or ELocalConnectionState.Started)
                    {
                        // TODO : flush send queues
                        CleanOutgoingMessages(conn);
                        _connectionToClientID.Remove(conn, out var clientID);
                        _clientIDToConnection.Remove(clientID);
                        OnConnectionUpdated?.Invoke(clientID, ERemoteConnectionState.Disconnected);
                    }
                    // TODO : handle reason
                    return true;
                default:
                    return false;
            }
        }

        private void HandleData(NetworkConnection conn, DataStreamReader reader, NetworkPipeline pipe)
        {
            var data = new byte[reader.Length];
            unsafe
            {
                fixed (byte* dataPtr = data)
                {
                    reader.ReadBytesUnsafe(dataPtr, reader.Length);
                }
            }

            if (IsClient && conn.Equals(_serverConnection))
            {
                OnClientReceivedData?.Invoke(new()
                {
                    Data = data,
                    Timestamp = DateTime.Now,
                    Channel = ParseChannelPipeline(pipe)
                });
            }
            else if (IsServer)
            {
                OnServerReceivedData?.Invoke(new()
                {
                    ClientID = _connectionToClientID[conn],
                    Data = data,
                    Timestamp = DateTime.Now,
                    Channel = ParseChannelPipeline(pipe)
                });
            }
        }
        
        #endregion
        
        #region outgoing

        public override void SendDataToServer(byte[] data, ENetworkChannel channel = ENetworkChannel.UnreliableUnordered)
        {
            if (IsHost)
            {
                OnServerReceivedData?.Invoke(new()
                {
                    ClientID = _hostClientID,
                    Data = data,
                    Timestamp = DateTime.Now,
                    Channel = channel
                });
                return;
            }
            
            if (!IsClient)
            {
                OnTransportLogAdded?.Invoke("The local client has to be started to send data to the server.", EMessageSeverity.Error);
                return;
            }

            SendTarget target = new(_serverConnection, ParseChannelPipeline(channel));
            if (!_outgoingMessages.TryGetValue(target, out var queue))
                _outgoingMessages[target] = queue = new(1);

            queue.Enqueue(new(data, Allocator.Persistent));
        }

        public override void SendDataToClient(uint clientID, byte[] data, ENetworkChannel channel = ENetworkChannel.UnreliableUnordered)
        {
            if (IsHost && clientID == _hostClientID)
            {
                OnClientReceivedData?.Invoke(new()
                {
                    Data = data,
                    Timestamp = DateTime.Now,
                    Channel = channel
                });
                return;
            }
            
            if (!IsServer)
            {
                OnTransportLogAdded?.Invoke("The server has to be started to send data to clients.", EMessageSeverity.Error);
                return;
            }

            if (!_clientIDToConnection.TryGetValue(clientID, out var conn))
            {
                OnTransportLogAdded?.Invoke($"The client with the ID {clientID} does not exist!", EMessageSeverity.Error);
                return;
            }
            
            SendTarget target = new(conn, ParseChannelPipeline(channel));
            if (!_outgoingMessages.TryGetValue(target, out var queue))
                _outgoingMessages[target] = queue = new(1);

            queue.Enqueue(new(data, Allocator.Persistent));
        }

        public override void IterateOutgoing()
        {
            if (!_driver.IsCreated) return;

            foreach (var (sendTarget, sendQueue) in _outgoingMessages)
            {
                if (!_driver.IsCreated) return;

                /* TODO : implement once message batching works
                 new SendQueueJob
                 {
                    Driver = _driver.ToConcurrent(),
                    Target = sendTarget,
                    Queue = sendQueue
                 }.Run();
                 */
                
                while (sendQueue.Count > 0)
                {
                    var result = _driver.BeginSend(sendTarget.Pipeline, sendTarget.Connection, out var writer);
                    if (result != (int)StatusCode.Success)
                    {
                        OnTransportLogAdded?.Invoke($"Sending data start failed: {ParseStatusCode(result)}", EMessageSeverity.Error);
                        return;
                    }

                    var data = sendQueue.Peek();
                    writer.WriteBytes(data);
                    result = _driver.EndSend(writer);

                    if (result == data.Length)
                    {
                        sendQueue.Dequeue().Dispose();
                        continue;
                    }

                    if (result != (int)StatusCode.NetworkSendQueueFull)
                    {
                        sendQueue.Dequeue().Dispose();
                        OnTransportLogAdded?.Invoke($"Sending data end failed: {ParseStatusCode(result)}", EMessageSeverity.Error);
                    }

                    return;
                }
            }
        }
        
        public override int GetRTTToServer()
        {
            if (!IsClient) return 0;
            if (IsHost) return LOCAL_HOST_RTT;
            
            _driver.GetPipelineBuffers(
                _reliablePipeline, 
                NetworkPipelineStageId.Get<ReliableSequencedPipelineStage>(),
                _serverConnection,
                out _,
                out _,
                out var sharedBuffer
            );

            unsafe
            {
                var sharedContext = (ReliableUtility.SharedContext*)sharedBuffer.GetUnsafePtr();
                return sharedContext->RttInfo.LastRtt;
            }
        }

        public override int GetRTTToClient(uint clientID)
        {
            if (!IsServer) return 0;
            if (IsHost && clientID == _hostClientID) return LOCAL_HOST_RTT;

            if (!_clientIDToConnection.TryGetValue(clientID, out var conn))
                return 0;
            
            _driver.GetPipelineBuffers(
                _reliablePipeline, 
                NetworkPipelineStageId.Get<ReliableSequencedPipelineStage>(),
                conn,
                out _,
                out _,
                out var sharedBuffer
            );

            unsafe
            {
                var sharedContext = (ReliableUtility.SharedContext*)sharedBuffer.GetUnsafePtr();
                return sharedContext->RttInfo.LastRtt;
            }
        }
        
        #endregion
        
        #region utility
        
        private void SetLocalServerState(ELocalConnectionState state)
        {
            if (_serverState == state) return;
            _serverState = state;
            OnServerStateUpdated?.Invoke(_serverState);
        }
        
        private void SetLocalClientState(ELocalConnectionState state)
        {
            if (_clientState == state) return;
            _clientState = state;
            OnClientStateUpdated?.Invoke(_clientState);
        }

        private void InitialiseSettings()
        {
            _networkSettings = new(Allocator.Persistent);
            _networkSettings.WithNetworkConfigParameters(
                connectTimeoutMS: Settings.ConnectTimeoutMS,
                maxConnectAttempts: Settings.MaxConnectAttempts,
                disconnectTimeoutMS: Settings.DisconnectTimeoutMS,
                heartbeatTimeoutMS: Settings.HeartbeatTimeoutMS
            );
            _networkSettings.WithFragmentationStageParameters(
                payloadCapacity: Settings.PayloadCapacity
            );
            _networkSettings.WithReliableStageParameters(
                windowSize: Settings.WindowSize,
                minimumResendTime: Settings.MinimumResendTime,
                maximumResendTime: Settings.MaximumResendTime
            );
        }

        private void InitialiseDrivers()
        {
            _driver = NetworkDriver.Create(_networkSettings);
            _unreliablePipeline = NetworkPipeline.Null;
            _unreliableSequencedPipeline = _driver.CreatePipeline(typeof(UnreliableSequencedPipelineStage));
            _reliablePipeline = _driver.CreatePipeline(typeof(FragmentationPipelineStage));
            _reliableSequencedPipeline = _driver.CreatePipeline(typeof(FragmentationPipelineStage), typeof(ReliableSequencedPipelineStage));
        }

        private void DisposeInternals()
        {
            if (_driver.IsCreated) _driver.Dispose();
            if (_networkSettings.IsCreated)
            {
                _networkSettings.Dispose();
                _networkSettings = default;
            }
            _unreliableSequencedPipeline = NetworkPipeline.Null;
            _reliablePipeline = NetworkPipeline.Null;
            _reliableSequencedPipeline = NetworkPipeline.Null;
        }

        private NetworkPipeline ParseChannelPipeline(ENetworkChannel channel)
        {
            return channel switch
            {
                ENetworkChannel.ReliableOrdered => _reliableSequencedPipeline,
                ENetworkChannel.ReliableUnordered => _reliablePipeline,
                ENetworkChannel.UnreliableOrdered => _unreliableSequencedPipeline,
                ENetworkChannel.UnreliableUnordered => _unreliablePipeline,
                _ => NetworkPipeline.Null
            };
        }

        private ENetworkChannel ParseChannelPipeline(NetworkPipeline pipeline)
        {
            if (pipeline == _reliableSequencedPipeline) return ENetworkChannel.ReliableOrdered;
            if (pipeline == _reliablePipeline) return ENetworkChannel.ReliableUnordered;
            if (pipeline == _unreliableSequencedPipeline) return ENetworkChannel.UnreliableOrdered;
            if (pipeline == _unreliablePipeline) return ENetworkChannel.UnreliableUnordered;
            return default;
        }

        private void CleanOutgoingMessages(NetworkConnection conn)
        {
            var sendTargets = new NativeList<SendTarget>(4, Allocator.Persistent);
            foreach (var kvp in _outgoingMessages)
            {
                if (kvp.Key.Connection.Equals(conn))
                {
                    sendTargets.Add(kvp.Key);
                }
            }

            foreach (var sendTarget in sendTargets)
            {
                _outgoingMessages.Remove(sendTarget, out var queue);
                queue.Dispose();
            }

            sendTargets.Dispose();
        }

        private static string ParseStatusCode(int code)
        {
            switch ((StatusCode)code)
            {
                case StatusCode.Success:
                    return "Operation completed successfully.";
                case StatusCode.NetworkIdMismatch:
                    return "Connection is invalid.";
                case StatusCode.NetworkVersionMismatch:
                    return "Connection is invalid. This is usually caused by an attempt to use a connection that has been already closed.";
                case StatusCode.NetworkStateMismatch:
                    return "State of the connection is invalid for the operation requested. This is usually caused by an attempt to send on a connecting/closed connection.";
                case StatusCode.NetworkPacketOverflow:
                    return "Packet is too large for the supported capacity.";
                case StatusCode.NetworkSendQueueFull:
                    return "Packet couldn't be sent because the send queue is full.";
                case StatusCode.NetworkDriverParallelForErr:
                    return "Attempted to process the same connection in different jobs.";
                case StatusCode.NetworkSendHandleInvalid:
                    return "The DataStreamWriter is invalid.";
                case StatusCode.NetworkReceiveQueueFull:
                    return "A message couldn't be received because the receive queue is full. This can only be returned through ReceiveErrorCode.";
                case StatusCode.NetworkSocketError:
                    return "There was an error from the underlying low-level socket.";
                default:
                    return string.Empty;
            }
        }
        
        #endregion
    }
}
