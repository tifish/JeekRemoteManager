using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jeek.Avalonia.Localization;
using JeekRemoteManager.Controls;
using JeekRemoteManager.Models;
using JeekRemoteManager.ViewModels;
using JeekTools;
using JeekRemoteManager.Views;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using ZLogger;

namespace JeekRemoteManager.Services;

/// <summary>Connection tree and storage probes: loading, reload ordering, watcher, read cache and editor switching.</summary>
internal static partial class DebugMcpServer
{
    /// <summary>
    /// Rebuilding the tree used to read and deserialize every connection file inline on
    /// the UI thread, which is very visible when the folder is on a network or file-synced
    /// drive and a sync burst makes the watcher fire repeatedly. Uses an isolated temp
    /// root — never the user's real connections folder, which may be under sync.
    /// </summary>
    private static async Task<JsonObject> ConnectionTreeLoadCheckAsync()
    {
        const int foldersCount = 8;
        const int perFolder = 40;
        var root = Path.Combine(
            Path.GetTempPath(),
            "JeekRemoteManager.TreeLoadProbe." + Guid.NewGuid().ToString("N"));
        var failures = new List<string>();

        try
        {
            var store = new ConnectionStore(root);
            var expected = 0;
            for (var f = 0; f < foldersCount; f++)
            {
                var folder = Path.Combine(root, $"folder{f}");
                for (var c = 0; c < perFolder; c++)
                {
                    store.Save(
                        new Connection
                        {
                            Type = ConnectionType.Ssh,
                            Name = $"probe{c}",
                            Host = $"host{c}.invalid",
                            Port = 22,
                            Username = "probe",
                        },
                        folder);
                    expected++;
                }
            }

            // The read must be safe off the UI thread — that is the entire point.
            var uiThreadDuringRead = true;
            var snapshot = await Task.Run(() =>
            {
                uiThreadDuringRead = Dispatcher.UIThread.CheckAccess();
                return store.ReadTree();
            }).ConfigureAwait(false);

            if (uiThreadDuringRead)
                failures.Add("the read ran on the UI thread");

            static int Count(ConnectionFolderSnapshot folder) =>
                folder.Connections.Count + folder.Folders.Sum(Count);

            var found = Count(snapshot);
            if (found != expected)
                failures.Add($"expected {expected} connections in the snapshot, found {found}");
            if (snapshot.Folders.Count != foldersCount)
                failures.Add($"expected {foldersCount} folders, found {snapshot.Folders.Count}");
            if (snapshot.Folders.Any(folder => folder.Connections.Count != perFolder))
                failures.Add("a folder came back with the wrong number of connections");
            if (snapshot.Connections.Any(entry => entry.Connection.Host.Length == 0))
                failures.Add("a connection came back unparsed");

            // While a read of that tree is in flight, the dispatcher has to keep running.
            var ticks = 0;
            var beat = await OnUiAsync(() =>
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
                timer.Tick += (_, _) => ticks++;
                timer.Start();
                return timer;
            });
            var reread = Task.Run(() =>
            {
                for (var i = 0; i < 5; i++)
                    store.ReadTree();
                return true;
            });
            await reread.ConfigureAwait(false);
            await OnUiAsync(() => { beat.Stop(); return true; });

            if (ticks == 0)
                failures.Add("the dispatcher did not run while the tree was being read");

            // SanitizeName decides the file name behind every connection, so the faster
            // lookup has to produce byte-identical results to the scan it replaced.
            static string ReferenceSanitize(string name)
            {
                if (string.IsNullOrWhiteSpace(name))
                    return "Unnamed";
                var invalid = Path.GetInvalidFileNameChars();
                var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
                return string.IsNullOrEmpty(cleaned) ? "Unnamed" : cleaned;
            }

            string[] names =
            [
                "", "   ", "plain", " padded ", "a/b", "a\\b", "a:b", "a*b?", "a\"b", "a<b>c|d",
                "\t\n", "...", "名字", "emoji 🚀", "trailing ", new string('/', 8),
                "mixed 名字/with:bad*chars", "control",
            ];
            var sanitizeMismatches = names
                .Where(name => ConnectionStore.SanitizeName(name) != ReferenceSanitize(name))
                .ToArray();
            if (sanitizeMismatches.Length != 0)
            {
                failures.Add(
                    "SanitizeName disagrees with the reference for: "
                    + string.Join(", ", sanitizeMismatches.Select(n => $"\"{n}\"")));
            }

            var passed = failures.Count == 0;
            var report =
                $"{(passed ? "PASS" : "FAIL")}: the connection tree is read off the UI thread\n"
                + $"sanitizeNamesChecked={names.Length}\n"
                + $"connections={found}\n"
                + $"folders={snapshot.Folders.Count}\n"
                + $"ranOffUiThread={!uiThreadDuringRead}\n"
                + $"dispatcherTicksDuringRead={ticks}\n"
                + $"failures={failures.Count}"
                + (passed ? "" : "\n" + string.Join("\n", failures));
            return ToolText(report, isError: !passed);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The file watcher starts background reloads fire-and-forget, and on a network or
    /// file-synced folder they finish in unpredictable order. Whichever read *started*
    /// last saw the newest state, so any other one must be discarded — otherwise a slow
    /// earlier read lands afterwards and puts back nodes that were deleted or renamed.
    /// Drives exactly that interleaving with a read whose duration the probe controls.
    /// </summary>
    private static async Task<JsonObject> ConnectionTreeReloadOrderCheckAsync()
    {
        var vm = await OnUiAsync(() =>
            Desktop?.MainWindow?.DataContext as ViewModels.MainWindowViewModel);
        if (vm is null)
            return ToolText("FAIL: MainWindowViewModel is not available.", isError: true);

        var root = vm.RootPath;
        static ConnectionFolderSnapshot Snapshot(string root, params string[] names) =>
            new(
                root,
                [],
                names
                    .Select(name => new ConnectionFileSnapshot(
                        Path.Combine(root, name + ConnectionStore.FileExtension),
                        new Connection
                        {
                            Type = ConnectionType.Ssh,
                            Name = name,
                            Host = "probe.invalid",
                            Port = 22,
                            Username = "probe",
                        }))
                    .ToArray());

        // The slow read is the OLDER one: it starts first and finishes last, which is
        // precisely the case that used to win and resurrect the removed connection.
        const string removed = "_probe_removed_by_newer_read";
        const string kept = "_probe_only_in_newer_read";

        var failures = new List<string>();
        var discardedBefore = vm.StaleTreeReloadsDiscardedForDebug;
        Task? slow = null;
        Task? fast = null;
        try
        {
            await OnUiAsync(() =>
            {
                vm.TreeReadOverrideForDebug = () =>
                {
                    Thread.Sleep(1500);
                    return Snapshot(root, removed);
                };
                slow = vm.DebugReloadTreeInBackgroundAsync();
                return true;
            });

            // Start the newer read a moment later, and let it return immediately.
            await Task.Delay(200);
            await OnUiAsync(() =>
            {
                vm.TreeReadOverrideForDebug = () => Snapshot(root, kept);
                fast = vm.DebugReloadTreeInBackgroundAsync();
                return true;
            });

            if (fast is not null)
                await fast;
            var afterNewerRead = await OnUiAsync(() => vm.Nodes.Select(node => node.Name).ToArray());

            if (slow is not null)
                await slow;
            // Give the discarded continuation a turn before sampling the tree again.
            await Task.Delay(200);
            var afterOlderRead = await OnUiAsync(() => vm.Nodes.Select(node => node.Name).ToArray());

            if (!afterNewerRead.Contains(kept))
                failures.Add($"newer read did not apply: [{string.Join(", ", afterNewerRead)}]");
            if (!afterOlderRead.Contains(kept))
                failures.Add($"newer read was overwritten: [{string.Join(", ", afterOlderRead)}]");
            if (afterOlderRead.Contains(removed))
                failures.Add($"stale read resurrected a removed node: [{string.Join(", ", afterOlderRead)}]");
            if (vm.StaleTreeReloadsDiscardedForDebug == discardedBefore)
                failures.Add("the stale read was applied instead of discarded");

            var passed = failures.Count == 0;
            var report =
                $"{(passed ? "PASS" : "FAIL")}: an older tree read cannot overwrite a newer one\n"
                + $"afterNewerRead=[{string.Join(", ", afterNewerRead)}]\n"
                + $"afterOlderRead=[{string.Join(", ", afterOlderRead)}]\n"
                + $"staleReadsDiscarded={vm.StaleTreeReloadsDiscardedForDebug - discardedBefore}\n"
                + $"failures={failures.Count}"
                + (passed ? "" : "\n" + string.Join("\n", failures));
            return ToolText(report, isError: !passed);
        }
        finally
        {
            // Always hand the real store back and rebuild from disk.
            await OnUiAsync(() =>
            {
                vm.TreeReadOverrideForDebug = null;
                vm.ReloadTreeFromDisk();
                return true;
            });
        }
    }

    /// <summary>
    /// The watcher used to ignore every event for a second after one of the app's own
    /// writes, so an external change landing in that window (a sync client, another
    /// instance) never reached the tree. Makes an own write, drops a file in from outside
    /// the store a moment later, and verifies the tree shows it — then that an own write
    /// on its own is still recognised and does not trigger a second reload.
    /// </summary>
    private static async Task<JsonObject> ConnectionExternalChangeCheckAsync()
    {
        const string folder = "_watcher_external_selftest";
        var failures = new List<string>();
        var vm = await OnUiAsync(() => Desktop?.MainWindow?.DataContext as ViewModels.MainWindowViewModel)
            .ConfigureAwait(false);
        if (vm is null)
            return ToolText("FAIL: main window view model not available.", isError: true);

        var folderPath = await OnUiAsync(() => vm.Store.CreateFolder(vm.RootPath, folder)).ConfigureAwait(false);
        var externalPath = Path.Combine(folderPath, "external" + ConnectionStore.FileExtension);
        long skippedBefore = 0, skippedAfter = 0, reloadsBefore = 0, reloadsAfter = 0;
        var externalShown = false;
        try
        {
            await OnUiAsync(() =>
            {
                vm.ReloadTreeFromDisk();
                return true;
            }).ConfigureAwait(false);
            await Task.Delay(1500).ConfigureAwait(false);

            // Own write, reflected in the tree right away as the UI does it...
            await OnUiAsync(() =>
            {
                vm.Store.Save(new Connection { Name = "own", Host = "own.invalid" }, folderPath);
                vm.ReloadTreeFromDisk();
                return true;
            }).ConfigureAwait(false);
            // ...then an external writer 200 ms later, well inside the old 1 s window.
            await Task.Delay(200).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                externalPath,
                """{"Name":"external","Host":"external.invalid","Type":"Ssh"}""").ConfigureAwait(false);
            await Task.Delay(2000).ConfigureAwait(false);
            externalShown = await OnUiAsync(() => vm.DebugTreeContains(externalPath)).ConfigureAwait(false);
            if (!externalShown)
                failures.Add("an external file written 200 ms after an own write never appeared in the tree");

            // An own write alone: the watcher must recognise it and skip the reload.
            (skippedBefore, reloadsBefore) = await OnUiAsync(
                () => (vm.WatcherReloadsSkippedForDebug, vm.TreeReloadCountForDebug)).ConfigureAwait(false);
            await OnUiAsync(() =>
            {
                vm.Store.Save(new Connection { Name = "own2", Host = "own2.invalid" }, folderPath);
                vm.ReloadTreeFromDisk();
                return true;
            }).ConfigureAwait(false);
            await Task.Delay(2000).ConfigureAwait(false);
            (skippedAfter, reloadsAfter) = await OnUiAsync(
                () => (vm.WatcherReloadsSkippedForDebug, vm.TreeReloadCountForDebug)).ConfigureAwait(false);
            if (reloadsAfter - reloadsBefore != 1)
                failures.Add($"an own write reloaded the tree {reloadsAfter - reloadsBefore} times (expected 1)");
            if (skippedAfter <= skippedBefore)
                failures.Add("the watcher never recognised the own write as already reflected");
        }
        finally
        {
            await OnUiAsync(() =>
            {
                vm.Store.DeleteFolder(folderPath);
                vm.ReloadTreeFromDisk();
                return true;
            }).ConfigureAwait(false);
        }

        var passed = failures.Count == 0;
        var report = $"{(passed ? "PASS" : "FAIL")}: external changes next to own writes reach the tree\n"
            + $"externalShown={externalShown}\nownWriteReloads={reloadsAfter - reloadsBefore}\n"
            + $"watcherSkips={skippedAfter - skippedBefore}\nfailures={failures.Count}"
            + (passed ? "" : "\n" + string.Join("\n", failures));
        return ToolText(report, isError: !passed);
    }

