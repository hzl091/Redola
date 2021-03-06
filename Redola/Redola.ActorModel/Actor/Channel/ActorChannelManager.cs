﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Logrila.Logging;
using Redola.ActorModel.Extensions;

namespace Redola.ActorModel
{
    public class ActorChannelManager
    {
        private ILog _log = Logger.Get<ActorChannelManager>();
        private ActorIdentity _localActor;
        private IActorDirectory _directory;
        private ActorChannelFactory _factory;

        private class ChannelItem
        {
            public ChannelItem() { }
            public ChannelItem(string channelIdentifier, IActorChannel channel)
            {
                this.ChannelIdentifier = channelIdentifier;
                this.Channel = channel;
            }

            public string ChannelIdentifier { get; set; }
            public IActorChannel Channel { get; set; }

            public string RemoteActorKey { get; set; }
            public ActorIdentity RemoteActor { get; set; }

            public override string ToString()
            {
                return string.Format("{0}#{1}", ChannelIdentifier, RemoteActor);
            }
        }
        private ConcurrentDictionary<string, ChannelItem> _channels
            = new ConcurrentDictionary<string, ChannelItem>(); // ChannelIdentifier -> ChannelItem
        private readonly object _syncLock = new object();

        public ActorChannelManager(IActorDirectory directory, ActorChannelFactory factory)
        {
            if (directory == null)
                throw new ArgumentNullException("directory");
            if (factory == null)
                throw new ArgumentNullException("factory");

            _directory = directory;
            _factory = factory;

            _directory.ActorsChanged += OnDirectoryActorsChanged;
        }

        private void OnDirectoryActorsChanged(object sender, ActorsChangedEventArgs e)
        {
            Task.Factory.StartNew(() =>
            {
                foreach (var usingActorType in _channels.Values.Select(v => v.RemoteActor.Type).Distinct())
                {
                    if (e.Actors.Any(a => a.Type == usingActorType))
                    {
                        BuildActorChannels(usingActorType);
                    }
                }
            }, TaskCreationOptions.PreferFairness);
        }

        public void ActivateLocalActor(ActorIdentity localActor)
        {
            if (localActor == null)
                throw new ArgumentNullException("localActor");
            if (_localActor != null)
                throw new InvalidOperationException("The local actor has already been activated.");

            var channel = _factory.BuildLocalActor(localActor);
            channel.ChannelConnected += OnActorChannelConnected;
            channel.ChannelDisconnected += OnActorChannelDisconnected;
            channel.ChannelDataReceived += OnActorChannelDataReceived;

            try
            {
                channel.Open();

                _localActor = localActor;

                var item = new ChannelItem(channel.Identifier, channel);
                item.RemoteActorKey = _localActor.GetKey();
                item.RemoteActor = _localActor;
                _channels.Add(channel.Identifier, item);

                _log.DebugFormat("Local actor [{0}] is activated on channel [{1}].", _localActor, channel);
            }
            catch
            {
                CloseChannel(channel);
                throw;
            }
        }

        public IActorChannel GetActorChannel(ActorIdentity remoteActor)
        {
            if (remoteActor == null)
                throw new ArgumentNullException("remoteActor");
            return GetActorChannel(remoteActor.Type, remoteActor.Name);
        }

        public IActorChannel GetActorChannel(string actorType, string actorName)
        {
            if (string.IsNullOrEmpty(actorName))
            {
                return GetActorChannel(actorType);
            }

            var actorKey = ActorIdentity.GetKey(actorType, actorName);
            ChannelItem item = null;

            item = _channels.Values.FirstOrDefault(i => i.RemoteActorKey == actorKey);
            if (item != null)
            {
                return item.Channel;
            }

            lock (_syncLock)
            {
                item = _channels.Values.FirstOrDefault(i => i.RemoteActorKey == actorKey);
                if (item != null)
                {
                    return item.Channel;
                }

                BuildActorChannel(actorType, actorName);

                item = _channels.Values.FirstOrDefault(i => i.RemoteActorKey == actorKey);
                if (item != null)
                {
                    return item.Channel;
                }

                throw new ActorNotFoundException(string.Format(
                    "Build actor channel failed, cannot connect remote actor, Type[{0}], Name[{1}].", actorType, actorName));
            }
        }

        public IActorChannel GetActorChannel(string actorType)
        {
            if (string.IsNullOrEmpty(actorType))
                throw new ArgumentNullException("actorType");

            ChannelItem item = null;

            item = _channels.Values.Where(i => i.RemoteActor.Type == actorType).OrderBy(t => Guid.NewGuid()).FirstOrDefault();
            if (item != null)
            {
                return item.Channel;
            }

            lock (_syncLock)
            {
                item = _channels.Values.Where(i => i.RemoteActor.Type == actorType).OrderBy(t => Guid.NewGuid()).FirstOrDefault();
                if (item != null)
                {
                    return item.Channel;
                }

                BuildActorChannels(actorType);

                item = _channels.Values.Where(i => i.RemoteActor.Type == actorType).OrderBy(t => Guid.NewGuid()).FirstOrDefault();
                if (item != null)
                {
                    return item.Channel;
                }

                throw new ActorNotFoundException(string.Format(
                    "Build actor channel failed, cannot connect remote actor, Type[{0}].", actorType));
            }
        }

