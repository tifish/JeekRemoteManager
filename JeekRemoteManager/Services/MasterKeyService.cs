using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace JeekRemoteManager.Services;

/// <summary>
/// Holds the unlocked data-encryption key (DEK) for the session and manages the
/// master-password envelope: a random DEK encrypts every connection password,
/// while the DEK itself is wrapped by a key derived from the user's master
/// password (PBKDF2). Only the wrapped DEK and salt are persisted (in settings,
/// hence portable), so changing the master password re-wraps the DEK without
/// rewriting any connection file.
///
/// For convenience the unlocked DEK is also cached machine-locally via DPAPI, so
/// day-to-day startup on the same Windows account needs no password. The cache is
/// stored under LocalApplicationData (never roams, never travels with a portable
/// folder); on a new machine the cache is absent and the user enters the master
/// password once to rebuild it.
/// </summary>
public sealed class MasterKeyService
{
    private const int Iterations = 210_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;   // AES-256
    private const int NonceSize = 12; // AES-GCM standard
    private const int TagSize = 16;

    // Marker encrypted with the DEK so a candidate DEK (from cache or unwrap) can
    // be validated against the current vault.
    private static readonly byte[] CheckMarker = Encoding.UTF8.GetBytes("JeekRemoteManager.dek.v1");

    // Extra entropy binding the DPAPI cache to this application.
    private static readonly byte[] CacheEntropy = Encoding.UTF8.GetBytes("JeekRemoteManager.masterkey.v1");

    private readonly SettingsService _settings;
    private byte[]? _dek;

    /// <summary>The session-wide instance, used by <see cref="PasswordProtector"/>.</summary>
    public static MasterKeyService? Current { get; set; }

    public MasterKeyService(SettingsService settings) => _settings = settings;

    /// <summary>True once a master password has been set up (wrapped DEK exists).</summary>
    public bool IsConfigured =>
        !string.IsNullOrEmpty(_settings.Settings.MasterSalt)
        && !string.IsNullOrEmpty(_settings.Settings.WrappedKey);

    /// <summary>True once the DEK has been unlocked and is held in memory.</summary>
    public bool IsUnlocked => _dek != null;

    // --- Password encryption (used by PasswordProtector) ---

    /// <summary>Encrypts a clear-text password with the unlocked DEK; returns Base64.</summary>
    public string EncryptPassword(string? clearText)
    {
        if (string.IsNullOrEmpty(clearText))
            return "";
        if (_dek is null)
            throw new InvalidOperationException("Master key is locked.");

        return GcmEncrypt(_dek, Encoding.UTF8.GetBytes(clearText));
    }

    /// <summary>Decrypts a Base64 blob produced by <see cref="EncryptPassword"/>. Returns "" on failure.</summary>
    public string DecryptPassword(string? encryptedBase64)
    {
        TryDecryptPassword(encryptedBase64, out var clear);
        return clear;
    }

    /// <summary>
    /// Attempts to decrypt a password blob. Returns true on success (including an
    /// empty blob, which yields ""), false when a non-empty blob cannot be decrypted
    /// — i.e. the key is locked or the blob belongs to a different master password.
    /// Callers can use the false result to avoid clobbering the stored ciphertext.
    /// </summary>
    public bool TryDecryptPassword(string? encryptedBase64, out string clear)
    {
        clear = "";
        if (string.IsNullOrEmpty(encryptedBase64))
            return true;
        if (_dek is null)
            return false;

        var data = GcmDecrypt(_dek, encryptedBase64);
        if (data is null)
            return false;

        clear = Encoding.UTF8.GetString(data);
        return true;
    }

    // --- Setup / unlock / change ---

    /// <summary>
    /// Creates a fresh master password: generates a new random DEK, wraps it with a
    /// key derived from <paramref name="password"/>, records the salt/wrapped DEK/
    /// check blob into settings (caller is responsible for saving) and caches the
    /// DEK locally. The DEK is held unlocked afterwards.
    /// </summary>
    public void Initialize(string password)
    {
        var dek = RandomNumberGenerator.GetBytes(KeySize);
        WrapAndStore(dek, password);
        _dek = dek;
        CacheDek(dek);
    }

    /// <summary>
    /// Derives the key from <paramref name="password"/>, unwraps the stored DEK and,
    /// on success, holds it and refreshes the local cache. Returns false if the
    /// password is wrong or no master password is configured.
    /// </summary>
    public bool TryUnlock(string password)
    {
        var s = _settings.Settings;
        if (string.IsNullOrEmpty(s.MasterSalt) || string.IsNullOrEmpty(s.WrappedKey))
            return false;

        var salt = Convert.FromBase64String(s.MasterSalt);
        var kek = DeriveKey(password, salt, s.MasterIterations);
        var dek = GcmDecrypt(kek, s.WrappedKey);
        if (dek is null)
            return false;

        _dek = dek;
        CacheDek(dek);
        return true;
    }

    /// <summary>
    /// Attempts a silent unlock from the DPAPI-protected local cache. The cached DEK
    /// is validated against the current vault's check blob, so a stale cache (e.g.
    /// settings replaced from another machine) is ignored. Returns false if there is
    /// no usable cache.
    /// </summary>
    public bool TryUnlockFromCache()
    {
        try
        {
            var path = CachePath;
            if (!File.Exists(path))
                return false;

            var dek = ProtectedData.Unprotect(
                File.ReadAllBytes(path), CacheEntropy, DataProtectionScope.CurrentUser);

            if (!DekMatchesVault(dek))
                return false;

            _dek = dek;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Re-wraps the (already unlocked) DEK with a key derived from a new master
    /// password and refreshes the cache. Caller saves settings. Throws if locked.
    /// </summary>
    public void ChangePassword(string newPassword)
    {
        if (_dek is null)
            throw new InvalidOperationException("Master key is locked.");

        WrapAndStore(_dek, newPassword);
        CacheDek(_dek);
    }

    // --- Internals ---

    private void WrapAndStore(byte[] dek, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var kek = DeriveKey(password, salt, Iterations);

        var s = _settings.Settings;
        s.MasterSalt = Convert.ToBase64String(salt);
        s.MasterIterations = Iterations;
        s.WrappedKey = GcmEncrypt(kek, dek);
        s.KeyCheck = GcmEncrypt(dek, CheckMarker);
    }

    private bool DekMatchesVault(byte[] dek)
    {
        var check = _settings.Settings.KeyCheck;
        if (string.IsNullOrEmpty(check))
            return false;

        var marker = GcmDecrypt(dek, check);
        return marker is not null && marker.AsSpan().SequenceEqual(CheckMarker);
    }

    /// <summary>
    /// Overrides the DPAPI cache file location. Intended for tests so they don't
    /// touch (or overwrite) the real per-user cache. Null = default location.
    /// </summary>
    public static string? CacheFileOverride { get; set; }

    private static string CachePath => CacheFileOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JeekRemoteManager",
        "masterkey.bin");

    private static void CacheDek(byte[] dek)
    {
        try
        {
            var path = CachePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var protectedDek = ProtectedData.Protect(dek, CacheEntropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, protectedDek);
        }
        catch
        {
            // A missing cache just means the next launch prompts for the password.
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeySize);

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
