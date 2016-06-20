﻿using System;
using System.IO;
using System.Linq;
using Akka.Actor;
using Akka.Event;
using Common.Logging;
using Lidgren.Network;

namespace Akka.Interfaced.SlimSocket.Server
{
    public class UdpChannel : ActorBoundChannel
    {
        private GatewayInitiator _initiator;
        private ILog _logger;
        private IActorRef _self;
        private EventStream _eventStream;
        private NetConnection _connection;
        private IPacketSerializer _packetSerializer;

        internal class CloseMessage
        {
        }

        internal class ReceiveMessage
        {
            public Packet Packet;
        }

        public UdpChannel(GatewayInitiator initiator, object connection)
        {
            // open by client connection.
            var netConnection = (NetConnection)connection;
            _initiator = initiator;
            _logger = _initiator.CreateChannelLogger(netConnection.RemoteEndPoint, connection);
            _connection = netConnection;
            _packetSerializer = initiator.PacketSerializer;
        }

        public UdpChannel(GatewayInitiator initiator, object connection, ActorBoundGatewayMessage.Open message)
        {
            // open by registerd token.
            var netConnection = (NetConnection)connection;
            _initiator = initiator;
            _logger = _initiator.CreateChannelLogger(netConnection.RemoteEndPoint, connection);
            _connection = netConnection;
            _packetSerializer = initiator.PacketSerializer;

            BindActor(message.Actor, message.Types.Select(t => new BoundType(t)));
        }

        protected override void PreStart()
        {
            base.PreStart();

            _self = Self;
            _eventStream = Context.System.EventStream;

            // create initial actors and bind them

            if (_initiator.CreateInitialActors != null)
            {
                var actors = _initiator.CreateInitialActors(Context, _connection);
                if (actors != null)
                {
                    foreach (var actor in actors)
                    {
                        BindActor(actor.Item1, actor.Item2.Select(t => new BoundType(t)));
                    }
                }
            }
        }

        protected override void PostStop()
        {
            _connection.Disconnect("Server Stop");

            base.PostStop();
        }

        private void SendPacket(Packet packet)
        {
            var msg = _connection.Peer.CreateMessage();
            var workStream = new MemoryStream();
            _packetSerializer.Serialize(workStream, packet);
            msg.Write(workStream.GetBuffer(), 0, (int)workStream.Length);
            _connection.SendMessage(msg, NetDeliveryMethod.ReliableOrdered, 0);
        }

        protected override void OnNotificationMessage(NotificationMessage message)
        {
            SendPacket(new Packet
            {
                Type = PacketType.Notification,
                ActorId = message.ObserverId,
                RequestId = message.NotificationId,
                Message = message.InvokePayload,
            });
        }

        protected override void OnResponseMessage(ResponseMessage message)
        {
            var actorId = GetBoundActorId(Sender);
            if (actorId != 0)
            {
                SendPacket(new Packet
                {
                    Type = PacketType.Response,
                    ActorId = actorId,
                    RequestId = message.RequestId,
                    Message = message.ReturnPayload,
                    Exception = message.Exception
                });
            }
            else
            {
                _logger.WarnFormat("Not bound actorId owned by ReponseMessage. (ActorId={0})", actorId);
            }
        }

        protected override void OnReceive(object message)
        {
            var close = message as CloseMessage;
            if (close != null)
            {
                OnConnectionClose();
                return;
            }

            var receive = message as ReceiveMessage;
            if (receive != null)
            {
                OnConnectionReceive(receive.Packet);
                return;
            }

            base.OnReceive(message);
        }

        protected void OnConnectionClose()
        {
            _self.Tell(PoisonPill.Instance);
        }

        protected void OnConnectionReceive(Packet packet)
        {
            // The thread that call this function is different from actor context thread.
            // To deal with this contention lock protection is required.

            var p = packet as Packet;
            if (p == null)
            {
                _eventStream.Publish(new Warning(
                    _self.Path.ToString(), GetType(),
                    $"Receives null packet from {_connection?.RemoteEndPoint}"));
                return;
            }

            var msg = p.Message as IInterfacedPayload;
            if (msg == null)
            {
                _eventStream.Publish(new Warning(
                    _self.Path.ToString(), GetType(),
                    $"Receives a bad packet without a message from {_connection?.RemoteEndPoint}"));
                return;
            }

            var actor = GetBoundActor(p.ActorId);
            if (actor == null)
            {
                if (p.RequestId != 0)
                {
                    SendPacket(new Packet
                    {
                        Type = PacketType.Response,
                        ActorId = p.ActorId,
                        RequestId = p.RequestId,
                        Message = null,
                        Exception = new RequestTargetException()
                    });
                }
                return;
            }

            var boundType = actor.FindBoundType(msg.GetInterfaceType());
            if (boundType == null)
            {
                if (p.RequestId != 0)
                {
                    SendPacket(new Packet
                    {
                        Type = PacketType.Response,
                        ActorId = p.ActorId,
                        RequestId = p.RequestId,
                        Message = null,
                        Exception = new RequestHandlerNotFoundException()
                    });
                }
                return;
            }

            if (boundType.IsTagOverridable)
            {
                var tagOverridable = (IPayloadTagOverridable)p.Message;
                tagOverridable.SetTag(boundType.TagValue);
            }

            var observerUpdatable = p.Message as IPayloadObserverUpdatable;
            if (observerUpdatable != null)
            {
                observerUpdatable.Update(o => ((InterfacedObserver)o).Channel = new ActorNotificationChannel(_self));
            }

            actor.Actor.Tell(new RequestMessage
            {
                RequestId = p.RequestId,
                InvokePayload = (IInterfacedPayload)p.Message
            }, _self);
        }
    }
}
