using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.Services;

/// <summary>
/// Imports SSH and RDP connections from a FinalShell <c>conn</c> directory.
///
/// FinalShell stores each group as a sub-folder containing a <c>folder.json</c>
/// (with the display name) and one <c>*_connect_config.json</c> file per
/// connection. Connection type 100 = SSH, 101 = RDP. The <c>password</c> field
/// is encrypted with a machine-bound key (unwrapped from config.json's <c>huw</c>
/// at FinalShell startup) and cannot be decrypted without FinalShell's own
/// runtime, so passwords are NOT imported — the user re-enters them in the editor.
/// </summary>
public class FinalShellImporter
{
    private readonly ConnectionStore _store;

    public FinalShellImporter(ConnectionStore store)
    {
        _store = store;
    }

    public record ImportResult(int Imported, int Skipped, int Folders);

    /// <summary>
    /// Walks <paramref name="finalShellConnRoot"/> and imports every SSH/RDP
    /// connection into the store's root. FinalShell groups that share a name
    /// with an existing folder under the store root are merged into it;
    /// duplicate connection names are auto-suffixed by <see cref="ConnectionStore.Save"/>.
    /// </summary>
    public ImportResult Import(string finalShellConnRoot)
    {
        if (!Directory.Exists(finalShellConnRoot))
            throw new DirectoryNotFoundException(finalShellConnRoot);

        var imported = 0;
        var skipped = 0;
        var folders = 0;

        ImportDirectory(finalShellConnRoot, _store.RootPath, isRoot: true, ref imported, ref skipped, ref folders);

        return new ImportResult(imported, skipped, folders);
    }

    private void ImportDirectory(
        string sourceDir,
        string targetParent,
        bool isRoot,
        ref int imported,
        ref int skipped,
        ref int folders)
    {
        var targetFolder = targetParent;
        if (!isRoot)
        {
            var folderJsonPath = Path.Combine(sourceDir, "folder.json");
            var hasSubItems = File.Exists(folderJsonPath)
                              || Directory.EnumerateFiles(sourceDir, "*_connect_config.json", SearchOption.AllDirectories).Any();
            if (!hasSubItems)
                return;

            var groupName = ReadGroupName(sourceDir, folderJsonPath);
            // Merge into an existing folder with the same name; otherwise create.
            targetFolder = Path.Combine(targetParent, ConnectionStore.SanitizeName(groupName));
            if (!Directory.Exists(targetFolder))
            {
                targetFolder = _store.CreateFolder(targetParent, groupName);
                folders++;
            }
        }

        foreach (var file in Directory.GetFiles(sourceDir, "*_connect_config.json"))
        {
            FinalShellConnection? raw;
            try
            {
                raw = JsonSerializer.Deserialize<FinalShellConnection>(File.ReadAllText(file));
            }
            catch
            {
                skipped++;
                continue;
            }

            if (raw is null)
            {
                skipped++;
                continue;
            }

            var connection = MapToConnection(raw);
            if (connection is null)
            {
                skipped++;
                continue;
            }

            _store.Save(connection, targetFolder);
            imported++;
        }

        foreach (var childDir in Directory.GetDirectories(sourceDir))
            ImportDirectory(childDir, targetFolder, isRoot: false, ref imported, ref skipped, ref folders);
    }

    private static string ReadGroupName(string groupDir, string folderJsonPath)
    {
        if (!File.Exists(folderJsonPath))
            return Path.GetFileName(groupDir);

        try
        {
            var folderInfo = JsonSerializer.Deserialize<FinalShellFolder>(
                File.ReadAllText(folderJsonPath));
            return string.IsNullOrWhiteSpace(folderInfo?.Name)
                ? Path.GetFileName(groupDir)
                : folderInfo.Name;
        }
        catch
        {
            return Path.GetFileName(groupDir);
        }
    }

    private static Connection? MapToConnection(FinalShellConnection raw)
    {
        ConnectionType type;
        switch (raw.ConnectionType)
        {
            case 100:
                type = Models.ConnectionType.Ssh;
                break;
            case 101:
                type = Models.ConnectionType.Rdp;
                break;
            default:
                return null; // ignore SFTP-only / other FinalShell types
        }

        var connection = new Connection
        {
            Type = type,
            Name = string.IsNullOrWhiteSpace(raw.Name) ? $"{type} {raw.Host}" : raw.Name!,
            Host = raw.Host ?? "",
            Port = raw.Port > 0 ? raw.Port : Connection.DefaultPort(type),
            Username = raw.UserName ?? "",
            Notes = raw.Description ?? "",
        };

        // FinalShell's password field is encrypted with a machine-bound key
        // we cannot reproduce without its runtime — leave it empty for the
        // user to fill in via the editor.

        if (type == Models.ConnectionType.Rdp)
        {
            connection.RdpFullScreen = raw.Fullscreen;
            // FinalShell stores 0 when "auto / default" — fall back to defaults.
            connection.RdpWidth = raw.Width > 0 ? raw.Width : 1280;
            connection.RdpHeight = raw.Height > 0 ? raw.Height : 720;
            connection.RdpRedirectDrives = raw.DriveStoreDirect;
        }

        return connection;
    }

    private sealed class FinalShellFolder
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class FinalShellConnection
    {
        [JsonPropertyName("conection_type")] // sic: FinalShell's spelling
        public int ConnectionType { get; set; }

        [JsonPropertyName("host")]
        public string? Host { get; set; }

        [JsonPropertyName("port")]
        public int Port { get; set; }

        [JsonPropertyName("user_name")]
        public string? UserName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("fullscreen")]
        public bool Fullscreen { get; set; }

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        [JsonPropertyName("drivestoredirect")]
        public bool DriveStoreDirect { get; set; }
    }
}