    /// <summary>
    /// Every tree action rebuilds the tree synchronously on the UI thread, and used to read
    /// every connection file to do it. Verifies a repeat read takes unchanged files from the
    /// cache, that a file changed on disk is read again (and its new content shows), and
    /// that fresh Connection objects are handed out each time.
    /// </summary>
    private static async Task<JsonObject> ConnectionTreeReadCacheCheckAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "jrm-read-cache-" + Guid.NewGuid().ToString("N"));
        var failures = new List<string>();
        long firstReads = 0, repeatReads = 0, changedReads = 0;
        try
        {
            await Task.Run(() =>
            {
                var store = new ConnectionStore(root);
                var folder = store.CreateFolder(root, "group");
                for (var i = 0; i < 5; i++)
                    store.Save(new Connection { Name = $"c{i}", Host = $"h{i}.invalid" }, folder);

                static IEnumerable<ConnectionFileSnapshot> All(ConnectionFolderSnapshot folder) =>
                    folder.Connections.Concat(folder.Folders.SelectMany(All));

                var baseline = store.ConnectionFileReadsForDebug;
                var first = store.ReadTree();
                firstReads = store.ConnectionFileReadsForDebug - baseline;

                baseline = store.ConnectionFileReadsForDebug;
                var repeat = store.ReadTree();
                repeatReads = store.ConnectionFileReadsForDebug - baseline;

                var target = All(repeat).First(c => c.Connection.Name == "c2");
                if (ReferenceEquals(target.Connection, All(first).First(c => c.Connection.Name == "c2").Connection))
                    failures.Add("a repeat read handed out the same Connection instance");

                var text = File.ReadAllText(target.Path).Replace("h2.invalid", "changed-host.invalid");
                File.WriteAllText(target.Path, text);
                baseline = store.ConnectionFileReadsForDebug;
                var changed = store.ReadTree();
                changedReads = store.ConnectionFileReadsForDebug - baseline;
                if (All(changed).First(c => c.Connection.Name == "c2").Connection.Host != "changed-host.invalid")
                    failures.Add("the changed file's new content did not show up");
            }).ConfigureAwait(false);

            if (firstReads != 5)
                failures.Add($"first read loaded {firstReads} files (expected 5)");
            if (repeatReads != 0)
                failures.Add($"repeat read loaded {repeatReads} files (expected 0)");
            if (changedReads != 1)
                failures.Add($"read after one change loaded {changedReads} files (expected 1)");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }

        var passed = failures.Count == 0;
        var report = $"{(passed ? "PASS" : "FAIL")}: tree reads reuse unchanged connection files\n"
            + $"firstReads={firstReads}\nrepeatReads={repeatReads}\nchangedReads={changedReads}\n"
            + $"failures={failures.Count}"
            + (passed ? "" : "\n" + string.Join("\n", failures));
        return ToolText(report, isError: !passed);
    }

    /// <summary>
    /// Each product-MCP write should rebuild the tree exactly once, from the explicit
    /// reload the handler performs. The file watcher recognises the app's own writes by
    /// comparing against ConnectionStore.KnownSignature — which is per-instance, so a
    /// handler that built its own store left the watcher firing a second, redundant
    /// full-tree rebuild a moment later.
    /// </summary>
    private static async Task<JsonObject> ConnectionWriteWatcherCheckAsync()
    {
        const string folder = "_mcp_watcher_selftest";
        const string connection = folder + "/probe";

        var pipeName = ProductMcpServer.PipeName;
        if (pipeName.Length == 0)
            return ToolText("FAIL: the product MCP server is not listening.", isError: true);

        static Task<long> ReloadCountAsync() => OnUiAsync(() =>
            (Desktop?.MainWindow?.DataContext as ViewModels.MainWindowViewModel)
            ?.TreeReloadCountForDebug ?? -1);

        var measurements = new List<(string Step, long Reloads)>();
        var failures = new List<string>();

        await using var session = await OpenPipeSessionAsync(pipeName).ConfigureAwait(false);
        await session.CallAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""")
            .ConfigureAwait(false);

        async Task<string> Measure(string step, string request)
        {
            // Let any watcher event from the previous step land before taking a baseline.
            await Task.Delay(1200).ConfigureAwait(false);
            var before = await ReloadCountAsync().ConfigureAwait(false);
            // Bounded, so a write that stops on a dialog fails the probe instead of hanging it.
            var response = await session.CallAsync(request)
                .WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            // Longer than the 400 ms watcher debounce plus the 1 s self-write window, so a
            // watcher-driven reload has every chance to show up before we count.
            await Task.Delay(1800).ConfigureAwait(false);
            var after = await ReloadCountAsync().ConfigureAwait(false);
            var reloads = after - before;
            measurements.Add((step, reloads));
            if (reloads != 1)
                failures.Add($"{step}: expected 1 tree reload, saw {reloads}");
            return response;
        }

        var rootPath = await OnUiAsync(() =>
            (Desktop?.MainWindow?.DataContext as ViewModels.MainWindowViewModel)?.RootPath ?? "").ConfigureAwait(false);

        // Deletes must be silent and recoverable: no error in the reply, the item gone from
        // disk, and one more item in the Recycle Bin of the tree's drive.
        async Task MeasureDelete(string step, string request, string diskPath)
        {
            var binBefore = RecycleBinItemCount(rootPath);
            var response = await Measure(step, request).ConfigureAwait(false);
            if (response.Contains("\"isError\":true", StringComparison.Ordinal))
                failures.Add($"{step}: tool returned an error: {response}");
            if (File.Exists(diskPath) || Directory.Exists(diskPath))
                failures.Add($"{step}: '{diskPath}' is still on disk");
            var binAfter = RecycleBinItemCount(rootPath);
            if (binAfter != binBefore + 1)
                failures.Add($"{step}: expected one new Recycle Bin item, saw {binBefore} -> {binAfter}");
        }

        static string Call(int id, string tool, string arguments) =>
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = tool,
                    ["arguments"] = JsonNode.Parse(arguments),
                },
            }.ToJsonString();

        try
        {
            await Measure(
                "connection_create",
                Call(10, "connection_create",
                    $$"""
                      {"name":"probe","folder":"{{folder}}","type":"SSH",
                       "host":"probe.invalid","port":22,"username":"probe"}
                      """));
            await Measure(
                "connection_update",
                Call(11, "connection_update",
                    $$"""{"connection":"{{connection}}","host":"probe2.invalid"}"""));
            // connection_set_secret is deliberately left out: it only writes when the
            // master password is unlocked, so its reload count is not deterministic here.
            await Measure(
                "connection_move",
                Call(13, "connection_move",
                    $$"""{"connection":"{{connection}}","folder":""}"""));
            await MeasureDelete(
                "connection_delete",
                Call(14, "connection_delete", """{"connection":"probe"}"""),
                Path.Combine(rootPath, "probe" + ConnectionStore.FileExtension));
            await MeasureDelete(
                "folder_delete",
                Call(15, "folder_delete", $$"""{"folder":"{{folder}}"}"""),
                Path.Combine(rootPath, folder));
        }
        catch (TimeoutException)
        {
            failures.Add("a product MCP write did not reply within 15 s (blocked on a dialog?)");
        }
        finally
        {
            // Normally a no-op; removes leftovers when a step above failed.
            await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow?.DataContext is ViewModels.MainWindowViewModel vm)
                {
                    vm.Store.DeleteFile(Path.Combine(vm.RootPath, "probe" + ConnectionStore.FileExtension));
                    vm.Store.DeleteFolder(Path.Combine(vm.RootPath, folder));
                    vm.ReloadTreeFromDisk();
                }
                return true;
            }).ConfigureAwait(false);
        }

        var passed = failures.Count == 0;
        var report =
            $"{(passed ? "PASS" : "FAIL")}: product MCP writes reload the tree once, deletes go to the Recycle Bin\n"
            + string.Join("\n", measurements.Select(m => $"{m.Step}: reloads={m.Reloads}"))
            + $"\nfailures={failures.Count}"
            + (passed ? "" : "\n" + string.Join("\n", failures));
        return ToolText(report, isError: !passed);
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    /// <summary>Items in the Recycle Bin of the drive holding <paramref name="path"/>, or -1.</summary>
    private static long RecycleBinItemCount(string path)
    {
        var info = new SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(Path.GetPathRoot(Path.GetFullPath(path)) ?? path, ref info) == 0
            ? info.i64NumItems
            : -1;
    }

    private static async Task<JsonObject> ConnectionEditorSwitchCheckAsync()
    {
        var (passed, report) = await OnUiAsync(() =>
        {
            if (ResolveRoot("MainVm") is not MainWindowViewModel vm)
                return (false, "FAIL: MainWindowViewModel is not available.");

            static void Collect(
                IEnumerable<TreeNodeViewModel> nodes,
                List<TreeNodeViewModel> result)
            {
                foreach (var node in nodes)
                {
                    if (!node.IsRecent && node.Connection is { IsSsh: true })
                        result.Add(node);
                    if (node.IsFolder)
                        Collect(node.Children, result);
                }
            }

            var candidates = new List<TreeNodeViewModel>();
            Collect(vm.Nodes, candidates);
            candidates = candidates.Take(12).ToList();
            if (candidates.Count == 0)
                return (true, "PASS: no SSH connections are available for the editor switch probe.");

            vm.FlushAutoSave();
            var previous = vm.SelectedNode;
            var editorsBuilt = 0;
            var stopwatch = Stopwatch.StartNew();
            foreach (var candidate in candidates)
            {
                vm.SelectedNode = candidate;
                if (vm.Editor is not null)
                    editorsBuilt++;
            }
            stopwatch.Stop();
            if (previous is not { IsRecent: true })
                vm.SelectedNode = previous;

            var averageMs = stopwatch.Elapsed.TotalMilliseconds / candidates.Count;
            var ok = editorsBuilt == candidates.Count;
            return (
                ok,
                $"{(ok ? "PASS" : "FAIL")}: connection editor switching\n"
                + $"connections={candidates.Count}\n"
                + $"editorsBuilt={editorsBuilt}\n"
                + $"totalMs={stopwatch.Elapsed.TotalMilliseconds:0.0}\n"
                + $"averageMs={averageMs:0.0}");
        });

        return ToolText(report, isError: !passed);
    }
}
