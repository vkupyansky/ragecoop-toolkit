using System;
using System.Collections;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
    private static void CheckSecurityLifecycle()
    {
        using var fixture = new Fixture();
        object security = Field(fixture.Server, "Security");
        var secured = (IDictionary)Field(security, "SecuredConnections");
        var rsa = (RSA)Field(security, "RSA");
        var endpoint = new IPEndPoint(IPAddress.Loopback, 42501);
        void Add(IPEndPoint target)
        {
            using Aes source = Aes.Create();
            Call(security, "AddConnection", target, rsa.Encrypt(source.Key, RSAEncryptionPadding.Pkcs1), rsa.Encrypt(source.IV, RSAEncryptionPadding.Pkcs1));
        }
        TrackingAes Track(IPEndPoint target)
        {
            var tracked = new TrackingAes((Aes)secured[target]!);
            secured[target] = tracked;
            return tracked;
        }

        Add(endpoint);
        TrackingAes first = Track(endpoint);
        byte[] plain = Enumerable.Range(0, 8193).Select(index => (byte)index).ToArray();
        byte[] cipher = (byte[])Call(security, "Encrypt", plain, endpoint)!;
        byte[] decoded = (byte[])Call(security, "Decrypt", cipher, endpoint)!;
        using Aes expected = Aes.Create();
        expected.Key = first.Key; expected.IV = first.IV;
        using ICryptoTransform standard = expected.CreateEncryptor();
        Assert(cipher.SequenceEqual(standard.TransformFinalBlock(plain, 0, plain.Length)) && decoded.SequenceEqual(plain) &&
            first.TransformsCreated == 2 && first.TransformsDisposed == 2,
            "the actual guarded Security methods preserve real AES ciphertext/plaintext and dispose both cryptographic transforms");
        try
        {
            Call(security, "AddConnection", endpoint, rsa.Encrypt(new byte[32], RSAEncryptionPadding.Pkcs1), rsa.Encrypt(new byte[3], RSAEncryptionPadding.Pkcs1));
            throw new InvalidOperationException("An invalid AES IV was accepted.");
        }
        catch (CryptographicException) { }
        Assert(ReferenceEquals(secured[endpoint], first) && first.Disposals == 0,
            "invalid replacement key material leaves the existing exact crypto session usable");
        Add(endpoint);
        Assert(first.Disposals == 1 && !first.DisposedWhileInUse,
            "the real AddConnection atomically publishes a replacement and disposes its previous AES once");

        TrackingAes concurrent = Track(endpoint);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        concurrent.EncryptEntered = entered;
        concurrent.EncryptRelease = release;
        Task encrypting = Task.Run(() => Call(security, "Encrypt", plain, endpoint));
        Assert(entered.Wait(2000), "real Security.Encrypt entered the tracked AES transform under its session monitor");
        using var removalStarted = new ManualResetEventSlim();
        Task removing = Task.Run(() => { removalStarted.Set(); Call(security, "RemoveConnection", endpoint); });
        try
        {
            Assert(removalStarted.Wait(2000) && !removing.Wait(70) && concurrent.Disposals == 0,
                "RemoveConnection waits for the active encryption instead of disposing an AES in use");
        }
        finally { release.Set(); }
        Assert(Task.WaitAll(new[] { encrypting, removing }, 3000) && !secured.Contains(endpoint) &&
            concurrent.Disposals == 1 && !concurrent.DisposedWhileInUse && concurrent.TransformsCreated == concurrent.TransformsDisposed,
            "encryption finishes before the real removal releases its exact session and its transform");

        Add(endpoint);
        TrackingAes clearedFirst = Track(endpoint);
        var secondEndpoint = new IPEndPoint(IPAddress.Loopback, 42502);
        Add(secondEndpoint);
        TrackingAes clearedSecond = Track(secondEndpoint);
        Call(security, "ClearConnections");
        Call(security, "ClearConnections");
        Assert(secured.Count == 0 && clearedFirst.Disposals == 1 && clearedSecond.Disposals == 1,
            "the actual ClearConnections disposes every retained AES exactly once and repeated clearing is harmless");

        Add(endpoint);
        TrackingAes stopped = Track(endpoint);
        fixture.Server.Stop();
        Assert(secured.Count == 0 && stopped.Disposals == 1 && !stopped.DisposedWhileInUse,
            "the actual server Stop releases remaining crypto sessions after its workers stop");
        try { Call(security, "GetPublicKey", null, null); throw new InvalidOperationException("Stopped security still exported its key."); }
        catch (ObjectDisposedException) { }
        Assert(true, "a stopped crypto owner rejects later key operations and repeated Stop remains safe");
    }

    // The exact host methods and dictionary remain real. This AES delegates all
    // cryptography to the platform and observes disposal at their shared boundary.
    private sealed class TrackingAes(Aes inner) : Aes
    {
        internal int Disposals, TransformsCreated, TransformsDisposed;
        internal bool DisposedWhileInUse;
        internal ManualResetEventSlim? EncryptEntered, EncryptRelease;
        public override byte[] Key { get => inner.Key; set => inner.Key = value; }
        public override byte[] IV { get => inner.IV; set => inner.IV = value; }
        public override ICryptoTransform CreateEncryptor(byte[] rgbKey, byte[]? rgbIV)
        {
            EncryptEntered?.Set();
            if (EncryptRelease != null && !EncryptRelease.Wait(5000)) { throw new TimeoutException("The encryption boundary was not released."); }
            return TrackTransform(inner.CreateEncryptor(rgbKey, rgbIV));
        }
        public override ICryptoTransform CreateDecryptor(byte[] rgbKey, byte[]? rgbIV) => TrackTransform(inner.CreateDecryptor(rgbKey, rgbIV));
        public override void GenerateIV() => inner.GenerateIV();
        public override void GenerateKey() => inner.GenerateKey();
        private ICryptoTransform TrackTransform(ICryptoTransform transform)
        {
            Interlocked.Increment(ref TransformsCreated);
            return new TrackingTransform(transform, () => Interlocked.Increment(ref TransformsDisposed));
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Increment(ref Disposals) == 1)
            {
                DisposedWhileInUse = TransformsCreated != TransformsDisposed;
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class TrackingTransform(ICryptoTransform inner, Action disposed) : ICryptoTransform
    {
        private int released;
        public bool CanReuseTransform => inner.CanReuseTransform;
        public bool CanTransformMultipleBlocks => inner.CanTransformMultipleBlocks;
        public int InputBlockSize => inner.InputBlockSize;
        public int OutputBlockSize => inner.OutputBlockSize;
        public int TransformBlock(byte[] inputBuffer, int inputOffset, int inputCount, byte[] outputBuffer, int outputOffset) =>
            inner.TransformBlock(inputBuffer, inputOffset, inputCount, outputBuffer, outputOffset);
        public byte[] TransformFinalBlock(byte[] inputBuffer, int inputOffset, int inputCount) => inner.TransformFinalBlock(inputBuffer, inputOffset, inputCount);
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) { return; }
            inner.Dispose();
            disposed();
        }
    }
}