        private bool ActivateChannel(IActorChannel channel)
        {
            channel.ChannelConnected += OnActorChannelConnected;
            channel.ChannelDisconnected += OnActorChannelDisconnected;
            channel.ChannelDataReceived += OnActorChannelDataReceived;

            ManualResetEventSlim waitingConnected = new ManualResetEventSlim(false);
            object connectedSender = null;
            ActorChannelConnectedEventArgs connectedEvent = null;
            EventHandler<ActorChannelConnectedEventArgs> onConnected =
                (s, e) =>
                {
                    connectedSender = s;
                    connectedEvent = e;
                    waitingConnected.Set();
                };

            channel.ChannelConnected += onConnected;
            channel.Open();

            bool connected = waitingConnected.Wait(TimeSpan.FromSeconds(5));
            channel.ChannelConnected -= onConnected;
            waitingConnected.Dispose();

            if (connected && channel.Active)
            {
                var item = new ChannelItem(((IActorChannel)connectedSender).Identifier, (IActorChannel)connectedSender);
                item.RemoteActorKey = connectedEvent.RemoteActor.GetKey();
                item.RemoteActor = connectedEvent.RemoteActor;
                _channels.TryAdd(channel.Identifier, item);
                return true;
            }
            else
            {
                CloseChannel(channel);
                return false;
            }
        }

        private void BuildActorChannel(string actorType, string actorName)
        {
            lock (_syncLock)
            {
                var channel = _factory.BuildActorChannel(_localActor, actorType, actorName);
                bool activated = ActivateChannel(channel);
                if (activated)
                {
                    _log.DebugFormat("Build actor channel [{0}] to remote actor, ActorType[{1}], ActorName[{2}].",
                        channel.Identifier, actorType, actorName);
                }
            }
        }

        private void BuildActorChannels(string actorType)
        {
            lock (_syncLock)
            {
                var remoteActors = _directory.LookupRemoteActors(actorType);
                if (remoteActors != null && remoteActors.Any())
                {
                    foreach (var remoteActor in remoteActors)
                    {
                        try
                        {
                            GetActorChannel(remoteActor);
                        }
                        catch (Exception ex)
                        {
                            _log.Error(ex.Message, ex);
                        }
                    }
                }
            }
        }

        public IActorChannel GetActorChannelByIdentifier(string identifier)
        {
            if (string.IsNullOrEmpty(identifier))
                throw new ArgumentNullException("identifier");

            ChannelItem item = null;

            item = _channels.Values.FirstOrDefault(i => i.ChannelIdentifier == identifier);
            if (item != null)
            {
                return item.Channel;
            }

            throw new ActorNotFoundException(string.Format(
                "Cannot find channel by identifier, Identifier[{0}].", identifier));
        }

        public IEnumerable<IActorChannel> GetActorChannels(string actorType)
        {
            if (string.IsNullOrEmpty(actorType))
                throw new ArgumentNullException("actorType");
            return _channels.Values.Where(i => i.RemoteActor.Type == actorType).Select(v => v.Channel);
        }

        public IEnumerable<IActorChannel> GetActorChannels()
        {
            return _channels.Values.Where(v => !v.RemoteActor.Equals(_localActor)).Select(v => v.Channel).Where(f => f != null);
        }

        public void CloseAllChannels()
        {
            foreach (var item in _channels.Values)
            {
                CloseChannel(item.Channel);
                _channels.Remove(item.ChannelIdentifier);
            }
        }

        private void CloseChannel(IActorChannel channel)
        {
            channel.Close();
            channel.ChannelConnected -= OnActorChannelConnected;
            channel.ChannelDisconnected -= OnActorChannelDisconnected;
            channel.ChannelDataReceived -= OnActorChannelDataReceived;
        }

        private void OnActorChannelConnected(object sender, ActorChannelConnectedEventArgs e)
        {
            var item = _channels.Get(e.ChannelIdentifier);
            if (item != null)
            {
                if (item.RemoteActorKey != e.RemoteActor.GetKey())
                {
                    _channels.Remove(e.ChannelIdentifier);
                    CloseChannel(item.Channel);

                    if (item.RemoteActor != null)
                    {
                        if (ChannelDisconnected != null)
                        {
                            ChannelDisconnected(sender, new ActorChannelDisconnectedEventArgs(item.ChannelIdentifier, item.RemoteActor));
                        }
                    }
                }
                else
                {
                    return;
                }
            }

            item = new ChannelItem(((IActorChannel)sender).Identifier, (IActorChannel)sender);
            item.RemoteActorKey = e.RemoteActor.GetKey();
            item.RemoteActor = e.RemoteActor;
            _channels.TryAdd(item.ChannelIdentifier, item);

            if (ChannelConnected != null)
            {
                ChannelConnected(sender, e);
            }
        }

        private void OnActorChannelDisconnected(object sender, ActorChannelDisconnectedEventArgs e)
        {
            var item = _channels.Get(e.ChannelIdentifier);
            if (item != null)
            {
                if (item.RemoteActorKey == e.RemoteActor.GetKey())
                {
                    _channels.Remove(e.ChannelIdentifier);
                    CloseChannel(item.Channel);

                    if (item.RemoteActor != null)
                    {
                        if (ChannelDisconnected != null)
                        {
                            ChannelDisconnected(sender, new ActorChannelDisconnectedEventArgs(item.ChannelIdentifier, item.RemoteActor));
                        }
                    }
                }
            }
        }

        private void OnActorChannelDataReceived(object sender, ActorChannelDataReceivedEventArgs e)
        {
            if (ChannelDataReceived != null)
            {
                ChannelDataReceived(sender, e);
            }
        }

        public event EventHandler<ActorChannelConnectedEventArgs> ChannelConnected;
        public event EventHandler<ActorChannelDisconnectedEventArgs> ChannelDisconnected;
        public event EventHandler<ActorChannelDataReceivedEventArgs> ChannelDataReceived;

        internal IEnumerable<ActorIdentity> GetAllActors()
        {
            return _channels.Values.Where(v => !v.RemoteActor.Equals(_localActor)).Select(c => c.RemoteActor).Where(f => f != null);
        }
    }
}
