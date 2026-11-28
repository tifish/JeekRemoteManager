using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;

namespace JeekRemoteManager.Tests;

public class MultiConnectionEditorTests
{
    [Fact]
    public void Uniform_and_mixed_ssh_fields_report_initial_state_and_apply_only_deltas()
    {
        var c1 = new Connection
        {
            ConnectionId = "id-1",
            Name = "web-01",
            ShortName = "A",
            Type = ConnectionType.Ssh,
            Host = "10.0.0.1",
            Port = 22,
            Username = "root",
            EncryptedPassword = "jrm1:blob-one",
            TerminalType = "xterm-256color",
            TerminalEncoding = "UTF-8",
            PrivateKeyPath = "/keys/a",
            EncryptedPrivateKeyPassphrase = "jrm1:pass-one",
            LoginCommands = "echo 1",
            AutoOpenMonitorPanel = true,
            AutoOpenFileBrowserPanel = false,
            AutoLogSession = true,
            PortForwards = "L 8080 db:5432",
            Notes = "note-1",
        };
        var c2 = new Connection
        {
            ConnectionId = "id-2",
            Name = "web-02",
            ShortName = "B",
            Type = ConnectionType.Ssh,
            Host = "10.0.0.2",
            Port = 22,
            Username = "root",
            EncryptedPassword = "jrm1:blob-two",
            TerminalType = "xterm",
            TerminalEncoding = "UTF-8",
            PrivateKeyPath = "/keys/b",
            EncryptedPrivateKeyPassphrase = "",
            LoginCommands = "echo 2",
            AutoOpenMonitorPanel = false,
            AutoOpenFileBrowserPanel = false,
            AutoLogSession = true,
            PortForwards = "D 1080",
            Notes = "note-2",
        };

        var editor = MultiConnectionEditorViewModel.FromConnections([c1, c2]);

        Assert.True(editor.IsAllSsh);
        Assert.True(editor.HasHostPort);
        Assert.True(editor.HasPassword);
        Assert.False(editor.HasPendingChanges);
        Assert.Equal(0, editor.ModifiedFieldCount);

        // Uniform values show directly; mixed values start blank / null / sentinel.
        Assert.Equal("root", editor.Username);
        Assert.Equal("22", editor.PortText);
        Assert.Equal("", editor.Host);
        Assert.Equal("", editor.ShortName);
        Assert.Null(editor.AutoOpenMonitorPanel);
        Assert.True(editor.AutoOpenMonitorPanelThreeState);
        Assert.Equal(false, editor.AutoOpenFileBrowserPanel);
        Assert.False(editor.AutoOpenFileBrowserPanelThreeState);
        Assert.Equal(true, editor.AutoLogSession);
        Assert.True(editor.SelectedTerminalType?.IsKeepUnchanged);
        Assert.Equal("UTF-8", editor.SelectedTerminalEncoding?.Value);

        // Edit only Username, Port, TerminalEncoding, AutoOpenMonitorPanel, and Clear ShortName.
        editor.Username = "deploy";
        editor.PortText = "2222";
        editor.SelectedTerminalEncoding = editor.TerminalEncodingChoices.Single(o => o.Value == "GBK");
        editor.AutoOpenMonitorPanel = true;
        editor.ClearShortName();

        Assert.Equal(5, editor.ModifiedFieldCount);
        Assert.True(editor.CanApply);

        var updated = editor.ApplyToTargets();
        Assert.Equal(2, updated);

        // Modified fields applied to both connections.
        Assert.Equal("deploy", c1.Username);
        Assert.Equal("deploy", c2.Username);
        Assert.Equal(2222, c1.Port);
        Assert.Equal(2222, c2.Port);
        Assert.Equal("GBK", c1.TerminalEncoding);
        Assert.Equal("GBK", c2.TerminalEncoding);
        Assert.True(c1.AutoOpenMonitorPanel);
        Assert.True(c2.AutoOpenMonitorPanel);
        Assert.Equal("", c1.ShortName);
        Assert.Equal("", c2.ShortName);

        // Untouched fields (including Names, Hosts, TerminalType, encrypted secrets, Notes) stay intact.
        Assert.Equal("web-01", c1.Name);
        Assert.Equal("web-02", c2.Name);
        Assert.Equal("10.0.0.1", c1.Host);
        Assert.Equal("10.0.0.2", c2.Host);
        Assert.Equal("xterm-256color", c1.TerminalType);
        Assert.Equal("xterm", c2.TerminalType);
        Assert.Equal("jrm1:blob-one", c1.EncryptedPassword);
        Assert.Equal("jrm1:blob-two", c2.EncryptedPassword);
        Assert.Equal("jrm1:pass-one", c1.EncryptedPrivateKeyPassphrase);
        Assert.Equal("", c2.EncryptedPrivateKeyPassphrase);
        Assert.Equal("note-1", c1.Notes);
        Assert.Equal("note-2", c2.Notes);
    }

