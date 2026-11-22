using JeekRemoteManager.Models;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;
using JeekTools;
using Renci.SshNet.Common;

namespace JeekRemoteManager.Tests;

[CollectionDefinition("MasterKeyStatic", DisableParallelization = true)]
public sealed class MasterKeyStaticCollection;

[Collection("MasterKeyStatic")]
public sealed class SshMfaTests
{
    private const string RfcSeed = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData("SHA1", RfcSeed, "07081804")]
    [InlineData("SHA256", "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZA====", "68084774")]
    [InlineData("SHA512", "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNA", "25091201")]
    public void Generates_rfc6238_vectors(string algorithm, string seed, string code)
    {
        Assert.True(SshTotp.TryParse($"otpauth://totp/Test?secret={seed}&algorithm={algorithm}&digits=8", out var credential));
        using (credential!)
            Assert.Equal(code, credential!.Generate(DateTimeOffset.FromUnixTimeSeconds(1111111109)));
    }

    [Theory]
    [InlineData("gez dgnbvgy3tqojq gezdgnbvgy3tqojq", 59)]
    [InlineData("otpauth://totp/Test?secret=" + RfcSeed + "&period=60", 119)]
    public void Supports_base32_and_explicit_time_steps(string input, long seconds)
    {
        Assert.True(SshTotp.TryParse(input, out var credential));
        using (credential!)
            Assert.Equal("287082", credential!.Generate(DateTimeOffset.FromUnixTimeSeconds(seconds)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("123456")]
    [InlineData("MZ======")]
    [InlineData("MY=")]
    [InlineData("otpauth://hotp/Test?secret=" + RfcSeed)]
    [InlineData("otpauth://totp/Test?secret=" + RfcSeed + "&secret=MY")]
    [InlineData("otpauth://totp/Test?secret=" + RfcSeed + "&algorithm=MD5")]
    [InlineData("otpauth://totp/Test?secret=" + RfcSeed + "&digits=0")]
    [InlineData("otpauth://totp/Test?secret=" + RfcSeed + "&period=0")]
    public void Rejects_invalid_credentials(string input) =>
        Assert.False(SshTotp.TryParse(input, out _));

    [Fact]
    public void Separates_password_and_totp_and_answers_only_once()
    {
        var conversation = new SshConnectionFactory.KeyboardInteractiveConversation();
        Assert.Equal(["password-fixture", "012345"], Fill(conversation, () => "012345", "",
            ("Password: ", false), ("Verification code: ", false)));
        Assert.Equal(["manual"], Fill(conversation, () => throw new InvalidOperationException(), "",
            ("Verification code: ", false)));
    }

    [Fact]
    public void A_custom_keyboard_interactive_prompt_with_password_wording_receives_only_the_totp()
    {
        var input = new SshMfaInput(SshMfaInputMode.MatchPrompt, "2nd Password:");
        var prompts = new List<AuthenticationPrompt>
        {
            new(0, false, "Password:"),
            new(1, false, "2ND PASSWORD:"),
        };
        var challenge = new AuthenticationPromptEventArgs("fixture", "", "en", prompts);
        var conversation = new SshConnectionFactory.KeyboardInteractiveConversation();
        SshConnectionFactory.HandleAuthenticationPrompt(challenge, "password-fixture", conversation,
            new("test.invalid", 22, "fixture", "", false, ""), _ => throw new InvalidOperationException(),
            () => "012345", input.MatchesPrompt);
        Assert.Equal("password-fixture", prompts[0].Response);
        Assert.Equal("012345", prompts[1].Response);
        Assert.True(conversation.TotpSupplied);
    }

    [Theory]
    [InlineData("\u001b[31m2nd Password:\u001b[0m", true)]
    [InlineData("2nd Password:\r\nWelcome!", false)]
    [InlineData("2nd Pass", false)]
    public void Shell_prompt_matching_uses_the_latest_visible_line(string output, bool matches) =>
        Assert.Equal(matches, new SshMfaInput(SshMfaInputMode.MatchPrompt, "2nd Password:").MatchesShellOutput(output));

    [Theory]
    [InlineData("OTP code:", true, "")]
    [InlineData("Passcode:", false, "")]
    [InlineData("Enter response:", false, "")]
    [InlineData("SMS verification code:", false, "")]
    [InlineData("Verification code:", false, "Enter the code sent to your phone")]
    [InlineData("Duo one-time password:", false, "")]
    [InlineData("DuoPush verification code:", false, "")]
    [InlineData("Recovery verification code:", false, "")]
    public void Routes_non_totp_prompts_to_the_user(string prompt, bool echoed, string instruction) =>
        Assert.Equal(["manual"], Fill(new(), () => throw new InvalidOperationException(), instruction, (prompt, echoed)));

    [Fact]
    public void Falls_back_when_the_profile_is_unavailable_or_the_round_has_multiple_codes()
    {
        Assert.Equal(["manual"], Fill(new(), () => null, "", ("MFA auth code:", false)));
        Assert.Equal(["manual", "manual"], Fill(new(), () => throw new InvalidOperationException(), "",
            ("OTP code:", false), ("Verification code:", false)));
    }

    [Theory]
    [InlineData("OTPCode:")]
    [InlineData("DuoPush:")]
    [InlineData("2FAcode:")]
    public void Unconfigured_connections_keep_second_factor_prompts_manual(string prompt) =>
        Assert.Equal(["manual"], Fill(new(), null, "", (prompt, false)));

    [Fact]
    public void Shared_id_resolves_current_profile_and_survives_config_migration()
    {
        var root = Path.Combine(Path.GetTempPath(), "jrm-mfa-unit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sourceConfig = Path.Combine(root, "source");
            var targetConfig = Path.Combine(root, "destination");
            var source = new ConnectionStore(Path.Combine(sourceConfig, "Connections"));
            var target = new ConnectionStore(Path.Combine(targetConfig, "Connections"));
            var credential = MasterKeyService.EncryptWithPassword("fixture", RfcSeed);
            var replacement = MasterKeyService.EncryptWithPassword("fixture", "JBSWY3DPEHPK3PXP");
            var id = source.SshMfaProfiles.Save(null, "Shared", credential)!.Value;
            var otherId = target.SshMfaProfiles.Save(null, "Existing", credential)!.Value;
            var a = source.Save(new Connection { Name = "first", SshMfaProfileId = id }, source.RootPath);
            var b = source.Save(new Connection { Name = "second", SshMfaProfileId = id }, source.RootPath);
            var first = source.Load(a);
            var second = source.Load(b);

            Assert.DoesNotContain(credential, File.ReadAllText(a));
            Assert.Equal(id, source.SshMfaProfiles.Save(id, "Renamed", replacement));
            Assert.All(new[] { first, second }, c =>
                Assert.Equal(replacement, c.SshMfaProfiles!.TryGetEncryptedCredential(c.SshMfaProfileId!.Value)));
            Assert.Equal(replacement, Assert.Single(source.EnumerateStoredPasswordBlobs()));

            SettingsService.MoveConfigRoot(sourceConfig, targetConfig);
            Assert.Equal(id, target.Load(Path.Combine(target.RootPath, Path.GetFileName(a))).SshMfaProfileId);
            Assert.Equal(replacement, target.SshMfaProfiles.TryGetEncryptedCredential(id));
            Assert.Equal(credential, target.SshMfaProfiles.TryGetEncryptedCredential(otherId));
            target.SshMfaProfiles.Remove(id);
            Assert.Null(target.SshMfaProfiles.TryGetEncryptedCredential(id));
            Assert.Equal(id, target.Load(Path.Combine(target.RootPath, Path.GetFileName(b))).SshMfaProfileId);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Master_password_change_migrates_shared_profiles_and_checks_all_secrets_first()
    {
        var root = Path.Combine(Path.GetTempPath(), "jrm-mfa-master-" + Guid.NewGuid().ToString("N"));
        var previousMaster = MasterKeyService.Current;
        var previousCache = MasterKeyService.CachePath;
        var master = new MasterKeyService();
        MainWindowViewModel? vm = null;
        try
        {
            MasterKeyService.CachePath = Path.Combine(root, "master.bin");
            MasterKeyService.Current = master;
            master.SetPassword("old-fixture");

            var settings = new SettingsService(Path.Combine(root, "machine.json"), Path.Combine(root, "roaming.json"));
            settings.Settings.StorageLocation = StorageLocation.CustomDirectory;
            settings.Settings.CustomStoragePath = root;
            var store = new ConnectionStore(settings.ResolveConnectionsRoot());
            var credential = master.EncryptPassword(RfcSeed);
            var input = new SshMfaInput(SshMfaInputMode.MatchPrompt, "2nd Password:");
            var id = store.SshMfaProfiles.Save(null, "Shared", credential, input)!.Value;
            var unreadableId = store.SshMfaProfiles.Save(null, "Unreadable",
                MasterKeyService.EncryptWithPassword("different-fixture", RfcSeed))!.Value;
            var file = store.Save(new Connection { Name = "fixture", SshMfaProfileId = id,
                EncryptedPassword = master.EncryptPassword("ssh-fixture") }, store.RootPath);
            vm = new MainWindowViewModel(store, new ConnectionLauncher(), settings);

            vm.ChangeMasterPassword("new-fixture");
            Assert.True(master.VerifyPassword("old-fixture"));
            Assert.Equal(credential, store.SshMfaProfiles.TryGetEncryptedCredential(id));

            store.SshMfaProfiles.Remove(unreadableId);
            vm.ChangeMasterPassword("new-fixture");
            var migrated = store.SshMfaProfiles.TryGetEncryptedCredential(id);
            Assert.True(master.VerifyPassword("new-fixture"));
            Assert.Equal(RfcSeed, MasterKeyService.DecryptWithPassword("new-fixture", migrated));
            Assert.Null(MasterKeyService.DecryptWithPassword("old-fixture", migrated));
            Assert.Equal(input, store.SshMfaProfiles.TryGetProfile(id)!.Input);
            var connection = store.Load(file);
            Assert.Equal(id, connection.SshMfaProfileId);
            Assert.Equal("ssh-fixture", master.DecryptPassword(connection.EncryptedPassword));
        }
        finally
        {
            vm?.StopWatchingForDebug();
            master.Lock();
            MasterKeyService.Current = previousMaster;
            MasterKeyService.CachePath = previousCache;
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static string[] Fill(SshConnectionFactory.KeyboardInteractiveConversation conversation,
        Func<string?>? generate, string instruction, params (string Text, bool Echoed)[] requests)
    {
        var prompts = requests.Select((request, index) => new AuthenticationPrompt(index, request.Echoed, request.Text)).ToList();
        var challenge = new AuthenticationPromptEventArgs("test", instruction, "en", prompts);
        SshConnectionFactory.HandleAuthenticationPrompt(challenge, "password-fixture", conversation,
            new("test.invalid", 22, "test", "", false, ""), _ => "manual", generate);
        return prompts.Select(prompt => prompt.Response ?? throw new InvalidOperationException("Missing response.")).ToArray();
    }
}
