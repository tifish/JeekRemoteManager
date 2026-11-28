using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;
using JeekTools;

namespace JeekRemoteManager.Tests;

public sealed class AuditRegressionTests
{
    [Fact]
    public void Port_forwarding_parses_bracketed_ipv6_endpoints_in_split_and_compact_forms()
    {
        var specs = SshPortForwarding.Parse(
            "L [::1]:8080 [::1]:5432\n"
            + "L [::1]:8081:[2001:db8::1]:5433\n"
            + "R [::]:9000 [2001:db8::2]:80\n"
            + "D [::1]:1080\n");

        Assert.Equal(4, specs.Count);
        Assert.Equal(new PortForwardSpec('L', "::1", 8080, "::1", 5432), specs[0]);
        Assert.Equal(new PortForwardSpec('L', "::1", 8081, "2001:db8::1", 5433), specs[1]);
        Assert.Equal(new PortForwardSpec('R', "::", 9000, "2001:db8::2", 80), specs[2]);
        Assert.Equal(new PortForwardSpec('D', "::1", 1080, "", 0), specs[3]);
    }

    [Fact]
    public void Login_command_sequence_validates_reuse_directives_without_duplicate()
    {
        Assert.Empty(LoginCommandSequence.Validate("#reuse-enter\n1\n#reuse-leave\nexit"));
        Assert.NotEmpty(LoginCommandSequence.Validate("#reuse-leave\nexit\n#reuse-enter\n1"));
    }

    [Fact]
    public void Login_key_sequence_rejects_non_ascii_letters()
    {
        Assert.True(LoginKeySequence.TryParse("Ctrl+c", out var sequence, out var error));
        Assert.Equal("\u0003", sequence);
        Assert.Equal("", error);
        Assert.False(LoginKeySequence.TryParse("Ctrl+中", out _, out _));
        Assert.False(LoginKeySequence.TryParse("Alt+中", out _, out _));
    }

    [Fact]
    public void Terminal_dim_color_filter_preserves_private_and_colon_csi_and_handles_sequential_sgr()
    {
        var filter = new TerminalDimColorFilter();

        var privateCsi = Encoding.UTF8.GetBytes("\u001b[>4;2m\u001b[?25h");
        Assert.Equal("\u001b[>4;2m\u001b[?25h", Encoding.UTF8.GetString(filter.Process(privateCsi)));

        var colonSgr = Encoding.UTF8.GetBytes("\u001b[38:2::10:20:30m");
        Assert.Equal("\u001b[38:2::10:20:30m", Encoding.UTF8.GetString(filter.Process(colonSgr)));

        // 0;2 resets then sets dim -> soft gray applied.
        var resetThenDim = Encoding.UTF8.GetString(filter.Process(Encoding.UTF8.GetBytes("\u001b[0;2m")));
        Assert.Contains("38;2;168;168;168", resetThenDim, StringComparison.Ordinal);

        // 2;0 sets dim then resets -> ends with reset and no trailing dim gray.
        var dimThenReset = Encoding.UTF8.GetString(filter.Process(Encoding.UTF8.GetBytes("\u001b[2;0m")));
        Assert.DoesNotContain("38;2;168;168;168", dimThenReset, StringComparison.Ordinal);
        Assert.Equal("\u001b[0m", dimThenReset);

        // 2;22 sets dim then cancels dim -> no dim gray.
        var dimThenNormal = Encoding.UTF8.GetString(filter.Process(Encoding.UTF8.GetBytes("\u001b[2;22m")));
        Assert.DoesNotContain("38;2;168;168;168", dimThenNormal, StringComparison.Ordinal);
    }

