using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using Lidgren.Network;
using HostServer = RageCoop.Server.Server;

namespace CoopAdmin.Server;

internal static partial class EngineServerGuard
{
    private static readonly ConditionalWeakTable<object, SecurityState> SecurityStates = new();
    [ThreadStatic] private static HandshakeLease? activeHandshake;

    private static void InstallSecurity(Harmony installer, SecurityContract crypto)
    {
        foreach (MethodInfo method in crypto.Methods)
        {
            if (method.Name == "GetPublicKey")
                Patch(installer, method, nameof(BeforeSecurityAccess), finalizer: nameof(AfterSecurityAccess));
            else
                Patch(installer, method, "BeforeSecurity" + method.Name);
        }
    }

    private static SecurityState SecurityStateFor(object security) => SecurityStates.GetValue(security, _ => new SecurityState());
    private static Dictionary<IPEndPoint, Aes> Connections(object security) =>
        (Dictionary<IPEndPoint, Aes>)Current.Crypto.Connections.GetValue(security)!;

    private static void BeforeSecurityAccess(object __instance, out SecurityState __state)
    {
        __state = SecurityStateFor(__instance);
        Monitor.Enter(__state.Gate);
        RequireSecurity(__state);
    }

    private static Exception? AfterSecurityAccess(Exception? __exception, SecurityState? __state)
    {
        if (__state != null) { Monitor.Exit(__state.Gate); }
        return __exception;
    }

    private static void RequireSecurity(SecurityState state)
    {
        if (state.Disposed) { throw new ObjectDisposedException("RageCoop.Server.Security"); }
    }

    private static bool BeforeSecurityHasSecuredConnection(object __instance, IPEndPoint target, ref bool __result)
    {
        lock (SecurityStateFor(__instance).Gate) { __result = Connections(__instance).ContainsKey(target); }
        return false;
    }

    private static bool BeforeSecurityAddConnection(object __instance, IPEndPoint endpoint, byte[] cryptedKey, byte[] cryptedIV)
    {
        SecurityState state = SecurityStateFor(__instance);
        lock (state.Gate)
        {
            RequireSecurity(state);
            var rsa = (RSA)Current.Crypto.Rsa.GetValue(__instance)!;
            Aes? candidate = Aes.Create();
            try
            {
                candidate.Key = rsa.Decrypt(cryptedKey, RSAEncryptionPadding.Pkcs1);
                candidate.IV = rsa.Decrypt(cryptedIV, RSAEncryptionPadding.Pkcs1);
                Dictionary<IPEndPoint, Aes> connections = Connections(__instance);
                connections.TryGetValue(endpoint, out Aes? previous);
                connections[endpoint] = candidate;
                // Capture only this handshake's first publication. A replacement
                // made by a callback is a separate session and must survive rollback.
                HandshakeLease? handshake = activeHandshake;
                if (handshake != null && handshake.Installed == null && ReferenceEquals(handshake.Security, __instance) &&
                    handshake.Connection.RemoteEndPoint.Equals(endpoint)) { handshake.Installed = candidate; }
                candidate = null;
                previous?.Dispose();
            }
            finally { candidate?.Dispose(); }
        }
        return false;
    }

    private static bool BeforeSecurityRemoveConnection(object __instance, IPEndPoint ep)
    {
        lock (SecurityStateFor(__instance).Gate)
            if (Connections(__instance).Remove(ep, out Aes? removed)) { removed.Dispose(); }
        return false;
    }

    private static bool BeforeSecurityClearConnections(object __instance)
    {
        ClearSecurity(__instance, disposeRsa: false);
        return false;
    }

    private static void ClearSecurity(object security, bool disposeRsa)
    {
        SecurityState state = SecurityStateFor(security);
        lock (state.Gate)
        {
            Dictionary<IPEndPoint, Aes> connections = Connections(security);
            Aes[] removed = connections.Values.ToArray();
            connections.Clear();
            foreach (Aes aes in removed) { aes.Dispose(); }
            if (disposeRsa && !state.Disposed)
            {
                ((RSA)Current.Crypto.Rsa.GetValue(security)!).Dispose();
                state.Disposed = true;
            }
        }
    }

    private static bool BeforeSecurityEncrypt(object __instance, byte[] data, IPEndPoint target, ref byte[] __result)
    {
        SecurityState state = SecurityStateFor(__instance);
        lock (state.Gate)
        {
            RequireSecurity(state);
            using var output = new MemoryStream();
            using (ICryptoTransform transform = Connections(__instance)[target].CreateEncryptor())
            using (var stream = new CryptoStream(output, transform, CryptoStreamMode.Write))
                stream.Write(data, 0, data.Length);
            __result = output.ToArray();
        }
        return false;
    }

