using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.Services;

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

        Directory.CreateDirectory(RootPath);
    }

    /// <summary>Absolute path to the top-level connections folder.</summary>
    public string RootPath { get; private set; }

    /// <summary>
    /// <see cref="Environment.TickCount64"/> of the last write this store made to
    /// disk. A file-system watcher can compare against this to tell the app's own
    /// writes apart from external changes.
    /// </summary>
    public long LastWriteTick { get; private set; }

    private void Touch() => LastWriteTick = Environment.TickCount64;

    /// <summary>Switches the store to a different root folder, creating it if needed.</summary>
    public void SetRoot(string newRoot)
    {
        RootPath = newRoot;
        Directory.CreateDirectory(RootPath);
        Touch();
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
    public void SaveInPlace(Connection connection, string filePath)
    {
        var json = JsonSerializer.Serialize(connection, JsonOptions);
        Touch();
        File.WriteAllText(filePath, json);
        Touch();
    }

    /// <summary>Loads a connection from a file.</summary>
    public Connection Load(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var connection = JsonSerializer.Deserialize<Connection>(json, JsonOptions)
                         ?? new Connection();

        // Keep the in-memory name in sync with the file name, which is authoritative.
        connection.Name = Path.GetFileNameWithoutExtension(filePath);
        return connection;
    }

    // --- Mutating the tree ---

    /// <summary>
    /// Saves a connection into <paramref name="folderPath"/>. The file name is
    /// derived from <see cref="Connection.Name"/>. If <paramref name="previousFilePath"/>
    /// is given and differs from the new path, the old file is removed (rename).
    /// Returns the path the connection was written to.
    /// </summary>
    public string Save(Connection connection, string folderPath, string? previousFilePath = null)
    {
        Touch();
        Directory.CreateDirectory(folderPath);

        var targetName = SanitizeName(connection.Name);
        var targetPath = Path.Combine(folderPath, targetName + FileExtension);

        // If renaming to a name that collides with a different existing file, disambiguate.
        if (!PathsEqual(targetPath, previousFilePath) && File.Exists(targetPath))
            targetPath = UniqueFilePath(folderPath, targetName);

        connection.Name = Path.GetFileNameWithoutExtension(targetPath);

        var json = JsonSerializer.Serialize(connection, JsonOptions);
        File.WriteAllText(targetPath, json);

        if (!string.IsNullOrEmpty(previousFilePath)
            && !PathsEqual(targetPath, previousFilePath)
            && File.Exists(previousFilePath))
        {
            File.Delete(previousFilePath);
        }

        Touch();
        return targetPath;
    }

    public void DeleteFile(string filePath)
    {
        if (File.Exists(filePath))
        {
            Touch();
            File.Delete(filePath);
            Touch();
        }
    }

    public void DeleteFolder(string folderPath)
    {
        if (Directory.Exists(folderPath))
        {
            Touch();
            Directory.Delete(folderPath, recursive: true);
            Touch();
        }
    }

    /// <summary>Creates a new sub-folder with a unique name; returns its path.</summary>
    public string CreateFolder(string parentPath, string desiredName)
    {
        Touch();
        Directory.CreateDirectory(parentPath);
        var path = UniqueFolderPath(parentPath, SanitizeName(desiredName));
        Directory.CreateDirectory(path);
        Touch();
        return path;
    }

    /// <summary>Renames a folder; returns the new path.</summary>
    public string RenameFolder(string folderPath, string newName)
    {
        var parent = Path.GetDirectoryName(folderPath)!;
        var target = Path.Combine(parent, SanitizeName(newName));
        if (PathsEqual(target, folderPath))
            return folderPath;

        if (Directory.Exists(target))
            target = UniqueFolderPath(parent, SanitizeName(newName));

        Touch();
        Directory.Move(folderPath, target);
        Touch();
        return target;
    }

    // --- Copy / move (for clipboard operations) ---

    /// <summary>Copies a connection file into a folder, giving it a unique name. Returns the new path.</summary>
    public string CopyFileInto(string filePath, string targetFolder, bool includeSshScriptBindings = true)
    {
        Touch();
        Directory.CreateDirectory(targetFolder);
        var baseName = Path.GetFileNameWithoutExtension(filePath);
        var target = UniqueFilePath(targetFolder, baseName);
        File.Copy(filePath, target);
        if (!includeSshScriptBindings)
            RemoveSshScriptBindings(target);
        Touch();
        return target;
    }

    /// <summary>
    /// Moves a connection file into a folder. If the destination folder is the
    /// same as the source folder this is a no-op and the original path is returned.
    /// Returns the resulting path.
    /// </summary>
    public string MoveFileInto(string filePath, string targetFolder)
    {
        var sourceFolder = Path.GetDirectoryName(filePath);
        if (PathsEqual(sourceFolder, targetFolder))
            return filePath;

        Touch();
        Directory.CreateDirectory(targetFolder);
        var baseName = Path.GetFileNameWithoutExtension(filePath);
        var target = UniqueFilePath(targetFolder, baseName);
        File.Move(filePath, target);
        Touch();
        return target;
    }

    /// <summary>Recursively copies a folder into a parent folder, with a unique name. Returns the new path.</summary>
    public string CopyFolderInto(string folderPath, string targetParent, bool includeSshScriptBindings = true)
    {
        Touch();
        Directory.CreateDirectory(targetParent);
        var name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar));
        var target = UniqueFolderPath(targetParent, name);
        CopyDirectory(folderPath, target, includeSshScriptBindings);
        Touch();
        return target;
    }

    /// <summary>
    /// Moves a folder into a parent folder. No-op if the parent is unchanged.
    /// Returns the resulting path.
    /// </summary>
    public string MoveFolderInto(string folderPath, string targetParent)
    {
        var currentParent = Path.GetDirectoryName(folderPath.TrimEnd(Path.DirectorySeparatorChar));
        if (PathsEqual(currentParent, targetParent))
            return folderPath;

        Touch();
        Directory.CreateDirectory(targetParent);
        var name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar));
        var target = UniqueFolderPath(targetParent, name);
        Directory.Move(folderPath, target);
        Touch();
        return target;
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
        // Refuse to copy a tree into itself or its own subtree (would recurse forever).
        if (!Directory.Exists(sourceRoot) || IsSameOrInside(sourceRoot, destRoot))
            return;

        Directory.CreateDirectory(destRoot);

        foreach (var file in Directory.GetFiles(sourceRoot, "*" + FileExtension))
            CopyFileInto(file, destRoot);

        foreach (var dir in Directory.GetDirectories(sourceRoot))
            CopyFolderInto(dir, destRoot);
    }

    /// <summary>
    /// Moves every file and sub-folder from one folder into another, then removes
    /// the now-empty source folder. Used to migrate between storage locations
    /// without keeping the old location.
    /// </summary>
    public void MoveTreeContents(string sourceRoot, string destRoot)
    {
        // Refuse to move a tree into itself or its own subtree.
        if (!Directory.Exists(sourceRoot) || IsSameOrInside(sourceRoot, destRoot))
            return;

        Directory.CreateDirectory(destRoot);

        foreach (var file in Directory.GetFiles(sourceRoot, "*" + FileExtension))
            MoveFileInto(file, destRoot);

        foreach (var dir in Directory.GetDirectories(sourceRoot))
            MoveFolderInto(dir, destRoot);
    }

    private void CopyDirectory(string sourceDir, string destDir, bool includeSshScriptBindings)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var target = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, target, overwrite: false);
            if (!includeSshScriptBindings
                && string.Equals(Path.GetExtension(target), FileExtension, StringComparison.OrdinalIgnoreCase))
            {
                RemoveSshScriptBindings(target);
            }
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)), includeSshScriptBindings);
    }

    private void RemoveSshScriptBindings(string filePath)
    {
        var connection = Load(filePath);
        if (!connection.IsSsh || connection.ScriptBindings.Count == 0)
            return;

        connection.ScriptBindings.Clear();
        SaveInPlace(connection, filePath);
    }

    // --- Helpers ---

    private static bool PathsEqual(string? a, string? b) =>
        string.Equals(
            a is null ? null : Path.GetFullPath(a),
            b is null ? null : Path.GetFullPath(b),
            StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Strips characters that are invalid in Windows file names.</summary>
    public static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Unnamed";

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "Unnamed" : cleaned;
    }
}
