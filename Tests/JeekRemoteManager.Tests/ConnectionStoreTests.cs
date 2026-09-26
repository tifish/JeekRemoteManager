using JeekRemoteManager.Models;
using JeekRemoteManager.Services;

namespace JeekRemoteManager.Tests;

public sealed class ConnectionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jrm-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ConnectionStore _store;

    public ConnectionStoreTests() => _store = new ConnectionStore(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private static IEnumerable<ConnectionFileSnapshot> All(ConnectionFolderSnapshot folder) =>
        folder.Connections.Concat(folder.Folders.SelectMany(All));

    [Fact]
    public void Only_a_tree_read_confirms_the_state_after_own_writes()
    {
        _store.ReadTree();
        var folder = _store.CreateFolder(_root, "group");
        _store.Save(new Connection { Name = "a", Host = "a.invalid" }, folder);

        Assert.Null(_store.KnownSignature);
        _store.ReadTree();
        Assert.NotNull(_store.KnownSignature);
        Assert.Equal(_store.ComputeSignature(), _store.KnownSignature);
    }

    [Fact]
    public void An_external_change_before_an_own_write_is_not_claimed()
    {
        _store.ReadTree();
        File.WriteAllText(Path.Combine(_root, "external.json"), "{\"Host\":\"x\"}");

        _store.Save(new Connection { Name = "own", Host = "own.invalid" }, _root);

        // Unknown, so the watcher reloads and shows the external file.
        Assert.Null(_store.KnownSignature);
    }

    [Fact]
    public void External_changes_during_a_batch_are_not_claimed_by_its_own_writes()
    {
        var path = _store.Save(new Connection { Name = "own", Host = "a.invalid" }, _root);
        _store.ReadTree();
        _store.RunBatch(() =>
        {
            _store.SaveInPlace(new Connection { Name = "own", Host = "b.invalid" }, path);
            // An independent writer lands between two writes in a master-password sweep.
            Task.Run(() => File.WriteAllText(Path.Combine(_root, "external.json"),
                "{\"ConnectionId\":\"11111111-1111-1111-1111-111111111111\",\"Host\":\"outside.invalid\"}"))
                .GetAwaiter().GetResult();
            _store.SaveInPlace(new Connection { Name = "own", Host = "c.invalid" }, path);
        });

        Assert.Null(_store.KnownSignature);
        Assert.Contains(All(_store.ReadTree()), file => file.Connection.Host == "outside.invalid");
        Assert.Equal(_store.ComputeSignature(), _store.KnownSignature);
    }

    [Fact]
    public void Repeat_reads_reuse_unchanged_files()
    {
        for (var i = 0; i < 4; i++)
            _store.Save(new Connection { Name = $"c{i}", Host = $"h{i}.invalid" }, _root);

        var reads = _store.ConnectionFileReadsForDebug;
        var first = _store.ReadTree();
        Assert.Equal(4, _store.ConnectionFileReadsForDebug - reads);

        reads = _store.ConnectionFileReadsForDebug;
        var second = _store.ReadTree();
        Assert.Equal(0, _store.ConnectionFileReadsForDebug - reads);
        Assert.NotSame(All(first).First().Connection, All(second).First().Connection);

        var path = All(second).Single(c => c.Connection.Name == "c1").Path;
        File.WriteAllText(path, File.ReadAllText(path).Replace("h1.invalid", "changed.invalid"));
        reads = _store.ConnectionFileReadsForDebug;
        var third = _store.ReadTree();
        Assert.Equal(1, _store.ConnectionFileReadsForDebug - reads);
        Assert.Equal("changed.invalid", All(third).Single(c => c.Connection.Name == "c1").Connection.Host);
    }

    [Fact]
    public void Tree_path_lookup_stays_inside_the_root()
    {
        var folder = _store.CreateFolder(_root, "vps");
        _store.Save(new Connection { Name = "bwg", Host = "bwg.invalid" }, folder);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_root)!, "outside-" + Path.GetFileName(_root) + ".json"), "{}");

        Assert.Equal("bwg.invalid", _store.TryLoadByTreePath("vps/bwg")?.Host);
        Assert.Equal("bwg.invalid", _store.TryLoadByTreePath("vps\\bwg.json")?.Host);
        Assert.Null(_store.TryLoadByTreePath("vps/missing"));
        Assert.Null(_store.TryLoadByTreePath("../outside-" + Path.GetFileName(_root)));
    }
}
