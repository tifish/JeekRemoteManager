using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using JeekTools;

namespace JeekRemoteManager.Services;

/// <summary>
/// Holds the unlocked master-password material for the session. Each saved
/// connection password is encrypted as a self-contained blob:
///
///     jrm1:Base64(salt || nonce || cipher || tag)
///
/// The random salt is stored inside the blob, so a single connection .json can
/// be carried to another machine and decrypted with the master password alone.
/// For day-to-day startup on the same machine the master-password material is
/// cached via DPAPI to skip the password prompt.
/// </summary>
public sealed class MasterKeyService
{
    public const string BlobPrefix = "jrm1:";

    private const int Iterations = 210_000;
    private const int KeySize = 32;   // AES-256
    private const int SaltSize = 16;
    private const int NonceSize = 12; // AES-GCM standard
    private const int TagSize = 16;

    // Extra entropy binding the DPAPI cache to this application/cache version.
    private static readonly byte[] CacheEntropy = Encoding.UTF8.GetBytes("JeekRemoteManager.masterpassword.v3");

    private byte[]? _masterSecret;
    private byte[]? _masterSecretHash;

    /// <summary>The session-wide instance, used by <see cref="PasswordProtector"/>.</summary>
    public static MasterKeyService? Current { get; set; }

    /// <summary>
    /// Full path to the DPAPI master-password cache. Always under per-user
    /// LocalApplicationData because it is machine-/account-bound. Never carried
    /// with portable data. Settable for tests.
    /// </summary>
    public static string CachePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JeekRemoteManager", "master-password.bin");

    /// <summary>True once the master password has been unlocked and is held in memory.</summary>
    public bool IsUnlocked => _masterSecret != null;

    /// <summary>True when a local DPAPI cache file is present.</summary>
    public static bool HasCache => File.Exists(CachePath);