    [Fact]
    public void Zmodem_file_info_round_trips_octal_mtime_and_guards_out_of_range()
    {
        var timestamp = new DateTime(2025, 4, 15, 12, 30, 45, DateTimeKind.Utc);
        var built = ZmodemFileInfo.Build("report.txt", 1234, timestamp);
        var parsed = ZmodemFileInfo.Parse(built);

        Assert.Equal("report.txt", parsed.FileName);
        Assert.Equal(1234, parsed.Size);
        Assert.Equal(new DateTimeOffset(timestamp), parsed.ModifiedAt);

        // Out-of-range octal timestamp must not throw.
        var huge = Encoding.ASCII.GetBytes("huge.bin\010 7777777777777777777 100644\0");
        var hugeParsed = ZmodemFileInfo.Parse(huge);
        Assert.Equal("huge.bin", hugeParsed.FileName);
        Assert.Null(hugeParsed.ModifiedAt);
    }

    [Fact]
    public void Interactive_shell_payload_monitor_withholds_split_exit_marker_prefix()
    {
        var payload = InteractiveShellPayloadRunner.Build("echo hi");
        var monitor = new InteractiveShellPayloadMonitor(payload);

        var markerPrefix = payload.ExitMarkerPrefix;
        var splitAt = markerPrefix.Length / 2;
        var part1 = $"{payload.ReadyMarker}\n{payload.BeginMarker}\nhello\n{markerPrefix[..splitAt]}";
        var part2 = $"{markerPrefix[splitAt..]}0\n";

        var display1 = Encoding.UTF8.GetString(monitor.Append(Encoding.UTF8.GetBytes(part1)));
        var display2 = Encoding.UTF8.GetString(monitor.Append(Encoding.UTF8.GetBytes(part2)));

        Assert.Contains("hello", display1, StringComparison.Ordinal);
        Assert.DoesNotContain(markerPrefix[..splitAt], display1, StringComparison.Ordinal);
        Assert.DoesNotContain(markerPrefix, display1 + display2, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-rwxr-xr-x", "755")]
    [InlineData("-rwsr-xr-x", "4755")]
    [InlineData("-rwSr-xr-x", "4655")]
    [InlineData("-rwxr-sr-x", "2755")]
    [InlineData("-rwxr-Sr-x", "2745")]
    [InlineData("drwxrwxrwt", "1777")]
    [InlineData("drwxrwxrwT", "1776")]
    public void File_browser_octal_permissions_handle_special_mode_bits(string symbolic, string expected) =>
        Assert.Equal(expected, FileBrowserViewModel.OctalFromPermissions(symbolic));

    [Theory]
    [InlineData(".", "Unnamed")]
    [InlineData("..", "Unnamed")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("name. ", "name")]
    [InlineData("normal-server", "normal-server")]
    public void Connection_store_sanitize_name_blocks_reserved_and_dot_names(string raw, string expected) =>
        Assert.Equal(expected, ConnectionStore.SanitizeName(raw));

    [Fact]
    public void Connection_store_enumerates_passphrases_and_script_secret_blobs()
    {
        var root = Path.Combine(Path.GetTempPath(), "jrm-blobs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConnectionStore(root);
            var pw = MasterKeyService.EncryptWithPassword("pw", "login-secret");
            var pp = MasterKeyService.EncryptWithPassword("pw", "key-passphrase");
            var sp = MasterKeyService.EncryptWithPassword("pw", "script-secret");

            store.Save(new Connection
            {
                Name = "srv",
                EncryptedPassword = pw,
                EncryptedPrivateKeyPassphrase = pp,
                ScriptBindings =
                [
                    new ConnectionScriptBinding
                    {
                        Name = "demo",
                        Params =
                        [
                            new ConnectionScriptParameterValue { Name = "PLAIN", Value = "hello" },
                            new ConnectionScriptParameterValue { Name = "TOKEN", Value = sp },
                        ],
                    },
                ],
            }, root);

            var blobs = store.EnumerateStoredPasswordBlobs().ToHashSet(StringComparer.Ordinal);
            Assert.Contains(pw, blobs);
            Assert.Contains(pp, blobs);
            Assert.Contains(sp, blobs);
            Assert.DoesNotContain("hello", blobs);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task McpHost_without_standard_tools_rejects_debug_tools_and_handles_slow_disconnect()
    {
        var pipeName = "JeekRemoteManager.Test." + Guid.NewGuid().ToString("N");
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new McpHost(new McpHostOptions
        {
            ServerName = "test-product",
            ServerTitle = "Test Product",
            RegisterStandardTools = false,
            PipeName = pipeName,
            DefaultPort = 0,
        });
        host.AddTool("ping_custom", _ => Task.FromResult(McpHost.ToolText("pong")));
        host.AddTool("slow_ignore_cancel", async (_, _) =>
        {
            slowStarted.TrySetResult();
            await releaseSlow.Task.ConfigureAwait(false);
            return McpHost.ToolText("late");
        });

        host.Start();
        try
        {
            await using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await client.ConnectAsync(5000);
                using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

                async Task<JsonObject> CallAsync(int id, string name)
                {
                    var req = new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = id,
                        ["method"] = "tools/call",
                        ["params"] = new JsonObject { ["name"] = name, ["arguments"] = new JsonObject() },
                    };
                    await writer.WriteLineAsync(req.ToJsonString());
                    var line = await reader.ReadLineAsync();
                    Assert.NotNull(line);
                    return JsonNode.Parse(line)!.AsObject();
                }

                var okResp = await CallAsync(1, "ping_custom");
                Assert.Equal("pong", okResp["result"]?["content"]?[0]?["text"]?.GetValue<string>());
                Assert.Null(okResp["result"]?["isError"]);

                foreach (var blocked in new[] { "describe", "get_value", "set_value", "invoke", "list_members", "read_logs" })
                {
                    var resp = await CallAsync(2, blocked);
                    Assert.True(resp["result"]?["isError"]?.GetValue<bool>() == true, $"{blocked} should be rejected");
                    Assert.Contains("Unknown tool", resp["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "");
                }

                // Trigger a slow handler that ignores cancellation, then immediately drop the pipe.
                var slowReq = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = 99,
                    ["method"] = "tools/call",
                    ["params"] = new JsonObject { ["name"] = "slow_ignore_cancel", ["arguments"] = new JsonObject() },
                };
                await writer.WriteLineAsync(slowReq.ToJsonString());
                await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }

            // Wait past the 2-second server teardown window, then release the slow handler.
            await Task.Delay(2300);
            releaseSlow.TrySetResult();
            await Task.Delay(100);
        }
        finally
        {
            releaseSlow.TrySetResult();
            host.Stop();
        }
    }

    [Fact]
    public void TerminalSessionOutputBuffer_isolates_dropped_bytes_across_generations()
    {
        var buffer = new TerminalSessionOutputBuffer();
        var chunk = new byte[1024 * 1024];
        // Flood generation 1 past MaxPendingBytes.
        for (var i = 0; i < 20; i++)
            buffer.Append(chunk, generation: 1);
        Assert.True(buffer.DroppedByteCount > 0);

        // Moving to generation 2 clears stale generation-1 dropped telemetry and does not
        // count dropped generation-1 chunks as generation-2 loss.
        buffer.Append(chunk, generation: 2);
        Assert.Equal(0, buffer.TakeDroppedByteCount());
    }

    [Fact]
    public void AgentProjectLink_rejects_malformed_json_and_unterminated_markdown_block()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jrm-audit-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var mcpPath = Path.Combine(dir, ".mcp.json");
            File.WriteAllText(mcpPath, "{ broken json");
            Assert.Throws<InvalidOperationException>(() =>
                AgentProjectLink.WriteApplicationInto(dir, [".mcp.json"]));
            Assert.Equal("{ broken json", File.ReadAllText(mcpPath));

            File.Delete(mcpPath);
            var agentsPath = Path.Combine(dir, "AGENTS.md");
            var brokenMarkdown = "Header\n<!-- BEGIN JeekRemoteManager link: application -->\nUser tail that must survive\n";
            File.WriteAllText(agentsPath, brokenMarkdown);
            Assert.Throws<InvalidOperationException>(() =>
                AgentProjectLink.WriteApplicationInto(dir, [".mcp.json"]));
            Assert.Equal(brokenMarkdown, File.ReadAllText(agentsPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PublicKeyInstaller_ensures_trailing_newline_before_appending()
    {
        var payload = PublicKeyInstaller.BuildTerminalPayload("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAITest user@host");
        Assert.Contains("tail -c 1 \"$HOME/.ssh/authorized_keys\"", payload, StringComparison.Ordinal);
        Assert.Contains("printf '\\n' >> \"$HOME/.ssh/authorized_keys\"", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalShellImporter_recursively_imports_root_and_nested_folders()
    {
        var root = Path.Combine(Path.GetTempPath(), "jrm-audit-fs-" + Guid.NewGuid().ToString("N"));
        var connDir = Path.Combine(root, "finalshell");
        var storeDir = Path.Combine(root, "store");
        Directory.CreateDirectory(Path.Combine(connDir, "grp1", "sub1"));
        try
        {
            File.WriteAllText(
                Path.Combine(connDir, "root_connect_config.json"),
                """{"conection_type":100,"name":"RootBox","host":"10.0.0.1","port":22,"user_name":"root"}""");
            File.WriteAllText(Path.Combine(connDir, "grp1", "folder.json"), """{"name":"Prod"}""");
            File.WriteAllText(Path.Combine(connDir, "grp1", "sub1", "folder.json"), """{"name":"EU"}""");
            File.WriteAllText(
                Path.Combine(connDir, "grp1", "sub1", "eu_connect_config.json"),
                """{"conection_type":100,"name":"EuBox","host":"10.0.0.2","port":22,"user_name":"ubuntu"}""");

            var store = new ConnectionStore(storeDir);
            var result = new FinalShellImporter(store).Import(connDir);
            Assert.Equal(2, result.Imported);
            Assert.Equal(2, result.Folders);
            Assert.True(File.Exists(Path.Combine(storeDir, "RootBox" + ConnectionStore.FileExtension)));
            Assert.True(File.Exists(Path.Combine(storeDir, "Prod", "EU", "EuBox" + ConnectionStore.FileExtension)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BastionLanding_detects_traditional_chinese_auth_prompts()
    {
        Assert.Equal(BastionLandingKind.AuthPrompt, BastionLanding.Classify("請輸入二次驗證碼：\n驗證碼："));
        Assert.Equal(BastionLandingKind.AuthPrompt, BastionLanding.Classify("請輸入動態口令:"));
        Assert.Equal(BastionLandingKind.AuthPrompt, BastionLanding.Classify("密碼："));
    }

    [Fact]
    public void BastionLoginProfileStore_backs_up_corrupted_file_on_reload()
    {
        var root = Path.Combine(Path.GetTempPath(), "jrm-audit-bastion-" + Guid.NewGuid().ToString("N"));
        var connectionsDir = Path.Combine(root, "Connections");
        Directory.CreateDirectory(connectionsDir);
        try
        {
            var templatePath = Path.Combine(root, "bastion-login-templates.json");
            File.WriteAllText(templatePath, "{ invalid json");

            var store = new BastionLoginProfileStore(connectionsDir);
            Assert.Empty(store.Profiles);
            Assert.True(File.Exists(templatePath + ".bak"));
            Assert.Equal("{ invalid json", File.ReadAllText(templatePath + ".bak"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FileBrowserViewModel_sanitizes_invalid_local_file_names()
    {
        Assert.Equal("a_b_c", FileBrowserViewModel.SanitizeLocalFileName("a:b*c"));
        Assert.Equal("_", FileBrowserViewModel.SanitizeLocalFileName(".."));
        Assert.Equal("_con.txt", FileBrowserViewModel.SanitizeLocalFileName("con.txt"));
        Assert.Equal(
            Path.Combine("dir_1", "file_name.txt"),
            FileBrowserViewModel.SanitizeLocalRelativePath("dir:1/file?name.txt"));
    }

    [Fact]
    public void SettingsService_normalizes_ide_run_mode_to_cli()
    {
        var root = Path.Combine(Path.GetTempPath(), "jrm-audit-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var machinePath = Path.Combine(root, "machine.json");
            var roamingPath = Path.Combine(root, "roaming.json");
            File.WriteAllText(machinePath, """{"AiRunMode":3,"AiGrokRunMode":3}""");
            File.WriteAllText(roamingPath, "{}");

            var service = new SettingsService(machinePath, roamingPath);
            Assert.Equal(AgentCliRunMode.Cli, service.Settings.AiRunMode);
            Assert.Equal(AgentCliRunMode.Cli, service.Settings.AiGrokRunMode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Duplicated_tab_mcp_configs_pin_both_connection_and_session()
    {
        var root = Path.Combine(Path.GetTempPath(), "jrm-audit-session-pin-" + Guid.NewGuid().ToString("N"));
        var connectionsRoot = Path.Combine(root, "Connections");
        var workspacesRoot = Path.Combine(root, "Workspaces");
        var projectDir = Path.Combine(root, "Project");
        Directory.CreateDirectory(Path.Combine(connectionsRoot, "vps"));
        Directory.CreateDirectory(projectDir);
        try
        {
            var sourcePath = Path.Combine(connectionsRoot, "vps", "bwg.json");
            var connection = new Connection { Name = "bwg", Host = "10.0.0.1", Username = "root" };

            var ws1 = AgentCliWorkspace.Ensure(connectionsRoot, sourcePath, connection, sessionNumber: 1, workspaceRoot: workspacesRoot);
            var mcp1 = File.ReadAllText(Path.Combine(ws1, ".mcp.json"));
            Assert.Contains("\"--connection\"", mcp1, StringComparison.Ordinal);
            Assert.DoesNotContain("\"--session\"", mcp1, StringComparison.Ordinal);

            var ws2 = AgentCliWorkspace.Ensure(connectionsRoot, sourcePath, connection, sessionNumber: 2, workspaceRoot: workspacesRoot);
            var mcp2 = File.ReadAllText(Path.Combine(ws2, ".mcp.json"));
            Assert.Contains("\"--connection\"", mcp2, StringComparison.Ordinal);
            Assert.Contains("\"vps/bwg\"", mcp2, StringComparison.Ordinal);
            Assert.Contains("\"--session\"", mcp2, StringComparison.Ordinal);
            Assert.Contains("\"vps/bwg (2)\"", mcp2, StringComparison.Ordinal);

            var link2 = AgentCliWorkspace.BuildLink(connectionsRoot, sourcePath, connection, sessionNumber: 2);
            AgentProjectLink.WriteInto(link2, projectDir, [".mcp.json"]);
            var linkedMcp = File.ReadAllText(Path.Combine(projectDir, ".mcp.json"));
            Assert.Contains("\"--connection\"", linkedMcp, StringComparison.Ordinal);
            Assert.Contains("\"--session\"", linkedMcp, StringComparison.Ordinal);
            Assert.Contains("\"vps/bwg (2)\"", linkedMcp, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Supported_agents_exclude_removed_tools_and_are_sorted_alphabetically()
    {
        var providerLabels = AgentCliCatalog.Discover().Select(p => p.Label).ToArray();
        Assert.Equal(
            [
                "Antigravity",
                "Claude",
                "Codex",
                "Cursor",
                "GitHub Copilot",
                "Grok",
                "OpenCode",
                "Pi",
                "T3 Code",
            ],
            providerLabels);

        var configLabels = AgentMcpConfigCatalog.All.Select(t => t.Label).ToArray();
        Assert.Equal(
            configLabels.OrderBy(label => label, StringComparer.OrdinalIgnoreCase).ToArray(),
            configLabels);
        Assert.DoesNotContain(AgentMcpConfigCatalog.All, t =>
            t.RelativePath is ".omp/mcp.json" or ".vscode/mcp.json" or ".zed/settings.json");

        var root = Path.Combine(Path.GetTempPath(), "jrm-agent-cleanup-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".omp"));
            Directory.CreateDirectory(Path.Combine(root, ".vscode"));
            Directory.CreateDirectory(Path.Combine(root, ".zed"));
            File.WriteAllText(Path.Combine(root, ".omp", "mcp.json"), "{}");
            File.WriteAllText(Path.Combine(root, ".vscode", "mcp.json"), "{}");
            File.WriteAllText(Path.Combine(root, ".zed", "settings.json"), "{}");

            AgentCliWorkspace.WriteProjectMcpConfigs(root, "vps/bwg");

            Assert.False(Directory.Exists(Path.Combine(root, ".omp")));
            Assert.False(Directory.Exists(Path.Combine(root, ".vscode")));
            Assert.False(Directory.Exists(Path.Combine(root, ".zed")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Global_application_mcp_writes_direct_adapter_and_preserves_existing_user_configs()
    {
        var globalLabels = AgentMcpConfigCatalog.GlobalAll.Select(t => t.Label).ToArray();
        Assert.Equal(
            globalLabels.OrderBy(label => label, StringComparer.OrdinalIgnoreCase).ToArray(),
            globalLabels);

        var root = Path.Combine(Path.GetTempPath(), "jrm-global-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var customCodexHome = Path.Combine(root, "custom-codex");
            var customPiDir = Path.Combine(root, "custom-pi");
            var env = AgentMcpConfigCatalog.GlobalConfigEnvironment.Isolated(
                root,
                new Dictionary<string, string?>
                {
                    ["CODEX_HOME"] = customCodexHome,
                    ["PI_CODING_AGENT_DIR"] = customPiDir,
                });

            // Simulate MSIX Claude Desktop package LocalCache.
            var msixClaudeDir = Path.Combine(
                root,
                "AppData",
                "Local",
                "Packages",
                "Claude_pzs8sxrjxfjjc",
                "LocalCache",
                "Roaming",
                "Claude");
            Directory.CreateDirectory(msixClaudeDir);

            // Pre-populate user configs with existing settings that must survive merge and removal.
            Directory.CreateDirectory(customCodexHome);
            File.WriteAllText(Path.Combine(customCodexHome, "config.toml"), "model = \"gpt-5\"\n");
            File.WriteAllText(
                Path.Combine(root, ".claude.json"),
                """{"numStartups": 10, "mcpServers": {"other": {"command": "npx"}}}""");

            var written = AgentProjectLink.WriteGlobalApplication(
                ["antigravity", "claude-code", "claude-desktop", "codex", "copilot", "opencode", "pi"],
                env);
            Assert.Equal(
                ["antigravity", "claude-code", "claude-desktop", "codex", "copilot", "opencode", "pi"],
                written);
            Assert.Equal(written, AgentProjectLink.ListWrittenGlobalApplicationTargetIds(env));

            // Global writes must never create AGENTS.md or the project-relative cmd launcher.
            Assert.False(File.Exists(Path.Combine(root, "AGENTS.md")));
            Assert.False(File.Exists(Path.Combine(root, AgentMcpConfigCatalog.ProjectLauncherFileName)));

            // Claude Desktop MSIX path is selected when package cache exists.
            var msixConfigPath = Path.Combine(msixClaudeDir, "claude_desktop_config.json");
            Assert.True(File.Exists(msixConfigPath));

            // Direct adapter path with no cwd and no --connection pin.
            var claudeJson = System.Text.Json.Nodes.JsonNode.Parse(
                File.ReadAllText(Path.Combine(root, ".claude.json")))!.AsObject();
            Assert.Equal(10, claudeJson["numStartups"]!.GetValue<int>());
            Assert.NotNull(claudeJson["mcpServers"]!["other"]);
            var jrmEntry = claudeJson["mcpServers"]![AgentProjectLink.ApplicationMcpServerName]!.AsObject();
            Assert.Equal(AgentWorkspaceLink.AdapterPath, jrmEntry["command"]!.GetValue<string>());
            Assert.Null(jrmEntry["cwd"]);
            Assert.DoesNotContain("--connection", jrmEntry.ToJsonString(), StringComparison.Ordinal);

            // Codex honors CODEX_HOME, preserves model, and approves tools.
            var codexToml = File.ReadAllText(Path.Combine(customCodexHome, "config.toml"));
            Assert.Contains("model = \"gpt-5\"", codexToml, StringComparison.Ordinal);
            Assert.Contains($"[mcp_servers.{AgentProjectLink.ApplicationMcpServerName}]", codexToml, StringComparison.Ordinal);
            Assert.Contains("default_tools_approval_mode = \"approve\"", codexToml, StringComparison.Ordinal);
            Assert.DoesNotContain("--connection", codexToml, StringComparison.Ordinal);

            // OpenCode uses local command array and permission allow.
            var openCodeJson = System.Text.Json.Nodes.JsonNode.Parse(
                File.ReadAllText(Path.Combine(root, ".config", "opencode", "opencode.json")))!.AsObject();
            Assert.Equal("local", openCodeJson["mcp"]![AgentProjectLink.ApplicationMcpServerName]!["type"]!.GetValue<string>());
            Assert.Equal("allow", openCodeJson["permission"]![$"{AgentProjectLink.ApplicationMcpServerName}_*"]!.GetValue<string>());

            // Copilot includes tools: ["*"].
            var copilotJson = System.Text.Json.Nodes.JsonNode.Parse(
                File.ReadAllText(Path.Combine(root, ".copilot", "mcp-config.json")))!.AsObject();
            Assert.Equal("*", copilotJson["mcpServers"]![AgentProjectLink.ApplicationMcpServerName]!["tools"]![0]!.GetValue<string>());

            // Pi honors PI_CODING_AGENT_DIR and writes standard mcpServers.
            var piJson = System.Text.Json.Nodes.JsonNode.Parse(
                File.ReadAllText(Path.Combine(customPiDir, "mcp.json")))!.AsObject();
            Assert.Equal(
                AgentWorkspaceLink.AdapterPath,
                piJson["mcpServers"]![AgentProjectLink.ApplicationMcpServerName]!["command"]!.GetValue<string>());

            // Selective update removes unchecked agents without deleting parent directories.
            AgentProjectLink.WriteGlobalApplication(["codex"], env);
            Assert.Equal(["codex"], AgentProjectLink.ListWrittenGlobalApplicationTargetIds(env));
            Assert.False(File.Exists(msixConfigPath));
            Assert.True(Directory.Exists(msixClaudeDir));

            // Remove all restores user files and leaves no JRM entries.
            AgentProjectLink.RemoveGlobalApplication(env);
            Assert.Empty(AgentProjectLink.ListWrittenGlobalApplicationTargetIds(env));
            var restoredClaude = System.Text.Json.Nodes.JsonNode.Parse(
                File.ReadAllText(Path.Combine(root, ".claude.json")))!.AsObject();
            Assert.Equal(10, restoredClaude["numStartups"]!.GetValue<int>());
            Assert.NotNull(restoredClaude["mcpServers"]!["other"]);
            Assert.Null(restoredClaude["mcpServers"]![AgentProjectLink.ApplicationMcpServerName]);
            var restoredCodex = File.ReadAllText(Path.Combine(customCodexHome, "config.toml"));
            Assert.Contains("model = \"gpt-5\"", restoredCodex, StringComparison.Ordinal);
            Assert.DoesNotContain(AgentProjectLink.ApplicationMcpServerName, restoredCodex, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