    private static bool BeforeSecurityDecrypt(object __instance, byte[] data, IPEndPoint target, ref byte[] __result)
    {
        SecurityState state = SecurityStateFor(__instance);
        lock (state.Gate)
        {
            RequireSecurity(state);
            using var input = new MemoryStream(data, writable: false);
            using ICryptoTransform transform = Connections(__instance)[target].CreateDecryptor();
            using var stream = new CryptoStream(input, transform, CryptoStreamMode.Read);
            using var output = new MemoryStream();
            stream.CopyTo(output);
            __result = output.ToArray();
        }
        return false;
    }

    private static HandshakeLease BeginHandshake(HostServer server, NetConnection connection)
    {
        var lease = new HandshakeLease(State(server), connection, Current.Crypto.ServerSecurity.GetValue(server)!, activeHandshake);
        Monitor.Enter(lease.State.ClientGate);
        activeHandshake = lease;
        return lease;
    }

    private static Exception? AfterHandshake(HostServer __instance, Exception? __exception, HandshakeLease? __state)
    {
        if (__state == null) { return __exception; }
        try
        {
            bool registered = Current.Clients(__instance).TryGetValue(__state.Connection.RemoteUniqueIdentifier, out RageCoop.Server.Client? client) &&
                ReferenceEquals(Connection(client), __state.Connection);
            if (!registered && __state.Installed is Aes installed)
            {
                lock (SecurityStateFor(__state.Security).Gate)
                {
                    Dictionary<IPEndPoint, Aes> connections = Connections(__state.Security);
                    if (connections.TryGetValue(__state.Connection.RemoteEndPoint, out Aes? current) && ReferenceEquals(current, installed))
                    {
                        connections.Remove(__state.Connection.RemoteEndPoint);
                        installed.Dispose();
                    }
                }
            }
            if (__exception == null) { Recount(__instance); }
            return __exception;
        }
        finally
        {
            activeHandshake = __state.Previous;
            Monitor.Exit(__state.State.ClientGate);
        }
    }

    private static void AfterSecurityStop(HostServer __instance) =>
        ClearSecurity(Current.Crypto.ServerSecurity.GetValue(__instance)!, disposeRsa: true);

    private sealed class SecurityState
    {
        internal readonly object Gate = new();
        internal bool Disposed;
    }

    private sealed class HandshakeLease(ServerState state, NetConnection connection, object security, HandshakeLease? previous)
    {
        internal ServerState State { get; } = state;
        internal NetConnection Connection { get; } = connection;
        internal object Security { get; } = security;
        internal HandshakeLease? Previous { get; } = previous;
        internal Aes? Installed;
    }

    private sealed class SecurityContract
    {
        internal readonly FieldInfo Connections, Rsa, ServerSecurity;
        internal readonly MethodInfo[] Methods;

        internal SecurityContract()
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type security = typeof(HostServer).Assembly.GetType("RageCoop.Server.Security", true)!;
            Connections = security.GetField("SecuredConnections", all)!;
            Rsa = security.GetField("RSA", all)!;
            ServerSecurity = typeof(HostServer).GetField("Security", all)!;
            if (Connections?.FieldType != typeof(Dictionary<IPEndPoint, Aes>) || !Connections.IsInitOnly ||
                Rsa?.FieldType != typeof(RSA) || ServerSecurity?.FieldType != security) { throw Unsupported("security fields"); }
            var bodies = new Dictionary<string, string>
            {
                ["HasSecuredConnection"] = "c8a122cf56ff474841b48c3819065ecc61d0953b6dd90b92e4189efacea5559b",
                ["Encrypt"] = "9d1044956e97337c809c089e61616991d382dc886b49355ffff8810fe3bdba25",
                ["Decrypt"] = "fecd7078a240d65722bbd4a150cfcbbf76d25b56235f0c4b8cfa92f01f120195",
                ["AddConnection"] = "e66b5475dc65fb20ce22cc820f701d5c3d079a3ebcd3ddb2f477186e7141db28",
                ["RemoveConnection"] = "8c2317d4ddb2fe0db644cb229a4effe66bbbb1a477b21af3b8f14148ffb8fa11",
                ["GetPublicKey"] = "a965b6248bf0911393d0df0ea6e9b9bccbcfc7b7f39407b4e4c62fbfad2f6ce7",
                ["ClearConnections"] = "d4cdb760819b6e11ab18197aa3d0618c2e08bad1a87c4eb3ab458fdba87fc598"
            };
            Methods = bodies.Keys.Select(name => security.GetMethod(name, all) ?? throw Unsupported("security " + name)).ToArray();
            foreach (MethodInfo method in Methods)
                if (Convert.ToHexStringLower(SHA256.HashData(method.GetMethodBody()!.GetILAsByteArray()!)) != bodies[method.Name])
                    throw Unsupported("security " + method.Name + " IL");
        }
    }
}
