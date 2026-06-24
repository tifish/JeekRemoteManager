using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace JeekRemoteManager.Services;

/// <summary>
/// Machine-local store of trusted SSH host-key fingerprints — a known_hosts
/// equivalent. SSH.NET has no built-in host-key verification and trusts every
/// host by default, so this adds trust-on-first-use with later mismatch
/// detection. Stored next to the machine settings file (host trust is a
/// per-machine decision, like OpenSSH's ~/.ssh/known_hosts).
/// </summary>
public static class KnownHostsStore
{
    public enum Status
    {
        /// <summary>No fingerprint stored for this host yet.</summary>
        Unknown,

        /// <summary>The presented fingerprint matches the stored one.</summary>
        Match,

        /// <summary>A fingerprint is stored but differs — possible MITM.</summary>
        Mismatch,
    }

    private static readonly object Gate = new();

    private static string FilePath =>
        Path.Combine(
            Path.GetDirectoryName(SettingsService.DefaultMachineSettingsPath) ?? AppContext.BaseDirectory,
            "known_hosts.json");

    private static string Key(string host, int port) =>
        $"{host.Trim().ToLowerInvariant()}:{(port > 0 ? port : 22)}";

    /// <summary>Compares a presented SHA256 fingerprint against the stored one.</summary>
    public static Status Check(string host, int port, string fingerprintSha256)
    {
        lock (Gate)
        {
            var map = Load();
            if (!map.TryGetValue(Key(host, port), out var saved))
                return Status.Unknown;
            return string.Equals(saved, fingerprintSha256, StringComparison.Ordinal)
                ? Status.Match
                : Status.Mismatch;
        }
    }

    /// <summary>Records a host's SHA256 fingerprint as trusted.</summary>
    public static void Trust(string host, int port, string fingerprintSha256)
    {
        lock (Gate)
        {
            var map = Load();
            map[Key(host, port)] = fingerprintSha256;
            Save(map);
        }
    }

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath))
                       ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            // An unreadable/corrupt file is treated as empty; the user is re-prompted.
        }

        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static void Save(Dictionary<string, string> map)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort; a failed save just means re-prompting next time.
        }
    }
}
