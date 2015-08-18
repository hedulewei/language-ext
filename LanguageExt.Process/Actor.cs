﻿using Microsoft.FSharp.Control;
using Microsoft.FSharp.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using LanguageExt.Trans;
using static LanguageExt.Prelude;
using static LanguageExt.Process;
using static LanguageExt.List;
using System.Reactive.Subjects;
using Newtonsoft.Json;

namespace LanguageExt
{
    /// <summary>
    /// Internal class that represents the state of a single process.
    /// </summary>
    /// <typeparam name="S">State</typeparam>
    /// <typeparam name="T">Message type</typeparam>
    internal class Actor<S, T> : IActor
    {
        readonly Func<S, T, S> actorFn;
        readonly Func<IActor, S> setupFn;
        S state;
        Map<string, ProcessId> children = Map.create<string, ProcessId>();
        Map<string, IDisposable> subs = Map.create<string, IDisposable>();
        Option<ICluster> cluster;
        Subject<object> publishSubject = new Subject<object>();
        Subject<object> stateSubject = new Subject<object>();
        ProcessFlags flags;
        int roundRobinIndex = -1;

        internal Actor(Option<ICluster> cluster, ProcessId parent, ProcessName name, Func<S, T, S> actor, Func<IActor, S> setup, ProcessFlags flags)
        {
            if (setup == null) throw new ArgumentNullException(nameof(setup));
            if (actor == null) throw new ArgumentNullException(nameof(actor));

            this.cluster = cluster;
            this.flags = flags;
            actorFn = actor;
            setupFn = setup;
            Parent = parent;
            Name = name;
            Id = parent.Child(name);

            SetupClusterStatePersist(cluster, flags);
        }

        public Actor(Option<ICluster> cluster, ProcessId parent, ProcessName name, Func<S, T, S> actor, Func<S> setup, ProcessFlags flags)
            :
            this(cluster, parent, name, actor, _ => setup(), flags)
        { }

        public Actor(Option<ICluster> cluster, ProcessId parent, ProcessName name, Func<T, Unit> actor, ProcessFlags flags)
            :
            this(cluster, parent, name, (s, t) => { actor(t); return default(S); }, () => default(S), flags)
        { }

        public Actor(Option<ICluster> cluster, ProcessId parent, ProcessName name, Action<T> actor, ProcessFlags flags)
            :
            this(cluster, parent, name, (s, t) => { actor(t); return default(S); }, () => default(S), flags)
        { }

        /// <summary>
        /// Start up - creates the initial state
        /// </summary>
        /// <returns></returns>
        public Unit Startup()
        {
            ActorContext.WithContext(
                this,
                ProcessId.NoSender,
                null,
                null,
                () => InitState()
            );
            stateSubject.OnNext(state);
            return unit;
        }

        public Unit AddSubscription(ProcessId pid, IDisposable sub)
        {
            RemoveSubscription(pid);
            subs = subs.Add(pid.Path, sub);
            return unit;
        }

        public Unit RemoveSubscription(ProcessId pid)
        {
            subs.Find(pid.Path).IfSome(x => x.Dispose());
            subs = subs.Remove(pid.Path);
            return unit;
        }

        private Unit RemoveAllSubscriptions()
        {
            subs.Iter(x => x.Dispose());
            subs = Map.empty<string, IDisposable>();
            return unit;
        }

        public int GetNextRoundRobinIndex() =>
            Children.Count == 0
                ? 0 
                : roundRobinIndex = (roundRobinIndex + 1) % Children.Count;

        public ProcessFlags Flags => 
            flags;

        private string StateKey => 
            Id.Path + "-state";

        private void SetupClusterStatePersist(Option<ICluster> cluster, ProcessFlags flags)
        {
            cluster.IfSome(c =>
            {
                if ((flags & ProcessFlags.PersistState) == ProcessFlags.PersistState)
                {
                    try
                    {
                        stateSubject.Subscribe(state => c.SetValue(StateKey, state));
                    }
                    catch (Exception e)
                    {
                        logSysErr(e);
                    }
                }
            });
        }

        private void InitState()
        {
            if (cluster.IsSome && ((flags & ProcessFlags.PersistState) == ProcessFlags.PersistState))
            {
                try
                {
                    logInfo("Restoring state: " + StateKey);

                    state = cluster.LiftUnsafe().Exists(StateKey)
                        ? cluster.LiftUnsafe().GetValue<S>(StateKey)
                        : setupFn(this);
                }
                catch (Exception e)
                {
                    state = setupFn(this);
                    logSysErr(e);
                }
            }
            else
            {
                state = setupFn(this);
            }
        }

        public IObservable<object> PublishStream => publishSubject;
        public IObservable<object> StateStream => stateSubject;

        /// <summary>
        /// Publish to the PublishStream
        /// </summary>
        public Unit Publish(object message)
        {
            publishSubject.OnNext(message);
            return unit;
        }

        /// <summary>
        /// Process path
        /// </summary>
        public ProcessId Id { get; }

        /// <summary>
        /// Process name
        /// </summary>
        public ProcessName Name { get; }

        /// <summary>
        /// Parent process
        /// </summary>
        public ProcessId Parent { get; }

        /// <summary>
        /// Child processes
        /// </summary>
        public Map<string, ProcessId> Children =>
            children;

