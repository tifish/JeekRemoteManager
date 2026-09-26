using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekRemoteManager.Services;

/// <summary>
/// Machine-local store of trusted SSH host-key fingerprints — a known_hosts
/// equivalent. SSH.NET has no built-in host-key verification and trusts every
/// host by default, so this adds trust-on-first-use with later mismatch
/// detection. Stored next to the machine settings file (host trust is a
/// per-machine decision, like OpenSSH's ~/.ssh/known_hosts).
/// </summary>
/// <remarks>
/// An instance per file: the app uses <see cref="Default"/>, tests and probes make their own
/// on a temporary file instead of redirecting the one the running app trusts hosts with.
/// </remarks>
public sealed class KnownHostsStore
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(KnownHostsStore));

    public enum Status
    {
        /// <summary>No fingerprint stored for this host yet.</summary>
        Unknown,

        /// <summary>The presented fingerprint matches the stored one.</summary>
        Match,

        /// <summary>A fingerprint is stored but differs — possible MITM.</summary>
        Mismatch,
    }

    /// <summary>One trusted host: <c>host:port</c>, its SHA256 fingerprint, and the key
    /// family it was seen with (empty for entries saved before families were recorded).</summary>
    public readonly record struct Entry(string Host, string Fingerprint, string KeyType);

    /// <summary>
    /// Suffix of the companion entry that remembers which key family a host presented.
    /// It lives beside the plain <c>host:port</c> entry rather than inside its value, so an
    /// older build sharing this machine-local file still reads every fingerprint unchanged.
    /// </summary>
    private const string KeyTypeSuffix = "#type";

    private readonly object _gate = new();

    public KnownHostsStore(string filePath) => FilePath = filePath;

    /// <summary>The machine's store, next to the machine-local settings file.</summary>
    public static KnownHostsStore Default { get; } = new(Path.Combine(
        Path.GetDirectoryName(SettingsService.DefaultMachineSettingsPath) ?? AppContext.BaseDirectory,
        "known_hosts.json"));

    public string FilePath { get; }

    private static string Key(string host, int port) =>
        $"{host.Trim().ToLowerInvariant()}:{(port > 0 ? port : 22)}";

    /// <summary>
    /// The key family of a host-key algorithm: RSA is negotiated as <c>rsa-sha2-512</c>,
    /// <c>rsa-sha2-256</c> or <c>ssh-rsa</c> but is one key with one fingerprint, and a
    /// certificate algorithm certifies the plain key it names.
    /// </summary>
    public static string KeyFamily(string algorithm)
    {
        const string certSuffix = "-cert-v01@openssh.com";
        var name = algorithm.EndsWith(certSuffix, StringComparison.Ordinal)
            ? algorithm[..^certSuffix.Length]
            : algorithm;
        return name is "rsa-sha2-256" or "rsa-sha2-512" ? "ssh-rsa" : name;
    }

    /// <summary>
    /// Compares a presented SHA256 fingerprint against the stored one. A match also records
    /// the key family when an older entry lacks it, so the next dial can prefer it.
    /// </summary>
    public Status Check(string host, int port, string keyType, string fingerprintSha256)
    {
        lock (_gate)
        {
            using var lease = SharedDataFile.Acquire(FilePath);
            var map = Load();
            var key = Key(host, port);
            if (!map.TryGetValue(key, out var saved))
                return Status.Unknown;
            if (!string.Equals(saved, fingerprintSha256, StringComparison.Ordinal))
                return Status.Mismatch;

            var family = KeyFamily(keyType);
            if (family.Length > 0
                && (!map.TryGetValue(key + KeyTypeSuffix, out var savedFamily) || savedFamily != family))
            {
                map[key + KeyTypeSuffix] = family;
                Save(map);
            }

            return Status.Match;
        }
    }

    /// <summary>Returns the trusted fingerprint for a host, if one is stored.</summary>
    public bool TryGet(string host, int port, out string fingerprintSha256)
    {
        lock (_gate)
        {
            using var lease = SharedDataFile.Acquire(FilePath);
            return Load().TryGetValue(Key(host, port), out fingerprintSha256!);
        }
    }

    /// <summary>Returns the key family a trusted host presented, if it was recorded.</summary>
    public bool TryGetKeyType(string host, int port, out string keyType)
    {
        lock (_gate)
        {
            using var lease = SharedDataFile.Acquire(FilePath);
            var map = Load();
            var key = Key(host, port);
            if (map.ContainsKey(key) && map.TryGetValue(key + KeyTypeSuffix, out var family) && family.Length > 0)
            {
                keyType = family;
                return true;
            }

            keyType = "";
            return false;
        }
    }

    /// <summary>Every trusted host with its SHA256 fingerprint and recorded key family.</summary>
    public IReadOnlyList<Entry> All()
    {
        lock (_gate)
        {
            using var lease = SharedDataFile.Acquire(FilePath);
            var map = Load();
            return map
                .Where(pair => !pair.Key.EndsWith(KeyTypeSuffix, StringComparison.Ordinal))
                .Select(pair => new Entry(
                    pair.Key,
                    pair.Value,
                    map.TryGetValue(pair.Key + KeyTypeSuffix, out var family) ? family : ""))
                .ToList();
        }
    }

    /// <summary>
    /// Drops a stored fingerprint, the equivalent of <c>ssh-keygen -R</c>: the next connection
    /// to that host is treated as new instead of failing the mismatch check. Returns false
    /// when nothing was stored for it.
    /// </summary>
    public bool Forget(string host, int port)
    {
        lock (_gate)
        {
            using var lease = SharedDataFile.Acquire(FilePath);
            var map = Load();
            var key = Key(host, port);
            map.Remove(key + KeyTypeSuffix);
            if (!map.Remove(key))
                return false;

            Save(map);
            return true;
        }
    }

    /// <summary>Records a host's SHA256 fingerprint as trusted, with the key family it used
    /// (empty = unknown, which leaves no family recorded).</summary>
    public void Trust(string host, int port, string fingerprintSha256, string keyType = "")
    {
        lock (_gate)
        {
            using var lease = SharedDataFile.Acquire(FilePath);
            var map = Load();
            var key = Key(host, port);
            map[key] = fingerprintSha256;
            var family = keyType.Length == 0 ? "" : KeyFamily(keyType);
            if (family.Length > 0)
                map[key + KeyTypeSuffix] = family;
            else
                map.Remove(key + KeyTypeSuffix);
            Save(map);
        }
    }

    /// <summary>
    /// Reads the store. A file that exists but does not parse is copied aside before it is
    /// treated as empty: the next save rewrites the file, and without the copy every host
    /// the user had trusted would be gone for good. A read that fails for I/O reasons
    /// throws instead — carrying on with an empty map would overwrite the real file.
    /// </summary>
    private Dictionary<string, string> Load()
    {
        var path = FilePath;
        if (!File.Exists(path))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var text = File.ReadAllText(path);
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(text);
            if (map is not null)
                return new Dictionary<string, string>(map, StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            BackUpCorruptFile(path, ex.Message);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        // Literal "null": nothing worth keeping, but still not a map.
        BackUpCorruptFile(path, "the file does not contain a JSON object");
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private void BackUpCorruptFile(string path, string reason)
    {
        var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Copy(path, backup, overwrite: true);
            File.Delete(path);
            Log.ZLogWarning($"known_hosts.json was unreadable ({reason}); moved it to {backup} and started empty.");
        }
        catch (Exception ex)
        {
            Log.ZLogWarning($"known_hosts.json was unreadable ({reason}) and could not be backed up: {ex.Message}");
            throw new InvalidDataException(
                $"The known-hosts file {path} is corrupt and could not be backed up: {ex.Message}");
        }
    }

    private void Save(Dictionary<string, string> map)
    {
        try
        {
            using var lease = SharedDataFile.Acquire(FilePath);
            SharedDataFile.WriteAllTextAtomic(
                FilePath,
                JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort; a failed save just means re-prompting next time.
        }
    }
}
