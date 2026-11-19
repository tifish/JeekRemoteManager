using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;

namespace JeekRemoteManager.Services;

/// <summary>A decoded RFC 6238 credential, kept only while validating or generating a code.</summary>
internal sealed class SshTotp(byte[] key, string algorithm, int digits, int period) : IDisposable
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static bool TryParse(string input, [NotNullWhen(true)] out SshTotp? credential)
    {
        credential = null;
        var secret = input.Trim();
        var algorithm = "SHA1";
        var digits = 6;
        var period = 30;
        if (secret.StartsWith("otpauth:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(secret, UriKind.Absolute, out var uri)
                || !uri.Host.Equals("totp", StringComparison.OrdinalIgnoreCase)
                || uri.UserInfo.Length > 0 || !uri.IsDefaultPort || uri.Fragment.Length > 0)
            {
                return false;
            }

            // Metadata such as issuer or account name has no effect on code generation.
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = parameter.IndexOf('=');
                if (equals < 1 || !parameters.TryAdd(Uri.UnescapeDataString(parameter[..equals]),
                        Uri.UnescapeDataString(parameter[(equals + 1)..])))
                {
                    return false;
                }
            }

            if (!parameters.TryGetValue("secret", out var uriSecret))
                return false;
            secret = uriSecret;
            algorithm = parameters.GetValueOrDefault("algorithm", algorithm).ToUpperInvariant();
            if (algorithm is not ("SHA1" or "SHA256" or "SHA512"))
                return false;
            if (parameters.TryGetValue("digits", out var digitsText)
                && (!TryParseNumber(digitsText, out digits) || digits is not (6 or 8)))
            {
                return false;
            }
            if (parameters.TryGetValue("period", out var periodText)
                && (!TryParseNumber(periodText, out period) || period < 1))
            {
                return false;
            }
        }

        var key = DecodeBase32(secret);
        if (key is null)
            return false;
        credential = new SshTotp(key, algorithm, digits, period);
        return true;
    }

    public string Generate(DateTimeOffset now)
    {
        Span<byte> counter = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(counter, now.ToUnixTimeSeconds() / period);
        Span<byte> hash = stackalloc byte[HMACSHA512.HashSizeInBytes];
        var length = algorithm switch
        {
            "SHA256" => HMACSHA256.HashData(key, counter, hash),
            "SHA512" => HMACSHA512.HashData(key, counter, hash),
            _ => HMACSHA1.HashData(key, counter, hash),
        };
        var offset = hash[length - 1] & 0x0f;
        var value = BinaryPrimitives.ReadUInt32BigEndian(hash[offset..]) & 0x7fffffff;
        CryptographicOperations.ZeroMemory(hash);
        return (value % (digits == 8 ? 100_000_000u : 1_000_000u))
            .ToString(digits == 8 ? "D8" : "D6", CultureInfo.InvariantCulture);
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(key);

    internal static string EncodeBase32(ReadOnlySpan<byte> key)
    {
        var encoded = new char[(key.Length * 8 + 4) / 5];
        var pending = 0;
        var bits = 0;
        var written = 0;
        foreach (var value in key)
        {
            pending = (pending << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                encoded[written++] = Base32Alphabet[(pending >> bits) & 31];
            }
            pending &= (1 << bits) - 1;
        }
        if (bits > 0)
            encoded[written] = Base32Alphabet[pending << (5 - bits)];
        var result = new string(encoded);
        Array.Clear(encoded);
        return result;
    }

    private static bool TryParseNumber(string value, out int number) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);

    private static byte[]? DecodeBase32(string secret)
    {
        var compact = string.Concat(secret.Where(c => !char.IsWhiteSpace(c)));
        var unpadded = compact.AsSpan().TrimEnd('=');
        var byteCount = unpadded.Length * 5 / 8;
        if (byteCount == 0
            || unpadded.Length != (byteCount * 8 + 4) / 5
            || (compact.Length != unpadded.Length && compact.Length != (unpadded.Length + 7) / 8 * 8))
        {
            return null;
        }

        var key = new byte[byteCount];
        var pending = 0;
        var bits = 0;
        var written = 0;
        foreach (var symbol in unpadded)
        {
            var value = char.IsAscii(symbol) ? Base32Alphabet.IndexOf(char.ToUpperInvariant(symbol)) : -1;
            if (value < 0)
                break;
            pending = (pending << 5) | value;
            bits += 5;
            if (bits < 8)
                continue;
            bits -= 8;
            key[written++] = (byte)(pending >> bits);
            pending &= (1 << bits) - 1;
        }

        if (written == byteCount && pending == 0 && bits == unpadded.Length * 5 % 8)
            return key;
        CryptographicOperations.ZeroMemory(key);
        return null;
    }
}
