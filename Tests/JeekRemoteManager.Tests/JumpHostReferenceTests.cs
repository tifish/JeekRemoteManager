using JeekRemoteManager.Models;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;

namespace JeekRemoteManager.Tests;

public sealed class JumpHostReferenceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "jrm-jump-refs-" + Guid.NewGuid().ToString("N"));
    private readonly ConnectionStore _store;

    public JumpHostReferenceTests() => _store = new ConnectionStore(Path.Combine(_folder, "Connections"));
    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private Connection Jump(Connection target) => Assert.Single(SshDialer.ResolveJumpRoute(target, _store.TryLoadById));

    [Fact]
    public void Renames_moves_and_folder_renames_preserve_the_pinned_jump()
    {
        var jump = new Connection { Name = "jump", Host = "original.invalid" };
        var jumpPath = _store.Save(jump, _store.RootPath);
        var target = new Connection { Name = "target", JumpHostConnectionId = jump.ConnectionId };
        var targetPath = _store.Save(target, _store.RootPath);
        Assert.Equal(jump.ConnectionId, target.JumpHostConnectionId);
        Assert.DoesNotContain("\"JumpHost\":", File.ReadAllText(targetPath));

        jump.Name = "renamed";
        jumpPath = _store.Save(jump, _store.RootPath, jumpPath);
        Assert.Equal(jump.ConnectionId, Jump(target).ConnectionId);
        var group = _store.CreateFolder(_store.RootPath, "group");
        _store.MoveFileInto(jumpPath, group);
        Assert.Equal(jump.ConnectionId, Jump(target).ConnectionId);
        _store.RenameFolder(group, "moved-group");
        Assert.Equal(jump.ConnectionId, Jump(target).ConnectionId);

        _store.ReadTree();
        var reloaded = _store.Load(targetPath);
        Assert.Equal("moved-group/renamed", _store.TryGetTreePathById(reloaded.JumpHostConnectionId));
        Assert.Equal(jump.ConnectionId, reloaded.JumpHostConnectionId);
    }

    [Fact]
    public void External_moves_work_after_reopening_the_store_without_rewriting_dependents()
    {
        var jump = new Connection { Name = "jump", Host = "original.invalid" };
        var jumpPath = _store.Save(jump, _store.RootPath);
        var targetPath = Path.Combine(_store.RootPath, "target.json");
        _store.SaveInPlace(new Connection { Name = "target", JumpHostConnectionId = jump.ConnectionId, Notes = "keep-notes" }, targetPath);
        var targetText = File.ReadAllText(targetPath);
        var target = _store.Load(targetPath);
        Assert.Equal(jump.ConnectionId, target.JumpHostConnectionId);

        var folder = _store.CreateFolder(_store.RootPath, "external");
        File.Move(jumpPath, Path.Combine(folder, "external-name.json"));
        var freshStore = new ConnectionStore(_store.RootPath);
        var route = SshDialer.ResolveJumpRoute(target, freshStore.TryLoadById);
        Assert.Equal(jump.ConnectionId, Assert.Single(route).ConnectionId);
        freshStore.ReadTree();
        Assert.Equal("external/external-name", freshStore.TryGetTreePathById(target.JumpHostConnectionId));
        Assert.Equal(targetText, File.ReadAllText(targetPath));
        Assert.Equal("keep-notes", freshStore.Load(targetPath).Notes);
    }

    [Fact]
    public void Deleted_identity_never_falls_back_to_a_replacement_at_the_old_path()
    {
        var jump = new Connection { Name = "jump", Host = "original.invalid" };
        var jumpPath = _store.Save(jump, _store.RootPath);
        var target = new Connection { Name = "target", JumpHostConnectionId = jump.ConnectionId };
        var targetPath = _store.Save(target, _store.RootPath);
        File.Move(jumpPath, Path.Combine(_folder, "removed-jump.json"));
        var replacement = new Connection { Name = "jump", Host = "replacement.invalid" };
        _store.Save(replacement, _store.RootPath);
        Assert.NotEqual(jump.ConnectionId, replacement.ConnectionId);
        _store.ReadTree();

        Assert.Equal(jump.ConnectionId, _store.Load(targetPath).JumpHostConnectionId);
        Assert.Contains("not a saved connection", Assert.Throws<InvalidOperationException>(() => Jump(target)).Message);
    }

    [Fact]
    public void Copying_does_not_steal_the_original_identity_and_duplicate_ids_fail_explicitly()
    {
        var jump = new Connection { Name = "jump" };
        var path = _store.Save(jump, _store.RootPath);
        var target = new Connection { Name = "target", JumpHostConnectionId = jump.ConnectionId };
        _store.Save(target, _store.RootPath);
        var copy = _store.CopyFileInto(path, _store.RootPath);
        Assert.NotEqual(jump.ConnectionId, _store.Load(copy).ConnectionId);
        Assert.Equal(jump.ConnectionId, Jump(target).ConnectionId);

        File.Copy(path, Path.Combine(_store.RootPath, "manual-copy.json"));
        Assert.Contains("ambiguous", Assert.Throws<InvalidOperationException>(() => Jump(target)).Message);
    }

    [Fact]
    public void Editor_displays_the_current_path_by_id_and_clearing_removes_the_identity()
    {
        var jump = new Connection { ConnectionId = Guid.NewGuid().ToString(), Host = "original.invalid" };
        var editor = ConnectionEditorViewModel.FromConnection(new Connection
        {
            JumpHostConnectionId = jump.ConnectionId,
        });
        editor.SetJumpHostConnections(new Dictionary<string, Connection> { ["new/path"] = jump });
        Assert.StartsWith("new/path", editor.JumpHostDisplay);
        Assert.False(editor.HasJumpHostValidationMessage);
        editor.ClearJumpHost();
        Assert.False(editor.HasJumpHost);
        Assert.Empty(editor.JumpHostConnectionId);
    }
}
