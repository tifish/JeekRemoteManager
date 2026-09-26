using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using JeekRemoteManager.Models;
using JeekTools;

namespace JeekRemoteManager.Services;

/// <summary>
/// One folder of the connections tree, already read from disk: sub-folders and the
/// parsed connections inside it. Reading a whole tree into these off the UI thread is
/// what keeps a rebuild from freezing the window — see <see cref="ConnectionStore.ReadTree"/>.
/// </summary>
public sealed record ConnectionFolderSnapshot(
    string Path,
    IReadOnlyList<ConnectionFolderSnapshot> Folders,
    IReadOnlyList<ConnectionFileSnapshot> Connections);

/// <summary>One connection file and the connection parsed out of it.</summary>
public sealed record ConnectionFileSnapshot(string Path, Connection Connection);

/// <summary>
/// Persists connections on disk as one file per connection, organised into a
/// folder tree. The root lives under the configured Config\Connections folder.
/// </summary>
public class ConnectionStore
{
    public const string FileExtension = ".json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public ConnectionStore(string? rootPath = null)
    {
        RootPath = rootPath ?? SettingsService.ResolveConnectionsRoot(StorageLocation.UserDirectory);
        BastionProfiles = new BastionLoginProfileStore(RootPath);
        Directory.CreateDirectory(RootPath);
    }

    /// <summary>Absolute path to the top-level connections folder.</summary>
    public string RootPath { get; private set; }

    public BastionLoginProfileStore BastionProfiles { get; }

    /// <summary>
    /// Fingerprint (<see cref="ComputeSignature"/>) of the on-disk state the app's tree
    /// reflects: set by every <see cref="ReadTree"/> and carried forward across the
    /// store's own writes. The file watcher compares the live fingerprint with this one
    /// instead of ignoring every event for a while after an own write — that window also
    /// swallowed any external change (a sync client, another instance) landing inside it,
    /// and the tree then stayed stale until something else changed. Null means unknown:
    /// the next watcher event always reloads.
    /// </summary>
    public string? KnownSignature { get; private set; }

    private readonly object _ownWriteGate = new();
    private int _ownWriteDepth;
    private bool _ownWriteInSync;

    /// <summary>
    /// Runs one of the store's own writes and carries <see cref="KnownSignature"/> across
    /// it. The fingerprint is only carried when the disk still matched it before the write;
    /// otherwise something external changed first, and adopting the post-write fingerprint
    /// would hide that change from the watcher. Nested writes (a batch) check once.
    /// </summary>
    private T OwnWrite<T>(Func<T> write)
    {
        lock (_ownWriteGate)
        {
            if (_ownWriteDepth++ == 0)
            {
                var known = KnownSignature;
                _ownWriteInSync = known is not null && known == ComputeSignature();
            }

            var completed = false;
            try
            {
                var result = write();
                completed = true;
                return result;
            }
            finally
            {
                // A write that threw may have left the disk half-changed; let the next
                // watcher event reload rather than trust a fingerprint of that state.
                if (--_ownWriteDepth == 0)
                    KnownSignature = completed && _ownWriteInSync ? ComputeSignature() : null;
                else if (!completed)
                    _ownWriteInSync = false;
            }
        }
    }

    private void OwnWrite(Action write) => OwnWrite(() =>
    {
        write();
        return true;
    });

    /// <summary>
    /// Runs several writes as one, so a sweep over every connection (re-encryption after a
    /// master-password change) fingerprints the tree twice instead of twice per file.
    /// </summary>
    public void RunBatch(Action writes) => OwnWrite(writes);

    /// <summary>
    /// Hash of every file and folder under the root: relative path, size and last-write
    /// time for files, just the path for folders (their times change with any child).
    /// Cheap next to <see cref="ReadTree"/> — metadata only, nothing is opened.
    /// </summary>
    public string ComputeSignature()
    {
        var root = RootPath;
        if (!Directory.Exists(root))
            return "";

        var entries = new List<string>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
        };
        foreach (var info in new DirectoryInfo(root).EnumerateFileSystemInfos("*", options))
        {
            var relative = Path.GetRelativePath(root, info.FullName);
            entries.Add(info is FileInfo file
                ? $"{relative}|{file.Length}|{file.LastWriteTimeUtc.Ticks}"
                : $"{relative}|dir");
        }

