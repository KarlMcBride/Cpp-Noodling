﻿using Lidgren.Network;
using System;
using System.Collections.Generic;
using System.Threading;

namespace ChatNetworking
{
    public class Server
    {
        private NetServer           m_server;
        private Thread              m_serverThread;
        private List<Participant>   m_ParticipantList;

        private bool                m_keepRunning;
        private int                 m_lastConnectedParticipantCount;


        public Server()
        {
            ServerInit();
        }


        private void ServerInit()
        {
            m_ParticipantList = new List<Participant>();

            NetPeerConfiguration config = new NetPeerConfiguration(SharedConstants.APP_IDENTIFIER)
            {
                Port                = SharedConstants.HOST_PORT,
                MaximumConnections  = 200,
                PingInterval        = SharedConstants.PING_INTERVAL_SECONDS,
                ConnectionTimeout   = SharedConstants.CONNECTION_TIMEOUT_SECONDS
            };
            config.EnableMessageType(NetIncomingMessageType.ConnectionApproval);

            m_server = new NetServer(config);
            m_server.Start();
            Console.WriteLine("Server Started");

            m_serverThread = new Thread(MainLoop);
            m_serverThread.Start();
        }


        private void MainLoop()
        {
            NetIncomingMessage incomingMessage;

            m_keepRunning = true;
            while (m_keepRunning)
            {
                incomingMessage = m_server.ReadMessage();

                if (incomingMessage != null && incomingMessage.LengthBytes > 0)
                {
                    switch (incomingMessage.MessageType)
                    {
                        // First message each client should create is request for connection approval.
                        case NetIncomingMessageType.ConnectionApproval:
                        {
                            if (incomingMessage.ReadByte() == (byte)PacketTypes.LOGIN)
                            {
                                Console.WriteLine("Server: incoming login");
                                incomingMessage.SenderConnection.Approve();

                                // Get participant name and add them to the participant list.
                                string newParticipantName = incomingMessage.ReadString();
                                m_ParticipantList.Add(new Participant(newParticipantName, incomingMessage.SenderEndPoint.ToString()));

                                NetOutgoingMessage outgoingMessage = m_server.CreateMessage();
                                outgoingMessage.Write((byte)PacketTypes.NOTIFY_CLIENTS_OF_UPDATED_PARTICIPANT_LIST);
                                outgoingMessage.Write(m_ParticipantList.Count);

                                foreach (Participant participant in m_ParticipantList)
                                {
                                    outgoingMessage.WriteAllProperties(participant);
                                }

                                // At this point, the packet will contain:
                                //    - byte            : packet type (PacketTypes.NOTIFY_CLIENTS_OF_NEW_PARTICIPANT).
                                //    - int n           : number of participants now present.
                                //    - participant * n : participant object containing their name and connection ID.
                                SendMessageToSingleParticipant(outgoingMessage, incomingMessage.SenderConnection);
                                Console.WriteLine("Server: approved new connection [" + newParticipantName + "]");

                                SendParticipantConnectedMessageToAll(newParticipantName);
                            }
                            break;
                        }
                        case NetIncomingMessageType.StatusChanged:
                        {
                            switch (incomingMessage.SenderConnection.Status)
                            {
                                // Disconnected state will be reported for two known conditions:
                                //   1. If participant deliberately disconnects - handled by Data message type with PacketTypes.CLIENT_DISCONNECTING enum.
                                //   2. If participant times out - the participant's client won't send any info about it disconnecting,
                                //      so server will deem that connection lost. Depends upon server config's ConnectionTimeout and PingInterval values
                                //      as to how long it will take for a client's connection to timeout.
                                case NetConnectionStatus.Disconnected:
                                {
                                    Console.WriteLine("Server: state change: client disconnected");
                                    RemoveParticipantFromList(incomingMessage.SenderEndPoint.ToString(), DisconnectReason.Timeout);
                                    break;
                                }
                                default:
                                {
                                    Console.WriteLine("Server: state change [" + incomingMessage.SenderConnection.Status.ToString() + "]");
                                    break;
                                }
                            }
                            break;
                        }
                        // Data type is for messages manually sent from the client (i.e. participant's messages).
                        case NetIncomingMessageType.Data:
                        {
                            // First byte indicates message type. Read it before logic switching as once we read it,
                            // it's removed from the incoming packet.
                            byte dataMessageType = incomingMessage.ReadByte();
                            if (dataMessageType == (byte)PacketTypes.NOTIFY_CLIENTS_OF_NEW_MESSAGE)
                            {
                                // Read message into class instance via properties, then iterate over each participant to receive it.
                                ParticipantMessage messageToBeRelayed = new ParticipantMessage();
                                incomingMessage.ReadAllProperties(messageToBeRelayed);

                                Console.WriteLine("Server: relaying message from [" + messageToBeRelayed.SenderName + "]: ["
                                                                                    + messageToBeRelayed.Message + "]");

                                NetOutgoingMessage outgoingMessage = m_server.CreateMessage();
                                outgoingMessage.Write((byte)PacketTypes.NOTIFY_CLIENTS_OF_NEW_MESSAGE);
                                outgoingMessage.WriteAllProperties(messageToBeRelayed);
                                SendMessageToAllParticipants(outgoingMessage);
                            }
                            else if (dataMessageType == (byte)PacketTypes.CLIENT_DISCONNECTING)
                            {
                                Console.WriteLine("Server: client decided to disconnect");
                                RemoveParticipantFromList(incomingMessage.SenderEndPoint.ToString(), DisconnectReason.UserDisconnect);
                            }
                            else
                            {
                                Console.WriteLine("Server: unknown data message type received [" + dataMessageType + "]");
                            }

                            break;
                        }
                        default:
                        {
                            Console.WriteLine("Server: unknown message type received: [" + incomingMessage.MessageType + "]");
                            break;
                        }
                    }
                }

                SendParticipantListIfChanged();

                Thread.Sleep(SharedConstants.MAIN_LOOP_DELAY_MS);
            }

            m_server.Shutdown("Server stopping");
        }

