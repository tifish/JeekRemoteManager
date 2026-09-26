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

/// <summary>MCP probes: transport, concurrency, the product surface and the offline adapter.</summary>
internal static partial class DebugMcpServer
{
    /// <summary>
    /// The adapter used to answer tools/list with an empty array while the app was closed.
    /// Only a tools/call starts the app, so an agent that began its session first saw no tools,
    /// never called one, and the product surface never came up. Runs the real adapter beside
    /// this build: offline it must advertise listChanged and list the full product contract;
    /// once a (fake) app with a different tool list appears, the next call must be followed by
    /// notifications/tools/list_changed.
    /// </summary>
    private static async Task<JsonObject> McpAdapterOfflineCheckAsync()
    {
        var adapterPath = Path.Combine(AppContext.BaseDirectory, "JeekRemoteManagerMcp.exe");
        if (!File.Exists(adapterPath))
            return ToolText($"FAIL: adapter not found at {adapterPath}", isError: true);

        var pipeName = "jrm-adapter-offline-check-" + Guid.NewGuid().ToString("N");
        var failures = new List<string>();
        var expectedTools = ProductMcpContract.BuildToolList().Count;
        var offlineTools = -1;
        var advertised = false;
        var notified = false;

        var psi = new ProcessStartInfo(adapterPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in new[] { "--surface", "product", "--pipe", pipeName, "--no-launch" })
            psi.ArgumentList.Add(arg);

        using var adapter = Process.Start(psi)!;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        async Task<JsonNode?> CallAsync(string line)
        {
            await adapter.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
            await adapter.StandardInput.FlushAsync().ConfigureAwait(false);
            var reply = await adapter.StandardOutput.ReadLineAsync(cancel.Token).ConfigureAwait(false);
            return reply is null ? null : JsonNode.Parse(reply);
        }

        try
        {
            var init = await CallAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""")
                .ConfigureAwait(false);
            advertised = init?["result"]?["capabilities"]?["tools"]?["listChanged"]?.GetValue<bool>() == true;
            if (!advertised)
                failures.Add("offline initialize did not advertise tools.listChanged");

            var list = await CallAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""").ConfigureAwait(false);
            offlineTools = list?["result"]?["tools"] is JsonArray tools ? tools.Count : -1;
            if (offlineTools != expectedTools)
                failures.Add($"offline tools/list returned {offlineTools} tools (expected {expectedTools})");

            // Now "start the app": a pipe server that serves a different tool list.
            var server = Task.Run(async () =>
            {
                await using var pipe = new NamedPipeServerStream(
                    pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancel.Token).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                while (await reader.ReadLineAsync(cancel.Token).ConfigureAwait(false) is { } line)
                {
                    var request = JsonNode.Parse(line)!;
                    JsonNode result = request["method"]?.GetValue<string>() == "tools/list"
                        ? new JsonObject { ["tools"] = new JsonArray(new JsonObject { ["name"] = "only_in_new_app" }) }
                        : new JsonObject { ["content"] = new JsonArray() };
                    await writer.WriteLineAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = request["id"]?.DeepClone(),
                        ["result"] = result,
                    }.ToJsonString()).ConfigureAwait(false);
                }
            }, cancel.Token);

            var call = await CallAsync(
                """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"connection_list","arguments":{}}}""")
                .ConfigureAwait(false);
            if (call?["id"]?.GetValue<int>() != 3)
                failures.Add($"tools/call reply was not forwarded: {call?.ToJsonString()}");

            var next = await adapter.StandardOutput.ReadLineAsync(cancel.Token).ConfigureAwait(false);
            notified = next is not null
                       && JsonNode.Parse(next)?["method"]?.GetValue<string>() == "notifications/tools/list_changed";
            if (!notified)
                failures.Add($"no tools/list_changed after reaching an app with other tools (got {next ?? "(eof)"})");

            adapter.StandardInput.Close();
            await Task.WhenAny(server, Task.Delay(5000)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { if (!adapter.HasExited) adapter.Kill(); } catch { /* ignore */ }
        }

        var passed = failures.Count == 0;
        var report = $"{(passed ? "PASS" : "FAIL")}: the adapter lists tools while the app is closed\n"
            + $"listChangedAdvertised={advertised}\nofflineTools={offlineTools}/{expectedTools}\n"
            + $"listChangedNotified={notified}\nfailures={failures.Count}"
            + (passed ? "" : "\n" + string.Join("\n", failures));
        return ToolText(report, isError: !passed);
    }

    /// <summary>
    /// Verifies the named-pipe transport from inside the app by connecting to its own pipe
    /// as an ordinary MCP client and running a handshake plus tools/list. Confirms the ACL
    /// lets this account in, the framing round-trips, and a second concurrent session is
    /// accepted while this one is open.
    /// </summary>
    private static async Task<JsonObject> McpTransportCheckAsync()
    {
        var pipeName = Host.PipeName;
        if (pipeName.Length == 0)
            return ToolText("FAIL: the pipe transport is not running (PipeName is empty).", isError: true);

        var report = new StringBuilder();
        report.AppendLine($"pipe: \\\\.\\pipe\\{pipeName}");
        report.AppendLine($"http: {(Host.Url.Length == 0 ? "(off)" : Host.Url)}");

        try
        {
            await using var first = await OpenPipeSessionAsync(pipeName).ConfigureAwait(false);
            var initialize = await first.CallAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""")
                .ConfigureAwait(false);
            var toolList = await first.CallAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""")
                .ConfigureAwait(false);

            // A second client must be served while the first session is still open.
            await using var second = await OpenPipeSessionAsync(pipeName).ConfigureAwait(false);
            var ping = await second.CallAsync("""{"jsonrpc":"2.0","id":3,"method":"ping"}""").ConfigureAwait(false);

            var toolCount = JsonNode.Parse(toolList)?["result"]?["tools"] is JsonArray tools ? tools.Count : 0;
            var handshake = initialize.Contains("\"protocolVersion\"", StringComparison.Ordinal);
            var concurrent = ping.Contains("\"id\":3", StringComparison.Ordinal);

            report.AppendLine($"initialize: {(handshake ? "ok" : "FAIL")}");
            report.AppendLine($"tools/list: {toolCount} tools");
            report.AppendLine($"concurrent session: {(concurrent ? "ok" : "FAIL")}");

            var passed = handshake && toolCount > 0 && concurrent;
            return ToolText($"{(passed ? "PASS" : "FAIL")}: MCP pipe transport\n{report.ToString().TrimEnd()}",
                isError: !passed);
        }
        catch (Exception ex)
        {
            return ToolText($"FAIL: MCP pipe transport threw {ex.GetType().Name}: {ex.Message}\n{report}",
                isError: true);
        }
    }

    /// <summary>
    /// Exercises request multiplexing and cancellation through the real stdio adapter, then
    /// checks the pipe host's expanded session capacity and idle-session reaper.
    /// </summary>
    private static async Task<JsonObject> McpConcurrencyCheckAsync()
    {
        var adapterPath = Path.Combine(AppContext.BaseDirectory, "JeekRemoteManagerMcp.exe");
        if (!File.Exists(adapterPath))
            return ToolText($"FAIL: adapter not found at {adapterPath}", isError: true);

        var pipeName = "jrm-concurrency-check-" + Guid.NewGuid().ToString("N");
        var failures = new List<string>();
        var host = new McpHost(new McpHostOptions
        {
            ServerName = "jrm-concurrency-check",
            ServerTitle = "JRM concurrency check",
            Graph = new ObjectGraph(new ObjectGraphOptions
            {
                ResolveRoot = _ => throw new InvalidOperationException("No roots."),
                RootNamesHelp = "(none)",
            }),
            PipeName = pipeName,
            DefaultPort = 0,
            MaxPipeSessions = 32,
            PipeSessionIdleTimeout = TimeSpan.FromMinutes(1),
        });
        host.AddTool("wait", async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return ToolText("unexpected completion");
        });

        var sessions = new List<NamedPipeClientStream>();
        Process? adapter = null;
        try
        {
            host.Start();
            var psi = new ProcessStartInfo(adapterPath)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardInputEncoding = new UTF8Encoding(false),
            };
            foreach (var arg in new[] { "--surface", "debug", "--pipe", pipeName, "--no-launch" })
                psi.ArgumentList.Add(arg);
            adapter = Process.Start(psi)!;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await adapter.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""")
                .ConfigureAwait(false);
            await adapter.StandardInput.FlushAsync().ConfigureAwait(false);
            _ = await adapter.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);

            await adapter.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"wait","arguments":{}}}""")
                .ConfigureAwait(false);
            await adapter.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":11,"method":"ping"}""").ConfigureAwait(false);
            await adapter.StandardInput.FlushAsync().ConfigureAwait(false);

            var first = await adapter.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (first is null || JsonNode.Parse(first)?["id"]?.GetValue<int>() != 11)
                failures.Add($"ping did not overtake the blocked tool call (got {first ?? "(eof)"})");

            await adapter.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":10,"reason":"probe"}}""")
                .ConfigureAwait(false);
            await adapter.StandardInput.FlushAsync().ConfigureAwait(false);
            var cancelled = await adapter.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            var cancelledNode = cancelled is null ? null : JsonNode.Parse(cancelled);
            if (cancelledNode?["id"]?.GetValue<int>() != 10
                || cancelledNode["error"]?["code"]?.GetValue<int>() != -32800)
            {
                failures.Add($"cancelled request did not return -32800 (got {cancelled ?? "(eof)"})");
            }

            // Keep twelve sessions connected at once; the old default stopped at eight.
            for (var i = 0; i < 12; i++)
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(3000, timeout.Token).ConfigureAwait(false);
                sessions.Add(pipe);
            }
        }
        catch (Exception ex)
        {
            failures.Add($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (adapter is not null)
            {
                try { adapter.StandardInput.Close(); } catch { /* already closed */ }
                try { if (!adapter.HasExited) adapter.Kill(); } catch { /* best effort */ }
                adapter.Dispose();
            }
            foreach (var session in sessions)
                await session.DisposeAsync().ConfigureAwait(false);
            host.Stop();
        }

        // A separate short-lived host proves a connected client with no active work releases
        // its pipe instance instead of occupying one forever.
        var idlePipeName = pipeName + "-idle";
        var idleHost = new McpHost(new McpHostOptions
        {
            ServerName = "jrm-idle-check",
            ServerTitle = "JRM idle check",
            Graph = new ObjectGraph(new ObjectGraphOptions
            {
                ResolveRoot = _ => throw new InvalidOperationException("No roots."),
                RootNamesHelp = "(none)",
            }),
            PipeName = idlePipeName,
            DefaultPort = 0,
            PipeSessionIdleTimeout = TimeSpan.FromMilliseconds(300),
        });
        try
        {
            idleHost.Start();
            await using var idle = new NamedPipeClientStream(
                ".", idlePipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await idle.ConnectAsync(3000).ConfigureAwait(false);
            using var reader = new StreamReader(idle, new UTF8Encoding(false), false, leaveOpen: true);
            using var idleTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                var line = await reader.ReadLineAsync(idleTimeout.Token).ConfigureAwait(false);
                if (line is not null)
                    failures.Add($"idle session produced unexpected data: {line}");
            }
            catch (IOException)
            {
                // Disconnection is the expected idle-reaper result.
            }
            catch (OperationCanceledException)
            {
                failures.Add("idle session was not reclaimed within 3 seconds");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"idle check {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            idleHost.Stop();
        }

        var passed = failures.Count == 0;
        return ToolText(
            $"{(passed ? "PASS" : "FAIL")}: concurrent MCP requests, cancellation, capacity, and idle reaping\n"
            + $"concurrentSessions={sessions.Count}/12\nfailures={failures.Count}"
            + (passed ? "" : "\n" + string.Join("\n", failures)),
            isError: !passed);
    }

    /// <summary>
    /// Drives the product MCP surface end to end over its own pipe, exactly as a user's agent
    /// would: create a connection, confirm passwords are write-only, set one, and check that
    /// in-session tools refuse clearly when nothing is open. Cleans up the connection it made.
    /// </summary>
    private static async Task<JsonObject> ProductMcpCheckAsync()
    {
        const string folder = "_mcp_selftest";
        const string connection = folder + "/probe";
        const string secret = "jrm-selftest-secret-2f4a";

        var pipeName = ProductMcpServer.PipeName;
        if (pipeName.Length == 0)
            return ToolText("FAIL: the product MCP server is not listening.", isError: true);

        var report = new StringBuilder();
        var failures = new List<string>();

        void Check(string name, bool ok)
        {
            report.AppendLine($"{(ok ? "ok  " : "FAIL")}: {name}");
            if (!ok)
                failures.Add(name);
        }

        try
        {
            var connectionsRoot = await OnUiAsync(() =>
                (Desktop?.MainWindow?.DataContext as ViewModels.MainWindowViewModel)?.RootPath ?? "");
            await using var session = await OpenPipeSessionAsync(pipeName).ConfigureAwait(false);

            var initialize = await session.CallAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""")
                .ConfigureAwait(false);
            Check("initialize", initialize.Contains("\"jeek-remote-manager\"", StringComparison.Ordinal));

            var toolList = await session.CallAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""")
                .ConfigureAwait(false);
            var toolCount = JsonNode.Parse(toolList)?["result"]?["tools"] is JsonArray tools ? tools.Count : 0;
            Check("tools/list advertises the connection surface", toolCount >= 18);
            Check("tools/list advertises terminal-tab reordering",
                toolList.Contains("\"session_move\"", StringComparison.Ordinal)
                && toolList.Contains("\"position\"", StringComparison.Ordinal));
            Check("debug tools stay off the product surface", !toolList.Contains("\"get_value\"", StringComparison.Ordinal));
            Check("command tools have no separate confirmation variants",
                !toolList.Contains("\"terminal_run_danger\"", StringComparison.Ordinal)
                && !toolList.Contains("\"terminal_run_batch_danger\"", StringComparison.Ordinal));

            var created = ExtractToolText(await session.CallAsync(ToolCall(3, "connection_create", new JsonObject
            {
                ["name"] = "probe",
                ["folder"] = folder,
                ["type"] = "ssh",
                ["host"] = "127.0.0.1",
                ["username"] = "selftest",
                ["notes"] = "debug probe",
            })).ConfigureAwait(false));
            Check("connection_create writes the connection", created.Contains("\"created\": true", StringComparison.Ordinal));

            var beforeSecret = await CallConnectionGetAsync(session, 4, connection).ConfigureAwait(false);
            Check("new connection has no password", beforeSecret.Contains("\"hasPassword\": false", StringComparison.Ordinal));

            var stored = ExtractToolText(await session.CallAsync(ToolCall(5, "connection_set_password", new JsonObject
            {
                ["connection"] = connection,
                ["mode"] = "value",
                ["value"] = secret,
            })).ConfigureAwait(false));
            Check("connection_set_password stores the value", stored.Contains("\"status\": \"saved\"", StringComparison.Ordinal));
            Check("set_password never echoes the secret", !stored.Contains(secret, StringComparison.Ordinal));

            var afterSecret = await CallConnectionGetAsync(session, 6, connection).ConfigureAwait(false);
            Check("connection_get reports hasPassword", afterSecret.Contains("\"hasPassword\": true", StringComparison.Ordinal));
            Check("connection_get never returns the secret", !afterSecret.Contains(secret, StringComparison.Ordinal));
            Check("connection_get never returns the encrypted blob",
                !afterSecret.Contains("EncryptedPassword", StringComparison.OrdinalIgnoreCase)
                && !afterSecret.Contains("jrm1", StringComparison.OrdinalIgnoreCase));

            var updated = ExtractToolText(await session.CallAsync(ToolCall(20, "connection_update", new JsonObject
            {
                ["connection"] = connection,
                ["host"] = "10.0.0.9",
                ["notes"] = "updated by probe",
            })).ConfigureAwait(false));
            Check("connection_update writes only the fields it was given",
                updated.Contains("\"host\": \"10.0.0.9\"", StringComparison.Ordinal)
                && updated.Contains("updated by probe", StringComparison.Ordinal)
                && updated.Contains("\"username\": \"selftest\"", StringComparison.Ordinal));
            Check("connection_update keeps the stored password", updated.Contains("\"hasPassword\": true", StringComparison.Ordinal));

            var moved = ExtractToolText(await session.CallAsync(ToolCall(21, "connection_move", new JsonObject
            {
                ["connection"] = connection,
                ["folder"] = folder + "/nested",
            })).ConfigureAwait(false));
            Check("connection_move relocates it in the tree",
                moved.Contains($"\"connection\": \"{folder}/nested/probe\"", StringComparison.Ordinal));

            var movedBack = ExtractToolText(await session.CallAsync(ToolCall(22, "connection_move", new JsonObject
            {
                ["connection"] = folder + "/nested/probe",
                ["folder"] = folder,
            })).ConfigureAwait(false));
            Check("connection_move accepts the new path afterwards",
                movedBack.Contains($"\"connection\": \"{connection}\"", StringComparison.Ordinal));

            var folderCreated = ExtractToolText(await session.CallAsync(ToolCall(23, "folder_create", new JsonObject
            {
                ["folder"] = folder + "/made-by-probe",
            })).ConfigureAwait(false));
            Check("folder_create adds a tree folder",
                folderCreated.Contains("\"status\": \"created\"", StringComparison.Ordinal)
                && Directory.Exists(Path.Combine(
                    connectionsRoot, folder, "made-by-probe")));

            var scripts = ExtractToolText(await session.CallAsync(ToolCall(24, "script_list", new JsonObject()))
                .ConfigureAwait(false));
            var scriptsNode = JsonNode.Parse(scripts);
            var suites = scriptsNode?["suites"] as JsonArray ?? [];
            Check("script_list returns suites with their scripts and parameters",
                suites.Count > 0
                && suites.Any(suite => suite?["scripts"] is JsonArray { Count: > 0 })
                && suites.All(suite => suite?["suite"] is not null && suite["parameters"] is JsonArray));
            Check("script_list returns no stored parameter values",
                suites.All(suite => (suite?["parameters"] as JsonArray ?? [])
                    .All(parameter => parameter?["type"]?.GetValue<string>() != "Secret"
                                      || (parameter["default"]?.GetValue<string>() ?? "").Length == 0)));

            var badSuite = ExtractToolText(await session.CallAsync(ToolCall(25, "script_run", new JsonObject
            {
                ["connection"] = connection,
                ["suite"] = "no-such-suite",
                ["script"] = "nope",
            })).ConfigureAwait(false));
            Check("script_run rejects an unknown suite and points at script_list",
                badSuite.Contains("script_list", StringComparison.Ordinal));

            var batch = ExtractToolText(await session.CallAsync(ToolCall(26, "script_run_batch", new JsonObject
            {
                ["connections"] = new JsonArray(connection, "nope/missing"),
                ["suite"] = "Demo",
                ["script"] = "print-all.sh",
                ["open_missing"] = false,
            })).ConfigureAwait(false));
            Check("script_run_batch reports one result per connection",
                JsonNode.Parse(batch)?["results"] is JsonArray { Count: 2 });
            Check("script_run_batch keeps going after a failed connection",
                batch.Contains("\"total\": 2", StringComparison.Ordinal)
                && batch.Contains("has no open session", StringComparison.Ordinal));

            var commandBatch = ExtractToolText(await session.CallAsync(ToolCall(
                32,
                "terminal_run_batch",
                new JsonObject
                {
                    ["connections"] = new JsonArray(connection, "nope/missing"),
                    ["command"] = "printf '%s\n' 'rm -rf /tmp/jrm-command-probe'",
                    ["open_missing"] = false,
                    ["max_parallel"] = 2,
                })).ConfigureAwait(false));
            Check("terminal_run_batch accepts command text without confirmation and reports each connection",
                JsonNode.Parse(commandBatch)?["results"] is JsonArray { Count: 2 });
            Check("terminal_run_batch keeps going after a failed connection",
                commandBatch.Contains("\"total\": 2", StringComparison.Ordinal)
                && commandBatch.Contains("\"status\": \"error\"", StringComparison.Ordinal)
                && commandBatch.Contains("has no open session", StringComparison.Ordinal));

            var keyMissing = ExtractToolText(await session.CallAsync(ToolCall(27, "public_key_install", new JsonObject
            {
                ["connection"] = connection,
                ["public_key_path"] = "Z:\no-such-key.pub",
            })).ConfigureAwait(false));
            Check("public_key_install rejects a missing key file",
                keyMissing.Contains("No public key file", StringComparison.Ordinal));

            var folderMoved = ExtractToolText(await session.CallAsync(ToolCall(28, "folder_move", new JsonObject
            {
                ["folder"] = folder + "/made-by-probe",
                ["name"] = "renamed-by-probe",
            })).ConfigureAwait(false));
            Check("folder_move renames a folder in place",
                folderMoved.Contains("renamed-by-probe", StringComparison.Ordinal)
                && Directory.Exists(Path.Combine(connectionsRoot, folder, "renamed-by-probe")));

            var importBadSource = ExtractToolText(await session.CallAsync(ToolCall(29, "connections_import", new JsonObject
            {
                ["source"] = "putty",
                ["path"] = connectionsRoot,
            })).ConfigureAwait(false));
            Check("connections_import names the sources it supports",
                importBadSource.Contains("xshell", StringComparison.Ordinal)
                && importBadSource.Contains("finalshell", StringComparison.Ordinal));

            var hosts = ExtractToolText(await session.CallAsync(ToolCall(30, "known_hosts_list", new JsonObject()))
                .ConfigureAwait(false));
            Check("known_hosts_list returns the trusted fingerprints",
                JsonNode.Parse(hosts)?["hosts"] is JsonArray);

            var forget = ExtractToolText(await session.CallAsync(ToolCall(31, "known_hosts_forget", new JsonObject
            {
                ["host"] = "no-such-host.invalid",
                ["port"] = 22,
            })).ConfigureAwait(false));
            Check("known_hosts_forget reports an unknown host instead of failing",
                forget.Contains("not_stored", StringComparison.Ordinal));

            var noSession = ExtractToolText(await session.CallAsync(ToolCall(7, "terminal_run", new JsonObject
            {
                ["session"] = "nope/none",
                ["command"] = "echo hi",
            })).ConfigureAwait(false));
            Check("in-session tools refuse clearly without a session",
                noSession.Contains("session_list", StringComparison.Ordinal)
                || noSession.Contains("session_open", StringComparison.Ordinal));

            var prompt = ExtractToolText(await session.CallAsync(ToolCall(8, "connection_set_password", new JsonObject
            {
                ["connection"] = connection,
                ["mode"] = "prompt",
            })).ConfigureAwait(false));
            Check("prompt mode hands the secret entry to the GUI",
                prompt.Contains("awaiting_user", StringComparison.Ordinal));

            // Session lifecycle against 127.0.0.1, which has no sshd here: the tab opens and
            // stays un-live, which is exactly the addressing path we need to exercise without
            // touching one of the user's real servers.
            var opened = ExtractToolText(await session.CallAsync(ToolCall(9, "session_open", new JsonObject
            {
                ["connection"] = connection,
                ["activate"] = false,
                ["wait_seconds"] = 2,
            })).ConfigureAwait(false));
            Check("session_open returns the tree-path session id",
                opened.Contains($"\"session\": \"{connection}\"", StringComparison.Ordinal));
            Check("session_open reports a status", opened.Contains("\"status\"", StringComparison.Ordinal));

            var listed = ExtractToolText(await session.CallAsync(ToolCall(10, "session_list", new JsonObject()))
                .ConfigureAwait(false));
            Check("session_list shows the new session", listed.Contains(connection, StringComparison.Ordinal));

            var openedSecond = ExtractToolText(await session.CallAsync(ToolCall(32, "session_open", new JsonObject
            {
                ["connection"] = connection,
                ["activate"] = false,
                ["wait_seconds"] = 1,
            })).ConfigureAwait(false));
            var secondSession = connection + " (2)";
            Check("session_open creates an addressable second tab",
                openedSecond.Contains($"\"session\": \"{secondSession}\"", StringComparison.Ordinal));

            var sessionMoved = ExtractToolText(await session.CallAsync(ToolCall(33, "session_move", new JsonObject
            {
                ["session"] = secondSession,
                ["position"] = 0,
            })).ConfigureAwait(false));
            var movedNode = JsonNode.Parse(sessionMoved);
            Check("session_move returns the new terminal-tab order",
                movedNode?["position"]?.GetValue<int>() == 0
                && movedNode?["sessions"] is JsonArray movedSessions
                && movedSessions.FirstOrDefault()?.GetValue<string>() == secondSession);

            var listedAfterMove = ExtractToolText(
                await session.CallAsync(ToolCall(34, "session_list", new JsonObject()))
                    .ConfigureAwait(false));
            Check("session_list reflects the moved tab",
                JsonNode.Parse(listedAfterMove)?["sessions"] is JsonArray reorderedSessions
                && reorderedSessions.FirstOrDefault()?["session"]?.GetValue<string>() == secondSession);

            var invalidMove = ExtractToolText(await session.CallAsync(ToolCall(35, "session_move", new JsonObject
            {
                ["session"] = secondSession,
                ["position"] = 999,
            })).ConfigureAwait(false));
            Check("session_move rejects an out-of-range position",
                invalidMove.Contains("must be between", StringComparison.Ordinal));

            var status = ExtractToolText(await session.CallAsync(ToolCall(11, "terminal_status", new JsonObject
            {
                ["session"] = connection,
            })).ConfigureAwait(false));
            Check("in-session tools resolve by session id", status.Length > 0
                && !status.Contains("No open session", StringComparison.Ordinal));

            var byConnection = ExtractToolText(await session.CallAsync(ToolCall(12, "terminal_status", new JsonObject
            {
                ["connection"] = connection,
            })).ConfigureAwait(false));
            Check("in-session tools resolve by connection path",
                !byConnection.Contains("has no open session", StringComparison.Ordinal));

            // Leave the probe tab addressable but close its transport. This exercises the
            // real command handler without sending anything to a shell or waiting for login.
            await OnUiAsync(() =>
            {
                ((Views.MainWindow)Desktop!.MainWindow!).EnumerateTerminalSessions()
                    .First(item => item.SessionId == connection).View!.Close();
                return true;
            }).ConfigureAwait(false);
            var commandResult = ExtractToolText(await session.CallAsync(ToolCall(37, "terminal_run", new JsonObject
            {
                ["session"] = connection,
                ["command"] = "printf '%s\\n' 'rm -rf /tmp/jrm-command-probe'",
            })).ConfigureAwait(false));
            Check("terminal_run forwards command text directly to the terminal without confirmation",
                commandResult == "[terminal closed]");

            var closed = ExtractToolText(await session.CallAsync(ToolCall(13, "session_close", new JsonObject
            {
                ["session"] = connection,
            })).ConfigureAwait(false));
            Check("session_close closes the tab", closed.Contains("Closed session", StringComparison.Ordinal));

            var closedSecond = ExtractToolText(await session.CallAsync(ToolCall(36, "session_close", new JsonObject
            {
                ["session"] = secondSession,
            })).ConfigureAwait(false));
            Check("session_close closes the reordered tab",
                closedSecond.Contains("Closed session", StringComparison.Ordinal));

            var afterClose = ExtractToolText(await session.CallAsync(ToolCall(14, "session_list", new JsonObject()))
                .ConfigureAwait(false));
            Check("session_list drops the closed session", !afterClose.Contains(connection, StringComparison.Ordinal));

            var passed = failures.Count == 0;
            return ToolText($"{(passed ? "PASS" : "FAIL")}: product MCP surface\n{report.ToString().TrimEnd()}",
                isError: !passed);
        }
        catch (Exception ex)
        {
            return ToolText($"FAIL: product MCP surface threw {ex.GetType().Name}: {ex.Message}\n{report}",
                isError: true);
        }
        finally
        {
            await CleanupSelfTestConnectionAsync(folder).ConfigureAwait(false);
        }
    }

    private static async Task<PipeProbeSession> OpenPipeSessionAsync(string pipeName)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000).ConfigureAwait(false);
        return new PipeProbeSession(pipe);
    }
}
