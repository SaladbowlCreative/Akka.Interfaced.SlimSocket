﻿using System;
using System.Threading;
using System.Net;
using Common.Logging;
using Lidgren.Network;

namespace Akka.Interfaced.SlimSocket.Client
{
    public class ChannelFactory
    {
        public ChannelType Type { get; set; }
        public IPEndPoint ConnectEndPoint { get; set; }
        public string ConnectToken { get; set; }
        public Func<ILog> CreateChannelLogger { get; set; }
        public ISlimTaskFactory TaskFactory { get; set; }
        public Action<SendOrPostCallback> ObserverEventPoster { get; set; }
        public IPacketSerializer PacketSerializer { get; set; }
        public object UdpConfig { get; set; }

        public ChannelFactory()
        {
            TaskFactory = new SlimTaskFactory();
            UdpConfig = new NetPeerConfiguration("SlimSocket");
        }

        public IChannel Create()
        {
            switch (Type)
            {
                case ChannelType.Tcp:
                    var tcpChannel = new TcpChannel(CreateChannelLogger(), ConnectEndPoint, ConnectToken, PacketSerializer);
                    InitializeChannel(tcpChannel);
                    return tcpChannel;

                case ChannelType.Udp:
                    var udpChannel = new UdpChannel(CreateChannelLogger(), ConnectEndPoint, ConnectToken, PacketSerializer, (NetPeerConfiguration)UdpConfig);
                    InitializeChannel(udpChannel);
                    return udpChannel;

                default:
                    throw new InvalidOperationException("Unknown TransportType");
            }
        }

        private void InitializeChannel(ChannelBase channel)
        {
            channel.TaskFactory = TaskFactory;
            channel.ObserverEventPoster = ObserverEventPoster;
        }
    }
}
