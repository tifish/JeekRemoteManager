using System.Security.Cryptography;
using Google.Protobuf;
using SkiaSharp;
using BarcodeFormat = ZXing.BarcodeFormat;

namespace JeekRemoteManager.Services;

internal static class SshMfaQrReader
{
    internal sealed class Account(string name, string encryptedCredential)
    {
        public string Name { get; } = name;
        public string EncryptedCredential { get; } = encryptedCredential;
    }

    internal static bool IsSupportedSize(int width, int height) =>
        width > 0 && height > 0 && (long)width * height <= 24_000_000;

    /// <summary>Returns null for an unreadable image or QR, and an empty list for a QR without supported accounts.</summary>
    internal static IReadOnlyList<Account>? Read(Stream image, Func<string, string> encrypt)
    {
        if (image.CanSeek && image.Length > 32 * 1024 * 1024)
            return null;
        using var codec = SKCodec.Create(image);
        if (codec is null || !IsSupportedSize(codec.Info.Width, codec.Info.Height))
            return null;
        using var pixels = SKBitmap.Decode(codec);
        if (pixels is null)
            return null;

        var reader = new ZXing.SkiaSharp.BarcodeReader { AutoRotate = true };
        reader.Options.PossibleFormats = [BarcodeFormat.QR_CODE];
        reader.Options.TryHarder = true;
        reader.Options.TryInverted = true;
        return reader.Decode(pixels) is { } qr ? Parse(qr.Text, encrypt) : null;
    }

    private static IReadOnlyList<Account> Parse(string text, Func<string, string> encrypt)
    {
        text = text.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0
            || !uri.IsDefaultPort || uri.Fragment.Length > 0)
        {
            return [];
        }

        if (uri.Scheme == "otpauth" && SshTotp.TryParse(text, out var credential))
        {
            credential.Dispose();
            var label = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            return [CreateAccount(label, Parameter(uri, "issuer") ?? "", text, encrypt)];
        }

        return uri.Scheme == "otpauth-migration" && uri.Host.Equals("offline", StringComparison.OrdinalIgnoreCase)
            && Parameter(uri, "data") is { } data ? ParseMigration(data, encrypt) : [];
    }

    // Google Authenticator exports a protobuf MigrationPayload. Each batch image has
    // complete OtpParameters entries of its own. Batch metadata and unknown tags can be skipped.
    private static IReadOnlyList<Account> ParseMigration(string data, Func<string, string> encrypt)
    {
        byte[] bytes;
        try
        {
            var base64 = string.Concat(data.Where(ch => !char.IsWhiteSpace(ch)))
                .Replace('-', '+')
                .Replace('_', '/')
                .TrimEnd('=');
            bytes = Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
        }
        catch (FormatException)
        {
            return [];
        }

        try
        {
            using var input = new CodedInputStream(bytes);
            var accounts = new List<Account>();
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                if (tag == 10) // otp_parameters, field 1
                {
                    using var entry = input.ReadBytes().CreateCodedInput();
                    if (ReadAccount(entry, encrypt) is { } account)
                        accounts.Add(account);
                }
                else
                    input.SkipLastField();
            }
            return accounts;
        }
        catch (InvalidProtocolBufferException)
        {
            return [];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static Account? ReadAccount(CodedInputStream entry, Func<string, string> encrypt)
    {
        var key = ByteString.Empty;
        var name = "";
        var issuer = "";
        var algorithm = 0;
        var digits = 0;
        var type = 0;
        uint tag;
        while ((tag = entry.ReadTag()) != 0)
        {
            switch (tag)
            {
                case 10: key = entry.ReadBytes(); break;
                case 18: name = entry.ReadString(); break;
                case 26: issuer = entry.ReadString(); break;
                case 32: algorithm = entry.ReadEnum(); break;
                case 40: digits = entry.ReadEnum(); break;
                case 48: type = entry.ReadEnum(); break;
                default: entry.SkipLastField(); break;
            }
        }

        var hash = algorithm switch { 0 or 1 => "SHA1", 2 => "SHA256", 3 => "SHA512", _ => null };
        var digitCount = digits switch { 0 or 1 => "6", 2 => "8", _ => null };
        if (type is not (0 or 2) || key.IsEmpty || hash is null || digitCount is null)
            return null;

        var setup = $"otpauth://totp/SSH?secret={SshTotp.EncodeBase32(key.Span)}&algorithm={hash}&digits={digitCount}";
        return CreateAccount(name, issuer, setup, encrypt);
    }

    private static Account CreateAccount(string name, string issuer, string setup, Func<string, string> encrypt)
    {
        name = CleanLabel(name);
        issuer = CleanLabel(issuer);
        var label = name.Length == 0 ? issuer : name;
        if (name.Length > 0 && issuer.Length > 0 && !name.Equals(issuer, StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith(issuer + ":", StringComparison.OrdinalIgnoreCase))
        {
            label = $"{issuer}: {name}";
        }
        var encrypted = encrypt(setup);
        if (string.IsNullOrEmpty(encrypted))
            throw new InvalidOperationException("Unable to protect the imported TOTP credential.");
        return new Account(label, encrypted);
    }

    private static string CleanLabel(string label) => string.Concat(label.Where(c => !char.IsControl(c))).Trim();

    private static string? Parameter(Uri uri, string name)
    {
        string? value = null;
        foreach (var field in uri.Query.TrimStart('?').Split('&'))
        {
            var equals = field.IndexOf('=');
            if (equals <= 0 || !Uri.UnescapeDataString(field[..equals]).Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (value is not null)
                return null;
            value = Uri.UnescapeDataString(field[(equals + 1)..]);
        }
        return value;
    }
}
