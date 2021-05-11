﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using MLAPI.Logging;
using MLAPI.Transports.Tasks;
using Steamworks;
using Steamworks.Data;
using UnityEngine;

namespace MLAPI.Transports.Facepunch
{
    using SocketConnection = Steamworks.Data.Connection;

    public class FacepunchTransport : NetworkTransport, IConnectionManager, ISocketManager
    {
        private ConnectionManager connectionManager;
        private SocketManager socketManager;
        private Dictionary<ulong, Client> connectedClients;
        private Dictionary<NetworkChannel, SendType> channelSendTypes;
        private Queue<ConnectionEvent> eventQueue;

        [SerializeField] private List<TransportChannel> channels = new List<TransportChannel>();

        [Space]
        [Tooltip("The Steam App ID of your game. Technically you're not allowed to use 480, but Valve doesn't do anything about it so it's fine for testing purposes.")]
        [SerializeField] private uint steamAppId = 480;

        [Tooltip("The Steam ID of the user targeted when joining as a client.")]
        [SerializeField] private ulong targetSteamId;

        [Header("Info")]
        [ReadOnly]
        [Tooltip("When in play mode, this will display your Steam ID.")]
        [SerializeField] private ulong userSteamId;

        public SteamId TargetSteamId { get => targetSteamId; set => targetSteamId = value; }

        private LogLevel LogLevel => NetworkManager.Singleton.LogLevel;

        private class Client
        {
            public SteamId steamId;
            public SocketConnection connection;
        }

        private struct ConnectionEvent
        {
            public NetworkEvent type;
            public ulong clientId;
            public NetworkChannel channel;
            public ArraySegment<byte> payload;
            public float receiveTime;
        }

        #region MonoBehaviour Messages

        private void Awake()
        {
            try
            {
                SteamClient.Init(steamAppId, false);
            }
            catch (Exception e)
            {
                if (LogLevel <= LogLevel.Error)
                    NetworkLog.LogErrorServer($"[{nameof(FacepunchTransport)}] - Caught an exeption during initialization of Steam client: {e}");
            }
            finally
            {
                StartCoroutine(InitSteamworks());
            }
        }

        private void FixedUpdate()
        {
            SteamClient.RunCallbacks();
        }

        private void OnDestroy()
        {
            SteamClient.Shutdown();
        }

        #endregion

        #region NetworkTransport Overrides

        public override ulong ServerClientId => 0;

        public override void DisconnectLocalClient()
        {
            connectionManager.Connection.Close();

            if (LogLevel <= LogLevel.Developer)
                NetworkLog.LogInfoServer($"[{nameof(FacepunchTransport)}] - Disconnecting local client.");
        }

        public override void DisconnectRemoteClient(ulong clientId)
        {
            if (connectedClients.TryGetValue(clientId, out Client user))
            {
                user.connection.Close();
                connectedClients.Remove(clientId);

                if (LogLevel <= LogLevel.Developer)
                    NetworkLog.LogInfoServer($"[{nameof(FacepunchTransport)}] - Disconnecting remote client with ID {clientId}.");
            }
            else if (LogLevel <= LogLevel.Normal)
                NetworkLog.LogWarningServer($"[{nameof(FacepunchTransport)}] - Failed to disconnect remote client with ID {clientId}, client not connected.");
        }

        public override unsafe ulong GetCurrentRtt(ulong clientId)
        {
            return 69;
        }

        public override void Init()
        {
            connectedClients = new Dictionary<ulong, Client>();
            channelSendTypes = new Dictionary<NetworkChannel, SendType>();
            eventQueue = new Queue<ConnectionEvent>();

            foreach (TransportChannel channel in MLAPI_CHANNELS.Concat(channels))
            {
                SendType sendType = channel.Delivery switch
                {
                    NetworkDelivery.Reliable => SendType.Reliable,
                    NetworkDelivery.ReliableFragmentedSequenced => SendType.Reliable,
                    NetworkDelivery.ReliableSequenced => SendType.Reliable,
                    NetworkDelivery.Unreliable => SendType.Unreliable,
                    NetworkDelivery.UnreliableSequenced => SendType.Unreliable,
                    _ => SendType.Reliable
                };

                channelSendTypes.Add(channel.Channel, sendType);
            }
        }