        entries.Sort(StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in entries)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(entry));
            hash.AppendData("\n"u8);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>Switches the store to a different root folder, creating it if needed.</summary>
    public void SetRoot(string newRoot)
    {
        using var lease = SharedDataFile.Acquire(newRoot);
        RootPath = newRoot;
        Directory.CreateDirectory(RootPath);
        BastionProfiles.SetRoot(RootPath);
        KnownSignature = null;
    }

    // --- Reading the tree ---

    /// <summary>Returns the full paths of immediate sub-folders, sorted by name.</summary>
    public IReadOnlyList<string> GetSubFolders(string folderPath) =>
        Directory.Exists(folderPath)
            ? Directory.GetDirectories(folderPath)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : Array.Empty<string>();

    /// <summary>Returns the full paths of connection files in a folder, sorted by name.</summary>
    public IReadOnlyList<string> GetConnectionFiles(string folderPath) =>
        Directory.Exists(folderPath)
            ? Directory.GetFiles(folderPath, "*" + FileExtension)
                .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : Array.Empty<string>();

    /// <summary>Returns the full paths of every connection file under the root, recursively.</summary>
    public IReadOnlyList<string> AllConnectionFiles() =>
        Directory.Exists(RootPath)
            ? Directory.GetFiles(RootPath, "*" + FileExtension, SearchOption.AllDirectories)
            : Array.Empty<string>();

    /// <summary>
    /// Rewrites a connection back to its existing file without renaming or moving it.
    /// Used by re-encryption sweeps that only change the EncryptedPassword field.
    /// </summary>
    public void SaveInPlace(Connection connection, string filePath) =>
        OwnWrite(() => WriteInPlace(connection, filePath));

    private void WriteInPlace(Connection connection, string filePath)
    {
        using var lease = SharedDataFile.Acquire(RootPath);
        EnsureConnectionId(connection);
        var json = JsonSerializer.Serialize(connection, JsonOptions);
        SharedDataFile.WriteAllTextAtomic(filePath, json);
    }

    /// <summary>
    /// The stored password blobs, for validating the master password at startup.
    /// Deliberately not <see cref="Load"/>: that also resolves bastion profiles and
    /// rewrites files that are missing an id, which is wasted work moments before the
    /// tree loads every file anyway. Files with no blob in their text are never parsed.
    /// </summary>
    public IEnumerable<string> EnumerateStoredPasswordBlobs()
    {
        foreach (var file in AllConnectionFiles())
        {
            Connection? connection;
            try
            {
                var json = File.ReadAllText(file);
                if (!json.Contains(MasterKeyService.BlobPrefix, StringComparison.Ordinal))
                    continue;

                connection = JsonSerializer.Deserialize<Connection>(json, JsonOptions);
            }
            catch
            {
                // Unreadable or malformed files are skipped, as the tree loader does.
                continue;
            }

            if (!string.IsNullOrEmpty(connection?.EncryptedPassword))
                yield return connection.EncryptedPassword;
        }
    }

    /// <summary>
    /// Reads the whole tree — every folder and every connection file — in one pass.
    /// Safe to call from a worker thread, and meant to be: doing this work inline meant
    /// a rebuild read and deserialized every connection on the UI thread, which is very
    /// visible once the folder lives on a network or file-synced drive.
    /// Unreadable files are skipped, exactly as loading them one at a time did.
    /// </summary>
    public ConnectionFolderSnapshot ReadTree()
    {
        // Fingerprint on both sides of the read: if something changed while it ran, the
        // snapshot may predate that change, so leave the fingerprint unknown and let the
        // watcher event for it reload again.
        var before = ComputeSignature();
        var snapshot = ReadFolder(RootPath);
        var after = ComputeSignature();
        KnownSignature = before == after ? after : null;
        PruneTextCache(snapshot);
        return snapshot;
    }

    /// <summary>Drops cached text for files the latest read no longer saw.</summary>
    private void PruneTextCache(ConnectionFolderSnapshot snapshot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Collect(ConnectionFolderSnapshot folder)
        {
            foreach (var connection in folder.Connections)
                seen.Add(connection.Path);
            foreach (var child in folder.Folders)
                Collect(child);
        }

        Collect(snapshot);
        lock (_textCache)
        {
            foreach (var path in _textCache.Keys.Where(path => !seen.Contains(path)).ToList())
                _textCache.Remove(path);
        }
    }

    private ConnectionFolderSnapshot ReadFolder(string folderPath)
    {
        var folders = new List<ConnectionFolderSnapshot>();
        foreach (var directory in GetSubFolders(folderPath))
            folders.Add(ReadFolder(directory));

        var connections = new List<ConnectionFileSnapshot>();
        if (!Directory.Exists(folderPath))
            return new ConnectionFolderSnapshot(folderPath, folders, connections);

        // Enumerating FileInfo returns size and write time with the directory listing
        // itself, so deciding which files are unchanged costs no extra round-trip per file.
        var files = new DirectoryInfo(folderPath)
            .EnumerateFiles("*" + FileExtension)
            .OrderBy(file => Path.GetFileNameWithoutExtension(file.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            try
            {
                connections.Add(new ConnectionFileSnapshot(
                    file.FullName,
                    LoadFromText(file.FullName, ReadConnectionText(file))));
            }
            catch
            {
                // Unreadable or malformed file; leave it out of the tree.
            }
        }

        return new ConnectionFolderSnapshot(folderPath, folders, connections);
    }

    /// <summary>
    /// Text of each connection file as last read, keyed by path and stamped with the size
    /// and write time it had. Every tree action (rename, move, paste, save) rebuilds the
    /// whole tree synchronously so the new node can be selected at once; reading every file
    /// again for that made each click cost a full pass over the folder, which is very visible
    /// on a network or file-synced drive. Unchanged files now come from here, and only the
    /// changed ones are read. The text is cached, not the parsed object: every read still
    /// hands out fresh <see cref="Connection"/> instances, since editors mutate them.
    /// </summary>
    private readonly Dictionary<string, (long Length, long WriteTicks, string Text)> _textCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Connection files actually read from disk (cache misses), for the Debug MCP.</summary>
    internal long ConnectionFileReadsForDebug => Interlocked.Read(ref _connectionFileReads);

    private long _connectionFileReads;

    private string ReadConnectionText(FileInfo file)
    {
        var length = file.Length;
        var ticks = file.LastWriteTimeUtc.Ticks;
        lock (_textCache)
        {
            if (_textCache.TryGetValue(file.FullName, out var cached)
                && cached.Length == length
                && cached.WriteTicks == ticks)
            {
                return cached.Text;
            }
        }

        var text = File.ReadAllText(file.FullName);
        Interlocked.Increment(ref _connectionFileReads);
        lock (_textCache)
            _textCache[file.FullName] = (length, ticks, text);
        return text;
    }

    /// <summary>
    /// Loads a saved connection by its tree path ("vps/bwg", the form MCP tools and jump
    /// hosts use), or null when there is none. Paths that would leave the root are refused.
    /// </summary>
    public Connection? TryLoadByTreePath(string treePath)
    {
        var relative = treePath.Trim().Replace('\\', '/').Trim('/');
        if (relative.Length == 0)
            return null;
        if (relative.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase))
            relative = relative[..^FileExtension.Length];

        var file = Path.GetFullPath(Path.Combine(RootPath, relative.Replace('/', Path.DirectorySeparatorChar) + FileExtension));
        if (!IsSameOrInside(RootPath, file) || !File.Exists(file))
            return null;

        try
        {
            return Load(file);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Loads a connection from a file.</summary>
    public Connection Load(string filePath) => LoadFromText(filePath, File.ReadAllText(filePath));

    private Connection LoadFromText(string filePath, string json)
    {
        var connection = JsonSerializer.Deserialize<Connection>(json, JsonOptions)
                         ?? new Connection();

        // Keep the in-memory name in sync with the file name, which is authoritative.
        connection.Name = Path.GetFileNameWithoutExtension(filePath);
        // Not an own-write for the fingerprint: this runs inside ReadTree, which
        // fingerprints the tree after reading it anyway.
        if (EnsureConnectionId(connection))
            WriteInPlace(connection, filePath);
        BastionProfiles.Resolve(connection);
        return connection;
    }

    // --- Mutating the tree ---

    /// <summary>
    /// Saves a connection into <paramref name="folderPath"/>. The file name is
    /// derived from <see cref="Connection.Name"/>. If <paramref name="previousFilePath"/>
    /// is given and differs from the new path, the old file is removed (rename).
    /// Returns the path the connection was written to.
    /// </summary>
    public string Save(Connection connection, string folderPath, string? previousFilePath = null) =>
        OwnWrite(() => SaveCore(connection, folderPath, previousFilePath));

    private string SaveCore(Connection connection, string folderPath, string? previousFilePath)
    {
        using var lease = SharedDataFile.Acquire(RootPath);
        Directory.CreateDirectory(folderPath);
        EnsureConnectionId(connection);

        var targetName = SanitizeName(connection.Name);
        var targetPath = Path.Combine(folderPath, targetName + FileExtension);

        // If renaming to a name that collides with a different existing file, disambiguate.
        if (!PathsEqual(targetPath, previousFilePath) && File.Exists(targetPath))
            targetPath = UniqueFilePath(folderPath, targetName);

        connection.Name = Path.GetFileNameWithoutExtension(targetPath);

        var json = JsonSerializer.Serialize(connection, JsonOptions);
        SharedDataFile.WriteAllTextAtomic(targetPath, json);

        if (!string.IsNullOrEmpty(previousFilePath)
            && !PathsEqual(targetPath, previousFilePath)
            && File.Exists(previousFilePath))
        {
            File.Delete(previousFilePath);
        }

        return targetPath;
    }

    /// <summary>Moves a connection file to the Recycle Bin, so the delete can be undone.</summary>
    public void DeleteFile(string filePath)
    {
        using var lease = SharedDataFile.Acquire(RootPath);
        if (File.Exists(filePath))
            OwnWrite(() => RecycleBin.Send(filePath));
    }

    /// <summary>Moves a folder and everything under it to the Recycle Bin.</summary>
    public void DeleteFolder(string folderPath)
    {
        using var lease = SharedDataFile.Acquire(RootPath);
        if (Directory.Exists(folderPath))
            OwnWrite(() => RecycleBin.Send(folderPath));
    }

    /// <summary>Creates a new sub-folder with a unique name; returns its path.</summary>
    public string CreateFolder(string parentPath, string desiredName)
    {
        using var lease = SharedDataFile.Acquire(RootPath);
        return OwnWrite(() =>
        {
            Directory.CreateDirectory(parentPath);
            var path = UniqueFolderPath(parentPath, SanitizeName(desiredName));
            Directory.CreateDirectory(path);
            return path;
        });
    }

    /// <summary>Renames a folder; returns the new path.</summary>
    public string RenameFolder(string folderPath, string newName)
    {
        using var lease = SharedDataFile.Acquire(RootPath);
        var parent = Path.GetDirectoryName(folderPath)!;
        var target = Path.Combine(parent, SanitizeName(newName));
        if (PathsEqual(target, folderPath))
            return folderPath;

        if (Directory.Exists(target))
            target = UniqueFolderPath(parent, SanitizeName(newName));

        OwnWrite(() => Directory.Move(folderPath, target));
        return target;
    }

    // --- Copy / move (for clipboard operations) ---

    /// <summary>Copies a connection file into a folder, giving it a unique name. Returns the new path.</summary>
    public string CopyFileInto(string filePath, string targetFolder, bool includeSshScriptBindings = true)
        => CopyFileIntoCore(filePath, targetFolder, includeSshScriptBindings, createNewConnectionId: true);

    private string CopyFileIntoCore(
        string filePath,
        string targetFolder,
        bool includeSshScriptBindings,
        bool createNewConnectionId)
    {
        using var lease = SharedDataFile.Acquire(RootPath);
        return OwnWrite(() =>
        {
            Directory.CreateDirectory(targetFolder);
            var baseName = Path.GetFileNameWithoutExtension(filePath);
            var target = UniqueFilePath(targetFolder, baseName);
            CopyConnectionFile(filePath, target, includeSshScriptBindings, createNewConnectionId);
            return target;
        });
    }

    /// <summary>
    /// Moves a connection file into a folder. If the destination folder is the
    /// same as the source folder this is a no-op and the original path is returned.
    /// Returns the resulting path.
    /// </summary>
    public string MoveFileInto(string filePath, string targetFolder)
    {
        using var lease = SharedDataFile.Acquire(RootPath);
        var sourceFolder = Path.GetDirectoryName(filePath);
        if (PathsEqual(sourceFolder, targetFolder))
            return filePath;

        return OwnWrite(() =>
        {
            Directory.CreateDirectory(targetFolder);
            var baseName = Path.GetFileNameWithoutExtension(filePath);
            var target = UniqueFilePath(targetFolder, baseName);
            File.Move(filePath, target);
            return target;
        });
    }

    /// <summary>Recursively copies a folder into a parent folder, with a unique name. Returns the new path.</summary>
    public string CopyFolderInto(string folderPath, string targetParent, bool includeSshScriptBindings = true)
        => CopyFolderIntoCore(folderPath, targetParent, includeSshScriptBindings, createNewConnectionIds: true);

    private string CopyFolderIntoCore(
        string folderPath,
        string targetParent,
        bool includeSshScriptBindings,
        bool createNewConnectionIds)
    {
        using var lease = SharedDataFile.Acquire(RootPath);
        return OwnWrite(() =>
        {
            Directory.CreateDirectory(targetParent);
            var name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar));
            var target = UniqueFolderPath(targetParent, name);
            CopyDirectory(folderPath, target, includeSshScriptBindings, createNewConnectionIds);
            return target;
        });
    }

    /// <summary>
    /// Moves a folder into a parent folder. No-op if the parent is unchanged.
    /// Returns the resulting path.
    /// </summary>
    public string MoveFolderInto(string folderPath, string targetParent)
    {
        using var lease = SharedDataFile.Acquire(RootPath);
        var currentParent = Path.GetDirectoryName(folderPath.TrimEnd(Path.DirectorySeparatorChar));
        if (PathsEqual(currentParent, targetParent))
            return folderPath;

        return OwnWrite(() =>
        {
            Directory.CreateDirectory(targetParent);
            var name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar));
            var target = UniqueFolderPath(targetParent, name);
            Directory.Move(folderPath, target);
            return target;
        });
    }

    /// <summary>
    /// True if <paramref name="candidate"/> is <paramref name="folder"/> itself or
    /// nested inside it. Used to stop a folder being pasted into its own subtree.
    /// </summary>
    public static bool IsSameOrInside(string folder, string candidate)
    {
        var a = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        var b = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);

        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return true;

        return b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Copies every file and sub-folder from one folder into another (used for migration).</summary>
    public void CopyTreeContents(string sourceRoot, string destRoot)
    {
        using var lease = SharedDataFile.AcquireMany(RootPath, sourceRoot, destRoot);
        // Refuse to copy a tree into itself or its own subtree (would recurse forever).
        if (!Directory.Exists(sourceRoot) || IsSameOrInside(sourceRoot, destRoot))
            return;

        Directory.CreateDirectory(destRoot);

        foreach (var file in Directory.GetFiles(sourceRoot, "*" + FileExtension))
            CopyFileIntoCore(file, destRoot, includeSshScriptBindings: true, createNewConnectionId: false);

        foreach (var dir in Directory.GetDirectories(sourceRoot))
            CopyFolderIntoCore(dir, destRoot, includeSshScriptBindings: true, createNewConnectionIds: false);
    }

    /// <summary>
    /// Moves every file and sub-folder from one folder into another, then removes
    /// the now-empty source folder. Used to migrate between storage locations
    /// without keeping the old location.
    /// </summary>
    public void MoveTreeContents(string sourceRoot, string destRoot)
    {
        using var lease = SharedDataFile.AcquireMany(RootPath, sourceRoot, destRoot);
        // Refuse to move a tree into itself or its own subtree.
        if (!Directory.Exists(sourceRoot) || IsSameOrInside(sourceRoot, destRoot))
            return;

        Directory.CreateDirectory(destRoot);

        foreach (var file in Directory.GetFiles(sourceRoot, "*" + FileExtension))
            MoveFileInto(file, destRoot);

        foreach (var dir in Directory.GetDirectories(sourceRoot))
            MoveFolderInto(dir, destRoot);
    }

    private void CopyDirectory(
        string sourceDir,
        string destDir,
        bool includeSshScriptBindings,
        bool createNewConnectionIds)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var target = Path.Combine(destDir, Path.GetFileName(file));
            if (string.Equals(Path.GetExtension(target), FileExtension, StringComparison.OrdinalIgnoreCase))
                CopyConnectionFile(file, target, includeSshScriptBindings, createNewConnectionIds);
            else
                SharedDataFile.CopyAtomic(file, target);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(
                dir,
                Path.Combine(destDir, Path.GetFileName(dir)),
                includeSshScriptBindings,
                createNewConnectionIds);
    }

    private void CopyConnectionFile(
        string sourcePath,
        string targetPath,
        bool includeSshScriptBindings,
        bool createNewConnectionId)
    {
        // Loading first also persists an id into a legacy source file. Storage-location
        // migration can then copy that same id, while an ordinary duplicate replaces it.
        var connection = Load(sourcePath);
        connection.Name = Path.GetFileNameWithoutExtension(targetPath);
        if (createNewConnectionId)
            connection.ConnectionId = NewConnectionId();

        if (!includeSshScriptBindings && connection.IsSsh)
            connection.ScriptBindings.Clear();

        SaveInPlace(connection, targetPath);
    }

    // --- Helpers ---

    private static bool PathsEqual(string? a, string? b) =>
        string.Equals(
            a is null ? null : Path.GetFullPath(a),
            b is null ? null : Path.GetFullPath(b),
            StringComparison.OrdinalIgnoreCase);

    private static bool EnsureConnectionId(Connection connection)
    {
        if (Guid.TryParse(connection.ConnectionId, out _))
            return false;

        connection.ConnectionId = NewConnectionId();
        return true;
    }

    private static string NewConnectionId() => Guid.NewGuid().ToString("D");

    private string UniqueFilePath(string folderPath, string baseName)
    {
        var candidate = Path.Combine(folderPath, baseName + FileExtension);
        var i = 2;
        while (File.Exists(candidate))
            candidate = Path.Combine(folderPath, $"{baseName} ({i++}){FileExtension}");
        return candidate;
    }

    private static string UniqueFolderPath(string parentPath, string baseName)
    {
        var candidate = Path.Combine(parentPath, baseName);
        var i = 2;
        while (Directory.Exists(candidate))
            candidate = Path.Combine(parentPath, $"{baseName} ({i++})");
        return candidate;
    }

    /// <summary>
    /// The invalid set is fixed, so hoist the membership test into a lookup instead of
    /// scanning the 41-element array for every character of every name. Every rename,
    /// save, copy and folder operation runs through here.
    /// </summary>
    private static readonly SearchValues<char> InvalidFileNameChars =
        SearchValues.Create(Path.GetInvalidFileNameChars());

    /// <summary>Strips characters that are invalid in Windows file names.</summary>
    public static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Unnamed";

        // Most names are already clean, so avoid building a new string for them.
        if (!name.AsSpan().ContainsAny(InvalidFileNameChars))
        {
            var trimmedOnly = name.Trim();
            return trimmedOnly.Length == 0 ? "Unnamed" : trimmedOnly;
        }

        var buffer = new char[name.Length];
        for (var i = 0; i < name.Length; i++)
            buffer[i] = InvalidFileNameChars.Contains(name[i]) ? '_' : name[i];

        var cleaned = new string(buffer).Trim();
        return cleaned.Length == 0 ? "Unnamed" : cleaned;
    }
}
