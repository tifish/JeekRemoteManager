using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.Services;

/// <summary>
/// Imports SSH connections from an Xshell Sessions directory.
///
/// Xshell stores each session as a Unicode/ANSI <c>.xsh</c> INI-style file under
/// e.g. <c>%USERPROFILE%\Documents\NetSarang Computer\{ver}\Xshell\Sessions</c>.
/// Subfolders map to groups. Passwords are RC4-encrypted with a version-dependent
/// key (SID / username+SID / master password); decryption is best-effort for the
/// current Windows user without a master password.
/// </summary>
public class XshellImporter
{
    private readonly ConnectionStore _store;

    public XshellImporter(ConnectionStore store)
    {
        _store = store;
    }

    public record ImportResult(int Imported, int Skipped, int Folders, int PasswordsImported);

    /// <summary>
    /// Walks <paramref name="sessionsRoot"/> recursively and imports every SSH
    /// <c>.xsh</c> session into the store, preserving the relative folder layout.
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

        var userName = Environment.UserName;
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "";

        foreach (var file in Directory.EnumerateFiles(sessionsRoot, "*.xsh", SearchOption.AllDirectories))
        {
            Dictionary<string, Dictionary<string, string>> sections;
            try
            {
                sections = ParseIniSections(ReadSessionText(file));
            }
            catch
            {
                skipped++;
                continue;
            }

            var connection = MapToConnection(sections, Path.GetFileNameWithoutExtension(file), userName, sid, out var passwordImported);
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
        Dictionary<string, Dictionary<string, string>> sections,
        string sessionName,
        string userName,
        string sid,
        out bool passwordImported)
    {
        passwordImported = false;

        var connectionSection = GetSection(sections, "CONNECTION");
        var protocol = GetValue(connectionSection, "Protocol");
        if (!IsSshProtocol(protocol))
            return null;

        var host = GetValue(connectionSection, "Host");
        if (string.IsNullOrWhiteSpace(host))
            return null;

        var portText = GetValue(connectionSection, "Port");
        var port = int.TryParse(portText, out var parsedPort) && parsedPort > 0
            ? parsedPort
            : Connection.DefaultPort(ConnectionType.Ssh);

        var auth = GetSection(sections, "CONNECTION:AUTHENTICATION");
        var username = GetValue(auth, "UserName");
        var description = GetValue(connectionSection, "Description");
        var versionText = GetValue(GetSection(sections, "SessionInfo"), "Version");
        if (!double.TryParse(versionText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var version))
            version = 8.0;

        var name = string.IsNullOrWhiteSpace(sessionName) ? host : sessionName;
        var connection = new Connection
        {
            Type = ConnectionType.Ssh,
            Name = name,
            Host = host.Trim(),
            Port = port,
            Username = username,
            Notes = description,
            PrivateKeyPath = ResolveUserKey(auth),
        };

        var encryptedPassword = GetValue(auth, "Password");
        if (!string.IsNullOrWhiteSpace(encryptedPassword))
        {
            var clear = TryDecryptPassword(encryptedPassword, version, userName, sid);
            if (!string.IsNullOrEmpty(clear))
            {
                connection.EncryptedPassword = PasswordProtector.Encrypt(clear);
                if (!string.IsNullOrEmpty(connection.EncryptedPassword))
                    passwordImported = true;
            }
        }

        return connection;
    }

