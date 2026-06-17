using System.Security.Cryptography;
using System.Text;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;

Console.OutputEncoding = Encoding.UTF8;

var roots = args.Length == 0
    ? new[]
    {
        @"C:\Users\fmy\AppData\Roaming\JeekRemoteManager\Connections",
        @"C:\Library\Software\Net\RemoteControl\JeekRemoteManager\Connections",
    }
    : args;

if (roots.Any(a => a is "-h" or "--help" or "/?"))
{
    PrintUsage();
    return 0;
}

var masterPassword = ReadPassword("Current master password: ");
if (masterPassword.Length == 0)
{
    Console.Error.WriteLine("Master password cannot be empty.");
    return 2;
}

var exitCode = 0;
foreach (var root in roots)
{
    try
    {
        var report = ConvertRoot(root, masterPassword);
        Console.WriteLine();
        Console.WriteLine(root);
        Console.WriteLine($"  JSON files:        {report.JsonCount}");
        Console.WriteLine($"  Empty passwords:   {report.EmptyCount}");
        Console.WriteLine($"  Converted:         {report.ConvertedCount}");
        Console.WriteLine($"  Already jrm1:      {report.AlreadyJrm1Count}");
        Console.WriteLine($"  Verified jrm1:     {report.VerifiedJrm1Count}");
        Console.WriteLine($"  Backup:            {report.BackupPath ?? "(not needed)"}");
    }
    catch (Exception ex)
    {
        exitCode = 1;
        Console.Error.WriteLine();
        Console.Error.WriteLine(root);
        Console.Error.WriteLine($"  ERROR: {ex.Message}");
    }
}

return exitCode;

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project tools\\LegacyPasswordConverter\\LegacyPasswordConverter.csproj -- [connections-root ...]");
    Console.WriteLine();
    Console.WriteLine("If no roots are passed, the converter updates the two known JeekRemoteManager connection roots.");
}

static ConvertReport ConvertRoot(string root, string masterPassword)
{
    if (!Directory.Exists(root))
        throw new DirectoryNotFoundException("Connections root does not exist.");

    var store = new ConnectionStore(root);
    var files = store.AllConnectionFiles();
    var legacyKey = LegacyCrypto.DeriveKey(masterPassword);
    var pending = new List<(string File, Connection Connection, string ClearPassword)>();
    var emptyCount = 0;
    var alreadyJrm1Count = 0;

    try
    {
        foreach (var file in files)
        {
            var connection = store.Load(file);
            var encrypted = connection.EncryptedPassword;
            if (string.IsNullOrEmpty(encrypted))
            {
                emptyCount++;
                continue;
            }

            if (MasterKeyService.IsPasswordBlob(encrypted))
            {
                if (MasterKeyService.DecryptWithPassword(masterPassword, encrypted) is null)
                    throw new InvalidOperationException($"Existing jrm1 password failed verification: {file}");

                alreadyJrm1Count++;
                continue;
            }

            var clear = LegacyCrypto.DecryptWithKey(legacyKey, encrypted);
            if (clear is null)
                throw new InvalidOperationException($"Legacy password could not be decrypted: {file}");

            pending.Add((file, connection, clear));
        }
    }
    finally
    {
        CryptographicOperations.ZeroMemory(legacyKey);
    }

    string? backupPath = null;
    if (pending.Count > 0)
    {
        backupPath = CreateBackup(root);
        foreach (var item in pending)
        {
            item.Connection.EncryptedPassword =
                MasterKeyService.EncryptWithPassword(masterPassword, item.ClearPassword);
            store.SaveInPlace(item.Connection, item.File);
        }
    }

    var verifiedJrm1Count = VerifyRoot(root, masterPassword, out var finalEmptyCount);
    return new ConvertReport(
        files.Count,
        finalEmptyCount,
        pending.Count,
        alreadyJrm1Count,
        verifiedJrm1Count,
        backupPath);
}

static int VerifyRoot(string root, string masterPassword, out int emptyCount)
{
    var store = new ConnectionStore(root);
    var verified = 0;
    emptyCount = 0;

    foreach (var file in store.AllConnectionFiles())
    {
        var connection = store.Load(file);
        if (string.IsNullOrEmpty(connection.EncryptedPassword))
        {
            emptyCount++;
            continue;
        }

        if (!MasterKeyService.IsPasswordBlob(connection.EncryptedPassword))
            throw new InvalidOperationException($"Non-jrm1 password remains after conversion: {file}");

        if (MasterKeyService.DecryptWithPassword(masterPassword, connection.EncryptedPassword) is null)
            throw new InvalidOperationException($"Converted password failed verification: {file}");

        verified++;
    }

    return verified;
}

static string CreateBackup(string root)
{
    var parent = Path.GetDirectoryName(Path.GetFullPath(root.TrimEnd(
        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
        ?? throw new InvalidOperationException("Cannot resolve backup parent folder.");
    var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    var backup = Path.Combine(parent, $"{name}.backup-{stamp}");
    var i = 2;
    while (Directory.Exists(backup))
        backup = Path.Combine(parent, $"{name}.backup-{stamp}-{i++}");

    CopyDirectory(root, backup);
    return backup;
}

static void CopyDirectory(string sourceDir, string destDir)
{
    Directory.CreateDirectory(destDir);

    foreach (var file in Directory.GetFiles(sourceDir))
        File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: false);

    foreach (var dir in Directory.GetDirectories(sourceDir))
        CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
}

static string ReadPassword(string prompt)
{
    Console.Write(prompt);
    var sb = new StringBuilder();

    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return sb.ToString();
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (sb.Length > 0)
                sb.Length--;
            continue;
        }

        if (!char.IsControl(key.KeyChar))
            sb.Append(key.KeyChar);
    }
}

internal sealed record ConvertReport(
    int JsonCount,
    int EmptyCount,
    int ConvertedCount,
    int AlreadyJrm1Count,
    int VerifiedJrm1Count,
    string? BackupPath);

internal static class LegacyCrypto
{
    private const int Iterations = 210_000;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly byte[] FixedSalt = Encoding.UTF8.GetBytes("JeekRemoteManager.master.v2");

    public static byte[] DeriveKey(string password) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), FixedSalt, Iterations,
            HashAlgorithmName.SHA256, KeySize);

    public static string? DecryptWithKey(byte[] key, string? encryptedBase64)
    {
        if (string.IsNullOrEmpty(encryptedBase64))
            return "";

        try
        {
            var blob = Convert.FromBase64String(encryptedBase64);
            if (blob.Length < NonceSize + TagSize)
                return null;

            var nonce = blob.AsSpan(0, NonceSize);
            var cipherLen = blob.Length - NonceSize - TagSize;
            var cipher = blob.AsSpan(NonceSize, cipherLen);
            var tag = blob.AsSpan(NonceSize + cipherLen, TagSize);

            var plain = new byte[cipherLen];
            using (var gcm = new AesGcm(key, TagSize))
                gcm.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }
}