    /// <summary>
    /// True if <paramref name="password"/> matches the currently-unlocked master
    /// password material. Used to gate reveal-password operations behind a
    /// re-entry of the master password. Returns false if locked.
    /// </summary>
    public bool VerifyPassword(string password)
    {
        if (_masterSecretHash is null)
            return false;

        var candidate = PasswordToBytes(password);
        try
        {
            var candidateHash = SHA256.HashData(candidate);
            return CryptographicOperations.FixedTimeEquals(candidateHash, _masterSecretHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
        }
    }

    /// <summary>
    /// Accepts a master password as the active session secret and refreshes the
    /// local DPAPI cache.
    /// </summary>
    public void SetPassword(string password)
    {
        var secret = PasswordToBytes(password);
        try
        {
            SetMasterSecret(secret, cache: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>Forgets the in-memory master-password material without touching the on-disk cache.</summary>
    public void Lock()
    {
        ClearInMemorySecret();
    }

    /// <summary>
    /// Loads the previously cached master-password material (DPAPI,
    /// machine-bound). Returns false if no usable cache exists; the caller then
    /// prompts for the master password.
    /// </summary>
    public bool TryUnlockFromCache()
    {
        byte[]? secret = null;
        try
        {
            if (!File.Exists(CachePath))
                return false;

            secret = ProtectedData.Unprotect(
                File.ReadAllBytes(CachePath), CacheEntropy, DataProtectionScope.CurrentUser);
            if (secret.Length == 0)
                return false;

            SetMasterSecret(secret, cache: false);
            return true;
        }
        catch
        {
            ClearInMemorySecret();
            return false;
        }
        finally
        {
            if (secret is not null)
                CryptographicOperations.ZeroMemory(secret);
        }
    }

    // --- Password encryption (used by PasswordProtector) ---

    /// <summary>Encrypts a clear-text password with the active master password; returns a jrm1 blob.</summary>
    public string EncryptPassword(string? clearText)
    {
        if (string.IsNullOrEmpty(clearText))
            return "";
        if (_masterSecret is null)
            throw new InvalidOperationException("Master password is locked.");

        return EncryptWithSecret(_masterSecret, clearText);
    }

    /// <summary>Decrypts a jrm1 blob. Returns "" on failure.</summary>
    public string DecryptPassword(string? encryptedBase64)
    {
        TryDecryptPassword(encryptedBase64, out var clear);
        return clear;
    }

    /// <summary>
    /// Attempts to decrypt a jrm1 password blob. Returns true on success (an
    /// empty blob yields ""), false when a non-empty blob cannot be decrypted
    /// with the active master password. Unknown non-empty formats fail.
    /// </summary>
    public bool TryDecryptPassword(string? encryptedBase64, out string clear)
    {
        clear = "";
        if (string.IsNullOrEmpty(encryptedBase64))
            return true;
        if (_masterSecret is null)
            return false;

        return TryDecryptWithSecret(_masterSecret, encryptedBase64, out clear);
    }

    // --- Static helpers (used by startup validation, re-encryption sweeps, and tools) ---

    public static bool IsPasswordBlob(string? encryptedBase64) =>
        !string.IsNullOrEmpty(encryptedBase64)
        && encryptedBase64.StartsWith(BlobPrefix, StringComparison.Ordinal);

    /// <summary>Encrypts a clear-text password under an arbitrary master password.</summary>
    public static string EncryptWithPassword(string password, string clearText)
    {
        var secret = PasswordToBytes(password);
        try
        {
            return EncryptWithSecret(secret, clearText);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>
    /// Decrypts a jrm1 blob under an arbitrary master password. Returns null on
    /// failure (wrong password, unsupported format, corrupted blob, or bad Base64).
    /// An empty input is a successful no-op.
    /// </summary>
    public static string? DecryptWithPassword(string password, string? encryptedBase64)
    {
        if (string.IsNullOrEmpty(encryptedBase64))
            return "";

        var secret = PasswordToBytes(password);
        try
        {
            return TryDecryptWithSecret(secret, encryptedBase64, out var clear) ? clear : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    // --- Cache ---

    private void SetMasterSecret(byte[] secret, bool cache)
    {
        ClearInMemorySecret();
        _masterSecret = (byte[])secret.Clone();
        _masterSecretHash = SHA256.HashData(_masterSecret);
        if (cache)
            CacheSecret(_masterSecret);
    }

    private void ClearInMemorySecret()
    {
        if (_masterSecret is not null)
            CryptographicOperations.ZeroMemory(_masterSecret);
        if (_masterSecretHash is not null)
            CryptographicOperations.ZeroMemory(_masterSecretHash);

        _masterSecret = null;
        _masterSecretHash = null;
    }

    private static void CacheSecret(byte[] secret)
    {
        try
        {
            using var lease = SharedDataFile.Acquire(CachePath);
            var protectedSecret = ProtectedData.Protect(secret, CacheEntropy, DataProtectionScope.CurrentUser);
            SharedDataFile.WriteAllBytesAtomic(CachePath, protectedSecret);
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
            using var lease = SharedDataFile.Acquire(CachePath);
            if (File.Exists(CachePath))
                File.Delete(CachePath);
        }
        catch
        {
            // Best-effort.
        }
    }

    // --- jrm1 AES-GCM primitives ---

    private static string EncryptWithSecret(byte[] masterSecret, string clearText)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plain = Encoding.UTF8.GetBytes(clearText);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        var key = DeriveBlobKey(masterSecret, salt);

        try
        {
            using (var gcm = new AesGcm(key, TagSize))
                gcm.Encrypt(nonce, plain, cipher, tag);

            var blob = new byte[SaltSize + NonceSize + cipher.Length + TagSize];
            Buffer.BlockCopy(salt, 0, blob, 0, SaltSize);
            Buffer.BlockCopy(nonce, 0, blob, SaltSize, NonceSize);
            Buffer.BlockCopy(cipher, 0, blob, SaltSize + NonceSize, cipher.Length);
            Buffer.BlockCopy(tag, 0, blob, SaltSize + NonceSize + cipher.Length, TagSize);
            return BlobPrefix + Convert.ToBase64String(blob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static bool TryDecryptWithSecret(byte[] masterSecret, string encryptedBase64, out string clear)
    {
        clear = "";
        if (!encryptedBase64.StartsWith(BlobPrefix, StringComparison.Ordinal))
            return false;

        try
        {
            var blob = Convert.FromBase64String(encryptedBase64[BlobPrefix.Length..]);
            if (blob.Length < SaltSize + NonceSize + TagSize)
                return false;

            var salt = blob.AsSpan(0, SaltSize);
            var nonce = blob.AsSpan(SaltSize, NonceSize);
            var cipherLen = blob.Length - SaltSize - NonceSize - TagSize;
            var cipher = blob.AsSpan(SaltSize + NonceSize, cipherLen);
            var tag = blob.AsSpan(SaltSize + NonceSize + cipherLen, TagSize);
            var plain = new byte[cipherLen];
            var key = DeriveBlobKey(masterSecret, salt);

            try
            {
                using (var gcm = new AesGcm(key, TagSize))
                    gcm.Decrypt(nonce, cipher, tag, plain);

                clear = Encoding.UTF8.GetString(plain);
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch
        {
            // Wrong password (GCM tag mismatch), corrupted blob, or bad Base64.
            clear = "";
            return false;
        }
    }

    private static byte[] DeriveBlobKey(byte[] masterSecret, ReadOnlySpan<byte> salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            masterSecret, salt, Iterations, HashAlgorithmName.SHA256, KeySize);

    private static byte[] PasswordToBytes(string password) => Encoding.UTF8.GetBytes(password);
}
