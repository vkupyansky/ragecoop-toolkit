using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace RageCoopAdmin.Client;

// For updates, the lock covers read, validation and replacement across resource
// AppDomains and GTA processes. A busy writer never stalls the game thread.
internal sealed class StorageFileLock : IDisposable
{
    private readonly Mutex _mutex;

    private StorageFileLock(Mutex mutex) => _mutex = mutex;

    internal static StorageFileLock Acquire(string path)
    {
        string canonical = Path.GetFullPath(path).ToUpperInvariant();
        string key;
        using (SHA256 hash = SHA256.Create())
            key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", string.Empty);
        var mutex = new Mutex(false, @"Local\RageCoop.CoopControl.Storage." + key);
        try
        {
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Another resource is updating this settings file. Retry the action.");
            return new StorageFileLock(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