    [Fact]
    public void Clearing_a_mixed_field_and_reverting_tracks_dirty_state_accurately()
    {
        var c1 = new Connection { Name = "a", Notes = "alpha", EncryptedPassword = "jrm1:secret" };
        var c2 = new Connection { Name = "b", Notes = "beta", EncryptedPassword = "" };

        var editor = MultiConnectionEditorViewModel.FromConnections([c1, c2]);

        Assert.True(editor.CanClearNotes);
        Assert.False(editor.IsNotesModified);
        Assert.True(editor.CanClearPassword);
        Assert.False(editor.IsPasswordModified);

        // Typing then erasing back to empty on a mixed field returns to unmodified.
        editor.Notes = "shared";
        Assert.True(editor.IsNotesModified);
        editor.Notes = "";
        Assert.False(editor.IsNotesModified);

        // Explicitly clearing marks dirty and writes empty on Apply.
        editor.ClearNotes();
        editor.ClearPassword();
        Assert.True(editor.IsNotesModified);
        Assert.True(editor.NotesWillClear);
        Assert.True(editor.IsPasswordModified);
        Assert.True(editor.PasswordWillClear);
        Assert.Equal(2, editor.ModifiedFieldCount);

        // Single-field revert restores Notes without affecting PasswordWillClear.
        editor.RevertNotes();
        Assert.False(editor.IsNotesModified);
        Assert.True(editor.IsPasswordModified);
        Assert.Equal(1, editor.ModifiedFieldCount);

        editor.ApplyToTargets();
        Assert.Equal("alpha", c1.Notes);
        Assert.Equal("beta", c2.Notes);
        Assert.Equal("", c1.EncryptedPassword);
        Assert.Equal("", c2.EncryptedPassword);
    }

    [Fact]
    public void Invalid_port_and_rdp_dimensions_block_apply()
    {
        var rdp1 = new Connection { Name = "r1", Type = ConnectionType.Rdp, Port = 3389, RdpFullScreen = false, RdpWidth = 1280, RdpHeight = 720 };
        var rdp2 = new Connection { Name = "r2", Type = ConnectionType.Rdp, Port = 3389, RdpFullScreen = true, RdpWidth = 1920, RdpHeight = 1080 };

        var editor = MultiConnectionEditorViewModel.FromConnections([rdp1, rdp2]);
        Assert.True(editor.IsAllRdp);
        Assert.Null(editor.RdpFullScreen);
        Assert.True(editor.ShowRdpUseAllMonitors);
        Assert.True(editor.ShowRdpDimensions);

        editor.PortText = "70000";
        Assert.True(editor.HasPortValidationMessage);
        Assert.False(editor.CanApply);

        editor.PortText = "3390";
        Assert.False(editor.HasPortValidationMessage);
        Assert.True(editor.CanApply);

        editor.RdpWidthText = "0";
        Assert.True(editor.HasRdpDimensionsValidationMessage);
        Assert.False(editor.CanApply);

        editor.RdpWidthText = "1600";
        editor.RdpHeightText = "900";
        Assert.False(editor.HasRdpDimensionsValidationMessage);
        Assert.True(editor.CanApply);

        editor.ApplyToTargets();
        Assert.Equal(3390, rdp1.Port);
        Assert.Equal(3390, rdp2.Port);
        Assert.Equal(1600, rdp1.RdpWidth);
        Assert.Equal(1600, rdp2.RdpWidth);
        Assert.Equal(900, rdp1.RdpHeight);
        Assert.Equal(900, rdp2.RdpHeight);
        Assert.False(rdp1.RdpFullScreen);
        Assert.True(rdp2.RdpFullScreen);
    }

    [Fact]
    public void Jump_host_validation_checks_all_selected_targets_and_supports_clear()
    {
        var jump = new Connection { ConnectionId = "jump-id", Name = "bastion", Type = ConnectionType.Ssh, Host = "bastion.invalid", Username = "ops" };
        var cycle = new Connection { ConnectionId = "cycle-id", Name = "cycle", Type = ConnectionType.Ssh, Host = "cycle.invalid", JumpHostConnectionId = "target-2" };
        var target1 = new Connection { ConnectionId = "target-1", Name = "t1", Type = ConnectionType.Ssh, JumpHostConnectionId = "jump-id" };
        var target2 = new Connection { ConnectionId = "target-2", Name = "t2", Type = ConnectionType.Ssh, JumpHostConnectionId = "" };

        var editor = MultiConnectionEditorViewModel.FromConnections([target1, target2]);
        editor.SetJumpHostConnections(
        [
            new KeyValuePair<string, Connection>("bastion", jump),
            new KeyValuePair<string, Connection>("cycle", cycle),
            new KeyValuePair<string, Connection>("t1", target1),
            new KeyValuePair<string, Connection>("t2", target2),
        ]);

        Assert.True(editor.IsJumpHostMixed);
        Assert.True(editor.CanClearJumpHost);

        // Self-reference to any selected target is rejected.
        Assert.False(editor.TrySetJumpHostFromTree("t1"));
        Assert.True(editor.IsJumpHostMixed);

        // Indirect cycle through target-2 is rejected.
        Assert.False(editor.TrySetJumpHostFromTree("cycle"));
        Assert.True(editor.IsJumpHostMixed);

        // Valid jump host succeeds and applies to all targets.
        Assert.True(editor.TrySetJumpHostFromTree("bastion"));
        Assert.False(editor.IsJumpHostMixed);
        Assert.True(editor.IsJumpHostModified);
        Assert.Equal("jump-id", editor.JumpHostConnectionId);

        // Clear restores direct connection on all targets.
        editor.ClearJumpHost();
        Assert.True(editor.IsJumpHostModified);
        Assert.Equal("", editor.JumpHostConnectionId);
        editor.ApplyToTargets();
        Assert.Equal("", target1.JumpHostConnectionId);
        Assert.Equal("", target2.JumpHostConnectionId);
    }

