using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace JeekRemoteManager.Services;

/// <summary>
/// Holds the unlocked encryption key for the session. The key is derived
/// directly from the user's master password via PBKDF2 with a fixed app-wide
/// salt — no separate vault file is needed, so portability is unconditional:
/// any single connection .json can be carried to another machine and decrypted
/// with the master password alone. For day-to-day startup on the same machine
/// the derived key is cached via DPAPI to skip the password prompt.
///
/// (A random per-install salt would only matter against an attacker doing a
/// rainbow-table attack across many installs of this app; in a single-user
/// personal tool that scenario does not apply, so the simpler fixed-salt design
/// is what actually fits the threat model.)
/// </summary>
public sealed class MasterKeyService
{
    private const int Iterations = 210_000;
    private const int KeySize = 32;   // AES-256
    private const int NonceSize = 12; // AES-GCM standard
    private const int TagSize = 16;

    // Fixed app-wide salt. See class summary for the rationale.
    private static readonly byte[] FixedSalt = Encoding.UTF8.GetBytes("JeekRemoteManager.master.v2");

    // Extra entropy binding the DPAPI cache to this application.
    private static readonly byte[] CacheEntropy = Encoding.UTF8.GetBytes("JeekRemoteManager.masterkey.v2");

    private byte[]? _key;

    /// <summary>The session-wide instance, used by <see cref="PasswordProtector"/>.</summary>
    public static MasterKeyService? Current { get; set; }

    /// <summary>
    /// Full path to the DPAPI key cache. Always under per-user LocalApplicationData
    /// because it is machine-/account-bound. Never carried with portable data.
    /// Settable for tests.
    /// </summary>
    public static string CachePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JeekRemoteManager", "key.bin");

    /// <summary>True once the master key has been unlocked and is held in memory.</summary>
    public bool IsUnlocked => _key != null;

    /// <summary>
    /// True if <paramref name="password"/> derives to the currently-unlocked key.
    /// Used to gate reveal-password operations behind a re-entry of the master
    /// password. Constant-time comparison so the check does not leak timing
    /// information. Returns false if the key is not yet unlocked.
    /// </summary>
    public bool VerifyPassword(string password)
    {
        if (_key is null)
            return false;
        var derived = DeriveKey(password);
        return CryptographicOperations.FixedTimeEquals(derived, _key);
    }

    /// <summary>True when a local DPAPI cache file is present.</summary>
    public static bool HasCache => File.Exists(CachePath);

    /// <summary>Derives a key from a master password using the fixed app salt. Pure function.</summary>
    public static byte[] DeriveKey(string password) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), FixedSalt, Iterations,
            HashAlgorithmName.SHA256, KeySize);

    /// <summary>
    /// Accepts a derived key as the active session key and refreshes the local
    /// cache. Used by the unlock flow once a password has been validated.
    /// </summary>
    public void SetKey(byte[] key)
    {
        _key = key;
        CacheKey(key);
    }

    /// <summary>
    /// Loads the previously cached key (DPAPI, machine-bound). Returns false if no
    /// usable cache exists; the caller then prompts for the master password.
    /// </summary>
    public bool TryUnlockFromCache()
    {
        try
        {
            if (!File.Exists(CachePath))
                return false;

            _key = ProtectedData.Unprotect(
                File.ReadAllBytes(CachePath), CacheEntropy, DataProtectionScope.CurrentUser);
            return _key.Length == KeySize;
        }
        catch
        {
            _key = null;
            return false;
        }
    }

    // --- Password encryption (used by PasswordProtector) ---

    /// <summary>Encrypts a clear-text password with the active key; returns Base64.</summary>
    public string EncryptPassword(string? clearText)
    {
        if (string.IsNullOrEmpty(clearText))
            return "";
        if (_key is null)
            throw new InvalidOperationException("Master key is locked.");

        return EncryptWithKey(_key, clearText);
    }

    /// <summary>Decrypts a Base64 blob produced by <see cref="EncryptPassword"/>. Returns "" on failure.</summary>
    public string DecryptPassword(string? encryptedBase64)
    {
        TryDecryptPassword(encryptedBase64, out var clear);
        return clear;
    }

    /// <summary>
    /// Attempts to decrypt a password blob. Returns true on success (an empty blob
    /// yields ""), false when a non-empty blob cannot be decrypted with the active
    /// key. Callers use the false result to preserve the original ciphertext rather
    /// than overwriting it.
    /// </summary>
    public bool TryDecryptPassword(string? encryptedBase64, out string clear)
    {
        clear = "";
        if (string.IsNullOrEmpty(encryptedBase64))
            return true;
        if (_key is null)
            return false;

        var data = GcmDecrypt(_key, encryptedBase64);
        if (data is null)
            return false;

        clear = Encoding.UTF8.GetString(data);
        return true;
    }

    // --- Static crypto helpers (used by re-encryption sweeps) ---

    /// <summary>Encrypts a clear-text password under an arbitrary key; returns Base64.</summary>
    public static string EncryptWithKey(byte[] key, string clearText) =>
        GcmEncrypt(key, Encoding.UTF8.GetBytes(clearText));

    /// <summary>
    /// Decrypts a Base64 blob under an arbitrary key. Returns null on failure (wrong
    /// key, corrupted blob, or bad Base64). An empty input is a successful no-op.
    /// </summary>
    public static string? DecryptWithKey(byte[] key, string? encryptedBase64)
    {
        if (string.IsNullOrEmpty(encryptedBase64))
            return "";

        var data = GcmDecrypt(key, encryptedBase64);
        return data is null ? null : Encoding.UTF8.GetString(data);
    }

    // --- Cache ---

    private static void CacheKey(byte[] key)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var protectedKey = ProtectedData.Protect(key, CacheEntropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(CachePath, protectedKey);
        }
        catch
        {
            // A missing cache just means the next launch prompts for the password.
        }
    }

    /// <summary>Deletes the local DPAPI cache, so the next start asks for the password.</summary>
    public static void ClearCache()
    {
        try
        {
            if (File.Exists(CachePath))
                File.Delete(CachePath);
        }
        catch
        {
            // Best-effort.
        }
    }

    // --- AES-GCM primitives ---

    private static string GcmEncrypt(byte[] key, byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using (var gcm = new AesGcm(key, TagSize))
            gcm.Encrypt(nonce, plain, cipher, tag);

        var blob = new byte[NonceSize + cipher.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceSize);
        Buffer.BlockCopy(cipher, 0, blob, NonceSize, cipher.Length);
        Buffer.BlockCopy(tag, 0, blob, NonceSize + cipher.Length, TagSize);
        return Convert.ToBase64String(blob);
    }

    private static byte[]? GcmDecrypt(byte[] key, string base64)
    {
        try
        {
            var blob = Convert.FromBase64String(base64);
            if (blob.Length < NonceSize + TagSize)
                return null;

            var nonce = blob.AsSpan(0, NonceSize);
            var cipherLen = blob.Length - NonceSize - TagSize;
            var cipher = blob.AsSpan(NonceSize, cipherLen);
            var tag = blob.AsSpan(NonceSize + cipherLen, TagSize);

            var plain = new byte[cipherLen];
            using (var gcm = new AesGcm(key, TagSize))
                gcm.Decrypt(nonce, cipher, tag, plain);
            return plain;
        }
        catch
        {
            // Wrong key (GCM tag mismatch), corrupted blob, or bad Base64.
            return null;
        }
    }
}
