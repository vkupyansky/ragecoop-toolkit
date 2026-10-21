using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RageCoop.Client.Scripting;

namespace RageCoopAdmin.Client;

// API has registration but no removal, and its dispatcher walks a mutable List.
// Keep only this resource's subscriptions and cancel captured callbacks on stop.
internal sealed class ResourceEventSubscriptions : IDisposable
{
    private readonly object _gate = new object();
    private readonly List<Subscription> _subscriptions = new List<Subscription>();
    private bool _disposed;
    private bool _dispatcherAcquired;

    internal void Register(int hash, Action<CustomEventReceivedArgs> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ResourceEventSubscriptions));
            if (!_dispatcherAcquired)
            {
                Dispatcher.Acquire();
                _dispatcherAcquired = true;
            }
            var subscription = new Subscription(this, hash, handler);
            _subscriptions.Add(subscription);
            API.RegisterCustomEventHandler(hash, subscription.Invoke);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (Subscription subscription in _subscriptions) subscription.Handler = null;
            Dictionary<int, List<Action<CustomEventReceivedArgs>>> handlers = Dispatcher.Handlers;
            lock (handlers)
            {
                foreach (Subscription subscription in _subscriptions)
                {
                    if (!handlers.TryGetValue(subscription.Hash, out List<Action<CustomEventReceivedArgs>>? list)) continue;
                    list.Remove(subscription.Invoke);
                    if (list.Count == 0) handlers.Remove(subscription.Hash);
                }
            }
            _subscriptions.Clear();
            if (_dispatcherAcquired)
            {
                Dispatcher.Release();
                _dispatcherAcquired = false;
            }
        }
    }

    private sealed class Subscription
    {
        private readonly ResourceEventSubscriptions _owner;
        internal readonly int Hash;
        internal Action<CustomEventReceivedArgs>? Handler;
        internal Subscription(ResourceEventSubscriptions owner, int hash, Action<CustomEventReceivedArgs> handler)
        { _owner = owner; Hash = hash; Handler = handler; }
        internal void Invoke(CustomEventReceivedArgs args)
        {
            // Stop waits for an executing callback; a previously captured
            // dispatch snapshot cannot call this instance after Stop returns.
            lock (_owner._gate) { Handler?.Invoke(args); }
        }
    }

    private static class Dispatcher
    {
        private const string PatchId = "friendslegacy.ragecoop.resourceevents";
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly object Gate = new object();
        private static readonly FieldInfo Table = typeof(API).GetField("CustomEventHandlers", Static)!;
        private static readonly Type Packet = typeof(RageCoop.Core.Logger).Assembly.GetType("RageCoop.Core.Packets+CustomEvent", true)!;
        private static readonly PropertyInfo Hash = Packet.GetProperty("Hash")!;
        private static readonly PropertyInfo Args = Packet.GetProperty("Args")!;
        private static Harmony? _harmony;
        private static MethodInfo? _target;
        private static int _owners;

        internal static Dictionary<int, List<Action<CustomEventReceivedArgs>>> Handlers =>
            (Dictionary<int, List<Action<CustomEventReceivedArgs>>>)Table.GetValue(null)!;

        internal static void Acquire()
        {
            lock (Gate)
            {
                if (_owners != 0) { _owners++; return; }
                MethodInfo? target = typeof(API.Events).GetMethod("InvokeCustomEventReceived", Static, null, new[] { Packet }, null);
                if (typeof(API).Assembly.GetName().Version != new Version(1, 5, 12, 15) ||
                    Table?.FieldType != typeof(Dictionary<int, List<Action<CustomEventReceivedArgs>>>) ||
                    target?.ReturnType != typeof(void) || Hash.PropertyType != typeof(int) || Args.PropertyType != typeof(object[]))
                    throw new InvalidOperationException("Unsupported RageCoop custom-event dispatcher contract.");
                List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(target);
                if (original.Count(code => code.operand is MethodInfo method && method.Name == "TryGetValue" && method.DeclaringType == Table.FieldType) != 1 ||
                    original.Count(code => code.operand is MethodInfo method && method.Name == "ForEach" && method.DeclaringType == typeof(List<Action<CustomEventReceivedArgs>>)) != 1)
                    throw new InvalidOperationException("Unsupported RageCoop custom-event dispatcher body.");
                var installing = new Harmony(PatchId);
                try { installing.Patch(target, prefix: new HarmonyMethod(typeof(Dispatcher), nameof(BeforeDispatch))); }
                catch { installing.Unpatch(target, HarmonyPatchType.Prefix, PatchId); throw; }
                _target = target; _harmony = installing; _owners = 1;
            }
        }

        internal static void Release()
        {
            lock (Gate)
            {
                if (--_owners != 0) return;
                _harmony!.Unpatch(_target!, HarmonyPatchType.Prefix, PatchId);
                _harmony = null; _target = null;
            }
        }

        private static bool BeforeDispatch(object __0)
        {
            var args = new CustomEventReceivedArgs { Hash = (int)Hash.GetValue(__0)!, Args = (object[])Args.GetValue(__0)! };
            Dictionary<int, List<Action<CustomEventReceivedArgs>>> handlers = Handlers;
            Action<CustomEventReceivedArgs>[] snapshot;
            lock (handlers)
                snapshot = handlers.TryGetValue(args.Hash, out List<Action<CustomEventReceivedArgs>>? list) ? list.ToArray() : Array.Empty<Action<CustomEventReceivedArgs>>();
            foreach (Action<CustomEventReceivedArgs> handler in snapshot) handler(args);
            return false;
        }
    }
}