        /// <summary>
        /// Removes participant from list based on endpoint, as these are uniquely identifiable, unlike names.
        /// </summary>
        /// <param name="_participantEndPoint">Unique IP:port combination to be removed.</param>
        /// <param name="_disconnectReason">Reason for disconnecting. Future use is to notify other participants
        ///                                 why they left, such as timeout, or simply closed the application.</param>
        private void RemoveParticipantFromList(string _participantEndPoint, DisconnectReason _disconnectReason)
        {
            for (int index = 0; index < m_ParticipantList.Count; index++)
            {
                if (_participantEndPoint == m_ParticipantList[index].EndPoint)
                {
                    SendParticipantDisconnectedMessageToAll(m_ParticipantList[index].Name, _disconnectReason);
                    Console.WriteLine("Server: removing " + m_ParticipantList[index].Name
                                      + " from participant list, disconnect reason [" + _disconnectReason.ToString() + "]");
                    m_ParticipantList.RemoveAt(index);
                    break;
                }
            }
        }


        /// <summary>
        /// Builds and sends a message to all connected participants to inform them that a participant has joined.
        /// </summary>
        /// <param name="_conParticipantName">Name of paricipant who connected.</param>
        private void SendParticipantConnectedMessageToAll(string _conParticipantName)
        {
            ParticipantMessage connectMessage = new ParticipantMessage(_conParticipantName, "connected");
            NetOutgoingMessage outgoingMessage = m_server.CreateMessage();
            outgoingMessage.Write((byte)PacketTypes.NOTIFY_CLIENTS_OF_NEW_MESSAGE);
            outgoingMessage.WriteAllProperties(connectMessage);
            SendMessageToAllParticipants(outgoingMessage);
        }


        /// <summary>
        /// Builds and sends a message to all connected participants to inform them that a participant has left,
        /// and the reason why.
        /// </summary>
        /// <param name="_dcParticipantName">Name of paricipant who disconnected.</param>
        /// <param name="_disconnectReason">Reason for disconnecting.</param>
        private void SendParticipantDisconnectedMessageToAll(string _dcParticipantName, DisconnectReason _disconnectReason)
        {
            ParticipantMessage disconnectMessage = new ParticipantMessage(_dcParticipantName, "disconnected (" + _disconnectReason.ToString() + ")");
            NetOutgoingMessage outgoingMessage = m_server.CreateMessage();
            outgoingMessage.Write((byte)PacketTypes.NOTIFY_CLIENTS_OF_NEW_MESSAGE);
            outgoingMessage.WriteAllProperties(disconnectMessage);
            SendMessageToAllParticipants(outgoingMessage);
        }


        private void SendParticipantListIfChanged()
        {
            if (m_lastConnectedParticipantCount != m_server.ConnectionsCount)
            {
                m_lastConnectedParticipantCount = m_server.ConnectionsCount;

                NetOutgoingMessage outgoingMessage = m_server.CreateMessage();
                outgoingMessage.Write((byte)PacketTypes.NOTIFY_CLIENTS_OF_UPDATED_PARTICIPANT_LIST);
                outgoingMessage.Write(m_ParticipantList.Count);

                foreach (Participant participant in m_ParticipantList)
                {
                    outgoingMessage.WriteAllProperties(participant);
                }

                SendMessageToAllParticipants(outgoingMessage);
            }
        }

        /// <summary>
        /// Sends the passed in outgoing message message to a single participant's endpoint.
        /// </summary>
        /// <param name="_outgoingMessage">Built message to be sent out.</param>
        /// <param name="_endPoint">Particpant end point to send message to e.g. sourced from incoming message sender connection.</param>
        private void SendMessageToSingleParticipant(NetOutgoingMessage _outgoingMessage, NetConnection _endPoint)
        {
            m_server.SendMessage(_outgoingMessage, _endPoint, SharedConstants.DELIVERY_METHOD);
        }

        /// <summary>
        /// Sends the passed in outgoing message message all currently connected participants.
        /// </summary>
        /// <param name="_outgoingMessage">Built message to be sent out.</param>
        private void SendMessageToAllParticipants(NetOutgoingMessage _outgoingMessage)
        {
            if (m_server.ConnectionsCount > 0)
            {
                m_server.SendMessage(_outgoingMessage, m_server.Connections, SharedConstants.DELIVERY_METHOD, ServerConstants.SEQUENCE_CHANNEL);
            }
        }

        public void Stop()
        {
            m_keepRunning = false;
            m_serverThread.Join();
        }
    }
}