    [Fact]
    public void Mixed_types_expose_only_common_applicable_sections()
    {
        var ssh = new Connection { Name = "s1", Type = ConnectionType.Ssh, Host = "10.0.0.1", Username = "alice" };
        var rdp = new Connection { Name = "r1", Type = ConnectionType.Rdp, Host = "10.0.0.2", Username = "alice" };
        var wsl = new Connection { Name = "w1", Type = ConnectionType.Wsl, Username = "alice" };

        var sshAndRdp = MultiConnectionEditorViewModel.FromConnections([ssh, rdp]);
        Assert.False(sshAndRdp.IsAllSsh);
        Assert.False(sshAndRdp.IsAllRdp);
        Assert.True(sshAndRdp.HasHostPort);
        Assert.True(sshAndRdp.HasPassword);
        Assert.False(sshAndRdp.SupportsScripts);

        var sshAndWsl = MultiConnectionEditorViewModel.FromConnections([ssh, wsl]);
        Assert.False(sshAndWsl.IsAllSsh);
        Assert.False(sshAndWsl.IsAllWsl);
        Assert.False(sshAndWsl.HasHostPort);
        Assert.False(sshAndWsl.HasPassword);
        Assert.True(sshAndWsl.SupportsScripts);
    }

    [Fact]
    public void Ssh_mfa_choices_distinguish_keep_unchanged_from_none()
    {
        var p1 = Guid.NewGuid();
        var p2 = Guid.NewGuid();
        var c1 = new Connection { Name = "s1", Type = ConnectionType.Ssh, SshMfaProfileId = p1 };
        var c2 = new Connection { Name = "s2", Type = ConnectionType.Ssh, SshMfaProfileId = p2 };

        var editor = MultiConnectionEditorViewModel.FromConnections([c1, c2]);
        editor.SetSshMfaProfileChoices([new(p1, "Prod MFA"), new(p2, "Dev MFA")]);

        Assert.True(editor.SelectedSshMfaProfile?.IsKeepUnchanged);
        Assert.False(editor.IsSshMfaModified);

        // Selecting "None" is distinct from keeping unchanged and clears MFA on both targets.
        var noneOption = editor.SshMfaProfileChoices.Single(o => !o.IsKeepUnchanged && o.ProfileId is null);
        editor.SelectedSshMfaProfile = noneOption;
        Assert.True(editor.IsSshMfaModified);

        editor.ApplyToTargets();
        Assert.Null(c1.SshMfaProfileId);
        Assert.Null(c2.SshMfaProfileId);
    }

    [Fact]
    public void ConnectionStore_SaveEdits_adopts_fingerprint_unless_external_change_landed()
    {
        var root = Path.Combine(Path.GetTempPath(), "jrm-save-edits-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConnectionStore(root);
            var c1 = new Connection { Name = "one", Host = "h1", Username = "u1" };
            var c2 = new Connection { Name = "two", Host = "h2", Username = "u2" };
            var p1 = store.Save(c1, root);
            var p2 = store.Save(c2, root);
            store.ReadTree();
            Assert.NotNull(store.KnownSignature);

            c1.Username = "shared";
            c2.Username = "shared";
            var adoptedBefore = store.OwnEditsAdoptedForDebug;
            var count = store.SaveEdits([(c1, p1), (c2, p2)]);

            Assert.Equal(2, count);
            Assert.Equal(adoptedBefore + 1, store.OwnEditsAdoptedForDebug);
            Assert.Equal(store.ComputeSignature(), store.KnownSignature);
            Assert.Equal("shared", store.Load(p1).Username);
            Assert.Equal("shared", store.Load(p2).Username);

            // An external file created before SaveEdits invalidates adoption so the watcher reloads.
            File.WriteAllText(Path.Combine(root, "outside.json"), """{"Name":"outside","Host":"ext"}""");
            c1.Notes = "after-external";
            c2.Notes = "after-external";
            var adoptedBeforeExternal = store.OwnEditsAdoptedForDebug;
            store.SaveEdits([(c1, p1), (c2, p2)]);
            Assert.Equal(adoptedBeforeExternal, store.OwnEditsAdoptedForDebug);
            Assert.Null(store.KnownSignature);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Debug_MCP_advertises_the_multi_connection_editor_probe() =>
        Assert.Contains(DebugMcpContract.BuildToolList(),
            tool => tool?["name"]?.GetValue<string>() == "multi_connection_editor_check");
}