        public override void Shutdown()
        {
            try
            {
                if (LogLevel <= LogLevel.Developer)
                    NetworkLog.LogInfoServer($"[{nameof(FacepunchTransport)}] - Shutting down.");

                connectionManager?.Close();
                socketManager?.Close();
            }
            catch (Exception e)
            {
                if (LogLevel <= LogLevel.Error)
                    NetworkLog.LogErrorServer($"[{nameof(FacepunchTransport)}] - Caught an exception while shutting down: {e}");
            }
        }

        public override unsafe void Send(ulong clientId, ArraySegment<byte> data, NetworkChannel networkChannel)
        {
            if (!channelSendTypes.TryGetValue(networkChannel, out SendType sendType))
                if (!channelSendTypes.TryGetValue(NetworkChannel.DefaultMessage, out sendType))
                    sendType = SendType.Reliable;

            if (clientId == ServerClientId)
                fixed (byte* pointer = data.Array)
                {
                    byte* buffer = stackalloc byte[data.Count + 1];

                    Buffer.MemoryCopy(pointer + data.Offset, buffer, data.Count, data.Count);
                    buffer[data.Count] = (byte)networkChannel;

                    connectionManager.Connection.SendMessage((IntPtr)buffer, data.Count + 1, sendType);
                }
            else if (connectedClients.TryGetValue(clientId, out Client user))
                fixed (byte* pointer = data.Array)
                {
                    byte* buffer = stackalloc byte[data.Count + 1];

                    Buffer.MemoryCopy(pointer + data.Offset, buffer, data.Count, data.Count);
                    buffer[data.Count] = (byte)networkChannel;

                    user.connection.SendMessage((IntPtr)buffer, data.Count + 1, sendType);
                }
            else if (LogLevel <= LogLevel.Normal)
                NetworkLog.LogWarningServer($"[{nameof(FacepunchTransport)}] - Failed to send packet to remote client with ID {clientId}, client not connected.");
        }

        public override NetworkEvent PollEvent(out ulong clientId, out NetworkChannel networkChannel, out ArraySegment<byte> payload, out float receiveTime)
        {
            connectionManager?.Receive();
            socketManager?.Receive();

            if (eventQueue.Count > 0)
            {
                ConnectionEvent e = eventQueue.Dequeue();
                clientId = e.clientId;
                networkChannel = e.channel;
                payload = e.payload;
                receiveTime = e.receiveTime;
                return e.type;
            }
            else
            {
                clientId = 0;
                networkChannel = default;
                receiveTime = Time.realtimeSinceStartup;
                return NetworkEvent.Nothing;
            }
        }

        public override SocketTasks StartClient()
        {
            if (LogLevel <= LogLevel.Developer)
                NetworkLog.LogInfoServer($"[{nameof(FacepunchTransport)}] - Starting as client.");

            connectionManager = SteamNetworkingSockets.ConnectRelay<ConnectionManager>(TargetSteamId);
            connectionManager.Interface = this;
            return SocketTask.Working.AsTasks();
        }

        public override SocketTasks StartServer()
        {
            if (LogLevel <= LogLevel.Developer)
                NetworkLog.LogInfoServer($"[{nameof(FacepunchTransport)}] - Starting as server.");

            socketManager = SteamNetworkingSockets.CreateRelaySocket<SocketManager>();
            socketManager.Interface = this;
            return SocketTask.Done.AsTasks();
        }

        #endregion

        #region ConnectionManager Implementation

        void IConnectionManager.OnConnecting(ConnectionInfo info)
        {
            if (LogLevel <= LogLevel.Developer)
                NetworkLog.LogInfoServer($"[{nameof(FacepunchTransport)}] - Connecting with Steam user {info.Identity.SteamId}.");
        }

        void IConnectionManager.OnConnected(ConnectionInfo info)
        {
            eventQueue.Enqueue(new ConnectionEvent()
            {
                type = NetworkEvent.Connect,
                clientId = ServerClientId,
                receiveTime = Time.realtimeSinceStartup
            });

            if (LogLevel <= LogLevel.Developer)
                NetworkLog.LogInfoServer($"[{nameof(FacepunchTransport)}] - Connected with Steam user {info.Identity.SteamId}.");
        }

