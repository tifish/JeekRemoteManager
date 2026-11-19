using JeekRemoteManager.Services;
using SkiaSharp;
using ZXing;
using ZXing.Common;

namespace JeekRemoteManager.Tests;

public sealed class SshMfaQrTests
{
    [Theory]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    public void Reads_a_standard_setup_QR_without_losing_its_period(SKEncodedImageFormat format)
    {
        using var image = Qr("otpauth://totp/Deploy%20SSH?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=Lab&period=60", format);
        var entries = SshMfaQrReader.Read(image, setup => MasterKeyService.EncryptWithPassword("fixture", setup));
        var account = Assert.Single(entries!);
        Assert.Equal("Lab: Deploy SSH", account.Name);
        Assert.True(MasterKeyService.IsPasswordBlob(account.EncryptedCredential));
        Assert.True(SshTotp.TryParse(MasterKeyService.DecryptWithPassword("fixture", account.EncryptedCredential)!, out var totp));
        using (totp!)
            Assert.Equal("287082", totp!.Generate(DateTimeOffset.FromUnixTimeSeconds(119)));
    }

    [Theory]
    [InlineData("https://example.test/qr")]
    [InlineData("otpauth-migration://offline?data=broken%25")]
    [InlineData("otpauth-migration://offline?data=Cgo%3D")]
    [InlineData("otpauth-migration://offline?data=CicKCW9hdGgtb25seRILSE9UUCBzYW1wbGUaB0ZpeHR1cmUgASgBMAE=")]
    public void A_QR_without_supported_accounts_does_not_encrypt_anything(string payload)
    {
        using var image = Qr(payload);
        Assert.Empty(SshMfaQrReader.Read(image, _ => throw new InvalidOperationException())!);
    }

    [Fact]
    public void An_invalid_image_has_no_QR()
    {
        using var image = new MemoryStream([1, 2, 3]);
        Assert.Null(SshMfaQrReader.Read(image, _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void Batch_import_adds_distinct_profiles_and_preserves_an_existing_match()
    {
        var root = Path.Combine(Path.GetTempPath(), "jrm-mfa-qr-batch-" + Guid.NewGuid().ToString("N"));
        var store = new SshMfaProfileStore(root);
        var original = MasterKeyService.EncryptWithPassword("fixture", "original");
        var imported = MasterKeyService.EncryptWithPassword("fixture", "imported");
        try
        {
            var existingId = store.Save(null, "Deploy", original)!.Value;
            var ids = store.AddBatch([("deploy", imported), ("Deploy", imported)]);
            var profiles = store.Read().ToDictionary(profile => profile.Id);

            Assert.Equal(3, profiles.Count);
            Assert.Equal(original, profiles[existingId].EncryptedCredential);
            Assert.Equal("deploy (2)", profiles[ids[0]].Name);
            Assert.Equal("Deploy (3)", profiles[ids[1]].Name);
            Assert.All(ids, id => Assert.Equal(imported, profiles[id].EncryptedCredential));

            Assert.Throws<ArgumentException>(() => store.AddBatch([("Valid", imported), ("Invalid", "")]));
            Assert.Equal(3, store.List().Count);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static MemoryStream Qr(string payload, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        var writer = new ZXing.SkiaSharp.BarcodeWriter
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions { Width = 300, Height = 300 },
        };
        using var bitmap = writer.Write(payload);
        using var encoded = bitmap.Encode(format, 85);
        return new MemoryStream(encoded.ToArray(), writable: false);
    }
}
