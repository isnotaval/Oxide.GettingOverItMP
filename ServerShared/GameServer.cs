﻿using System;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using LiteNetLib.Utils;
using ServerShared.Player;
using UnityEngine;

namespace ServerShared
{
    public class GameServer
    {
        class PendingConnection
        {
            public readonly NetPeer Peer;
            public readonly DateTime JoinTime;

            public PendingConnection(NetPeer peer, DateTime joinTime)
            {
                Peer = peer;
                JoinTime = joinTime;
            }
        }

        public static readonly TimeSpan PendingConnectionTimeout = new TimeSpan(0, 0, 0, 5); // 5 seconds

        public readonly int Port;
        public readonly bool ListenServer;

        public readonly Dictionary<NetPeer, NetPlayer> Players = new Dictionary<NetPeer, NetPlayer>();

        private readonly EventBasedNetListener listener;
        private readonly NetManager server;

        private double nextSendTime = 0;
        private readonly List<PendingConnection> pendingConnections = new List<PendingConnection>();

        public GameServer(int maxConnections, int port, bool listenServer)
        {
            if (maxConnections <= 0)
                throw new ArgumentException("Max connections needs to be > 0.");

            listener = new EventBasedNetListener();

            listener.PeerConnectedEvent += OnPeerConnected;
            listener.PeerDisconnectedEvent += OnPeerDisconnected;
            listener.NetworkReceiveEvent += OnReceiveData;

            server = new NetManager(listener, maxConnections, SharedConstants.AppName);
            Port = port;
            ListenServer = listenServer;

            server.UpdateTime = 33; // Send/receive 30 times per second.
            
            if (listenServer)
            {
                // Todo: Implement NAT punchthrough.
            }
        }

        public void Start()
        {
            server.Start(Port);
        }

        public void Stop()
        {
            server.Stop();
        }
        
        public void Update()
        {
            server.PollEvents();

            // Disconnect timed out pending connections
            foreach (var connection in pendingConnections.ToList())
            {
                if (DateTime.UtcNow - connection.JoinTime >= PendingConnectionTimeout)
                {
                    Console.WriteLine("Disconnecting pending connection (handshake timeout)");
                    DisconnectPeer(connection.Peer, DisconnectReason.HandshakeTimeout);
                }
            }

            double ms = DateTime.UtcNow.Ticks / 10_000d;
            
            if (ms >= nextSendTime)
            {
                nextSendTime = ms + server.UpdateTime;

                if (Players.Count <= 0)
                    return;

                Dictionary<int, PlayerMove> toSend = Players.Values.Where(plr => !plr.Spectating).ToDictionary(plr => plr.Id, plr => plr.Movement);

                var writer = new NetDataWriter();
                writer.Put(MessageType.MoveData);
                writer.Put(toSend);

                Broadcast(writer, SendOptions.Sequenced);
            }
        }

        public void BroadcastChatMessage(string message, NetPeer except = null)
        {
            BroadcastChatMessage(message, Color.white, except);
        }

        public void BroadcastChatMessage(string message, Color color, NetPeer except = null)
        {
            BroadcastChatMessage(message, color, null, except);
        }

        public void BroadcastChatMessage(string message, Color color, NetPlayer player, NetPeer except = null)
        {
            var writer = new NetDataWriter();
            writer.Put(MessageType.ChatMessage);
            writer.Put(player?.Name);
            writer.Put(color);
            writer.Put(message);

            Broadcast(writer, SendOptions.ReliableOrdered, except);
        }

        private NetPlayer AddPeer(NetPeer peer, string playerName)
        {
            var netPlayer = new NetPlayer(peer, playerName, this);
            Players[peer] = netPlayer;

            var writer = new NetDataWriter();
            writer.Put(MessageType.HandshakeResponse);
            writer.Put(netPlayer.Id);
            writer.Put(netPlayer.Name);

            var allPlayers = Players.Values.Where(plr => !plr.Spectating && plr.Peer != peer).ToList();
            var allNames = allPlayers.ToDictionary(plr => plr.Id, plr => plr.Name);
            var allPlayersDict = allPlayers.ToDictionary(plr => plr.Id, plr => plr.Movement);
            writer.Put(allNames);
            writer.Put(allPlayersDict);
            peer.Send(writer, SendOptions.ReliableOrdered);

            Console.WriteLine($"Added peer from {peer.EndPoint} with id {netPlayer.Id} (total: {Players.Count})");
            return netPlayer;
        }

        private void RemovePeer(NetPeer peer)
        {
            if (!Players.ContainsKey(peer))
                return;

            int playerId = Players[peer].Id;

            Players.Remove(peer);
            
            var writer = new NetDataWriter();
            writer.Put(MessageType.RemovePlayer);
            writer.Put(playerId);
            Broadcast(writer, SendOptions.ReliableOrdered);

            Console.WriteLine($"Removed peer from {peer.EndPoint} with id {playerId} (total: {Players.Count})");
        }

        /// <summary>
        /// Sends a message to all spawned clients.
        /// </summary>
        public void Broadcast(NetDataWriter writer, SendOptions sendOptions, NetPeer except = null)
        {
            foreach (var kv in Players)
            {
                if (except == null || kv.Key != except)
                {
                    kv.Key.Send(writer, sendOptions);
                }
            }
        }

        private void DisconnectPeer(NetPeer peer, DisconnectReason reason)
        {
            Console.WriteLine($"Disconnecting peer from {peer.EndPoint}: {reason}");

            var writer = new NetDataWriter();
            writer.Put(reason);
            server.DisconnectPeer(peer, writer);
        }

