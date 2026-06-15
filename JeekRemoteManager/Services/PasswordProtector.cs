using System;
using System.Security.Cryptography;
using System.Text;

namespace JeekRemoteManager.Services;

/// <summary>
/// Encrypts and decrypts secrets using the Windows Data Protection API (DPAPI),
/// scoped to the current user. Encrypted blobs can only be read back by the same
/// Windows user account on the same machine.
/// </summary>
public static class PasswordProtector
{
    // A small, fixed entropy value adds a little extra protection so that the
    // encrypted blob is bound to this application, not just the user account.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("JeekRemoteManager.v1");

    /// <summary>Encrypts a clear-text password and returns a Base64 string.</summary>
    public static string Encrypt(string? clearText)
    {
        if (string.IsNullOrEmpty(clearText))
            return "";

        var data = Encoding.UTF8.GetBytes(clearText);
        var encrypted = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    /// <summary>Decrypts a Base64 blob produced by <see cref="Encrypt"/>.</summary>
    public static string Decrypt(string? encryptedBase64)
    {
        if (string.IsNullOrEmpty(encryptedBase64))
            return "";

        try
        {
            var encrypted = Convert.FromBase64String(encryptedBase64);
            var data = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception)
        {
            // Blob created by another user/machine, or corrupted. Treat as empty.
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
