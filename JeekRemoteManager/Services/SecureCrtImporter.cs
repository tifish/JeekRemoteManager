using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.Services;

/// <summary>
/// Imports SSH connections from a SecureCRT Sessions directory.
///
/// SecureCRT stores each session as an <c>.ini</c> file under
/// <c>%APPDATA%\VanDyke\Config\Sessions</c> (or a portable Config\Sessions path).
/// Subfolders map to groups. Only SSH1/SSH2 sessions are imported.
/// Passwords use SecureCRT's machine/config-passphrase encryption; decryption is
/// best-effort for Password V2 with an empty config passphrase.
/// </summary>
public class SecureCrtImporter
{
    private static readonly HashSet<string> SkippedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Default.ini",
        "__FolderData__.ini",
    };

    private static readonly Regex FieldLine = new(
        @"^(?<type>[SDBZ]):""(?<name>[^""]+)""=(?<value>.*)$",
        RegexOptions.Compiled);

    private readonly ConnectionStore _store;

    public SecureCrtImporter(ConnectionStore store)
    {
        _store = store;
    }

    public record ImportResult(int Imported, int Skipped, int Folders, int PasswordsImported);

    /// <summary>
    /// Walks <paramref name="sessionsRoot"/> recursively and imports every SSH
    /// session into the store, preserving the relative folder layout.
    /// </summary>
    public ImportResult Import(string sessionsRoot)
    {
        if (!Directory.Exists(sessionsRoot))
            throw new DirectoryNotFoundException(sessionsRoot);

        var imported = 0;
        var skipped = 0;
        var folders = 0;
        var passwords = 0;
        var folderCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(sessionsRoot, "*.ini", SearchOption.AllDirectories))
        {
            if (SkippedFileNames.Contains(Path.GetFileName(file)))
            {
                skipped++;
                continue;
            }

            Dictionary<string, string> fields;
            try
            {
                fields = ParseIniFields(File.ReadAllText(file, DetectEncoding(file)));
            }
            catch
            {
                skipped++;
                continue;
            }

            var connection = MapToConnection(fields, Path.GetFileNameWithoutExtension(file), out var passwordImported);
            if (connection is null)
            {
                skipped++;
                continue;
            }

            var relativeDir = Path.GetRelativePath(sessionsRoot, Path.GetDirectoryName(file)!);
            var targetFolder = ResolveTargetFolder(relativeDir, folderCache, ref folders);
            _store.Save(connection, targetFolder);
            imported++;
            if (passwordImported)
                passwords++;
        }

        return new ImportResult(imported, skipped, folders, passwords);
    }

    private string ResolveTargetFolder(
        string relativeDir,
        Dictionary<string, string> folderCache,
        ref int folders)
    {
        if (string.IsNullOrEmpty(relativeDir) || relativeDir == ".")
            return _store.RootPath;

        if (folderCache.TryGetValue(relativeDir, out var cached))
            return cached;

        var parts = relativeDir.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        var current = _store.RootPath;
        var built = "";
        foreach (var part in parts)
        {
            built = string.IsNullOrEmpty(built) ? part : Path.Combine(built, part);
            if (folderCache.TryGetValue(built, out var existing))
            {
                current = existing;
                continue;
            }

            var candidate = Path.Combine(current, ConnectionStore.SanitizeName(part));
            if (!Directory.Exists(candidate))
            {
                candidate = _store.CreateFolder(current, part);
                folders++;
            }

            folderCache[built] = candidate;
            current = candidate;
        }

        return current;
    }

    private static Connection? MapToConnection(
        Dictionary<string, string> fields,
        string sessionName,
        out bool passwordImported)
    {
        passwordImported = false;

        var protocol = GetString(fields, "Protocol Name");
        if (!IsSshProtocol(protocol))
            return null;

        // Explicit non-session marker (rare); still require a host to import.
        if (fields.TryGetValue("Is Session", out var isSession)
            && ParseHexInt(isSession) == 0)
            return null;

        var host = GetString(fields, "Hostname");
        if (string.IsNullOrWhiteSpace(host))
            return null;

        var port = ParsePort(fields);
        var username = GetString(fields, "Username");
        var name = string.IsNullOrWhiteSpace(sessionName) ? host : sessionName;

        var connection = new Connection
        {
            Type = ConnectionType.Ssh,
            Name = name,
            Host = host.Trim(),
            Port = port > 0 ? port : Connection.DefaultPort(ConnectionType.Ssh),
            Username = username,
            PrivateKeyPath = ResolveIdentityPath(fields),
            TerminalType = MapEmulation(GetString(fields, "Emulation")),
        };

        var clearPassword = TryDecryptPassword(fields);
        if (!string.IsNullOrEmpty(clearPassword))
        {
            // Encrypt returns "" when no master key is active; never write cleartext.
            var blob = PasswordProtector.Encrypt(clearPassword);
            if (!string.IsNullOrEmpty(blob))
            {
                connection.EncryptedPassword = blob;
                passwordImported = true;
            }
        }

        return connection;
    }

    private static bool IsSshProtocol(string protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol))
            return false;

        return protocol.Equals("SSH2", StringComparison.OrdinalIgnoreCase)
               || protocol.Equals("SSH1", StringComparison.OrdinalIgnoreCase)
               || protocol.Equals("SSH", StringComparison.OrdinalIgnoreCase);
    }

    private static int ParsePort(Dictionary<string, string> fields)
    {
        // Prefer protocol-specific ports.
        foreach (var key in new[] { "[SSH2] Port", "[SSH1] Port", "Port" })
        {
            if (fields.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw))
            {
                var value = ParseHexInt(raw);
                if (value > 0)
                    return value;
            }
        }

        return 0;
    }

    private static string ResolveIdentityPath(Dictionary<string, string> fields)
    {
        foreach (var key in new[] { "Identity Filename V2", "Identity Filename" })
        {
            var value = GetString(fields, key);
            if (string.IsNullOrWhiteSpace(value))
                continue;

            // SecureCRT sometimes prefixes paths; keep only existing files.
            var path = value.Trim().Trim('"');
            if (File.Exists(path))
                return path;
        }

        return "";
    }

    private static string MapEmulation(string emulation)
    {
        if (string.IsNullOrWhiteSpace(emulation))
            return Connection.DefaultTerminalType;

        // SecureCRT uses labels like "Xterm", "VT100"; map common ones to TERM values.
        return emulation.Trim().ToLowerInvariant() switch
        {
            "xterm" => "xterm-256color",
            "linux" => "linux",
            "vt100" => "vt100",
            "vt220" => "vt220",
            _ => Connection.DefaultTerminalType,
        };
    }

    private static string? TryDecryptPassword(Dictionary<string, string> fields)
    {
        // Prefer V2 (post-7.3.3).
        var v2 = GetString(fields, "Password V2");
        if (!string.IsNullOrWhiteSpace(v2))
        {
            var plain = TryDecryptPasswordV2(v2);
            if (plain is not null)
                return plain;
        }

        // Legacy fixed-key Blowfish algorithm is intentionally skipped (needs Blowfish).
        return null;
    }

    /// <summary>
    /// Decrypts SecureCRT Password V2 when the config passphrase is empty.
    /// Format: <c>02:hex</c> (AES-256-CBC, key = SHA256("")) or <c>03:hex</c>
    /// (bcrypt-pbkdf2 — not supported here without a passphrase UI).
    /// </summary>
    internal static string? TryDecryptPasswordV2(string raw, string configPassphrase = "")
    {
        raw = raw.Trim();
        if (raw.Length < 3 || raw[2] != ':')
            return null;

        var prefix = raw[..2];
        var hex = raw[3..];
        byte[] cipherBytes;
        try
        {
            cipherBytes = Convert.FromHexString(hex);
        }
        catch
        {
            return null;
        }

        if (prefix != "02")
            return null; // "03" requires bcrypt_pbkdf2; skip without UI for passphrase.

        try
        {
            var key = SHA256.HashData(Encoding.UTF8.GetBytes(configPassphrase));
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = new byte[16];
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            var padded = aes.CreateDecryptor().TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
            if (padded.Length < 4 + 32)
                return null;

            var length = BitConverter.ToInt32(padded, 0);
            if (length < 0 || 4 + length + 32 > padded.Length)
                return null;

            var plainBytes = padded.AsSpan(4, length).ToArray();
            var checksum = padded.AsSpan(4 + length, 32).ToArray();
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(plainBytes), checksum))
                return null;

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> ParseIniFields(string content)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            // Binary multi-line blobs start with a leading space continuation — skip.
            if (line.Length == 0 || line[0] is ' ' or '\t')
                continue;

            var match = FieldLine.Match(line.TrimEnd('\r'));
            if (!match.Success)
                continue;

            var name = match.Groups["name"].Value;
            var value = match.Groups["value"].Value;
            // Last write wins for duplicates.
            fields[name] = value;
        }

        return fields;
    }

    private static string GetString(Dictionary<string, string> fields, string name) =>
        fields.TryGetValue(name, out var value) ? value ?? "" : "";

    private static int ParseHexInt(string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0)
            return 0;

        // SecureCRT D: fields are usually 8 hex digits (e.g. 00000016 = 22).
        if (int.TryParse(raw, System.Globalization.NumberStyles.HexNumber, null, out var hex))
            return hex;

        if (int.TryParse(raw, out var dec))
            return dec;

        return 0;
    }

    private static Encoding DetectEncoding(string path)
    {
        var preamble = new byte[3];
        using (var stream = File.OpenRead(path))
        {
            var read = stream.Read(preamble, 0, 3);
            if (read >= 2 && preamble[0] == 0xFF && preamble[1] == 0xFE)
                return Encoding.Unicode;
            if (read >= 2 && preamble[0] == 0xFE && preamble[1] == 0xFF)
                return Encoding.BigEndianUnicode;
            if (read >= 3 && preamble[0] == 0xEF && preamble[1] == 0xBB && preamble[2] == 0xBF)
                return Encoding.UTF8;
        }

        // Most modern SecureCRT session files are UTF-8 without multi-byte needs.
        return Encoding.UTF8;
    }
}