    private static bool IsSshProtocol(string protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol))
            return false;

        return protocol.Equals("SSH", StringComparison.OrdinalIgnoreCase)
               || protocol.Equals("SSH2", StringComparison.OrdinalIgnoreCase)
               || protocol.Equals("SSH1", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveUserKey(Dictionary<string, string> auth)
    {
        // UserKey is usually an Xshell key-store name, not a filesystem path.
        // Only keep it when it points at an existing private key file.
        var userKey = GetValue(auth, "UserKey");
        if (string.IsNullOrWhiteSpace(userKey))
            return "";

        var path = userKey.Trim().Trim('"');
        return File.Exists(path) ? path : "";
    }

    /// <summary>
    /// Best-effort decrypt for Xshell passwords without a master password.
    /// Tries the version-specific key material for the current Windows user.
    /// </summary>
    internal static string? TryDecryptPassword(
        string base64Cipher,
        double version,
        string userName,
        string sid,
        string? masterPassword = null)
    {
        byte[] data;
        try
        {
            data = Convert.FromBase64String(base64Cipher.Trim());
        }
        catch
        {
            return null;
        }

        if (data.Length == 0)
            return null;

        foreach (var key in EnumerateKeys(version, userName, sid, masterPassword))
        {
            try
            {
                var plain = DecryptWithKey(data, key, version);
                if (plain is not null)
                    return plain;
            }
            catch
            {
                // Try next key material.
            }
        }

        return null;
    }

    private static IEnumerable<byte[]> EnumerateKeys(
        double version,
        string userName,
        string sid,
        string? masterPassword)
    {
        if (!string.IsNullOrEmpty(masterPassword))
            yield return SHA256.HashData(Encoding.UTF8.GetBytes(masterPassword));

        if (version is >= 5.1 and <= 5.2)
        {
            if (!string.IsNullOrEmpty(sid))
                yield return SHA256.HashData(Encoding.UTF8.GetBytes(sid));
        }
        else if (version > 5.2)
        {
            if (!string.IsNullOrEmpty(userName) && !string.IsNullOrEmpty(sid))
                yield return SHA256.HashData(Encoding.UTF8.GetBytes(userName + sid));
            // Some installs still decrypt with SID alone or the legacy fixed key.
            if (!string.IsNullOrEmpty(sid))
                yield return SHA256.HashData(Encoding.UTF8.GetBytes(sid));
        }

        // version < 5.1 and as last-resort fallback
        yield return MD5.HashData(Encoding.ASCII.GetBytes("!X@s#h$e%l^l&"));
    }

    private static string? DecryptWithKey(byte[] data, byte[] key, double version)
    {
        if (version < 5.1)
        {
            var plain = Rc4Transform(key, data);
            return Encoding.UTF8.GetString(plain);
        }

        if (data.Length <= 32)
            return null;

        var ciphertext = data.AsSpan(0, data.Length - 32).ToArray();
        var checksum = data.AsSpan(data.Length - 32).ToArray();
        var plainBytes = Rc4Transform(key, ciphertext);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(plainBytes), checksum))
            return null;

        return Encoding.UTF8.GetString(plainBytes);
    }

    /// <summary>Classic RC4 (ARC4) stream cipher used by Xshell/Xmanager.</summary>
    internal static byte[] Rc4Transform(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (var i = 0; i < 256; i++)
            s[i] = (byte)i;

        var j = 0;
        for (var i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var output = new byte[data.Length];
        var x = 0;
        var y = 0;
        for (var k = 0; k < data.Length; k++)
        {
            x = (x + 1) & 0xFF;
            y = (y + s[x]) & 0xFF;
            (s[x], s[y]) = (s[y], s[x]);
            output[k] = (byte)(data[k] ^ s[(s[x] + s[y]) & 0xFF]);
        }

        return output;
    }

    private static string ReadSessionText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.Default.GetString(bytes);
    }

    private static Dictionary<string, Dictionary<string, string>> ParseIniSections(string content)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        sections[""] = current;

        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
                continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                var name = line[1..^1].Trim();
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections[name] = current;
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            current[key] = value;
        }

        return sections;
    }

    private static Dictionary<string, string> GetSection(
        Dictionary<string, Dictionary<string, string>> sections,
        string name) =>
        sections.TryGetValue(name, out var section)
            ? section
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static string GetValue(Dictionary<string, string> section, string key) =>
        section.TryGetValue(key, out var value) ? value ?? "" : "";
}
