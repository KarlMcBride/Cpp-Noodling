﻿using Lidgren.Network;
using System;
using System.Collections.Generic;
using System.Threading;

namespace ChatNetworking
{
    public class Client
    {
        public event EventHandler<ParticipantMessageEventArgs>          NewMessageReceived_Event;
        public event EventHandler<ConnectedParticipantListEventArgs>    NewConnectedParticipantListReceived_Event;

        private string              m_name;
        private NetClient           m_client;
        private Thread              m_clientThread;
        private List<Participant>   m_ParticipantList;

        private string              m_serverIp;
        private bool                m_connected;
        private bool                m_keepRunning;

        private List<string>        m_outboundMessageList;


        public Client(string _name, string _serverIp)
        {
            m_name = _name;
            m_outboundMessageList = new List<string>();
            m_serverIp = _serverIp;
            m_connected = false;
            m_clientThread = new Thread(Init);
            m_clientThread.Start();
        }


        public void Init()
        {
            m_ParticipantList = new List<Participant>();

            NetPeerConfiguration config = new NetPeerConfiguration(Constants.APP_IDENTIFIER);

            m_client = new NetClient(config);
            m_client.Start();

            NetOutgoingMessage outgoingMessage = m_client.CreateMessage();
            outgoingMessage.Write((byte)PacketTypes.LOGIN);
            outgoingMessage.Write(m_name);
            m_client.Connect(m_serverIp, Constants.HOST_PORT, outgoingMessage);

            ReadLoop();
        }


        private void ReadLoop()
        {
            NetIncomingMessage incomingMessage;

            m_keepRunning = true;
            while (m_keepRunning)
            {
                if ((incomingMessage = m_client.ReadMessage()) != null)
                {
                    switch (incomingMessage.MessageType)
                    {
                        // All manually sent messages are type of "Data".
                        case NetIncomingMessageType.Data:
                        {
                            // First byte indicates message type. Read it before logic switching as once we read it,
                            // it's removed from the incoming packet.
                            byte messageType = incomingMessage.ReadByte();
                            if (messageType == (byte)PacketTypes.NOTIFY_CLIENTS_OF_NEW_PARTICIPANT)
                            {
                                // Packet will contain
                                //    - byte            : packet type (PacketTypes.NOTIFY_CLIENTS_OF_NEW_PARTICIPANT).
                                //    - int n           : number of participants now present.
                                //    - participant * n : participant object containing their name and connection ID.

                                m_ParticipantList.Clear();
                                int participantCount = incomingMessage.ReadInt32();

                                // Iterate over all participants
                                for (int index = 0; index < participantCount; index++)
                                {
                                    Participant newParticipant = new Participant();

                                    // Read all properties from the packet and add them into the newParticipant instance.
                                    incomingMessage.ReadAllProperties(newParticipant);
                                    m_ParticipantList.Add(newParticipant);
                                }

                                ConnectedParticipantListEventArgs newParticipantListEventArgs = new ConnectedParticipantListEventArgs(m_ParticipantList);
                                NewConnectedParticipantListReceived_Event?.Invoke(this, newParticipantListEventArgs);

                                Console.WriteLine("Client connected");
                            }
                            else if (messageType == (byte)PacketTypes.NOTIFY_CLIENTS_OF_NEW_MESSAGE)
                            {
                                ParticipantMessage newMessage = new ParticipantMessage();
                                incomingMessage.ReadAllProperties(newMessage);
                                Console.WriteLine(m_name + " got new message from [" + newMessage.Sender + "]: [" + newMessage.Message + "]");

                                ParticipantMessageEventArgs messageEventArgs = new ParticipantMessageEventArgs(newMessage);

                                if (NewMessageReceived_Event != null)
                                {
                                    NewMessageReceived_Event(this, messageEventArgs);
                                }
                                else
                                {
                                    Console.WriteLine("Client: not invoking ClientNewMessageReceived_Event, nothing attached");
                                }
                            }
                            break;
                        }
                        case NetIncomingMessageType.StatusChanged:
                        {
                            Console.WriteLine("Client: status changed: [" + incomingMessage.SenderConnection.Status + "]");
                            m_connected = true;
                            break;
                        }
                        case NetIncomingMessageType.WarningMessage:
                        {
                            string message = incomingMessage.ReadString();
                            Console.WriteLine("Clinet: warning: [" + message + "]");
                            break;
                        }
                        default:
                        {
                            Console.WriteLine("Client: unknown message type received: [" + incomingMessage.MessageType + "]");
                            break;
                        }
                    }
                }

                if (m_connected)
                {
                    SendNewMessages();
                }

                Thread.Sleep(Constants.MAIN_LOOP_DELAY_MS);
            }
        }

        private void SendNewMessages()
        {
            foreach (string message in m_outboundMessageList)
            {
                NetOutgoingMessage outboundMessage = m_client.CreateMessage();
                ParticipantMessage messageContents = new ParticipantMessage(m_name, message);

                outboundMessage.Write((byte)PacketTypes.NOTIFY_CLIENTS_OF_NEW_MESSAGE);
                outboundMessage.WriteAllProperties(messageContents);
                m_client.SendMessage(outboundMessage, NetDeliveryMethod.ReliableOrdered);
            }
            m_outboundMessageList.Clear();
        }

        /// <summary>
        /// Interface to allow UI to add a message to be sent as part of the networking thread.
        /// </summary>
        /// <param name="_message">New message to be sent.</param>
        public void QueueMessage(string _message)
        {
            m_outboundMessageList.Add(_message);
        }

        public void Stop()
        {
            m_keepRunning = false;
            m_clientThread.Join();

            SendDisconnectMessage();
            m_client.Disconnect(m_name + " stopping");
        }

        private void SendDisconnectMessage()
        {
            NetOutgoingMessage outboundMessage = m_client.CreateMessage();
            outboundMessage.Write((byte)PacketTypes.PARTICIPANT_DISCONNECTING);
            ParticipantMessage messageContents = new ParticipantMessage(m_name, "DISCONNECTING");

            outboundMessage.WriteAllProperties(messageContents);
            m_client.SendMessage(outboundMessage, NetDeliveryMethod.ReliableOrdered);
        }
    }
}