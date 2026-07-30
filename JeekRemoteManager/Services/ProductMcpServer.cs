using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Jeek.Avalonia.Localization;
using JeekTools;
using JeekRemoteManager.Models;
using JeekRemoteManager.ViewModels;
using JeekRemoteManager.Views;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekRemoteManager.Services;

/// <summary>
/// The product MCP surface: JeekRemoteManager as a second front-end for an agent, served on
/// a named pipe (see <see cref="McpPipeNames"/>) so a user's project config never carries a
/// port or a token. Ships in every build, unlike the Debug object-graph surface, which stays
/// a separate endpoint with its own pipe — the two tool registries must never merge.
///
/// Responses are assembled field by field, never by serializing a <see cref="Connection"/>:
/// passwords are write-only, so no encrypted blob or clear text can leak through a field
/// added to the model later.
/// </summary>
internal static class ProductMcpServer
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(ProductMcpServer));
    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };

    private static readonly McpHost Host = CreateHost();

    public static void Start()
    {
        Host.Start();
        if (Host.PipeName.Length > 0)
            Log.ZLogInformation($@"Product MCP listening on \\.\pipe\{Host.PipeName}");
    }

    public static void Stop() => Host.Stop();

    /// <summary>Pipe currently accepting product sessions ("" when stopped).</summary>
    public static string PipeName => Host.PipeName;

    private static McpHost CreateHost()
    {
        var host = new McpHost(new McpHostOptions
        {
            ServerName = "jeek-remote-manager",
            ServerTitle = "JeekRemoteManager",
            Graph = new ObjectGraph(new ObjectGraphOptions
            {
                // The product surface exposes no object graph; the standard get/set/invoke
                // tools are never advertised by ProductMcpContract.
                ResolveRoot = name => throw new InvalidOperationException(
                    $"The product MCP surface has no object roots ('{name}')."),
                RootNamesHelp = "(none)",
            }),
            GetVersion = () => $"{AutoUpdateService.GetLocalCommitCount()}",
            PipeName = DebugInstanceContext.ProductMcpPipeName,
            DefaultPort = 0,
            UiInvoker = func => Dispatcher.UIThread.InvokeAsync(func).GetTask()
                .WaitAsync(TimeSpan.FromMinutes(5)),
            Describe = BuildDescribeText,
            ToolListProvider = ProductMcpContract.BuildToolList,
        });

        host.AddTool("connection_list", ConnectionListAsync);
        host.AddTool("connection_get", ConnectionGetAsync);
        host.AddTool("connection_create", ConnectionCreateAsync);
        host.AddTool("connection_update", ConnectionUpdateAsync);
        host.AddTool("connection_move", ConnectionMoveAsync);
        host.AddTool("connection_delete", ConnectionDeleteAsync);
        host.AddTool("connection_set_password", ConnectionSetPasswordAsync);

        host.AddTool("folder_create", FolderCreateAsync);
        host.AddTool("folder_delete", FolderDeleteAsync);
        host.AddTool("folder_move", FolderMoveAsync);

        host.AddTool("connections_import", ConnectionsImportAsync);
        host.AddTool("known_hosts_list", _ => KnownHostsListAsync());
        host.AddTool("known_hosts_forget", KnownHostsForgetAsync);

        host.AddTool("script_list", _ => ScriptListAsync());
        host.AddTool("script_run", ScriptRunAsync);
        host.AddTool("script_run_batch", ScriptRunBatchAsync);
        host.AddTool("public_key_install", PublicKeyInstallAsync);

        host.AddTool("session_list", _ => SessionListAsync());
        host.AddTool("session_open", SessionOpenAsync);
        host.AddTool("session_close", args => SessionCommandAsync(args, close: true));
        host.AddTool("session_activate", args => SessionCommandAsync(args, close: false));

        host.AddTool("terminal_status", args => InSessionAsync(args, (tools, _) => tools.GetStatusAsync()));
        host.AddTool("terminal_run", args => RunCommandAsync(args, forceDanger: false));
        host.AddTool("terminal_run_danger", args => RunCommandAsync(args, forceDanger: true));
        host.AddTool("terminal_interrupt", args => InSessionAsync(args,
            (tools, _) => tools.RunTerminalActionAsync(AgentTerminalAction.ForceInterrupt)));
        host.AddTool("terminal_reconnect", args => InSessionAsync(args,
            (tools, _) => tools.RunTerminalActionAsync(AgentTerminalAction.Reconnect)));
        host.AddTool("terminal_scrollback", args => InSessionAsync(args,
            (tools, a) => tools.GetScrollbackAsync(Math.Clamp(a["lines"]?.GetValue<int>() ?? 200, 1, 5000))));
        host.AddTool("terminal_send_keys", args => InSessionAsync(args,
            (tools, a) => tools.SendKeysAsync(McpHost.RequiredString(a, "text"))));
        host.AddTool("file_upload", args => TransferAsync(args, isUpload: true));
        host.AddTool("file_download", args => TransferAsync(args, isUpload: false));
        host.AddTool("monitor_snapshot", args => InSessionAsync(args, (tools, _) => tools.GetMonitorSnapshotAsync()));
        return host;
    }

    private static JsonObject ToolText(string text, bool isError = false) =>
        McpHost.ToolText(text, isError);

    private static Task<T> OnUiAsync<T>(Func<T> func) => Host.OnUiAsync(func);

    private static IClassicDesktopStyleApplicationLifetime? Desktop =>
        Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

    private static MainWindow MainWindow =>
        Desktop?.MainWindow as MainWindow
        ?? throw new InvalidOperationException(
            "The JeekRemoteManager window is not ready yet (it may be waiting for the master password).");

    private static MainWindowViewModel MainVm =>
        MainWindow.DataContext as MainWindowViewModel
        ?? throw new InvalidOperationException("The main window has no view model yet.");

    private static string BuildDescribeText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("JeekRemoteManager product MCP surface.");
        sb.AppendLine($@"Pipe: \\.\pipe\{Host.PipeName}");
        sb.AppendLine();
        sb.AppendLine("Start with connection_list to see saved connections, session_open to get a shell,");
        sb.AppendLine("then terminal_run and friends against the returned session id.");
        sb.AppendLine();
        sb.AppendLine(ProductMcpContract.SessionHelp);
        sb.AppendLine();
        sb.AppendLine("Passwords are write-only: no tool returns one, and connection_set_password defaults");
        sb.AppendLine("to letting the user type it in the app window. Two-factor prompts always happen there.");
        return sb.ToString();
    }

    #region Connections

    private static async Task<JsonObject> ConnectionListAsync(JsonObject args)
    {
        var filter = args["filter"]?.GetValue<string>();
        var folder = NormalizeTreePath(args["folder"]?.GetValue<string>());
        var limit = Math.Clamp(args["limit"]?.GetValue<int>() ?? 200, 1, 2000);

        var entries = await OnUiAsync(() =>
        {
            var root = MainVm.RootPath;
            var store = new ConnectionStore(root);
            var list = new List<JsonObject>();
            foreach (var file in store.AllConnectionFiles())
            {
                var path = ToTreePath(root, file);
                if (folder.Length > 0
                    && !path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Connection connection;
                try
                {
                    connection = store.Load(file);
                }
                catch (Exception ex)
                {
                    Log.ZLogWarning($"Could not load '{file}' for connection_list: {ex.Message}");
                    continue;
                }

                if (!MatchesFilter(connection, path, filter))
                    continue;

                list.Add(DescribeConnection(connection, path, full: false));
                if (list.Count >= limit)
                    break;
            }

            return list;
        }).ConfigureAwait(false);

        var result = new JsonObject
        {
            ["count"] = entries.Count,
            ["connections"] = new JsonArray(entries.Cast<JsonNode>().ToArray()),
        };
        return ToolText(result.ToJsonString(PrettyOptions));
    }

    private static async Task<JsonObject> ConnectionGetAsync(JsonObject args)
    {
        var path = NormalizeTreePath(McpHost.RequiredString(args, "connection"));
        var described = await OnUiAsync(() =>
        {
            var (connection, treePath, _) = LoadConnection(path);
            return DescribeConnection(connection, treePath, full: true);
        }).ConfigureAwait(false);

        return ToolText(described.ToJsonString(PrettyOptions));
    }

    private static async Task<JsonObject> ConnectionCreateAsync(JsonObject args)
    {
        var name = McpHost.RequiredString(args, "name").Trim();
        if (name.Length == 0)
            return ToolText("A connection name is required.", isError: true);

        var typeText = (args["type"]?.GetValue<string>() ?? "ssh").Trim().ToLowerInvariant();
        var type = typeText switch
        {
            "ssh" => ConnectionType.Ssh,
            "wsl" => ConnectionType.Wsl,
            "rdp" => ConnectionType.Rdp,
            _ => throw new InvalidOperationException($"Unknown connection type '{typeText}'. Use ssh, wsl, or rdp."),
        };

        var folder = NormalizeTreePath(args["folder"]?.GetValue<string>());
        var open = args["open"]?.GetValue<bool>() ?? false;

        var connection = new Connection
        {
            ConnectionId = Guid.NewGuid().ToString(),
            Type = type,
            Name = name,
            Port = Connection.DefaultPort(type),
        };
        ApplyEditableFields(connection, args);

        var report = await OnUiAsync(() =>
        {
            var root = MainVm.RootPath;
            var store = new ConnectionStore(root);
            var targetFolder = folder.Length == 0
                ? root
                : Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(targetFolder);

            var savedPath = store.Save(connection, targetFolder);
            MainVm.ReloadTreeFromDisk(savedPath);

            var treePath = ToTreePath(root, savedPath);
            var result = DescribeConnection(connection, treePath, full: true);
            result["created"] = true;

            if (open)
                result["session"] = MainWindow.OpenTerminalSession(connection, savedPath, duplicate: false, activate: true);

            return result;
        }).ConfigureAwait(false);

        return ToolText(report.ToJsonString(PrettyOptions));
    }

    private static async Task<JsonObject> ConnectionUpdateAsync(JsonObject args)
    {
        var path = NormalizeTreePath(McpHost.RequiredString(args, "connection"));
        var report = await OnUiAsync(() =>
        {
            var (connection, _, filePath) = LoadConnection(path);
            ApplyEditableFields(connection, args);

            // Save into the same folder; a changed name renames the file and drops the old one.
            var store = new ConnectionStore(MainVm.RootPath);
            var folder = Path.GetDirectoryName(filePath) ?? MainVm.RootPath;
            var savedPath = store.Save(connection, folder, previousFilePath: filePath);
            MainVm.ReloadTreeFromDisk(savedPath);

            var described = DescribeConnection(connection, ToTreePath(MainVm.RootPath, savedPath), full: true);
            described["updated"] = true;
            return described;
        }).ConfigureAwait(false);

        return ToolText(report.ToJsonString(PrettyOptions));
    }

    private static async Task<JsonObject> ConnectionMoveAsync(JsonObject args)
    {
        var path = NormalizeTreePath(McpHost.RequiredString(args, "connection"));
        var folder = NormalizeTreePath(McpHost.RequiredString(args, "folder"));

        var report = await OnUiAsync(() =>
        {
            var (connection, _, filePath) = LoadConnection(path);
            var root = MainVm.RootPath;
            var targetFolder = folder.Length == 0
                ? root
                : Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(targetFolder);

            var movedPath = new ConnectionStore(root).MoveFileInto(filePath, targetFolder);
            MainVm.ReloadTreeFromDisk(movedPath);

            var described = DescribeConnection(connection, ToTreePath(root, movedPath), full: false);
            described["moved"] = true;
            return described;
        }).ConfigureAwait(false);

        return ToolText(report.ToJsonString(PrettyOptions));
    }

    /// <summary>
    /// Deleting a saved connection is the user's data, so it is always confirmed in the
    /// JeekRemoteManager window — an agent cannot remove one on its own.
    /// </summary>
    private static async Task<JsonObject> ConnectionDeleteAsync(JsonObject args)
    {
        var path = NormalizeTreePath(McpHost.RequiredString(args, "connection"));
        var (treePath, filePath, name) = await OnUiAsync(() =>
        {
            var (connection, resolved, file) = LoadConnection(path);
            return (resolved, file, connection.Name);
        }).ConfigureAwait(false);

        if (!await ConfirmInWindowAsync(
                Localizer.Get("DialogDeleteTitle"),
                string.Format(Localizer.Get("DialogDeleteConnectionPrompt"), name)).ConfigureAwait(false))
        {
            return ToolText(
                $"The user declined deleting '{treePath}' in the JeekRemoteManager window.",
                isError: true);
        }

        await OnUiAsync(() =>
        {
            new ConnectionStore(MainVm.RootPath).DeleteFile(filePath);
            MainVm.ReloadTreeFromDisk();
            return true;
        }).ConfigureAwait(false);

        return ToolText(new JsonObject
        {
            ["status"] = "deleted",
            ["connection"] = treePath,
        }.ToJsonString(PrettyOptions));
    }

    /// <summary>
    /// The only fields an agent may write, shared by create and update. Credentials are not
    /// here on purpose — they go through <c>connection_set_password</c>.
    /// </summary>
    private static void ApplyEditableFields(Connection connection, JsonObject args)
    {
        if (args["name"]?.GetValue<string>() is { } name && name.Trim().Length > 0)
            connection.Name = name.Trim();
        if (args["host"]?.GetValue<string>() is { } host)
            connection.Host = host.Trim();
        if (args["port"] is { } port)
            connection.Port = port.GetValue<int>();
        if (args["username"]?.GetValue<string>() is { } username)
            connection.Username = username.Trim();
        if (args["private_key_path"]?.GetValue<string>() is { } keyPath)
            connection.PrivateKeyPath = keyPath.Trim();
        if (args["terminal_type"]?.GetValue<string>() is { } terminalType && terminalType.Trim().Length > 0)
            connection.TerminalType = terminalType.Trim();
        if (args["login_commands"]?.GetValue<string>() is { } loginCommands)
            connection.LoginCommands = loginCommands;
        if (args["wsl_distro"]?.GetValue<string>() is { } distro)
            connection.WslDistro = distro.Trim();
        if (args["wsl_start_directory"]?.GetValue<string>() is { } startDirectory)
            connection.WslStartDirectory = startDirectory.Trim();
        if (args["notes"]?.GetValue<string>() is { } notes)
            connection.Notes = notes;
        if (args["auto_open_monitor_panel"] is { } monitorPanel)
            connection.AutoOpenMonitorPanel = monitorPanel.GetValue<bool>();
        if (args["auto_open_file_browser_panel"] is { } fileBrowserPanel)
            connection.AutoOpenFileBrowserPanel = fileBrowserPanel.GetValue<bool>();
    }

    /// <summary>
    /// Write-only credential path. The default mode never accepts the secret: it selects the
    /// connection in the tree, which opens its editor, and asks the user to type it there.
    /// </summary>
    private static async Task<JsonObject> ConnectionSetPasswordAsync(JsonObject args)
    {
        var path = NormalizeTreePath(McpHost.RequiredString(args, "connection"));
        var target = (args["target"]?.GetValue<string>() ?? "password").Trim().ToLowerInvariant();
        if (target is not ("password" or "key_passphrase"))
            return ToolText($"Unknown target '{target}'. Use password or key_passphrase.", isError: true);

        var mode = (args["mode"]?.GetValue<string>() ?? "prompt").Trim().ToLowerInvariant();
        if (mode == "prompt")
        {
            var prompted = await OnUiAsync(() =>
            {
                var (_, treePath, filePath) = LoadConnection(path);
                MainVm.SelectNodeByPath(filePath);
                MainWindow.ActivateMainWindow();
                return treePath;
            }).ConfigureAwait(false);

            return ToolText(new JsonObject
            {
                ["status"] = "awaiting_user",
                ["connection"] = prompted,
                ["message"] = $"Opened '{prompted}' in the JeekRemoteManager window. Ask the user to type the "
                              + $"{(target == "password" ? "password" : "key passphrase")} there and save; "
                              + "the secret is never passed through this channel.",
            }.ToJsonString(PrettyOptions));
        }

        if (mode != "value")
            return ToolText($"Unknown mode '{mode}'. Use prompt or value.", isError: true);

        if (args["value"]?.GetValue<string>() is not { } value)
            return ToolText("mode 'value' requires the 'value' argument.", isError: true);

        var saved = await OnUiAsync(() =>
        {
            var (connection, treePath, filePath) = LoadConnection(path);
            var encrypted = PasswordProtector.Encrypt(value);
            if (target == "password")
                connection.EncryptedPassword = encrypted;
            else
                connection.EncryptedPrivateKeyPassphrase = encrypted;

            new ConnectionStore(MainVm.RootPath).SaveInPlace(connection, filePath);
            MainVm.ReloadTreeFromDisk(filePath);
            return treePath;
        }).ConfigureAwait(false);

        // Deliberately echoes nothing about the value, not even its length.
        return ToolText(new JsonObject
        {
            ["status"] = "saved",
            ["connection"] = saved,
            ["target"] = target,
        }.ToJsonString(PrettyOptions));
    }

    /// <summary>
    /// Explicit whitelist — the only place connection fields reach an agent. Credentials are
    /// reported as booleans; neither the clear text nor the encrypted blob is ever included.
    /// </summary>
    private static JsonObject DescribeConnection(Connection connection, string treePath, bool full)
    {
        var described = new JsonObject
        {
            ["connection"] = treePath,
            ["name"] = connection.Name,
            ["type"] = connection.Type.ToString().ToUpperInvariant(),
            ["target"] = connection.TargetLabel,
        };

        if (!full)
            return described;

        described["host"] = connection.Host;
        described["port"] = connection.Port;
        described["username"] = connection.Username;
        described["hasPassword"] = !string.IsNullOrEmpty(connection.EncryptedPassword);
        described["hasKeyPassphrase"] = !string.IsNullOrEmpty(connection.EncryptedPrivateKeyPassphrase);
        described["privateKeyPath"] = connection.PrivateKeyPath;
        described["terminalType"] = connection.TerminalType;
        described["loginCommands"] = connection.LoginCommands;
        described["autoOpenMonitorPanel"] = connection.AutoOpenMonitorPanel;
        described["autoOpenFileBrowserPanel"] = connection.AutoOpenFileBrowserPanel;
        described["wslDistro"] = connection.WslDistro;
        described["wslStartDirectory"] = connection.WslStartDirectory;
        described["notes"] = connection.Notes;
        return described;
    }

    private static bool MatchesFilter(Connection connection, string treePath, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        return treePath.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || connection.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || connection.Host.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || connection.Username.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Resolves a tree path such as <c>vps/bwg</c> to its file and loaded model.</summary>
    private static (Connection Connection, string TreePath, string FilePath) LoadConnection(string treePath)
    {
        var root = MainVm.RootPath;
        var store = new ConnectionStore(root);
        var relative = treePath.Replace('/', Path.DirectorySeparatorChar);
        var filePath = Path.Combine(root, relative + ConnectionStore.FileExtension);

        if (!File.Exists(filePath))
        {
            // Fall back to a name match so 'bwg' works when it is unambiguous.
            var matches = store.AllConnectionFiles()
                .Where(f => string.Equals(
                    Path.GetFileNameWithoutExtension(f), Path.GetFileName(treePath), StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0)
                throw new InvalidOperationException($"No connection at '{treePath}'. Use connection_list to see the tree.");
            if (matches.Count > 1)
            {
                throw new InvalidOperationException(
                    $"'{treePath}' matches {matches.Count} connections; use the full tree path from connection_list.");
            }

            filePath = matches[0];
        }

        return (store.Load(filePath), ToTreePath(root, filePath), filePath);
    }

    private static string ToTreePath(string root, string filePath)
    {
        var relative = Path.GetRelativePath(root, filePath);
        if (relative.EndsWith(ConnectionStore.FileExtension, StringComparison.OrdinalIgnoreCase))
            relative = relative[..^ConnectionStore.FileExtension.Length];
        return relative.Replace('\\', '/');
    }

    private static string NormalizeTreePath(string? value) =>
        (value ?? "").Trim().Replace('\\', '/').Trim('/');

    #endregion

    #region Folders

    private static async Task<JsonObject> FolderCreateAsync(JsonObject args)
    {
        var folder = NormalizeTreePath(McpHost.RequiredString(args, "folder"));
        if (folder.Length == 0)
            return ToolText("'folder' must name a folder below the tree root.", isError: true);

        var created = await OnUiAsync(() =>
        {
            var root = MainVm.RootPath;
            var path = Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(path);
            MainVm.ReloadTreeFromDisk(path);
            return ToTreePath(root, path);
        }).ConfigureAwait(false);

        return ToolText(new JsonObject { ["status"] = "created", ["folder"] = created }.ToJsonString(PrettyOptions));
    }

    /// <summary>
    /// Deletes a folder and everything under it, so it always goes through the same GUI
    /// confirmation as deleting a connection.
    /// </summary>
    private static async Task<JsonObject> FolderDeleteAsync(JsonObject args)
    {
        var folder = NormalizeTreePath(McpHost.RequiredString(args, "folder"));
        if (folder.Length == 0)
            return ToolText("Refusing to delete the tree root.", isError: true);

        var path = await OnUiAsync(() =>
        {
            var full = Path.Combine(MainVm.RootPath, folder.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(full))
                throw new InvalidOperationException($"No folder at '{folder}'.");
            return full;
        }).ConfigureAwait(false);

        if (!await ConfirmInWindowAsync(
                Localizer.Get("DialogDeleteTitle"),
                string.Format(Localizer.Get("DialogDeleteFolderPrompt"), folder)).ConfigureAwait(false))
        {
            return ToolText(
                $"The user declined deleting '{folder}' in the JeekRemoteManager window.",
                isError: true);
        }

        await OnUiAsync(() =>
        {
            new ConnectionStore(MainVm.RootPath).DeleteFolder(path);
            MainVm.ReloadTreeFromDisk();
            return true;
        }).ConfigureAwait(false);

        return ToolText(new JsonObject { ["status"] = "deleted", ["folder"] = folder }.ToJsonString(PrettyOptions));
    }

    /// <summary>Moves a folder under another parent, or renames it in place.</summary>
    private static async Task<JsonObject> FolderMoveAsync(JsonObject args)
    {
        var folder = NormalizeTreePath(McpHost.RequiredString(args, "folder"));
        if (folder.Length == 0)
            return ToolText("Refusing to move the tree root.", isError: true);

        var parent = args["parent"]?.GetValue<string>() is { } value ? NormalizeTreePath(value) : null;
        var newName = args["name"]?.GetValue<string>()?.Trim();
        if (parent is null && string.IsNullOrEmpty(newName))
            return ToolText("Pass 'parent' to move the folder, 'name' to rename it, or both.", isError: true);

        var moved = await OnUiAsync(() =>
        {
            var root = MainVm.RootPath;
            var store = new ConnectionStore(root);
            var path = Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(path))
                throw new InvalidOperationException($"No folder at '{folder}'.");

            if (parent is not null)
            {
                var targetParent = parent.Length == 0
                    ? root
                    : Path.Combine(root, parent.Replace('/', Path.DirectorySeparatorChar));
                if (ConnectionStore.IsSameOrInside(path, targetParent))
                    throw new InvalidOperationException("A folder cannot be moved inside itself.");

                Directory.CreateDirectory(targetParent);
                path = store.MoveFolderInto(path, targetParent);
            }

            if (!string.IsNullOrEmpty(newName))
                path = store.RenameFolder(path, newName);

            MainVm.ReloadTreeFromDisk(path);
            return ToTreePath(root, path);
        }).ConfigureAwait(false);

        return ToolText(new JsonObject { ["status"] = "moved", ["folder"] = moved }.ToJsonString(PrettyOptions));
    }

    #endregion

    #region Import and host keys

    /// <summary>
    /// Bulk-imports connections from another SSH client. Existing connections are left alone;
    /// the importer skips duplicates rather than overwriting them.
    /// </summary>
    private static async Task<JsonObject> ConnectionsImportAsync(JsonObject args)
    {
        var source = McpHost.RequiredString(args, "source").Trim().ToLowerInvariant();
        var path = McpHost.RequiredString(args, "path").Trim();
        if (!Directory.Exists(path))
            return ToolText($"No folder at '{path}'.", isError: true);

        var report = await OnUiAsync(() =>
        {
            var store = new ConnectionStore(MainVm.RootPath);
            var result = new JsonObject { ["source"] = source, ["path"] = path };
            switch (source)
            {
                case "xshell":
                    {
                        var imported = new XshellImporter(store).Import(path);
                        result["imported"] = imported.Imported;
                        result["skipped"] = imported.Skipped;
                        result["folders"] = imported.Folders;
                        result["passwordsImported"] = imported.PasswordsImported;
                        break;
                    }

                case "securecrt":
                    {
                        var imported = new SecureCrtImporter(store).Import(path);
                        result["imported"] = imported.Imported;
                        result["skipped"] = imported.Skipped;
                        result["folders"] = imported.Folders;
                        result["passwordsImported"] = imported.PasswordsImported;
                        break;
                    }

                case "finalshell":
                    {
                        var imported = new FinalShellImporter(store).Import(path);
                        result["imported"] = imported.Imported;
                        result["skipped"] = imported.Skipped;
                        result["folders"] = imported.Folders;
                        // FinalShell passwords cannot be decrypted; the user fills them in later.
                        result["passwordsImported"] = 0;
                        break;
                    }

                default:
                    throw new InvalidOperationException(
                        $"Unknown source '{source}'. Use xshell, securecrt, or finalshell.");
            }

            MainVm.ReloadTreeFromDisk();
            return result;
        }).ConfigureAwait(false);

        return ToolText(report.ToJsonString(PrettyOptions));
    }

    private static async Task<JsonObject> KnownHostsListAsync()
    {
        var hosts = await Task.Run(() => KnownHostsStore.All()
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(JsonNode (entry) => new JsonObject
            {
                ["host"] = entry.Key,
                ["fingerprint"] = entry.Value,
            })
            .ToArray()).ConfigureAwait(false);

        return ToolText(new JsonObject
        {
            ["count"] = hosts.Length,
            ["hosts"] = new JsonArray(hosts),
        }.ToJsonString(PrettyOptions));
    }

    /// <summary>
    /// Drops a stored host fingerprint, the equivalent of <c>ssh-keygen -R</c> — what you need
    /// after a server is rebuilt and its key legitimately changed.
    /// </summary>
    private static async Task<JsonObject> KnownHostsForgetAsync(JsonObject args)
    {
        var host = McpHost.RequiredString(args, "host").Trim();
        var port = args["port"]?.GetValue<int>() ?? 22;

        var forgotten = await Task.Run(() => KnownHostsStore.Forget(host, port)).ConfigureAwait(false);
        return ToolText(new JsonObject
        {
            ["status"] = forgotten ? "forgotten" : "not_stored",
            ["host"] = $"{host}:{port}",
        }.ToJsonString(PrettyOptions));
    }

    #endregion

    #region Scripts

    /// <summary>Brings the window forward and awaits the app's own confirmation dialog.</summary>
    private static async Task<bool> ConfirmInWindowAsync(string title, string message)
    {
        var confirm = await OnUiAsync(() =>
        {
            MainWindow.ActivateMainWindow();
            return MainVm.ConfirmAsync?.Invoke(title, message) ?? Task.FromResult(false);
        }).ConfigureAwait(false);

        return await confirm.ConfigureAwait(false);
    }


    private static async Task<JsonObject> ScriptListAsync()
    {
        var suites = await OnUiAsync(() => MainVm.ScriptSuites
            .Select(suite => new JsonObject
            {
                ["suite"] = suite.Name,
                ["source"] = suite.Source.ToString(),
                ["scripts"] = new JsonArray(suite.Scripts
                    .Select(JsonNode (script) => new JsonObject
                    {
                        ["script"] = script.Name,
                        ["title"] = script.DisplayName,
                    })
                    .ToArray()),
                // Parameter shapes only. Stored values are never returned: a Secret parameter
                // holds a master-password-encrypted blob, and this surface is write-only.
                ["parameters"] = new JsonArray(suite.Parameters
                    .Select(JsonNode (parameter) => new JsonObject
                    {
                        ["name"] = parameter.Name,
                        ["type"] = parameter.Type.ToString(),
                        ["default"] = parameter.Type == RemoteScriptParameterType.Secret
                            ? ""
                            : parameter.DefaultValue,
                        ["options"] = new JsonArray(parameter.EnumOptions.Select(JsonNode (o) => o).ToArray()),
                    })
                    .ToArray()),
                ["errors"] = new JsonArray(suite.Errors.Select(JsonNode (e) => e).ToArray()),
            })
            .ToList()).ConfigureAwait(false);

        var result = new JsonObject
        {
            ["count"] = suites.Count,
            ["suites"] = new JsonArray(suites.Cast<JsonNode>().ToArray()),
        };
        return ToolText(result.ToJsonString(PrettyOptions));
    }

    /// <summary>
    /// Runs one script of a suite on an open session. Parameter values come from the
    /// connection's saved binding, with anything passed in <c>params</c> layered on top for
    /// this run only — including secrets, which are accepted but never read back.
    /// </summary>
    private static async Task<JsonObject> ScriptRunAsync(JsonObject args)
    {
        var suiteName = McpHost.RequiredString(args, "suite");
        var scriptName = McpHost.RequiredString(args, "script");
        var overrides = args["params"] as JsonObject;

        // Resolve the script before the session: a wrong suite name should be reported as
        // such, not masked by "no session is open".
        var (suite, scriptFile) = await ResolveScriptAsync(suiteName, scriptName).ConfigureAwait(false);

        var view = await ResolveSessionViewAsync(args).ConfigureAwait(false);
        var binding = await OnUiAsync(() => BuildScriptBinding(suite, view.Connection, overrides))
            .ConfigureAwait(false);

        if (await OnUiAsync(() => view.IsScriptRunning).ConfigureAwait(false))
            return ToolText("This session is already running a script; wait for it to finish.", isError: true);

        var result = await view.RunScriptAsync(suite, scriptFile, binding).ConfigureAwait(false);

        // The run streams into the session's terminal; hand back the tail so the agent can
        // read what happened without a second round trip.
        var tail = await view.AgentRemoteTools.GetScrollbackAsync(200).ConfigureAwait(false);
        return ToolText(new JsonObject
        {
            ["suite"] = suite.Name,
            ["script"] = scriptFile.Name,
            ["exitCode"] = result.ExitCode,
            ["seconds"] = Math.Round((result.FinishedAt - result.StartedAt).TotalSeconds, 1),
            ["terminalTail"] = tail,
        }.ToJsonString(PrettyOptions));
    }

    /// <summary>
    /// Runs one script across several connections — the "apply this to all of them" case.
    /// Sessions are opened as needed, each connection keeps its own saved parameter binding,
    /// and one failure does not stop the rest.
    /// </summary>
    private static async Task<JsonObject> ScriptRunBatchAsync(JsonObject args)
    {
        var suiteName = McpHost.RequiredString(args, "suite");
        var scriptName = McpHost.RequiredString(args, "script");
        var overrides = args["params"] as JsonObject;
        var openMissing = args["open_missing"]?.GetValue<bool>() ?? true;
        var sequential = args["sequential"]?.GetValue<bool>() ?? false;

        var connections = (args["connections"] as JsonArray)?
            .Select(node => NormalizeTreePath(node?.GetValue<string>()))
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        if (connections.Count == 0)
            return ToolText("'connections' must list at least one connection tree path.", isError: true);

        var (suite, scriptFile) = await ResolveScriptAsync(suiteName, scriptName).ConfigureAwait(false);

        async Task<JsonObject> RunOneAsync(string path)
        {
            var entry = new JsonObject { ["connection"] = path };
            try
            {
                var view = await ResolveOrOpenSessionAsync(path, openMissing).ConfigureAwait(false);
                var binding = await OnUiAsync(() => BuildScriptBinding(suite, view.Connection, overrides))
                    .ConfigureAwait(false);
                var result = await view.RunScriptAsync(suite, scriptFile, binding).ConfigureAwait(false);

                entry["status"] = result.ExitCode == 0 ? "ok" : "failed";
                entry["exitCode"] = result.ExitCode;
                entry["seconds"] = Math.Round((result.FinishedAt - result.StartedAt).TotalSeconds, 1);
            }
            catch (Exception ex)
            {
                entry["status"] = "error";
                entry["error"] = ex.Message;
            }

            return entry;
        }

        var results = new List<JsonObject>(connections.Count);
        if (sequential)
        {
            foreach (var path in connections)
                results.Add(await RunOneAsync(path).ConfigureAwait(false));
        }
        else
        {
            // Each session owns its own shell and script lock, so the default is all at once.
            results.AddRange(await Task.WhenAll(connections.Select(RunOneAsync)).ConfigureAwait(false));
        }

        return ToolText(new JsonObject
        {
            ["suite"] = suite.Name,
            ["script"] = scriptFile.Name,
            ["succeeded"] = results.Count(r => r["status"]?.GetValue<string>() == "ok"),
            ["total"] = results.Count,
            ["results"] = new JsonArray(results.Cast<JsonNode>().ToArray()),
        }.ToJsonString(PrettyOptions));
    }

    private static Task<(RemoteScriptSuite Suite, RemoteScriptFile Script)> ResolveScriptAsync(
        string suiteName,
        string scriptName) =>
        OnUiAsync(() =>
        {
            var found = MainVm.ScriptSuites.FirstOrDefault(s =>
                            string.Equals(s.Name, suiteName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException(
                            $"No script suite '{suiteName}'. Call script_list.");

            var file = found.Scripts.FirstOrDefault(s =>
                           string.Equals(s.Name, scriptName, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(s.DisplayName, scriptName, StringComparison.OrdinalIgnoreCase))
                       ?? throw new InvalidOperationException(
                           $"Suite '{found.Name}' has no script '{scriptName}'. Call script_list.");

            return (found, file);
        });

    /// <summary>Existing session for a connection, opening one when allowed.</summary>
    private static async Task<TerminalView> ResolveOrOpenSessionAsync(string connectionPath, bool openMissing)
    {
        var existing = await OnUiAsync(() => MainWindow.EnumerateTerminalSessions()
            .FirstOrDefault(s => s.SessionId == connectionPath
                                 || s.SessionId.StartsWith(connectionPath + " (", StringComparison.OrdinalIgnoreCase))
            .View).ConfigureAwait(false);
        if (existing is not null)
            return existing;

        if (!openMissing)
            throw new InvalidOperationException($"'{connectionPath}' has no open session.");

        return await OnUiAsync(() =>
        {
            var (connection, _, filePath) = LoadConnection(connectionPath);
            var sessionId = MainWindow.OpenTerminalSession(connection, filePath, duplicate: false, activate: false);
            return MainWindow.EnumerateTerminalSessions().First(s => s.SessionId == sessionId).View;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Installs a local public key into the session's remote account. Idempotent: an
    /// already-present key is reported, not duplicated.
    /// </summary>
    private static async Task<JsonObject> PublicKeyInstallAsync(JsonObject args)
    {
        var keyPath = args["public_key_path"]?.GetValue<string>();
        var keyText = args["public_key"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(keyText))
        {
            if (string.IsNullOrWhiteSpace(keyPath))
                return ToolText("Pass either 'public_key' or 'public_key_path'.", isError: true);
            if (!File.Exists(keyPath))
                return ToolText($"No public key file at '{keyPath}'.", isError: true);
            keyText = await File.ReadAllTextAsync(keyPath).ConfigureAwait(false);
        }

        var view = await ResolveSessionViewAsync(args).ConfigureAwait(false);
        var result = await view.InstallPublicKeyAsync(keyText).ConfigureAwait(false);
        return ToolText(new JsonObject
        {
            ["status"] = result.AlreadyPresent ? "already_present" : "installed",
            ["output"] = result.Output,
        }.ToJsonString(PrettyOptions));
    }

    /// <summary>
    /// Saved binding for this suite (secrets decrypted for the run) with the caller's
    /// overrides layered on top. Nothing is written back to the connection.
    /// </summary>
    private static ConnectionScriptBinding BuildScriptBinding(
        RemoteScriptSuite suite,
        Connection? connection,
        JsonObject? overrides)
    {
        var saved = connection?.ScriptBindings.FirstOrDefault(b => string.Equals(
            RemoteScriptSuiteNames.NormalizeBindingName(b.Name),
            RemoteScriptSuiteNames.NormalizeBindingName(suite.Name),
            StringComparison.OrdinalIgnoreCase));

        var binding = saved is null
            ? new ConnectionScriptBinding { Name = suite.Name }
            : RemoteScriptLauncher.UnprotectSecretValues(suite, RemoteScriptLauncher.CloneBinding(saved));

        if (overrides is null)
            return binding;

        foreach (var (name, value) in overrides)
        {
            var text = value?.ToString() ?? "";
            if (binding.Params.FirstOrDefault(p =>
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is { } existing)
            {
                existing.Value = text;
            }
            else
            {
                binding.Params.Add(new ConnectionScriptParameterValue { Name = name, Value = text });
            }
        }

        return binding;
    }

    #endregion

    #region Sessions

    private static async Task<JsonObject> SessionListAsync()
    {
        var sessions = await OnUiAsync(() => MainWindow.EnumerateTerminalSessions()
            .Select(s => new JsonObject
            {
                ["session"] = s.SessionId,
                ["name"] = s.View.Connection?.Name ?? s.SessionId,
                ["type"] = (s.View.Connection?.Type ?? ConnectionType.Ssh).ToString().ToUpperInvariant(),
                ["target"] = s.View.Connection?.TargetLabel ?? "",
                ["live"] = s.View.IsSessionLive,
                ["active"] = ReferenceEquals(s.Tab, MainWindow.SelectedTerminalTab),
            })
            .ToList()).ConfigureAwait(false);

        var result = new JsonObject
        {
            ["count"] = sessions.Count,
            ["sessions"] = new JsonArray(sessions.Cast<JsonNode>().ToArray()),
        };
        return ToolText(result.ToJsonString(PrettyOptions));
    }

    private static async Task<JsonObject> SessionOpenAsync(JsonObject args)
    {
        var path = NormalizeTreePath(McpHost.RequiredString(args, "connection"));
        var duplicate = args["duplicate"]?.GetValue<bool>() ?? false;
        var activate = args["activate"]?.GetValue<bool>() ?? true;
        var waitSeconds = Math.Clamp(args["wait_seconds"]?.GetValue<int>() ?? 30, 1, 300);

        var sessionId = await OnUiAsync(() =>
        {
            var (connection, _, filePath) = LoadConnection(path);
            return MainWindow.OpenTerminalSession(connection, filePath, duplicate, activate);
        }).ConfigureAwait(false);

        // Logging in can need the user (master password, two-factor, a bastion menu). Wait a
        // bounded time, then hand back a pollable id instead of holding the tool call open.
        var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
        var live = false;
        while (DateTime.UtcNow < deadline)
        {
            live = await OnUiAsync(() => MainWindow.EnumerateTerminalSessions()
                .Any(s => s.SessionId == sessionId && s.View.IsSessionLive)).ConfigureAwait(false);
            if (live)
                break;
            await Task.Delay(250).ConfigureAwait(false);
        }

        return ToolText(new JsonObject
        {
            ["status"] = live ? "open" : "awaiting_user",
            ["session"] = sessionId,
            ["message"] = live
                ? "The shell is up; address it with this session id."
                : "Still logging in — the JeekRemoteManager window may be waiting for the user "
                  + "(master password, two-factor, bastion menu). Poll session_list for 'live'.",
        }.ToJsonString(PrettyOptions));
    }

    private static async Task<JsonObject> SessionCommandAsync(JsonObject args, bool close)
    {
        var id = McpHost.RequiredString(args, "session");
        var report = await OnUiAsync(() =>
        {
            if (MainWindow.EnumerateTerminalSessions().FirstOrDefault(s => s.SessionId == id) is not { View: not null } session)
                throw new InvalidOperationException($"No open session '{id}'. Call session_list.");

            if (close)
            {
                MainWindow.CloseTerminalSession(session.Tab);
                return $"Closed session '{id}'.";
            }

            MainWindow.ActivateTerminalSession(session.Tab, session.View);
            return $"Brought session '{id}' to the front.";
        }).ConfigureAwait(false);

        return ToolText(report);
    }

    private static async Task<IAgentRemoteTools> ResolveToolsAsync(JsonObject args) =>
        (await ResolveSessionViewAsync(args).ConfigureAwait(false)).AgentRemoteTools;

    /// <summary>Resolves the session an in-session tool addresses, by id or by connection path.</summary>
    private static async Task<TerminalView> ResolveSessionViewAsync(JsonObject args)
    {
        var id = args["session"]?.GetValue<string>();
        var connection = NormalizeTreePath(args["connection"]?.GetValue<string>());

        return await OnUiAsync(() =>
        {
            var sessions = MainWindow.EnumerateTerminalSessions();
            if (sessions.Count == 0)
                throw new InvalidOperationException("No terminal session is open. Call session_open first.");

            if (!string.IsNullOrWhiteSpace(id))
            {
                return sessions.FirstOrDefault(s => s.SessionId == id) is { View: not null } match
                    ? match.View
                    : throw new InvalidOperationException($"No open session '{id}'. Call session_list.");
            }

            if (connection.Length > 0)
            {
                // 'vps/bwg' also matches its duplicated tabs 'vps/bwg (2)'; prefer the first.
                var byConnection = sessions
                    .Where(s => s.SessionId == connection
                                || s.SessionId.StartsWith(connection + " (", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (byConnection.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"'{connection}' has no open session. Call session_open first.");
                }

                return byConnection[0].View;
            }

            if (sessions.Count == 1)
                return sessions[0].View;

            throw new InvalidOperationException(
                $"{sessions.Count} sessions are open; pass 'session' or 'connection'. Call session_list.");
        }).ConfigureAwait(false);
    }

    private static async Task<JsonObject> InSessionAsync(
        JsonObject args,
        Func<IAgentRemoteTools, JsonObject, Task<string>> action)
    {
        var tools = await ResolveToolsAsync(args).ConfigureAwait(false);
        return ToolText(await action(tools, args).ConfigureAwait(false));
    }

    private static async Task<JsonObject> RunCommandAsync(JsonObject args, bool forceDanger)
    {
        var command = McpHost.RequiredString(args, "command");
        int? timeout = args["timeout_seconds"] is { } node ? node.GetValue<int>() : null;
        var tools = await ResolveToolsAsync(args).ConfigureAwait(false);

        // The app's own confirmation for destructive commands, unless the user turned it off
        // in the AI panel (Auto-approve). The agent's own approval flow still applies.
        if (forceDanger || DangerousCommandDetector.IsDangerous(command))
        {
            var autoApprove = await OnUiAsync(() =>
                (Desktop?.MainWindow?.DataContext as MainWindowViewModel)?.AiAutoApproveDangerousCommands
                ?? false).ConfigureAwait(false);
            if (!autoApprove
                && !await tools.ConfirmDangerousCommandAsync(command).ConfigureAwait(false))
            {
                return ToolText("The user declined this command in the JeekRemoteManager window.", isError: true);
            }
        }

        return ToolText(await tools.RunCommandAsync(command, timeout).ConfigureAwait(false));
    }

    private static async Task<JsonObject> TransferAsync(JsonObject args, bool isUpload)
    {
        var sources = (args["sources"] as JsonArray)?
            .Select(node => node?.GetValue<string>() ?? "")
            .Where(value => value.Length > 0)
            .ToList() ?? [];
        if (sources.Count == 0)
            return ToolText("'sources' must list at least one file path.", isError: true);

        var destination = args["destination"]?.GetValue<string>();
        var tools = await ResolveToolsAsync(args).ConfigureAwait(false);
        var transfer = new AgentFileTransfer(isUpload, sources, string.IsNullOrWhiteSpace(destination) ? null : destination);
        return ToolText(await tools.TransferFilesAsync(transfer).ConfigureAwait(false));
    }

    #endregion
}
