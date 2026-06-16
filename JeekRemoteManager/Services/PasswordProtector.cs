using System;
using System.Security.Cryptography;
using System.Text;

namespace JeekRemoteManager.Services;

/// <summary>
/// Encrypts and decrypts connection passwords. The stored form is bound to the
/// user's master password (see <see cref="MasterKeyService"/>): a random data key
/// encrypts each password with AES-GCM, and that key is itself protected by the
/// master password. This makes the encrypted data portable across machines once
/// the master password is re-entered.
///
/// Two helpers stay on the Windows Data Protection API (DPAPI) because they are
/// inherently machine-local: <see cref="EncryptForRdpFile"/> produces the exact
/// blob mstsc.exe expects, and <see cref="DpapiDecryptLegacy"/> reads passwords
/// that older versions stored with DPAPI, for one-time migration.
/// </summary>
public static class PasswordProtector
{
    /// <summary>Entropy used by the legacy (pre-master-password) DPAPI scheme.</summary>
    private static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("JeekRemoteManager.v1");

    /// <summary>Encrypts a clear-text password with the session master key; returns Base64.</summary>
    public static string Encrypt(string? clearText) =>
        MasterKeyService.Current?.EncryptPassword(clearText) ?? "";

    /// <summary>Decrypts a Base64 blob produced by <see cref="Encrypt"/>. Returns "" on failure.</summary>
    public static string Decrypt(string? encryptedBase64) =>
        MasterKeyService.Current?.DecryptPassword(encryptedBase64) ?? "";

    /// <summary>
    /// Decrypts a password stored by an older version using DPAPI (current user).
    /// Used only during the one-time migration to master-password encryption.
    /// Returns "" if the blob was not produced on this machine/account.
    /// </summary>
    public static string DpapiDecryptLegacy(string? encryptedBase64)
    {
        if (string.IsNullOrEmpty(encryptedBase64))
            return "";

        try
        {
            var encrypted = Convert.FromBase64String(encryptedBase64);
            var data = ProtectedData.Unprotect(encrypted, LegacyEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// Produces the value for the <c>password 51:b:</c> field of an .rdp file:
    /// the clear password encoded as UTF-16LE, encrypted with DPAPI (current user)
    /// and rendered as an upper-case hexadecimal string. This is the exact format
    /// mstsc.exe expects.
    /// </summary>
    public static string EncryptForRdpFile(string? clearText)
    {
        if (string.IsNullOrEmpty(clearText))
            return "";

        var data = Encoding.Unicode.GetBytes(clearText); // UTF-16LE
        var encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        return Convert.ToHexString(encrypted);
    }
}