        /// <summary>
        /// Clears the state (keeps the mailbox items)
        /// </summary>
        public Unit Restart()
        {
            RemoveAllSubscriptions();
            DisposeState();
            InitState();
            stateSubject.OnNext(state);
            tellChildren(SystemMessage.Restart);
            return unit;
        }

        /// <summary>
        /// Disowns a child process
        /// </summary>
        public Unit UnlinkChild(ProcessId pid)
        {
            children = children.Remove(pid.GetName().Value);
            return unit;
        }

        /// <summary>
        /// Gains a child process
        /// </summary>
        public Unit LinkChild(ProcessId pid)
        {
            children = children.AddOrUpdate(pid.GetName().Value, pid);
            return unit;
        }

        /// <summary>
        /// Shutdown everything from this node down
        /// </summary>
        public Unit Shutdown()
        {
            RemoveAllSubscriptions();
            publishSubject.OnCompleted();
            stateSubject.OnCompleted();
            DisposeState();
            return unit;
        }

        public Option<T> PreProcessMessageContent(object message)
        {
            if (message == null)
            {
                tell(ActorContext.DeadLetters, DeadLetter.create(Sender, Self, "Message is null for tell (expected " + typeof(T) + ")", message));
                return None;
            }

            if (typeof(T) != typeof(string) && message is string)
            {
                try
                {
                    // This allows for messages to arrive from JS and be dealt with at the endpoint 
                    // (where the type is known) rather than the gateway (where it isn't)
                    return Some(JsonConvert.DeserializeObject<T>((string)message));
                }
                catch
                {
                    tell(ActorContext.DeadLetters, DeadLetter.create(Sender, Self, "Invalid message type for tell (expected " + typeof(T) + ")", message));
                    return None;
                }
            }

            if (!typeof(T).IsAssignableFrom(message.GetType()))
            {
                tell(ActorContext.DeadLetters, DeadLetter.create(Sender, Self, "Invalid message type for tell (expected " + typeof(T) + ")", message));
                return None;
            }

            return Some((T)message);
        }

        public Unit ProcessAsk(ActorRequest request)
        {
            var savedMsg = ActorContext.CurrentMsg;
            var savedReq = ActorContext.CurrentRequest;

            try
            {
                ActorContext.CurrentRequest = request;
                ActorContext.ProcessFlags = flags;
                ActorContext.CurrentMsg = request.Message;

                if (typeof(T) != typeof(string) && request.Message is string)
                {
                    state = PreProcessMessageContent(request.Message).Match(
                                Some: tmsg =>
                                {
                                    var s = actorFn(state, tmsg);
                                    stateSubject.OnNext(state);
                                    return s;
                                },
                                None: ()   => state
                            );
                }
                else if (request.Message is T)
                {
                    T msg = (T)request.Message;
                    state = actorFn(state, msg);
                    stateSubject.OnNext(state);
                }
                else
                {
                    logErr("ProcessAsk request.Message is not T " + request.Message);
                }
            }
            catch (SystemKillActorException)
            {
                kill(Id);
            }
            catch (Exception e)
            {
                // TODO: Add extra strategy behaviours here
                Restart();
                tell(ActorContext.Errors, e);
                tell(ActorContext.DeadLetters,
                     DeadLetter.create(request.ReplyTo, request.To, e, "Process error (ask): ", request.Message));
            }
            finally
            {
                ActorContext.CurrentMsg = savedMsg;
                ActorContext.CurrentRequest = savedReq;
            }
            return unit;
        }

        /// <summary>
        /// Process an inbox message
        /// </summary>
        /// <param name="message"></param>
        /// <returns></returns>
        public Unit ProcessMessage(object message)
        {
            var savedReq = ActorContext.CurrentRequest;
            var savedFlags = ActorContext.ProcessFlags;
            var savedMsg = ActorContext.CurrentMsg;

            try
            {
                ActorContext.CurrentRequest = null;
                ActorContext.ProcessFlags = flags;
                ActorContext.CurrentMsg = message;

                if (typeof(T) != typeof(string) && message is string)
                {
                    state = PreProcessMessageContent(message).Match(
                                Some: tmsg =>
                                {
                                    var s = actorFn(state, tmsg);
                                    stateSubject.OnNext(state);
                                    return s;
                                },
                                None: () => state
                            );
                }
                else
                {
                    state = actorFn(state, (T)message);
                    stateSubject.OnNext(state);
                }
            }
            catch (SystemKillActorException)
            {
                logInfo("Process message - system kill " + Id);
                kill(Id);
            }
            catch (Exception e)
            {
                // TODO: Add extra strategy behaviours here
                Restart();
                tell(ActorContext.Errors, e);
                tell(ActorContext.DeadLetters, DeadLetter.create(Sender, Self, e, "Process error (tell): ", message));
            }
            finally
            {
                ActorContext.CurrentRequest = savedReq;
                ActorContext.ProcessFlags = savedFlags;
                ActorContext.CurrentMsg = savedMsg;
            }
            return unit;
        }

        public void Dispose()
        {
            RemoveAllSubscriptions();
            DisposeState();
        }

        private void DisposeState()
        {
            if (state is IDisposable)
            {
                var s = state as IDisposable;
                if (s != null)
                {
                    s.Dispose();
                    state = default(S);
                }
            }
        }
    }
}
