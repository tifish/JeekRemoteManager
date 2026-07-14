using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace JeekRemoteManager.Services;

/// <summary>Cross-process locking and atomic replacement for shared app data.</summary>
public static class SharedDataFile
{
    public static IDisposable Acquire(string path, TimeSpan? timeout = null)
    {
        var normalized = Path.GetFullPath(path).ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24];
        var mutex = new Mutex(false, $"JeekRemoteManager.Data.{key}");
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(timeout ?? TimeSpan.FromSeconds(30));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
                throw new TimeoutException($"Timed out waiting for shared data lock: {path}");
            return new MutexLease(mutex);
        }
        catch
        {
            if (!acquired)
                mutex.Dispose();
            throw;
        }
    }

    public static IDisposable AcquireMany(params string[] paths)
    {
        var leases = new List<IDisposable>();
        try
        {
            foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p))
                         .Select(Path.GetFullPath)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                leases.Add(Acquire(path));
            return new CompositeLease(leases);
        }
        catch
        {
            for (var i = leases.Count - 1; i >= 0; i--)
                leases[i].Dispose();
            throw;
        }
    }

    public static void WriteAllTextAtomic(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, contents);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { /* best-effort cleanup */ }
        }
    }

    public static void WriteAllBytesAtomic(string path, byte[] contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, contents);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { /* best-effort cleanup */ }
        }
    }

    public static void CopyAtomic(string sourcePath, string destinationPath, bool overwrite = false)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var temporary = destinationPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(sourcePath, temporary, overwrite: false);
            File.Move(temporary, destinationPath, overwrite);
        }
        finally
        {
            try { File.Delete(temporary); } catch { /* best-effort cleanup */ }
        }
    }

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _mutex, null);
            if (value is null)
                return;
            try { value.ReleaseMutex(); } finally { value.Dispose(); }
        }
    }

    private sealed class CompositeLease(List<IDisposable> leases) : IDisposable
    {
        private List<IDisposable>? _leases = leases;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _leases, null);
            if (value is null)
                return;
            for (var i = value.Count - 1; i >= 0; i--)
                value[i].Dispose();
        }
    }
}
