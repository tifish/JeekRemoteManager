using JeekRemoteManager.Services;
using Renci.SshNet;

namespace JeekRemoteManager.Tests;

/// <summary>Each test gets its own store on a temporary file, so they run in parallel and
/// never touch the machine's real known_hosts.</summary>
public sealed class KnownHostsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "jrm-tests-" + Guid.NewGuid().ToString("N"));
    private readonly KnownHostsStore _store;

    public KnownHostsTests()
    {
        Directory.CreateDirectory(_folder);
        _store = new KnownHostsStore(Path.Combine(_folder, "known_hosts.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void First_key_is_trusted_silently_and_a_change_needs_confirmation()
    {
        var prompted = false;
        Assert.True(SshHostKey.Evaluate(_store, "h", 22, "ssh-ed25519", "first", onMismatch: (_, _, _) => prompted = true));
        Assert.False(prompted);

        Assert.False(SshHostKey.Evaluate(_store, "h", 22, "ssh-ed25519", "second", onMismatch: (_, _, _) => false));
        Assert.True(SshHostKey.Evaluate(_store, "h", 22, "ssh-ed25519", "second", onMismatch: (_, saved, presented) =>
            saved == "first" && presented == "second"));
        Assert.Equal(KnownHostsStore.Status.Match, _store.Check("h", 22, "ssh-ed25519", "second"));
    }

    [Fact]
    public void Another_key_family_is_still_a_mismatch()
    {
        _store.Trust("h", 22, "ed-fp", "ssh-ed25519");

        Assert.Equal(KnownHostsStore.Status.Mismatch, _store.Check("h", 22, "ecdsa-sha2-nistp256", "ec-fp"));
    }

    [Theory]
    [InlineData("rsa-sha2-512", "ssh-rsa")]
    [InlineData("rsa-sha2-256-cert-v01@openssh.com", "ssh-rsa")]
    [InlineData("ssh-ed25519", "ssh-ed25519")]
    public void Key_families(string algorithm, string family) =>
        Assert.Equal(family, KnownHostsStore.KeyFamily(algorithm));

    [Fact]
    public void Remembered_family_is_offered_first()
    {
        _store.Trust("h", 22, "fp", "rsa-sha2-512");
        var info = new ConnectionInfo("h", 22, "u", new PasswordAuthenticationMethod("u", "p"));
        var count = info.HostKeyAlgorithms.Count;

        SshHostKey.PreferRememberedKeyType(_store, info, "h", 22);

        Assert.Equal("ssh-rsa", KnownHostsStore.KeyFamily(info.HostKeyAlgorithms.Keys.First()));
        Assert.Equal(count, info.HostKeyAlgorithms.Count);
    }

    [Fact]
    public void Legacy_entry_learns_its_family_on_match()
    {
        _store.Trust("h", 22, "fp");
        Assert.False(_store.TryGetKeyType("h", 22, out _));

        _store.Check("h", 22, "ssh-ed25519", "fp");

        Assert.True(_store.TryGetKeyType("h", 22, out var family));
        Assert.Equal("ssh-ed25519", family);
        Assert.DoesNotContain(_store.All(), entry => entry.Host.Contains('#'));
    }

    [Fact]
    public void Corrupt_file_is_backed_up_not_overwritten()
    {
        File.WriteAllText(_store.FilePath, "{ \"kept:22\": \"abc\", broken");

        Assert.Equal(KnownHostsStore.Status.Unknown, _store.Check("kept", 22, "ssh-ed25519", "abc"));
        _store.Trust("new", 22, "def", "ssh-ed25519");

        var backup = Assert.Single(Directory.GetFiles(_folder, "known_hosts.json.corrupt-*"));
        Assert.Contains("kept:22", File.ReadAllText(backup));
        Assert.Equal(KnownHostsStore.Status.Match, _store.Check("new", 22, "ssh-ed25519", "def"));
    }

    [Fact]
    public void Dial_options_default_to_the_machine_store() =>
        Assert.Same(KnownHostsStore.Default, new SshDialOptions().Store);
}