        private void OnPeerConnected(NetPeer peer)
        {
            // Todo: protect against mass connections from the same ip.

            Console.WriteLine($"Connection from {peer.EndPoint}");
            pendingConnections.Add(new PendingConnection(peer, DateTime.UtcNow));
        }

        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectinfo)
        {
            Console.WriteLine($"Connection gone from {peer.EndPoint} ({disconnectinfo.Reason})");
            pendingConnections.RemoveAll(conn => conn.Peer == peer);

            NetPlayer player = null;

            if (Players.ContainsKey(peer))
                player = Players[peer];

            foreach (var netPlayer in Players.Values)
            {
                if (netPlayer.SpectateTarget == player)
                {
                    netPlayer.Spectate(null);
                }
            }

            RemovePeer(peer);

            if (player != null)
                BroadcastChatMessage($"{player.Name} left the server.", SharedConstants.ColorBlue);
        }

        private void OnReceiveData(NetPeer peer, NetDataReader reader)
        {
            try
            {
                MessageType messageType = (MessageType) reader.GetByte();

                if (messageType != MessageType.ClientHandshake && pendingConnections.Any(conn => conn.Peer == peer))
                {
                    DisconnectPeer(peer, DisconnectReason.NotAccepted);
                    return;
                }

                NetPlayer peerPlayer = Players.ContainsKey(peer) ? Players[peer] : null;

                switch (messageType)
                {
                    default: throw new UnexpectedMessageFromClientException(messageType);
                    case MessageType.ClientHandshake:
                    {
                        if (Players.ContainsKey(peer))
                        {
                            DisconnectPeer(peer, DisconnectReason.DuplicateHandshake);
                            break;
                        }

                        int version = reader.GetInt();
                        string playerName = reader.GetString();
                        PlayerMove movementData = reader.GetPlayerMove();

                        if (version != SharedConstants.Version)
                        {
                            DisconnectPeer(peer, version < SharedConstants.Version ? DisconnectReason.VersionOlder : DisconnectReason.VersionNewer);
                            break;
                        }

                        if (playerName.Length > SharedConstants.MaxNameLength || !playerName.All(ch => SharedConstants.AllowedCharacters.Contains(ch)))
                        {
                            DisconnectPeer(peer, DisconnectReason.InvalidName);
                            break;
                        }

                        pendingConnections.RemoveAll(conn => conn.Peer == peer);
                        Console.WriteLine($"Got valid handshake from {peer.EndPoint}");

                        var player = AddPeer(peer, playerName);
                        Players[peer].Movement = movementData;
                        
                        var writer = new NetDataWriter();
                        writer.Put(MessageType.CreatePlayer);
                        writer.Put(player.Id);
                        writer.Put(player.Name);
                        writer.Put(player.Movement);
                        Broadcast(writer, SendOptions.ReliableOrdered, peer);
                        
                        Console.WriteLine($"Peer with id {player.Id} is now spawned");
                        BroadcastChatMessage($"{player.Name} joined the server.", SharedConstants.ColorBlue, peer);
                        
                        break;
                    }
                    case MessageType.MoveData:
                    {
                        peerPlayer.Movement = reader.GetPlayerMove();
                        break;
                    }
                    case MessageType.ChatMessage:
                    {
                        var message = reader.GetString();

                        message = message.Trim();

                        if (message.Length > SharedConstants.MaxChatLength)
                            message = message.Substring(0, SharedConstants.MaxChatLength);

                        // Todo: better handling of chat commands
                        if (message.StartsWith("/"))
                        {
                            if (message.StartsWith("/spectate "))
                            {
                                NetPlayer target;
                                int targetId;
                                if (!int.TryParse(message.Substring("/spectate ".Length), out targetId))
                                {
                                    var players = Players.Values.Where(plr => !plr.Spectating && plr.Name.ToLower().StartsWith(message.Substring("/spectate ".Length).ToLower())).ToList();

                                    if (players.Count == 0)
                                    {
                                        peerPlayer.SendChatMessage("There is no player with this name.", SharedConstants.ColorRed);
                                        return;
                                    }

                                    if (players.Count > 1)
                                    {
                                        peerPlayer.SendChatMessage("Found more than 1 player with this name. Try be more specific or type their id instead.", SharedConstants.ColorRed);
                                        return;
                                    }

                                    target = players.First();
                                }
                                else
                                {
                                    target = Players.Values.FirstOrDefault(plr => !plr.Spectating && plr.Id == targetId);

                                    if (target == null)
                                    {
                                        peerPlayer.SendChatMessage("There is no player with this id.", SharedConstants.ColorRed);
                                        return;
                                    }
                                }

                                if (target == peerPlayer)
                                {
                                    peerPlayer.SendChatMessage("You can't spectate yourself dummy.", SharedConstants.ColorRed);
                                    return;
                                }

                                peerPlayer.Spectate(target);
                            }

                            return;
                        }

                        Color color = Color.white;
                        BroadcastChatMessage(message, color, Players[peer]);

                        break;
                    }
                    case MessageType.ClientStopSpectating:
                    {
                        peerPlayer.Spectate(null);
                        break;
                    }
                }
            }
            catch (UnexpectedMessageFromClientException ex)
            {
                Console.WriteLine($"Peer sent unexpected message type: {ex.MessageType}");
                DisconnectPeer(peer, DisconnectReason.InvalidMessage);
            }
            catch (Exception ex)
            {
                Console.WriteLine("OnReceiveData errored:\n" + ex);
                DisconnectPeer(peer, DisconnectReason.InvalidMessage);
            }
        }
    }
}