        void IConnectionManager.OnDisconnected(ConnectionInfo info)
        {
            eventQueue.Enqueue(new ConnectionEvent()
            {
                type = NetworkEvent.Disconnect,
                clientId = ServerClientId,
                receiveTime = Time.realtimeSinceStartup
            });

            if (LogLevel <= LogLevel.Developer)
                NetworkLog.LogInfoServer($"[{nameof(FacepunchTransport)}] - Disconnected Steam user {info.Identity.SteamId}.");
        }

        void IConnectionManager.OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel)
        {
            byte[] payload = new byte[size];
            Marshal.Copy(data, payload, 0, size);
            eventQueue.Enqueue(new ConnectionEvent()
            {
                channel = (NetworkChannel)payload[size - 1],
                clientId = ServerClientId,
                payload = new ArraySegment<byte>(payload, 0, size - 1),
                receiveTime = Time.realtimeSinceStartup,
                type = NetworkEvent.Data
            });
        }

        #endregion

        #region SocketManager Implementation

        void ISocketManager.OnConnecting(SocketConnection connection, ConnectionInfo info)
        {
            if (LogLevel <= LogLevel.Developer)
                NetworkLog.LogInfoServer($"[{nameof(FacepunchTransport)}] - Accepting connection from Steam user {info.Identity.SteamId}.");

            connection.Accept();
        }

        void ISocketManager.OnConnected(SocketConnection connection, ConnectionInfo info)
        {
            if (!connectedClients.ContainsKey(connection.Id))
            {
                connectedClients.Add(connection.Id, new Client()
                {
                    connection = connection,
                    steamId = info.Identity.SteamId
                });

                eventQueue.Enqueue(new ConnectionEvent()
                {
                    type = NetworkEvent.Connect,
                    clientId = connection.Id,
                    receiveTime = Time.realtimeSinceStartup
                });

                if (LogLevel <= LogLevel.Developer)
                    NetworkLog.LogInfoServer($"[{nameof(FacepunchTransport)}] - Connected with Steam user {info.Identity.SteamId}.");
            }
            else if (LogLevel <= LogLevel.Normal)
                NetworkLog.LogWarningServer($"[{nameof(FacepunchTransport)}] - Failed to connect client with ID {connection.Id}, client already connected.");
        }

        void ISocketManager.OnDisconnected(SocketConnection connection, ConnectionInfo info)
        {
            connectedClients.Remove(connection.Id);

            eventQueue.Enqueue(new ConnectionEvent()
            {
                type = NetworkEvent.Disconnect,
                clientId = connection.Id,
                receiveTime = Time.realtimeSinceStartup
            });

            if (LogLevel <= LogLevel.Developer)
                NetworkLog.LogInfoServer($"[{nameof(FacepunchTransport)}] - Disconnected Steam user {info.Identity.SteamId}");
        }

        void ISocketManager.OnMessage(SocketConnection connection, NetIdentity identity, IntPtr data, int size, long messageNum, long recvTime, int channel)
        {
            byte[] payload = new byte[size];
            Marshal.Copy(data, payload, 0, size);
            eventQueue.Enqueue(new ConnectionEvent()
            {
                channel = (NetworkChannel)payload[size - 1],
                clientId = connection.Id,
                payload = new ArraySegment<byte>(payload, 0, size - 1),
                receiveTime = Time.realtimeSinceStartup,
                type = NetworkEvent.Data
            });
        }

        #endregion

        #region Utility Methods

        private IEnumerator InitSteamworks()
        {
            yield return new WaitUntil(() => SteamClient.IsValid);

            SteamNetworkingUtils.InitRelayNetworkAccess();

            if (LogLevel <= LogLevel.Developer)
                NetworkLog.LogInfoServer($"[{nameof(FacepunchTransport)}] - Initialized access to Steam Relay Network.");

            userSteamId = SteamClient.SteamId;

            if (LogLevel <= LogLevel.Developer)
                NetworkLog.LogInfoServer($"[{nameof(FacepunchTransport)}] - Fetched user Steam ID.");
        }

        #endregion
    }
}