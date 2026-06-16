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
/// <see cref="EncryptForRdpFile"/> stays on the Windows Data Protection API (DPAPI)
/// because it must produce the exact, machine-local blob mstsc.exe expects.
/// </summary>
public static class PasswordProtector
{
    /// <summary>Encrypts a clear-text password with the session master key; returns Base64.</summary>
    public static string Encrypt(string? clearText) =>
        MasterKeyService.Current?.EncryptPassword(clearText) ?? "";

    /// <summary>Decrypts a Base64 blob produced by <see cref="Encrypt"/>. Returns "" on failure.</summary>
    public static string Decrypt(string? encryptedBase64) =>
        MasterKeyService.Current?.DecryptPassword(encryptedBase64) ?? "";

    /// <summary>
    /// Attempts to decrypt a password blob. Returns true on success (an empty blob
    /// counts as success, yielding ""), false when a non-empty blob cannot be
    /// decrypted with the current master key. Use the false result to preserve the
    /// stored ciphertext rather than overwriting it.
    /// </summary>
    public static bool TryDecrypt(string? encryptedBase64, out string clear)
    {
        if (MasterKeyService.Current is { } master)
            return master.TryDecryptPassword(encryptedBase64, out clear);

        clear = "";
        return string.IsNullOrEmpty(encryptedBase64);
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
