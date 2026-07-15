using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;
using JeekRemoteManager.Views;
using SvcSystems.UI.Terminal;
using XTerm.Selection;

int failures = 0;
void Check(bool cond, string label)
{
    Console.WriteLine((cond ? "PASS  " : "FAIL  ") + label);
    if (!cond) failures++;
}

string FindRepoRoot()
{
    var dir = new DirectoryInfo(Environment.CurrentDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "JeekRemoteManager.slnx")))
            return dir.FullName;
        dir = dir.Parent;
    }

    return Environment.CurrentDirectory;
}

// Isolated temp root so we don't touch the user's real data.
var root = Path.Combine(Path.GetTempPath(), "jrm_smoke_" + Guid.NewGuid().ToString("N"));
var store = new ConnectionStore(root);

try
{
    Check(Directory.Exists(root), "Store creates its root folder");

    Check(ApplicationMenuDefinition.CommonItems.Select(item => item.Action).SequenceEqual(
          [
              ApplicationMenuAction.Settings,
              ApplicationMenuAction.ImportFromFinalShell,
              ApplicationMenuAction.CheckForUpdates,
              ApplicationMenuAction.Exit,
          ]),
          "Window and tray menus share one ordered common-action definition");
    Check(ApplicationMenuDefinition.CommonItems.Select(item => item.LocalizationKey).Distinct().Count()
          == ApplicationMenuDefinition.CommonItems.Count,
          "Shared application-menu actions use unique localization keys");

    var repoRoot = FindRepoRoot();
    var terminalViewXaml = File.ReadAllText(Path.Combine(
            repoRoot, "JeekRemoteManager", "Views", "TerminalView.axaml"))
        .Replace("\r\n", "\n", StringComparison.Ordinal);
    var mainWindowXaml = File.ReadAllText(Path.Combine(
        repoRoot, "JeekRemoteManager", "Views", "MainWindow.axaml"));
    var mainWindowCode = File.ReadAllText(Path.Combine(
        repoRoot, "JeekRemoteManager", "Views", "MainWindow.axaml.cs"));
    Check(terminalViewXaml.IndexOf("x:Name=\"AiPanelHost\"", StringComparison.Ordinal)
              < terminalViewXaml.IndexOf("x:Name=\"MonitorPanelHost\"", StringComparison.Ordinal)
          && terminalViewXaml.Contains("x:Name=\"AiPanelHost\"\n                Grid.Column=\"0\"", StringComparison.Ordinal)
          && terminalViewXaml.Contains("x:Name=\"MonitorPanelHost\"\n                Grid.Column=\"4\"", StringComparison.Ordinal),
          "AI panel is left of the terminal and monitor panel is right of it");
    Check(mainWindowXaml.IndexOf("x:Name=\"AiPanelToolbarButton\"", StringComparison.Ordinal)
              < mainWindowXaml.IndexOf("x:Name=\"MonitorToolbarButton\"", StringComparison.Ordinal),
          "Terminal toolbar places AI before monitor");
    Check(mainWindowCode.IndexOf("menu.Items.Add(aiPanel);", StringComparison.Ordinal)
              < mainWindowCode.IndexOf("menu.Items.Add(monitor);", StringComparison.Ordinal),
          "Terminal tab menu places AI before monitor");

    // --- Master-password setup (password material cached via DPAPI) ---
    // Point the DPAPI cache at the temp root so we never touch the real one.
    MasterKeyService.CachePath = Path.Combine(root, "master-password.bin");
    const string masterPassword = "Corr3ct-Horse-密码";
    var master = new MasterKeyService();
    Check(!master.IsUnlocked, "Master password starts locked");
    master.SetPassword(masterPassword);
    Check(master.IsUnlocked, "SetPassword unlocks the master password");
    Check(master.VerifyPassword(masterPassword), "VerifyPassword accepts the current master password");
    Check(!master.VerifyPassword("not-the-master"), "VerifyPassword rejects a different password");
    master.Lock();
    Check(!master.IsUnlocked && !master.VerifyPassword(masterPassword), "Lock forgets the in-memory master password");
    master.SetPassword(masterPassword);
    MasterKeyService.Current = master;

    // --- Password encryption round-trip (self-contained jrm1 envelope) ---
    const string secret = "S3cr3t!™密码";
    var enc = PasswordProtector.Encrypt(secret);
    Check(MasterKeyService.IsPasswordBlob(enc), "Encrypt produces a jrm1 blob");
    Check(enc != secret && enc.Length > MasterKeyService.BlobPrefix.Length, "Encrypt produces non-plaintext blob");
    Check(!Encoding.UTF8.GetString(Convert.FromBase64String(enc[MasterKeyService.BlobPrefix.Length..])).Contains(secret),
          "Plaintext password is not present in the encrypted blob bytes");
    Check(PasswordProtector.Encrypt(secret) != enc, "jrm1 encryption uses fresh salt/nonce each time");
    Check(PasswordProtector.Decrypt(enc) == secret, "Decrypt round-trips the password");
    Check(PasswordProtector.Encrypt("") == "", "Empty password encrypts to empty");

    // --- SSH never goes through the OS-client launcher (in-app terminal only) ---
    var sshLaunchRejected = false;
    try
    {
        new ConnectionLauncher().Launch(new Connection
        {
            Type = ConnectionType.Ssh,
            Host = "example.com",
            Username = "root",
        });
    }
    catch (NotSupportedException)
    {
        sshLaunchRejected = true;
    }
    Check(sshLaunchRejected, "ConnectionLauncher rejects SSH connections");
    Check(ConnectionType.Ssh.ToDisplayName() == "SSH" && ConnectionType.Rdp.ToDisplayName() == "RDP",
          "Connection types display as uppercase acronyms");
    var missingPageantIgnored = false;
    try
    {
        _ = SshConnectionFactory.Build(new Connection
        {
            Type = ConnectionType.Ssh,
            Host = "example.com",
            Username = "root",
            PrivateKeyPath = Path.Combine(root, "missing-key"),
        });
        missingPageantIgnored = true;
    }
    catch (InvalidOperationException ex)
    {
        missingPageantIgnored =
            ex.Message.StartsWith("No usable credential", StringComparison.Ordinal)
            && !ex.ToString().Contains("Pageant Window not found", StringComparison.Ordinal);
    }
    Check(missingPageantIgnored,
          "Missing Pageant is ignored during SSH credential discovery");

    // --- Login-command directives ---
    const string loginCommands = "first\r\n#input\nsecond\n#duplicate\nduplicate-first\n#DUPLICATE\nduplicate-second";
    Check(LoginCommandSequence.Select(loginCommands, isDuplicatedSession: false)
              .SequenceEqual(["first", "#input", "second", "duplicate-first", "duplicate-second"]),
          "Normal sessions ignore duplicate-start markers and run every command");
    Check(LoginCommandSequence.Select(loginCommands, isDuplicatedSession: true)
              .SequenceEqual(["duplicate-first", "duplicate-second"]),
          "Duplicated sessions start after the first duplicate marker");
    Check(LoginCommandSequence.Select("first\nsecond", isDuplicatedSession: true)
              .SequenceEqual(["first", "second"]),
          "Duplicated sessions without a marker preserve existing login-command behavior");
    Check(LoginCommandSequence.IsManualInputDirective("  #INPUT  "),
          "Manual-input login directive remains case-insensitive");
    var autoPanelConnection = new Connection
    {
        Type = ConnectionType.Ssh,
        Name = "auto-panels",
        Host = "example.com",
        AutoOpenMonitorPanel = true,
        AutoOpenAiPanel = true,
        AutoOpenFileBrowserPanel = true,
    };
    var autoPanelJson = JsonSerializer.Serialize(autoPanelConnection);
    var autoPanelPersisted = JsonSerializer.Deserialize<Connection>(autoPanelJson)!;
    var autoPanelEditor = ConnectionEditorViewModel.FromConnection(autoPanelConnection);
    var autoPanelRoundTrip = new Connection();
    autoPanelEditor.ApplyTo(autoPanelRoundTrip);
    Check(autoPanelEditor.AutoOpenMonitorPanel
          && autoPanelEditor.AutoOpenAiPanel
          && autoPanelEditor.AutoOpenFileBrowserPanel
          && autoPanelRoundTrip.AutoOpenMonitorPanel
          && autoPanelRoundTrip.AutoOpenAiPanel
          && autoPanelRoundTrip.AutoOpenFileBrowserPanel
          && autoPanelPersisted.AutoOpenMonitorPanel
          && autoPanelPersisted.AutoOpenAiPanel
          && autoPanelPersisted.AutoOpenFileBrowserPanel,
          "SSH auto-open panel preferences round-trip through the editor and JSON");
    using (var monitorSession = new ServerMonitorSession(
               () => null,
               Connection.DefaultTerminalType,
               loginCommands,
               _ => { },
               () => { },
               () => { }))
    {
        Check(monitorSession.ChannelMode == "PersistentDuplicatedShell"
              && !monitorSession.IsShellReady
              && monitorSession.SampleCount == 0
              && monitorSession.ShellGeneration == 0,
              "Server monitor exposes its persistent duplicated-shell strategy for Debug MCP verification");
    }
    var coloredMonitorSections = (Dictionary<string, List<string>>)typeof(ServerMonitorSession)
        .GetMethod("SplitSections", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, ["\u001b]0;monitor-title\u0007@JRM@ip\r\n\u001b[35m172.18.6.30\u001b[0m\r\n"])!;
    var parsedColoredIp = (string?)typeof(ServerMonitorSession)
        .GetMethod("ParseRemoteIp", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, [coloredMonitorSections.GetValueOrDefault("ip")]);
    Check(parsedColoredIp == "172.18.6.30",
          "Server monitor strips PTY ANSI and OSC sequences before parsing target fields");
    using (var processSortVm = new ServerMonitorViewModel(
               () => null,
               Connection.DefaultTerminalType,
               "",
               "test",
               "127.0.0.1"))
    {
        var processSnapshot = new List<ServerMonitorProcess>
        {
            new(1_000, 1, "memory-heavy"),
            new(100, 2, "filler-1"),
            new(99, 3, "filler-2"),
            new(98, 4, "filler-3"),
            new(97, 5, "filler-4"),
            new(96, 6, "filler-5"),
            new(95, 7, "filler-6"),
            new(94, 8, "filler-7"),
            new(1, 99, "cpu-heavy"),
        };
        typeof(ServerMonitorViewModel)
            .GetField("_latestProcesses", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(processSortVm, processSnapshot);
        typeof(ServerMonitorViewModel)
            .GetMethod("UpdateProcessRows", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(processSortVm, null);
        var memoryFirst = processSortVm.Processes.FirstOrDefault()?.Command;
        processSortVm.SortProcessesByCpuCommand.Execute(null);
        Check(memoryFirst == "memory-heavy"
              && processSortVm.Processes.FirstOrDefault()?.Command == "cpu-heavy"
              && processSortVm.ProcessSort == ServerMonitorProcessSort.Cpu
              && processSortVm.IsProcessSortByCpu,
              "Server monitor process list switches between memory and CPU top-eight sorting");
    }

    // --- Terminal clipboard text ---
    var softWrapTerminal = new TerminalControlModel(new TerminalOptions { Cols = 10, Rows = 5, Scrollback = 10 });
    softWrapTerminal.Feed("abcdefghijklmnop\r\nXYZ");
    softWrapTerminal.Terminal.Selection.StartSelection(0, 0, SelectionMode.Normal);
    softWrapTerminal.Terminal.Selection.UpdateSelection(5, 1);
    Check(softWrapTerminal.Terminal.Selection.GetSelectionText() == "abcdefghij\r\nklmnop",
          "XTerm selection includes CRLF at a soft wrap boundary");
    Check(TerminalClipboardText.BuildSelectedTextWithoutSoftWraps(softWrapTerminal.Terminal) == "abcdefghijklmnop",
          "Terminal clipboard text joins soft-wrapped rows");

    var hardWrapTerminal = new TerminalControlModel(new TerminalOptions { Cols = 10, Rows = 5, Scrollback = 10 });
    hardWrapTerminal.Feed("abc\r\nXYZ");
    hardWrapTerminal.Terminal.Selection.StartSelection(0, 0, SelectionMode.Normal);
    hardWrapTerminal.Terminal.Selection.UpdateSelection(2, 1);
    Check(TerminalClipboardText.BuildSelectedTextWithoutSoftWraps(hardWrapTerminal.Terminal)
          == hardWrapTerminal.Terminal.Selection.GetSelectionText(),
          "Terminal clipboard text preserves hard line breaks");
    Check(AiCommandTerminalText.NormalizeForTerminalEcho("echo one\necho two\r\necho three\r") == "echo one\r\necho two\r\necho three",
          "AI command echo uses terminal CRLF line breaks");

    var resizeOutput = new TerminalResizeOutputBuffer();
    resizeOutput.Start();
    Check(resizeOutput.TryAppend("\r"u8) && resizeOutput.TryAppend("user@host:~$ "u8)
          && resizeOutput.IsActive && resizeOutput.PendingByteCount == 14,
          "Terminal resize output buffers split prompt redraw packets");
    Check(Encoding.UTF8.GetString(resizeOutput.StopAndDrain()) == "\ruser@host:~$ "
          && !resizeOutput.IsActive && resizeOutput.PendingByteCount == 0
          && !resizeOutput.TryAppend("later"u8),
          "Terminal resize output drains atomically and then resumes direct display");

    // --- Terminal buffer resize cursor repair (maximize/shrink) ---
    static string BufferLineText(XTerm.Buffer.BufferLine? line)
    {
        if (line is null)
            return "";
        var sb = new StringBuilder(line.Length);
        foreach (var cell in line)
            sb.Append(string.IsNullOrEmpty(cell.Content) ? " " : cell.Content);
        return sb.ToString().TrimEnd();
    }

    static TerminalControlModel FeedScrollbackPrompt(int rows)
    {
        var model = new TerminalControlModel(new TerminalOptions { Cols = 40, Rows = rows, Scrollback = 100 });
        for (var i = 0; i < 25; i++)
            model.Feed($"line-{i:D2} history\r\n");
        model.Feed("prompt$ ");
        return model;
    }

    var growModel = FeedScrollbackPrompt(10);
    var growAbsBefore = growModel.Terminal.Buffer.YBase + growModel.Terminal.Buffer.Y;
    var growRelBefore = growModel.Terminal.Buffer.Y;
    growModel.Terminal.Resize(40, 20);
    // XTerm.NET leaves the cursor on older history after growing the viewport.
    Check(growModel.Terminal.Buffer.YBase + growModel.Terminal.Buffer.Y != growAbsBefore
          && BufferLineText(growModel.Terminal.Buffer.GetLine(
                 growModel.Terminal.Buffer.YBase + growModel.Terminal.Buffer.Y))
             .StartsWith("line-", StringComparison.Ordinal),
          "XTerm grow-without-repair lands the cursor on older scrollback text");
    Check(TerminalBufferResizeRepair.TryRepair(
              growModel.Terminal.Buffer, growAbsBefore, growRelBefore, 20)
          && growModel.Terminal.Buffer.YBase + growModel.Terminal.Buffer.Y == growAbsBefore
          && BufferLineText(growModel.Terminal.Buffer.GetLine(
                 growModel.Terminal.Buffer.YBase + growModel.Terminal.Buffer.Y)) == "prompt$",
          "Terminal resize repair keeps the cursor on the prompt after maximize/grow");

    var shrinkModel = FeedScrollbackPrompt(20);
    var shrinkAbsBefore = shrinkModel.Terminal.Buffer.YBase + shrinkModel.Terminal.Buffer.Y;
    var shrinkRelBefore = shrinkModel.Terminal.Buffer.Y;
    shrinkModel.Terminal.Resize(40, 10);
    Check(TerminalBufferResizeRepair.TryRepair(
              shrinkModel.Terminal.Buffer, shrinkAbsBefore, shrinkRelBefore, 10)
          && shrinkModel.Terminal.Buffer.YBase + shrinkModel.Terminal.Buffer.Y == shrinkAbsBefore
          && BufferLineText(shrinkModel.Terminal.Buffer.GetLine(
                 shrinkModel.Terminal.Buffer.YBase + shrinkModel.Terminal.Buffer.Y)) == "prompt$",
          "Terminal resize repair keeps the cursor on the prompt after shrink");

    var maximizeModel = FeedScrollbackPrompt(10);
    var maxAbsBefore = maximizeModel.Terminal.Buffer.YBase + maximizeModel.Terminal.Buffer.Y;
    var maxRelBefore = maximizeModel.Terminal.Buffer.Y;
    maximizeModel.Terminal.Resize(120, 40);
    Check(TerminalBufferResizeRepair.TryRepair(
              maximizeModel.Terminal.Buffer, maxAbsBefore, maxRelBefore, 40)
          && maximizeModel.Terminal.Buffer.YBase + maximizeModel.Terminal.Buffer.Y == maxAbsBefore
          && BufferLineText(maximizeModel.Terminal.Buffer.GetLine(
                 maximizeModel.Terminal.Buffer.YBase + maximizeModel.Terminal.Buffer.Y)) == "prompt$",
          "Terminal resize repair keeps the cursor on the prompt after large maximize");

    var noScrollbackModel = new TerminalControlModel(new TerminalOptions { Cols = 40, Rows = 10, Scrollback = 100 });
    noScrollbackModel.Feed("few lines\r\n");
    noScrollbackModel.Feed("prompt$ ");
    var noSbAbs = noScrollbackModel.Terminal.Buffer.YBase + noScrollbackModel.Terminal.Buffer.Y;
    var noSbRel = noScrollbackModel.Terminal.Buffer.Y;
    noScrollbackModel.Terminal.Resize(40, 20);
    Check(!TerminalBufferResizeRepair.TryRepair(
              noScrollbackModel.Terminal.Buffer, noSbAbs, noSbRel, 20)
          && noScrollbackModel.Terminal.Buffer.YBase + noScrollbackModel.Terminal.Buffer.Y == noSbAbs,
          "Terminal resize repair is a no-op when absolute cursor is already correct");

    // --- AI panel conversation reset ---
    var aiVm = new AgentChatViewModel(
        [
            new AgentProvider(
                "Test",
                "",
                [new AgentOption("Default", null)],
                [new AgentOption("Default", null)],
                (_, _) => null),
        ],
        () => null,
        null);
    var activeAiSession = new FakeAgentChatSession();
    typeof(AgentChatViewModel)
        .GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(aiVm, activeAiSession);
    aiVm.Messages.Add(new ChatMessageViewModel(ChatRole.User, "old context"));
    Check(aiVm.HasMessages,
          "AI chat reports a non-empty transcript after a message is added");
    aiVm.InputText = "draft";
    Check(aiVm.NewConversationCommand.CanExecute(null),
          "AI new conversation command is available while idle");
    aiVm.NewConversationCommand.Execute(null);
    Check(aiVm.Messages.Count == 0 && !aiVm.HasMessages && aiVm.InputText == "draft" && aiVm.StatusText == "",
          "AI new conversation clears the transcript and keeps the draft");
    Check(activeAiSession.DisposeCount == 1,
          "AI new conversation disposes the active session");

    var steeringSession = new FakeAgentChatSession(supportsSteering: true);
    var steerVm = new AgentChatViewModel(
        [
            new AgentProvider(
                "Codex",
                "",
                [new AgentOption("Default", null)],
                [new AgentOption("Default", null)],
                (_, _) => steeringSession),
        ],
        () => null,
        null);
    steerVm.InputText = "start the task";
    await steerVm.SendCommand.ExecuteAsync(null);
    Check(steerVm.IsBusy && steerVm.IsModelTurnActive && steerVm.CanSteer
          && steerVm.SteerCommand.CanExecute(null),
          "AI chat enables steer only during a steerable provider turn");
    steerVm.InputText = "use the safer approach";
    await steerVm.SteerCommand.ExecuteAsync(null);
    Check(steeringSession.SteeredTexts.SequenceEqual(["use the safer approach"])
          && steerVm.InputText == ""
          && steerVm.IsBusy
          && steerVm.Messages.Select(m => m.Role).SequenceEqual(
              [ChatRole.User, ChatRole.User, ChatRole.Assistant]),
          "AI steer appends to the active turn and splits the transcript at the insertion point");
    await steerVm.DisposeAsync();

    var exitedAiSession = new FakeAgentChatSession();
    typeof(AgentChatViewModel)
        .GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(aiVm, exitedAiSession);
    typeof(AgentChatViewModel)
        .GetField("_sessionStarted", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(aiVm, true);
    typeof(AgentChatViewModel)
        .GetMethod("HandleSessionExited", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(aiVm, null);
    Check(typeof(AgentChatViewModel)
              .GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!
              .GetValue(aiVm) is null
          && exitedAiSession.DisposeCount == 1,
          "AI agent exit invalidates and disposes the session");

    var authError = (bool)typeof(AgentChatViewModel)
        .GetMethod("IsAuthenticationError", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, ["Your authentication token has expired. Please login again."])!;
    var ordinaryError = (bool)typeof(AgentChatViewModel)
        .GetMethod("IsAuthenticationError", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, ["The model is temporarily overloaded."])!;
    Check(authError && !ordinaryError,
          "AI authentication errors are separated from retryable agent errors");

    var terminalMessageCount = aiVm.Messages.Count;
    aiVm.NotifyTerminalDisconnected();
    aiVm.NotifyTerminalReconnected();
    Check(aiVm.TerminalConnectionState == "connected"
          && aiVm.TerminalDisconnectNotificationCount == 1
          && aiVm.Messages.Count == terminalMessageCount + 2
          && aiVm.Messages[^2].Role == ChatRole.System
          && aiVm.Messages[^1].Role == ChatRole.System,
          "AI chat surfaces terminal disconnect and reconnect state changes");
    aiVm.NotifyTerminalReconnectFailed("network unavailable");
    Check(aiVm.TerminalConnectionState == "failed"
          && aiVm.Messages.Count == terminalMessageCount + 3
          && aiVm.Messages[^1].Role == ChatRole.System
          && aiVm.StatusText == aiVm.Messages[^1].Text,
          "AI chat surfaces automatic terminal reconnect failures");

    // --- AI conversation history persists and restores provider-native sessions ---
    var aiHistoryStore = new AiConversationStore(Path.Combine(root, "ai-conversations"));
    var savedConversation = new AiConversation
    {
        Id = "saved-codex-conversation",
        ScopeId = "ssh:user@example:22",
        ConnectionLabel = "Example server",
        Provider = "Codex",
        NativeSessionId = "codex-thread-123",
        Model = "gpt-test",
        Effort = "high",
        Title = "Investigate the server",
        Messages =
        {
            new AiConversationMessage { Role = "User", Text = "Investigate the server" },
            new AiConversationMessage { Role = "Assistant", Text = "I found the issue." },
        },
    };
    aiHistoryStore.Save(savedConversation);
    var stableConversationScope = "connection:11111111-2222-3333-4444-555555555555";
    var resumeFactorySessionId = "";
    var restoreVm = new AgentChatViewModel(
        [
            new AgentProvider(
                "Codex",
                "",
                [new AgentOption("Default", null), new AgentOption("GPT Test", "gpt-test")],
                [new AgentOption("Default", null), new AgentOption("High", "high")],
                (_, _) => new FakeAgentChatSession(),
                ResumeSessionFactory: (_, _, sessionId) =>
                {
                    resumeFactorySessionId = sessionId;
                    return new FakeAgentChatSession();
                }),
        ],
        () => null,
        null,
        conversationStore: aiHistoryStore,
        conversationScopeId: stableConversationScope,
        connectionLabel: "Example server",
        legacyConversationScopeIds: ["ssh:user@example:22"]);
    Check(restoreVm.ConversationHistory.Count == 1
          && restoreVm.ConversationHistory[0].CanRestore
          && restoreVm.ConversationStorePath == aiHistoryStore.RootPath
          && restoreVm.ConversationScopeId == stableConversationScope
          && restoreVm.MigratedConversationCount == 1
          && aiHistoryStore.Load(savedConversation.Id)?.ScopeId == stableConversationScope,
          "AI conversation history migrates from a legacy path scope to the connection GUID");
    Check(restoreVm.RestoreConversation(savedConversation.Id)
          && restoreVm.Messages.Count == 2
          && restoreVm.Messages[0].IsUser
          && restoreVm.Messages[1].IsAssistant
          && restoreVm.CurrentConversationId == savedConversation.Id
          && restoreVm.CurrentNativeSessionId == "codex-thread-123"
          && restoreVm.PendingResumeSessionId == "codex-thread-123",
          "AI conversation restore reloads the transcript and queues the native session id");
    Check(resumeFactorySessionId == "",
          "AI conversation restore remains lazy until the user sends the next message");
    restoreVm.InputText = "Continue";
    await restoreVm.SendCommand.ExecuteAsync(null);
    Check(resumeFactorySessionId == "codex-thread-123",
          "AI conversation sends the saved native id through the provider resume factory");
    restoreVm.StopCommand.Execute(null);

    var otherScopeVm = new AgentChatViewModel(
        restoreVm.Providers.ToArray(),
        () => null,
        null,
        conversationStore: aiHistoryStore,
        conversationScopeId: "ssh:user@other:22",
        connectionLabel: "Other server");
    Check(otherScopeVm.ConversationHistory.Count == 0
          && !otherScopeVm.RestoreConversation(savedConversation.Id),
          "AI conversation history cannot be restored on a different connection");

    Check(restoreVm.MoveConversationToTrash(savedConversation.Id)
          && restoreVm.CurrentConversationId is null
          && restoreVm.Messages.Count == 0
          && restoreVm.ConversationHistory.Count == 0
          && restoreVm.TrashedConversationHistory.Count == 1
          && !restoreVm.RestoreConversation(savedConversation.Id),
          "AI conversation delete moves the current session into the recycle bin");
    Check(restoreVm.RestoreConversationFromTrash(savedConversation.Id)
          && restoreVm.ConversationHistory.Count == 1
          && restoreVm.TrashedConversationHistory.Count == 0,
          "AI conversation recycle-bin entries can be restored to history");
    Check(restoreVm.MoveConversationToTrash(savedConversation.Id)
          && restoreVm.DeleteConversationPermanently(savedConversation.Id)
          && aiHistoryStore.Load(savedConversation.Id) is null,
          "AI conversation recycle-bin entries can be permanently deleted");

    var expiryStore = new AiConversationStore(Path.Combine(root, "ai-conversation-expiry"));
    var expiryNow = DateTimeOffset.UtcNow;
    expiryStore.Save(new AiConversation
    {
        Id = "expired-trash-entry",
        ScopeId = stableConversationScope,
        Title = "Expired",
        DeletedAt = expiryNow - AiConversationStore.TrashRetention - TimeSpan.FromMinutes(1),
    });
    expiryStore.Save(new AiConversation
    {
        Id = "recent-trash-entry",
        ScopeId = stableConversationScope,
        Title = "Recent",
        DeletedAt = expiryNow - AiConversationStore.TrashRetention + TimeSpan.FromMinutes(1),
    });
    Check(expiryStore.PurgeExpiredTrash(expiryNow) == 1
          && expiryStore.Load("expired-trash-entry") is null
          && expiryStore.Load("recent-trash-entry") is not null,
          "AI conversation recycle bin permanently deletes only entries older than 30 days");

    var claudeResume = new ClaudeChatSession("claude", root, resumeSessionId: "claude-session");
    var codexResume = new CodexChatSession("codex", root, resumeThreadId: "codex-thread");
    var grokResume = new GrokChatSession("grok", root, resumeSessionId: "grok-session");
    Check((string?)typeof(ClaudeChatSession).GetField("_resumeSessionId", BindingFlags.Instance | BindingFlags.NonPublic)!
              .GetValue(claudeResume) == "claude-session"
          && (string?)typeof(CodexChatSession).GetField("_resumeThreadId", BindingFlags.Instance | BindingFlags.NonPublic)!
              .GetValue(codexResume) == "codex-thread"
          && (string?)typeof(GrokChatSession).GetField("_resumeSessionId", BindingFlags.Instance | BindingFlags.NonPublic)!
              .GetValue(grokResume) == "grok-session",
          "Claude, Codex, and Grok sessions retain their provider-native resume ids");
    var grokReplayText = "";
    grokResume.TextDelta += text => grokReplayText += text;
    using (var replayUpdate = JsonDocument.Parse(
               "{\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"old answer\"}}}"))
    {
        var handleUpdate = typeof(GrokChatSession)
            .GetMethod("HandleSessionUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        handleUpdate.Invoke(grokResume, [replayUpdate.RootElement]);
        typeof(GrokChatSession)
            .GetField("_acceptPromptUpdates", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(grokResume, true);
        handleUpdate.Invoke(grokResume, [replayUpdate.RootElement]);
    }
    Check(grokReplayText == "old answer",
          "Grok ignores transcript replay during session/load but streams new prompt updates");

    var grokRetry = new GrokChatSession("grok", root);
    var grokPreview = "";
    AgentTurnResult? grokRetryResult = null;
    grokRetry.TextDelta += text => grokPreview += text;
    grokRetry.TextReplaced += text => grokPreview = text;
    grokRetry.TurnCompleted += result => grokRetryResult = result;
    typeof(GrokChatSession)
        .GetField("_acceptPromptUpdates", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(grokRetry, true);
    var handleGrokSessionUpdate = typeof(GrokChatSession)
        .GetMethod("HandleSessionUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var handleGrokRetryUpdate = typeof(GrokChatSession)
        .GetMethod("HandleGrokSessionUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var handleGrokPromptCompleted = typeof(GrokChatSession)
        .GetMethod("HandlePromptCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!;
    using (var staleThought = JsonDocument.Parse(
               "{\"update\":{\"sessionUpdate\":\"agent_thought_chunk\",\"content\":{\"type\":\"text\",\"text\":\"thinking\"}},\"_meta\":{\"streamStartMs\":100}}"))
        handleGrokSessionUpdate.Invoke(grokRetry, [staleThought.RootElement]);
    using (var staleText = JsonDocument.Parse(
               "{\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"stale tool block\"}},\"_meta\":{\"streamStartMs\":100}}"))
        handleGrokSessionUpdate.Invoke(grokRetry, [staleText.RootElement]);
    using (var retryState = JsonDocument.Parse(
               "{\"update\":{\"sessionUpdate\":\"retry_state\",\"type\":\"retrying\"},\"_meta\":{\"agentTimestampMs\":150}}"))
        handleGrokRetryUpdate.Invoke(grokRetry, [retryState.RootElement]);
    using (var lateStaleText = JsonDocument.Parse(
               "{\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"late stale block\"}},\"_meta\":{\"streamStartMs\":100}}"))
        handleGrokSessionUpdate.Invoke(grokRetry, [lateStaleText.RootElement]);
    using (var finalThought = JsonDocument.Parse(
               "{\"update\":{\"sessionUpdate\":\"agent_thought_chunk\",\"content\":{\"type\":\"text\",\"text\":\"thinking again\"}},\"_meta\":{\"streamStartMs\":200}}"))
        handleGrokSessionUpdate.Invoke(grokRetry, [finalThought.RootElement]);
    using (var finalText = JsonDocument.Parse(
               "{\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"committed prefix\"}},\"_meta\":{\"streamStartMs\":200}}"))
        handleGrokSessionUpdate.Invoke(grokRetry, [finalText.RootElement]);
    using (var builtInTool = JsonDocument.Parse(
               "{\"update\":{\"sessionUpdate\":\"tool_call\"},\"_meta\":{\"streamStartMs\":200}}"))
        handleGrokSessionUpdate.Invoke(grokRetry, [builtInTool.RootElement]);
    using (var followUpRetry = JsonDocument.Parse(
               "{\"update\":{\"sessionUpdate\":\"retry_state\",\"type\":\"retrying\"},\"_meta\":{\"agentTimestampMs\":250}}"))
        handleGrokRetryUpdate.Invoke(grokRetry, [followUpRetry.RootElement]);
    using (var failedFollowUp = JsonDocument.Parse(
               "{\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"discarded follow-up\"}},\"_meta\":{\"streamStartMs\":220}}"))
        handleGrokSessionUpdate.Invoke(grokRetry, [failedFollowUp.RootElement]);
    using (var recoveredFollowUp = JsonDocument.Parse(
               "{\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\" final tool block\"}},\"_meta\":{\"streamStartMs\":300}}"))
        handleGrokSessionUpdate.Invoke(grokRetry, [recoveredFollowUp.RootElement]);
    using (var completed = JsonDocument.Parse("{\"stopReason\":\"end_turn\"}"))
        handleGrokPromptCompleted.Invoke(grokRetry, [completed.RootElement]);
    Check(grokPreview == "committed prefix final tool block"
          && grokRetryResult?.Text == "committed prefix final tool block",
          "Grok retry updates retract failed-attempt text before tool parsing");

    var grokSuperseded = new GrokChatSession("grok", root);
    var grokSupersededPreview = "";
    AgentTurnResult? grokSupersededResult = null;
    grokSuperseded.TextDelta += text => grokSupersededPreview += text;
    grokSuperseded.TextReplaced += text => grokSupersededPreview = text;
    grokSuperseded.TurnCompleted += result => grokSupersededResult = result;
    typeof(GrokChatSession)
        .GetField("_acceptPromptUpdates", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(grokSuperseded, true);
    using (var firstCandidate = JsonDocument.Parse(
               "{\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"first failed tool block\"}},\"_meta\":{\"streamStartMs\":100}}"))
        handleGrokSessionUpdate.Invoke(grokSuperseded, [firstCandidate.RootElement]);
    using (var replacementThought = JsonDocument.Parse(
               "{\"update\":{\"sessionUpdate\":\"agent_thought_chunk\",\"content\":{\"type\":\"text\",\"text\":\"retrying\"}},\"_meta\":{\"streamStartMs\":200}}"))
        handleGrokSessionUpdate.Invoke(grokSuperseded, [replacementThought.RootElement]);
    using (var replacementCandidate = JsonDocument.Parse(
               "{\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"final tool block\"}},\"_meta\":{\"streamStartMs\":200}}"))
        handleGrokSessionUpdate.Invoke(grokSuperseded, [replacementCandidate.RootElement]);
    using (var completed = JsonDocument.Parse("{\"stopReason\":\"end_turn\"}"))
        handleGrokPromptCompleted.Invoke(grokSuperseded, [completed.RootElement]);
    Check(grokSupersededPreview == "final tool block"
          && grokSupersededResult?.Text == "final tool block",
          "Grok replaces an uncommitted candidate when a new model stream starts");

    var grokExhausted = new GrokChatSession("grok", root);
    var grokExhaustedPreview = "";
    AgentTurnResult? grokExhaustedResult = null;
    grokExhausted.TextDelta += text => grokExhaustedPreview += text;
    grokExhausted.TextReplaced += text => grokExhaustedPreview = text;
    grokExhausted.TurnCompleted += result => grokExhaustedResult = result;
    typeof(GrokChatSession)
        .GetField("_acceptPromptUpdates", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(grokExhausted, true);
    using (var failedCandidate = JsonDocument.Parse(
               "{\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"stale exhausted block\"}},\"_meta\":{\"streamStartMs\":300}}"))
        handleGrokSessionUpdate.Invoke(grokExhausted, [failedCandidate.RootElement]);
    using (var exhausted = JsonDocument.Parse(
               "{\"update\":{\"sessionUpdate\":\"retry_state\",\"type\":\"exhausted\",\"reason\":\"service temporarily at capacity\"},\"_meta\":{\"agentTimestampMs\":350}}"))
        handleGrokRetryUpdate.Invoke(grokExhausted, [exhausted.RootElement]);
    using (var lateFailedCandidate = JsonDocument.Parse(
               "{\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"late stale exhausted block\"}},\"_meta\":{\"streamStartMs\":300}}"))
        handleGrokSessionUpdate.Invoke(grokExhausted, [lateFailedCandidate.RootElement]);
    using (var completed = JsonDocument.Parse("{\"stopReason\":\"rate_limit\"}"))
        handleGrokPromptCompleted.Invoke(grokExhausted, [completed.RootElement]);
    Check(grokExhaustedPreview.Length == 0
          && grokExhaustedResult is { IsError: true, Text: "service temporarily at capacity" },
          "Grok drops exhausted retry candidates and reports the rate-limit reason");

    var codexItems = new CodexChatSession("codex", root);
    var codexPreview = "";
    AgentTurnResult? codexItemResult = null;
    codexItems.TextDelta += text => codexPreview += text;
    codexItems.TextReplaced += text => codexPreview = text;
    codexItems.TurnCompleted += result => codexItemResult = result;
    var handleCodexNotification = typeof(CodexChatSession)
        .GetMethod("HandleNotification", BindingFlags.Instance | BindingFlags.NonPublic)!;
    void SendCodexNotification(string method, string json)
    {
        using var notification = JsonDocument.Parse(json);
        handleCodexNotification.Invoke(codexItems, [method, notification.RootElement]);
    }
    SendCodexNotification("item/started",
        "{\"params\":{\"item\":{\"type\":\"agentMessage\",\"id\":\"item-1\"}}}");
    SendCodexNotification("item/agentMessage/delta",
        "{\"params\":{\"itemId\":\"item-1\",\"delta\":\"first commentary\"}}");
    SendCodexNotification("item/completed",
        "{\"params\":{\"item\":{\"type\":\"agentMessage\",\"id\":\"item-1\",\"text\":\"first commentary\"}}}");
    SendCodexNotification("item/started",
        "{\"params\":{\"item\":{\"type\":\"agentMessage\",\"id\":\"item-2\"}}}");
    SendCodexNotification("item/agentMessage/delta",
        "{\"params\":{\"itemId\":\"item-2\",\"delta\":\"final partial\"}}");
    SendCodexNotification("item/completed",
        "{\"params\":{\"item\":{\"type\":\"agentMessage\",\"id\":\"item-2\",\"text\":\"final authoritative\"}}}");
    SendCodexNotification("item/started",
        "{\"params\":{\"item\":{\"type\":\"agentMessage\",\"id\":\"item-3\"}}}");
    SendCodexNotification("item/completed",
        "{\"params\":{\"item\":{\"type\":\"agentMessage\",\"id\":\"item-3\",\"text\":\"\"}}}");
    SendCodexNotification("turn/completed",
        "{\"params\":{\"turn\":{\"id\":\"turn-1\",\"status\":\"completed\"}}}");
    Check(codexPreview == "first commentary\n\nfinal authoritative"
          && codexItemResult?.Text == "first commentary\n\nfinal authoritative",
          "Codex accumulates agent-message items and corrects only the completed item");
    Check(CodexChatSession.DebugAgentMessageAccumulationLifecycle().EndsWith("accumulated=True"),
          "Codex preserves commentary and tool items before an empty final answer");

    var codexFailedTurn = new CodexChatSession("codex", root);
    var codexImmediateErrors = new List<string>();
    AgentTurnResult? codexFailedResult = null;
    codexFailedTurn.Errored += message => codexImmediateErrors.Add(message);
    codexFailedTurn.TurnCompleted += result => codexFailedResult = result;
    var handleCodexFailedNotification = typeof(CodexChatSession)
        .GetMethod("HandleNotification", BindingFlags.Instance | BindingFlags.NonPublic)!;
    void SendCodexFailedNotification(string method, string json)
    {
        using var notification = JsonDocument.Parse(json);
        handleCodexFailedNotification.Invoke(codexFailedTurn, [method, notification.RootElement]);
    }
    SendCodexFailedNotification("error",
        "{\"params\":{\"threadId\":\"thread-1\",\"turnId\":\"turn-failed\",\"willRetry\":true,\"error\":{\"message\":\"temporary network failure\"}}}");
    SendCodexFailedNotification("error",
        "{\"params\":{\"threadId\":\"thread-1\",\"turnId\":\"turn-failed\",\"willRetry\":false,\"error\":{\"message\":\"TLS handshake EOF\"}}}");
    Check(codexImmediateErrors.Count == 0 && codexFailedResult is null,
          "Codex turn errors wait for turn/completed instead of stopping the AI panel early");
    SendCodexFailedNotification("turn/completed",
        "{\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-failed\",\"status\":\"failed\"}}}");
    Check(codexImmediateErrors.Count == 0
          && codexFailedResult is { IsError: true, Text: "TLS handshake EOF" },
          "Codex failed turns surface the cached terminal error at authoritative completion");

    var claudeResultSession = new ClaudeChatSession("claude", root);
    var claudePreview = "";
    AgentTurnResult? claudeFinalResult = null;
    claudeResultSession.TextDelta += text => claudePreview += text;
    claudeResultSession.TextReplaced += text => claudePreview = text;
    claudeResultSession.TurnCompleted += result => claudeFinalResult = result;
    var handleClaudeStreamEvent = typeof(ClaudeChatSession)
        .GetMethod("HandleStreamEvent", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var handleClaudeResult = typeof(ClaudeChatSession)
        .GetMethod("HandleResult", BindingFlags.Instance | BindingFlags.NonPublic)!;
    using (var partialClaude = JsonDocument.Parse(
               "{\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"stale partial\"}}}"))
        handleClaudeStreamEvent.Invoke(claudeResultSession, [partialClaude.RootElement]);
    using (var finalClaude = JsonDocument.Parse(
               "{\"type\":\"result\",\"result\":\"final authoritative\",\"is_error\":false}"))
        handleClaudeResult.Invoke(claudeResultSession, [finalClaude.RootElement]);
    Check(claudePreview == "final authoritative"
          && claudeFinalResult?.Text == "final authoritative",
          "Claude replaces partial stream text with the authoritative result event");

    // --- AI model catalogs survive a restart and remain usable during refresh ---
    var originalCatalogCachePath = AgentModelCatalogCache.CachePath;
    AgentModelCatalogCache.CachePath = Path.Combine(root, "ai-model-catalogs.json");
    var cachedCodexModels = new List<AgentModelInfo>
    {
        new("cached-model", "Cached Model", true, ["low", "high"]),
    };
    AgentModelCatalogCache.Save("Codex", cachedCodexModels);
    var reloadedCodexModels = AgentModelCatalogCache.Load("codex");
    Check(File.Exists(AgentModelCatalogCache.CachePath)
          && reloadedCodexModels is { Count: 1 }
          && reloadedCodexModels[0].Id == "cached-model"
          && reloadedCodexModels[0].ReasoningEfforts.SequenceEqual(["low", "high"]),
          "AI model catalog round-trips through the machine-local cache");

    var refreshPending = new TaskCompletionSource<IReadOnlyList<AgentModelInfo>?>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var cachedCatalogVm = new AgentChatViewModel(
        [
            new AgentProvider(
                "Codex",
                "",
                [new AgentOption("Default", null)],
                [new AgentOption("Default", null)],
                (_, _) => null,
                () => refreshPending.Task,
                reloadedCodexModels,
                models => AgentModelCatalogCache.Save("Codex", models)),
        ],
        () => null,
        null);
    Check(cachedCatalogVm.ModelOptions.Any(option => option.Value == "cached-model")
          && cachedCatalogVm.EffortOptions.Select(option => option.Value).Contains("high")
          && cachedCatalogVm.PersistedCatalogProviderLabels.SequenceEqual(["Codex"])
          && cachedCatalogVm.LiveCatalogProviderLabels.Count == 0,
          "AI panel uses the persisted model catalog while live refresh is pending");
    await cachedCatalogVm.DisposeAsync();
    AgentModelCatalogCache.CachePath = originalCatalogCachePath;

    Check(aiVm.RequiresDangerConfirmation("rm -rf /tmp/example", dangerTagged: false)
          && aiVm.RequiresDangerConfirmation("echo example", dangerTagged: true)
          && !aiVm.RequiresDangerConfirmation("echo example", dangerTagged: false),
          "AI dangerous commands require confirmation by default");
    aiVm.AutoApproveDangerousCommands = true;
    Check(!aiVm.RequiresDangerConfirmation("rm -rf /tmp/example", dangerTagged: false)
          && !aiVm.RequiresDangerConfirmation("echo example", dangerTagged: true),
          "AI auto-approve bypasses local and model-tagged danger confirmation");
    Check(aiVm.DebugReconcileCompletedText("stale streamed tool", "final authoritative tool")
              == "final authoritative tool"
          && aiVm.DebugReconcileCompletedText("stream only", "") == "stream only",
          "AI completed turns replace stale previews while preserving stream-only fallbacks");

    var runTool = AgentChatViewModel.ExtractFirstToolRequest(
        "```jrm-tool\nterminal.run\nprintf ok\n```");
    var dangerousRunTool = AgentChatViewModel.ExtractFirstToolRequest(
        "```jrm-tool\nterminal.run-danger\nrm -rf /tmp/example\n```");
    var uploadTool = AgentChatViewModel.ExtractFirstToolRequest(
        "```jrm-tool\nfile.upload\nC:\\\\Temp\\\\a.txt -> /tmp\n```");
    var interruptTool = AgentChatViewModel.ExtractFirstToolRequest(
        "Recover the shell.\n```jrm-tool\nterminal.interrupt\n```");
    var reconnectTool = AgentChatViewModel.ExtractFirstToolRequest(
        "```jrm-tool\nterminal.reconnect\n```");
    var invalidThenValidTool = AgentChatViewModel.ExtractFirstToolRequest(
        "```jrm-tool\nterminal.reset\n```\n```jrm-tool\nterminal.run\necho fallback\n```");
    var legacyTool = AgentChatViewModel.ExtractFirstToolRequest(
        "```bash\necho legacy\n```\n```terminal-action\nreconnect\n```");
    Check(runTool is { Name: "terminal.run", Command: "printf ok", Dangerous: false }
          && dangerousRunTool is { Name: "terminal.run-danger", Dangerous: true }
          && uploadTool is { Name: "file.upload", Transfer.IsUpload: true }
          && interruptTool?.TerminalAction == AgentTerminalAction.ForceInterrupt
          && reconnectTool?.TerminalAction == AgentTerminalAction.Reconnect,
          "AI jrm-tool protocol normalizes command, file, interrupt, and reconnect tools");
    Check(invalidThenValidTool is { Name: "terminal.run", Command: "echo fallback" }
          && legacyTool is null,
          "AI skips invalid jrm-tool blocks and rejects every legacy tool format");

    var isCodexTracingLine = typeof(CodexChatSession)
        .GetMethod("IsTracingLine", BindingFlags.Static | BindingFlags.NonPublic)!;
    var structuredCodexWarning = "{\"timestamp\":\"2026-07-14T04:50:34.336651Z\",\"level\":\"WARN\",\"fields\":{\"message\":\"ignoring interface.icon_small: icon path with '..' must resolve under plugin assets/\"},\"target\":\"codex_core_skills::loader\"}";
    Check((bool)isCodexTracingLine.Invoke(null, [structuredCodexWarning])!
          && !(bool)isCodexTracingLine.Invoke(null, ["Codex failed to start"])!,
          "AI Codex structured tracing warnings stay out of the chat transcript");

    var thinkingMessage = new ChatMessageViewModel(ChatRole.Assistant, "")
    {
        IsThinking = true,
        ThinkingText = "Thinking...",
    };
    Check(thinkingMessage.RenderState == MessageRenderState.Thinking
          && thinkingMessage.ShowsThinking && !thinkingMessage.ShowsAssistantMarkdown,
          "AI assistant empty response shows the thinking placeholder");
    thinkingMessage.Text = "answer";
    Check(thinkingMessage.RenderState == MessageRenderState.CompletedMarkdown
          && !thinkingMessage.ShowsThinking && thinkingMessage.ShowsAssistantMarkdown,
          "AI thinking placeholder hides after response text appears");

    const string attachedFence = "Checking the server```bash\nuname -a\n```";
    Check(ChatMessageViewModel.NormalizeMarkdown(attachedFence)
          == "Checking the server\n```bash\nuname -a\n```",
          "AI Markdown repairs a code fence attached directly to prose");

    const string mentionedFence =
        "Checking the server; use ```bash code blocks for remote commands.\n\n"
        + "```bash\nuname -a\n```";
    Check(ChatMessageViewModel.NormalizeMarkdown(mentionedFence) == mentionedFence,
          "AI Markdown keeps inline fence mentions separate from later code blocks");

    var streamingMessage = new ChatMessageViewModel(ChatRole.Assistant, "partial answer")
    {
        IsStreaming = true,
    };
    Check(streamingMessage.RenderState == MessageRenderState.StreamingPlainText
          && streamingMessage.ShowsPlainText && !streamingMessage.ShowsAssistantMarkdown
          && streamingMessage.RenderedMarkdown == "",
          "AI streamed answers skip Markdown parsing until the turn completes");
    streamingMessage.IsStreaming = false;
    Check(streamingMessage.RenderState == MessageRenderState.CompletedMarkdown
          && !streamingMessage.ShowsPlainText && streamingMessage.ShowsAssistantMarkdown
          && streamingMessage.RenderedMarkdown == "partial answer",
          "AI completed answers switch to one final Markdown render");

    var oversizedMessage = new ChatMessageViewModel(
        ChatRole.Assistant,
        new string('x', ChatMessageViewModel.MarkdownCharacterLimit + 1));
    Check(oversizedMessage.RenderState == MessageRenderState.CompletedPlainText
          && oversizedMessage.ShowsPlainText && !oversizedMessage.ShowsAssistantMarkdown
          && oversizedMessage.RenderedMarkdown == "",
          "AI oversized answers remain plain text instead of building an unbounded Markdown tree");

    var transcriptVm = new AgentChatViewModel(
        [
            new AgentProvider(
                "Test",
                "",
                [new AgentOption("Default", null)],
                [new AgentOption("Default", null)],
                (_, _) => null),
        ],
        () => null,
        null);
    for (var i = 0; i < AgentChatViewModel.InitialTranscriptMessageLimit + 25; i++)
        transcriptVm.Messages.Add(new ChatMessageViewModel(ChatRole.User, $"message-{i}"));
    Check(transcriptVm.Messages.Count == AgentChatViewModel.InitialTranscriptMessageLimit + 25
          && transcriptVm.TranscriptMessages.Count == AgentChatViewModel.InitialTranscriptMessageLimit
          && transcriptVm.HiddenEarlierMessageCount == 25
          && transcriptVm.TranscriptMessages[0].Text == "message-25",
          "AI transcript keeps full history while projecting a bounded recent window");
    transcriptVm.LoadEarlierMessagesCommand.Execute(null);
    Check(transcriptVm.TranscriptMessages.Count == AgentChatViewModel.InitialTranscriptMessageLimit + 25
          && transcriptVm.HiddenEarlierMessageCount == 0
          && transcriptVm.TranscriptMessages[0].Text == "message-0",
          "AI transcript loads earlier history in a separate page operation");
    await transcriptVm.DisposeAsync();

    var scrollController = new TranscriptScrollController();
    Check(scrollController.IsFollowingLatest
          && scrollController.ShouldFollowLayoutChange(20, 0),
          "AI transcript follows layout growth only in latest-message mode");
    scrollController.BeginManualNavigation();
    Check(!scrollController.IsFollowingLatest
          && !scrollController.ShouldFollowLayoutChange(20, 0),
          "AI transcript manual navigation disables automatic following");
    scrollController.CompleteManualNavigation(isAtBottom: false);
    Check(!scrollController.IsFollowingLatest,
          "AI transcript remains in browsing mode away from the bottom");
    scrollController.CompleteManualNavigation(isAtBottom: true);
    Check(scrollController.IsFollowingLatest,
          "AI transcript resumes following after the user reaches the bottom");

    var streamingVm = new AgentChatViewModel(
        [
            new AgentProvider(
                "Test",
                "",
                [new AgentOption("Default", null)],
                [new AgentOption("Default", null)],
                (_, _) => null),
        ],
        () => null,
        null);
    var coalescedMessage = new ChatMessageViewModel(ChatRole.Assistant, "");
    streamingVm.Messages.Add(coalescedMessage);
    typeof(AgentChatViewModel)
        .GetField("_pendingAssistant", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(streamingVm, coalescedMessage);
    var onTextDelta = typeof(AgentChatViewModel)
        .GetMethod("OnTextDelta", BindingFlags.Instance | BindingFlags.NonPublic)!;
    for (var i = 0; i < 1_000; i++)
        onTextDelta.Invoke(streamingVm, ["0123456789"]);
    Check(streamingVm.PendingStreamCharacterCount == 10_000 && coalescedMessage.Text.Length == 0,
          "AI token bursts wait in one coalescing buffer before touching the UI message");
    typeof(AgentChatViewModel)
        .GetMethod("FlushPendingTextDeltas", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(streamingVm, null);
    Check(coalescedMessage.Text.Length == 10_000 && coalescedMessage.IsStreaming
          && streamingVm.StreamRenderUpdateCount == 1
          && streamingVm.PendingStreamCharacterCount == 0,
          "AI token burst is applied as one plain-text UI update");
    await streamingVm.DisposeAsync();

    var runningMessage = new ChatMessageViewModel(ChatRole.Assistant, "")
    {
        IsThinking = true,
        ThinkingText = "Running...",
    };
    Check(runningMessage.ShowsThinking && runningMessage.ThinkingText.StartsWith("Running", StringComparison.Ordinal),
          "AI command execution reuses the thinking placeholder for Running status");

    var activityVm = new AgentChatViewModel(
        [
            new AgentProvider(
                "Test",
                "",
                [new AgentOption("Default", null)],
                [new AgentOption("Default", null)],
                (_, _) => null),
        ],
        () => null,
        null);
    activityVm.DebugShowThinkingActivity();
    var thinkingActivity = activityVm.Messages.Last(m => m.IsAssistant);
    Check(thinkingActivity.ShowsThinking && activityVm.ActivityElapsedSeconds == 0
          && activityVm.StatusText == thinkingActivity.ThinkingText,
          "AI Thinking activity starts its elapsed-seconds counter at zero");
    await Task.Delay(1100);
    typeof(AgentChatViewModel)
        .GetMethod("OnThinkingTimerTick", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(activityVm, [null, EventArgs.Empty]);
    Check(activityVm.ActivityElapsedSeconds >= 1
          && activityVm.StatusText == thinkingActivity.ThinkingText,
          "AI Thinking activity updates its elapsed time in seconds");
    activityVm.DebugClearActivity();
    activityVm.DebugShowRunningActivity();
    var runningActivity = activityVm.Messages.Last(m => m.IsAssistant);
    Check(runningActivity.ShowsThinking && activityVm.ActivityElapsedSeconds == 0
          && activityVm.StatusText == runningActivity.ThinkingText,
          "AI Running activity starts its elapsed-seconds counter at zero");
    activityVm.DebugClearActivity();
    await activityVm.DisposeAsync();

    // RunAndContinueAsync shows Running… while the shell command is in flight.
    var commandStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseCommand = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var commandAiVm = new AgentChatViewModel(
        [
            new AgentProvider(
                "Test",
                "",
                [new AgentOption("Default", null)],
                [new AgentOption("Default", null)],
                (_, _) => null),
        ],
        () => null,
        async (_, _) =>
        {
            commandStarted.TrySetResult();
            await releaseCommand.Task;
            return "ok\n";
        });
    var runAndContinue = typeof(AgentChatViewModel)
        .GetMethod("RunAndContinueAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var runningTurn = (Task)runAndContinue.Invoke(commandAiVm, ["echo ok"])!;
    await commandStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var runningPlaceholder = commandAiVm.Messages.LastOrDefault(m => m.IsAssistant);
    // Localizer may not be loaded in smoke tests, so assert the activity bubble state
    // rather than a specific English/Chinese label string.
    Check(runningPlaceholder is { ShowsThinking: true, HasText: false }
          && !string.IsNullOrWhiteSpace(runningPlaceholder.ThinkingText)
          && commandAiVm.Messages.Any(m => m.IsTool && m.Text.Contains("echo ok", StringComparison.Ordinal)),
          "AI auto-run shows Running activity while the command is executing");
    releaseCommand.TrySetResult();
    await runningTurn.WaitAsync(TimeSpan.FromSeconds(2));
    await commandAiVm.DisposeAsync();

    // --- Portability: a connection file alone (no vault, no cache) suffices ---
    // Carry just the EncryptedPassword to a fresh "machine" and decrypt with the
    // master password. The salt needed for PBKDF2 is inside the jrm1 blob.
    var samePasswordSession = new MasterKeyService();
    samePasswordSession.SetPassword(masterPassword);
    Check(samePasswordSession.DecryptPassword(enc) == secret,
          "A fresh service decrypts a jrm1 blob with the same master password");
    Check(MasterKeyService.DecryptWithPassword(masterPassword, enc) == secret,
          "Static decrypt accepts the same master password");

    // --- Wrong password fails to decrypt ---
    Check(MasterKeyService.DecryptWithPassword("not-the-master", enc) is null,
          "Different password cannot decrypt the jrm1 blob");

    // --- Legacy/unknown non-empty blobs are rejected by the main runtime ---
    var legacyLikeBlob = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    Check(MasterKeyService.DecryptWithPassword(masterPassword, legacyLikeBlob) is null,
          "Non-jrm1 blob is rejected by static decrypt");
    Check(!PasswordProtector.TryDecrypt(legacyLikeBlob, out var legacyClear) && legacyClear == "",
          "Non-jrm1 blob reports decrypt failure");

    // --- A foreign password reports decrypt failure, not silent emptiness ---
    // Use a separate cache path so the foreign SetPassword doesn't clobber the real cache.
    var primaryCachePath = MasterKeyService.CachePath;
    MasterKeyService.CachePath = Path.Combine(root, "foreign-master-password.bin");
    var otherSession = new MasterKeyService();
    otherSession.SetPassword("totally-different");
    MasterKeyService.Current = otherSession;
    Check(!PasswordProtector.TryDecrypt(enc, out var failClear) && failClear == "",
          "Foreign password reports decrypt failure (TryDecrypt=false)");
    Check(PasswordProtector.Decrypt(enc) == "", "Foreign password decrypts to empty string");
    MasterKeyService.Current = master;
    MasterKeyService.CachePath = primaryCachePath; // restore

    // --- Cache round-trip via DPAPI ---
    var fromCache = new MasterKeyService();
    Check(fromCache.TryUnlockFromCache(), "DPAPI cache reloads the master password material");
    Check(fromCache.DecryptPassword(enc) == secret, "Cached master password decrypts existing passwords");

    // --- Fresh machine/cache path still decrypts with the master password alone ---
    MasterKeyService.CachePath = Path.Combine(root, "fresh-machine-master-password.bin"); // no cache here
    var portable = new MasterKeyService();
    Check(!portable.TryUnlockFromCache(), "Fresh machine has no DPAPI cache yet");
    portable.SetPassword(masterPassword);
    Check(portable.DecryptPassword(enc) == secret,
          "An encrypted connection decrypts on a fresh machine with the master password alone");

    // --- RDP password hex format (UTF-16LE + DPAPI + hex) ---
    var rdpHex = PasswordProtector.EncryptForRdpFile(secret);
    Check(rdpHex.Length > 0 && rdpHex.All(Uri.IsHexDigit), "RDP password field is uppercase hex");

    // --- Folder tree + one file per connection ---
    var folder = store.CreateFolder(store.RootPath, "Servers");
    Check(Directory.Exists(folder), "CreateFolder makes a directory");

    var ssh = new Connection
    {
        Type = ConnectionType.Ssh, Name = "web01", Host = "10.0.0.1",
        Port = 22, Username = "root", EncryptedPassword = enc,
    };
    var sshPath = store.Save(ssh, folder);

    var rdp = new Connection
    {
        Type = ConnectionType.Rdp, Name = "win-box", Host = "10.0.0.2",
        Port = 3389, Username = "admin", EncryptedPassword = enc,
    };
    var rdpPath = store.Save(rdp, folder);

    Check(File.Exists(sshPath) && File.Exists(rdpPath), "Each connection saved to its own file");
    Check(Guid.TryParse(ssh.ConnectionId, out _)
          && Guid.TryParse(rdp.ConnectionId, out _)
          && ssh.ConnectionId != rdp.ConnectionId,
          "New connections receive distinct persistent GUIDs");
    Check(store.GetConnectionFiles(folder).Count == 2, "Folder lists exactly two connection files");

    // Plaintext password must never appear on disk.
    var sshOnDisk = File.ReadAllText(sshPath);
    Check(!sshOnDisk.Contains(secret), "Plaintext password is not present in the saved file");

    // --- Load round-trip ---
    var loaded = store.Load(sshPath);
    Check(loaded.Type == ConnectionType.Ssh && loaded.Host == "10.0.0.1"
          && loaded.Name == "web01", "Loaded connection matches what was saved");
    Check(loaded.ConnectionId == ssh.ConnectionId,
          "Loading a connection preserves its GUID");
    Check(PasswordProtector.Decrypt(loaded.EncryptedPassword) == secret,
          "Password decrypts after load");

    // --- Editor auto-save should only write real edits ---
    var vmRoot = Path.Combine(root, "ViewModelStorage");
    Directory.CreateDirectory(vmRoot);
    var vmSettingsPath = Path.Combine(vmRoot, "machine-settings.json");
    var vmRoamingSettingsPath = Path.Combine(vmRoot, "roaming-settings.json");
    File.WriteAllText(vmSettingsPath, JsonSerializer.Serialize(new AppSettings
    {
        StorageLocation = StorageLocation.CustomDirectory,
        CustomStoragePath = vmRoot,
    }));

    var vmSettings = new SettingsService(vmSettingsPath, vmRoamingSettingsPath);
    var vmStore = new ConnectionStore(vmSettings.ResolveConnectionsRoot());
    var autoSaveA = new Connection
    {
        Type = ConnectionType.Ssh,
        Name = "autosave-a",
        Host = "a.example",
        Port = 22,
        Username = "root",
        EncryptedPassword = PasswordProtector.Encrypt("autosave-password"),
    };
    var autoSaveB = new Connection
    {
        Type = ConnectionType.Ssh,
        Name = "autosave-b",
        Host = "b.example",
        Port = 22,
        Username = "root",
        EncryptedPassword = PasswordProtector.Encrypt("autosave-password"),
    };
    var autoSaveAPath = vmStore.Save(autoSaveA, vmStore.RootPath);
    _ = vmStore.Save(autoSaveB, vmStore.RootPath);
    var autoSaveAPasswordBeforeEdit = autoSaveA.EncryptedPassword;

    vmSettings.Settings.RecentConnectionPaths.Add(autoSaveAPath);
    vmSettings.Settings.RecentExpanded = true;
    var recentVm = new MainWindowViewModel(vmStore, new ConnectionLauncher(), vmSettings);
    var recentNode = recentVm.Nodes.Single(n => n.IsRecent).Children.Single(n => n.FullPath == autoSaveAPath);
    string? recentLaunchPath = null;
    recentVm.OpenSshTerminalAsync = (_, sourcePath) =>
    {
        recentLaunchPath = sourcePath;
        return Task.CompletedTask;
    };
    recentVm.SuppressRecentAutoLaunch = true;
    recentVm.SelectedNode = recentNode;
    recentVm.SuppressRecentAutoLaunch = false;
    await recentVm.ConnectCommand.ExecuteAsync(null);
    Check(recentLaunchPath == autoSaveAPath, "Recent context-menu Connect launches the selected connection");
    Check(recentVm.SelectedNode is null, "Recent context-menu Connect clears the stale shadow selection");

    var vm = new MainWindowViewModel(vmStore, new ConnectionLauncher(), vmSettings);
    var nodeA = vm.Nodes.Single(n => n.Name == "autosave-a");
    var nodeB = vm.Nodes.Single(n => n.Name == "autosave-b");
    vm.SelectedNode = nodeA;
    var autoSaveAJsonBeforeSwitch = File.ReadAllText(autoSaveAPath);
    vm.SelectedNode = nodeB;
    Check(File.ReadAllText(autoSaveAPath) == autoSaveAJsonBeforeSwitch,
          "Switching selected connections without edits does not rewrite the old connection");

    vm.SelectedNode = nodeA;
    vm.Editor!.Host = "changed.example";
    vm.SelectedNode = nodeB;
    var autoSaveAAfterHostEdit = vmStore.Load(autoSaveAPath);
    Check(autoSaveAAfterHostEdit.Host == "changed.example",
          "Switching selected connections still flushes actual editor edits");
    Check(autoSaveAAfterHostEdit.EncryptedPassword == autoSaveAPasswordBeforeEdit,
          "Editing non-password fields preserves the existing password blob");

    vm.SelectedNode = nodeA;
    vm.Editor!.Password = "changed-autosave-password";
    vm.SelectedNode = nodeB;
    var autoSaveAAfterPasswordEdit = vmStore.Load(autoSaveAPath);
    Check(autoSaveAAfterPasswordEdit.EncryptedPassword != autoSaveAPasswordBeforeEdit
          && PasswordProtector.Decrypt(autoSaveAAfterPasswordEdit.EncryptedPassword) == "changed-autosave-password",
          "Editing the password writes a new decryptable password blob");

    var renamePromptCalled = false;
    var inlineRenameFocusRequested = false;
    vm.PromptAsync = (_, _, _) =>
    {
        renamePromptCalled = true;
        return Task.FromResult<string?>("prompt-rename");
    };
    vm.RequestFocusTreeNameEditor = node => inlineRenameFocusRequested = ReferenceEquals(node, vm.SelectedNode);
    TreeNodeViewModel? inlineRenameCommittedFocusNode = null;
    vm.RequestFocusTreeNode = node => inlineRenameCommittedFocusNode = node;
    vm.SelectedNode = vm.Nodes.Single(n => n.Name == "autosave-a");
    vm.RenameCommand.Execute(null);
    Check(vm.SelectedNode is { IsNameEditing: true, EditName: "autosave-a" },
          "RenameCommand starts inline tree name editing");
    Check(inlineRenameFocusRequested, "Inline tree rename requests editor focus");
    Check(!renamePromptCalled, "RenameCommand does not use the prompt dialog");
    Check(!vm.ConnectCommand.CanExecute(null), "Connection cannot launch while inline tree rename is active");

    vm.SelectedNode!.EditName = "autosave-inline";
    vm.CommitNodeNameEdit(vm.SelectedNode);
    var inlineRenamedPath = Path.Combine(vmStore.RootPath, "autosave-inline.json");
    Check(File.Exists(inlineRenamedPath) && !File.Exists(autoSaveAPath),
          "Committing inline tree rename moves the connection file");
    Check(vm.SelectedNode is { Name: "autosave-inline", IsNameEditing: false },
          "Inline tree rename selects the renamed node");
    Check(vm.ConnectCommand.CanExecute(null), "Connection launch is re-enabled after inline tree rename");
    Check(ReferenceEquals(inlineRenameCommittedFocusNode, vm.SelectedNode),
          "Committing inline tree rename requests focus on the renamed node");

    inlineRenameFocusRequested = false;
    var foldersBeforeNewFolder = Directory.GetDirectories(vmStore.RootPath).Length;
    vm.NewFolderCommand.Execute(null);
    var newFolderNode = vm.SelectedNode;
    Check(newFolderNode is not null
          && newFolderNode.IsFolder
          && newFolderNode.IsNameEditing
          && newFolderNode.EditName == newFolderNode.Name
          && Directory.Exists(newFolderNode.FullPath),
          "NewFolderCommand starts inline rename on the created folder");
    Check(Directory.GetDirectories(vmStore.RootPath).Length == foldersBeforeNewFolder + 1,
          "NewFolderCommand creates one folder at the current level");
    Check(inlineRenameFocusRequested, "NewFolderCommand requests tree name editor focus");
    if (newFolderNode is not null)
        vm.CancelNodeNameEdit(newFolderNode, requestFocus: false);

    var deleteSelectionFolder = vmStore.CreateFolder(vmStore.RootPath, "delete-selection");
    _ = vmStore.Save(new Connection { Name = "delete-a", Host = "a.example" }, deleteSelectionFolder);
    _ = vmStore.Save(new Connection { Name = "delete-b", Host = "b.example" }, deleteSelectionFolder);
    _ = vmStore.Save(new Connection { Name = "delete-c", Host = "c.example" }, deleteSelectionFolder);
    vm.RefreshCommand.Execute(null);
    var deleteSelectionFolderNode = vm.Nodes.Single(n => n.Name == "delete-selection");
    vm.ConfirmAsync = (_, _) => Task.FromResult(true);
    vm.SelectedNode = deleteSelectionFolderNode.Children.Single(n => n.Name == "delete-b");
    await vm.DeleteCommand.ExecuteAsync(null);
    Check(vm.SelectedNode is { Name: "delete-c" },
          "Deleting a tree node selects its next sibling");
    await vm.DeleteCommand.ExecuteAsync(null);
    Check(vm.SelectedNode is { Name: "delete-a" },
          "Deleting the last tree node selects its previous sibling");

    // --- Rename via Save (file follows the name) ---
    loaded.Name = "web01-renamed";
    var renamedConnectionId = loaded.ConnectionId;
    var renamedPath = store.Save(loaded, folder, sshPath);
    Check(!File.Exists(sshPath) && File.Exists(renamedPath), "Renaming moves the file, old one removed");
    Check(store.Load(renamedPath).ConnectionId == renamedConnectionId,
          "Renaming a connection preserves its GUID");

    // --- Name collision disambiguation ---
    var dup = new Connection { Type = ConnectionType.Ssh, Name = "win-box", Host = "x" };
    var dupPath = store.Save(dup, folder);
    Check(dupPath != rdpPath && File.Exists(dupPath), "Colliding name gets a unique file");

    // --- Copy / move files ---
    var sub = store.CreateFolder(store.RootPath, "Sub");

    var copied = store.CopyFileInto(rdpPath, sub);
    Check(File.Exists(copied) && File.Exists(rdpPath) && Path.GetDirectoryName(copied) == sub,
          "CopyFileInto copies and keeps the original");
    Check(store.Load(copied).ConnectionId != store.Load(rdpPath).ConnectionId,
          "CopyFileInto assigns the copied connection a new GUID");

    var sshWithScriptParameters = new Connection
    {
        Type = ConnectionType.Ssh,
        Name = "ssh-with-script-parameters",
        Host = "script.example",
        ScriptBindings =
        [
            new ConnectionScriptBinding
            {
                Name = "test-suite",
                Params = [new ConnectionScriptParameterValue { Name = "TARGET", Value = "original" }],
            },
        ],
    };
    var sshWithScriptParametersPath = store.Save(sshWithScriptParameters, folder);
    var copiedSshWithoutParameters = store.CopyFileInto(
        sshWithScriptParametersPath,
        sub,
        includeSshScriptBindings: false);
    Check(store.Load(copiedSshWithoutParameters).ScriptBindings.Count == 0
          && store.Load(sshWithScriptParametersPath).ScriptBindings.Count == 1,
          "Clipboard-style SSH copy omits script parameters and keeps the original unchanged");

    var movedFile = store.MoveFileInto(dupPath, sub);
    Check(File.Exists(movedFile) && !File.Exists(dupPath), "MoveFileInto moves the file");
    Check(store.Load(movedFile).ConnectionId == dup.ConnectionId,
          "MoveFileInto preserves the connection GUID");

    var noop = store.MoveFileInto(movedFile, sub);
    Check(noop == movedFile && File.Exists(movedFile), "MoveFileInto into same folder is a no-op");

    // --- Copy / move folders ---
    var copiedFolder = store.CopyFolderInto(folder, store.RootPath);
    Check(Directory.Exists(copiedFolder) && copiedFolder != folder
          && store.GetConnectionFiles(copiedFolder).Count == store.GetConnectionFiles(folder).Count,
          "CopyFolderInto recursively copies with a unique name");
    var sourceFolderIds = store.GetConnectionFiles(folder).Select(path => store.Load(path).ConnectionId).ToHashSet();
    var copiedFolderIds = store.GetConnectionFiles(copiedFolder).Select(path => store.Load(path).ConnectionId).ToHashSet();
    Check(!sourceFolderIds.Overlaps(copiedFolderIds),
          "CopyFolderInto assigns new GUIDs to all copied connections");

    var copiedFolderWithoutParameters = store.CopyFolderInto(
        folder,
        store.RootPath,
        includeSshScriptBindings: false);
    var copiedNestedSsh = store.GetConnectionFiles(copiedFolderWithoutParameters)
        .Select(store.Load)
        .Single(c => c.Host == sshWithScriptParameters.Host);
    Check(copiedNestedSsh.ScriptBindings.Count == 0,
          "Clipboard-style folder copy omits script parameters from nested SSH connections");

    var folderNoop = store.MoveFolderInto(sub, store.RootPath);
    Check(folderNoop == sub && Directory.Exists(sub), "MoveFolderInto into same parent is a no-op");

    var movedFolder = store.MoveFolderInto(sub, folder);
    Check(Directory.Exists(movedFolder) && !Directory.Exists(sub)
          && Path.GetDirectoryName(movedFolder) == folder, "MoveFolderInto moves the folder");

    // --- Descendant guard ---
    Check(ConnectionStore.IsSameOrInside(folder, folder), "IsSameOrInside: a folder is inside itself");
    Check(ConnectionStore.IsSameOrInside(folder, movedFolder), "IsSameOrInside: detects nested folder");
    Check(!ConnectionStore.IsSameOrInside(movedFolder, folder), "IsSameOrInside: parent is not inside child");

    // --- Migration copy (to a separate root, as the app does between storage locations) ---
    var migrateDest = Path.Combine(Path.GetTempPath(), "jrm_migrate_" + Guid.NewGuid().ToString("N"));
    try
    {
        var sourceMigrationIds = store.AllConnectionFiles()
            .Select(path => store.Load(path).ConnectionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        store.CopyTreeContents(store.RootPath, migrateDest);
        Check(Directory.Exists(migrateDest) && Directory.GetDirectories(migrateDest).Length >= 1,
              "CopyTreeContents migrates the tree to a separate root");
        var migratedStore = new ConnectionStore(migrateDest);
        var migratedConnectionIds = migratedStore.AllConnectionFiles()
            .Select(path => migratedStore.Load(path).ConnectionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check(sourceMigrationIds.SetEquals(migratedConnectionIds),
              "CopyTreeContents preserves GUIDs during storage-location migration");

        // It must refuse to copy into its own subtree (would otherwise recurse forever).
        var insideDest = Path.Combine(store.RootPath, "InsideDest");
        store.CopyTreeContents(store.RootPath, insideDest);
        Check(!Directory.Exists(insideDest), "CopyTreeContents refuses to copy into its own subtree");
    }
    finally
    {
        try { if (Directory.Exists(migrateDest)) Directory.Delete(migrateDest, true); } catch { }
    }

    // --- Settings location resolution ---
    var progRoot = SettingsService.ResolveConnectionsRoot(StorageLocation.ProgramDirectory);
    var userRoot = SettingsService.ResolveConnectionsRoot(StorageLocation.UserDirectory);
    var progConfigRoot = Path.GetDirectoryName(progRoot)!;
    var topLevelProgramConnectionsRoot = Path.Combine(AppContext.BaseDirectory, "Connections");
    var programConfigRootExisted = Directory.Exists(progConfigRoot);
    var topLevelProgramConnectionsRootExisted = Directory.Exists(topLevelProgramConnectionsRoot);
    if (programConfigRootExisted)
    {
        Check(true, "Top-level program Connections folder alone does not select portable storage");
    }
    else
    {
        if (!topLevelProgramConnectionsRootExisted)
            Directory.CreateDirectory(topLevelProgramConnectionsRoot);
        try
        {
            var topLevelOnlySettingsPath = Path.Combine(root, "TopLevelOnly", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(topLevelOnlySettingsPath)!);
            File.WriteAllText(
                topLevelOnlySettingsPath,
                JsonSerializer.Serialize(new AppSettings { StorageLocation = StorageLocation.ProgramDirectory }));
            var topLevelOnlySettings = new SettingsService(topLevelOnlySettingsPath);
            Check(topLevelOnlySettings.Settings.StorageLocation == StorageLocation.UserDirectory,
                  "Stale ProgramDirectory setting without app Config falls back to user storage");
            Check(topLevelOnlySettings.CurrentStorageLocation == StorageLocation.UserDirectory,
                  "Top-level program Connections folder alone does not select portable storage");
            Check(!Directory.Exists(progConfigRoot),
                  "Stale ProgramDirectory setting does not create app Config");
        }
        finally
        {
            if (!topLevelProgramConnectionsRootExisted && Directory.Exists(topLevelProgramConnectionsRoot))
                Directory.Delete(topLevelProgramConnectionsRoot, true);
        }
    }
    if (!programConfigRootExisted)
        Directory.CreateDirectory(progConfigRoot);
    try
    {
        var autoPortableSettingsPath = Path.Combine(root, "AutoPortable", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(autoPortableSettingsPath)!);
        File.WriteAllText(
            autoPortableSettingsPath,
            JsonSerializer.Serialize(new AppSettings { StorageLocation = StorageLocation.UserDirectory }));
        var autoPortableSettings = new SettingsService(autoPortableSettingsPath);
        Check(autoPortableSettings.Settings.StorageLocation == StorageLocation.UserDirectory
              && autoPortableSettings.CurrentStorageLocation == StorageLocation.ProgramDirectory,
              "Existing program Config folder selects portable storage without rewriting settings");
        Check(string.Equals(
                  Path.GetFullPath(autoPortableSettings.ResolveConnectionsRoot()),
                  Path.GetFullPath(progRoot),
                  StringComparison.OrdinalIgnoreCase),
              "Auto-detected portable storage resolves to the program Config Connections folder");
        Check(SettingsService.IsPortable,
              "Existing program Config folder marks the app as portable");
    }
    finally
    {
        if (!programConfigRootExisted && Directory.Exists(progConfigRoot))
            Directory.Delete(progConfigRoot, true);
    }
    if (!programConfigRootExisted)
    {
        Directory.CreateDirectory(progConfigRoot);
        File.WriteAllText(Path.Combine(progConfigRoot, "settings.json"), "{}");
        Check(SettingsService.TryDeleteProgramConfig(out var deleteError)
              && !Directory.Exists(progConfigRoot),
              "Leaving portable storage deletes the program Config marker");
        Check(string.IsNullOrEmpty(deleteError), "Deleting the program Config marker reports no error");
    }
    var sourceConfigRoot = Path.Combine(root, "SourceConfig");
    var destConfigRoot = Path.Combine(root, "DestConfig");
    Directory.CreateDirectory(Path.Combine(sourceConfigRoot, "Connections", "Servers"));
    Directory.CreateDirectory(Path.Combine(sourceConfigRoot, "Scripts", "Deploy"));
    Directory.CreateDirectory(destConfigRoot);
    File.WriteAllText(Path.Combine(sourceConfigRoot, "settings.json"), "{\"Theme\":\"Dark\"}");
    File.WriteAllText(Path.Combine(sourceConfigRoot, "Connections", "Servers", "web01.json"), "{}");
    File.WriteAllText(Path.Combine(sourceConfigRoot, "Scripts", "Deploy", RemoteScriptStore.ParameterFileName), "TARGET=string");
    File.WriteAllText(Path.Combine(destConfigRoot, "settings.json"), "{\"Theme\":\"Light\"}");
    SettingsService.MoveConfigRoot(sourceConfigRoot, destConfigRoot);
    Check(!Directory.Exists(sourceConfigRoot), "Moving Config removes the source Config root");
    Check(File.ReadAllText(Path.Combine(destConfigRoot, "settings.json")).Contains("Dark")
          && File.Exists(Path.Combine(destConfigRoot, "Connections", "Servers", "web01.json"))
          && File.Exists(Path.Combine(destConfigRoot, "Scripts", "Deploy", RemoteScriptStore.ParameterFileName)),
          "Moving Config transfers settings, connections, and scripts together");
    var nestedConfigRoot = Path.Combine(root, "NestedConfig");
    Directory.CreateDirectory(nestedConfigRoot);
    var nestedRejected = false;
    try
    {
        SettingsService.MoveConfigRoot(nestedConfigRoot, Path.Combine(nestedConfigRoot, "Child", "Config"));
    }
    catch (InvalidOperationException)
    {
        nestedRejected = true;
    }
    Check(nestedRejected && Directory.Exists(nestedConfigRoot),
          "Moving Config refuses a destination inside the source Config root");
    var noMoveSourceBase = Path.Combine(root, "NoMoveSource");
    var noMoveTargetBase = Path.Combine(root, "NoMoveTarget");
    var noMoveMachineSettingsPath = Path.Combine(root, "NoMoveMachine", "settings.json");
    Directory.CreateDirectory(Path.GetDirectoryName(noMoveMachineSettingsPath)!);
    File.WriteAllText(
        noMoveMachineSettingsPath,
        JsonSerializer.Serialize(new AppSettings
        {
            StorageLocation = StorageLocation.CustomDirectory,
            CustomStoragePath = noMoveSourceBase,
        }));
    var noMoveSettings = new SettingsService(noMoveMachineSettingsPath);
    var noMoveStore = new ConnectionStore(noMoveSettings.ResolveConnectionsRoot());
    var noMoveConnectionFolder = Path.Combine(noMoveStore.RootPath, "Servers");
    Directory.CreateDirectory(noMoveConnectionFolder);
    var noMoveConnectionPath = Path.Combine(noMoveConnectionFolder, "web01.json");
    File.WriteAllText(noMoveConnectionPath, "{}");
    var noMoveVm = new MainWindowViewModel(noMoveStore, new ConnectionLauncher(), noMoveSettings);
    var noMovePromptCount = 0;
    noMoveVm.ConfirmAsync = (_, _) =>
    {
        noMovePromptCount++;
        return Task.FromResult(false);
    };
    noMoveVm.PickSettingsAsync = (_, _, language, theme, checkOnStartup, intervalHours, editorPath) =>
        Task.FromResult<SettingsDialogResult?>(new SettingsDialogResult(
            StorageLocation.CustomDirectory,
            noMoveTargetBase,
            language,
            theme,
            checkOnStartup,
            intervalHours,
            editorPath));
    await noMoveVm.OpenSettingsCommand.ExecuteAsync(null);
    Check(noMovePromptCount == 1, "Changing Config location asks whether to move files");
    Check(File.Exists(noMoveConnectionPath)
          && !File.Exists(Path.Combine(noMoveTargetBase, "Config", "Connections", "Servers", "web01.json")),
          "Choosing not to move Config data leaves existing files in place");
    Check(noMoveSettings.Settings.StorageLocation == StorageLocation.CustomDirectory
          && string.Equals(noMoveSettings.Settings.CustomStoragePath, noMoveTargetBase, StringComparison.OrdinalIgnoreCase)
          && string.Equals(
              Path.GetFullPath(noMoveVm.RootPath),
              Path.GetFullPath(SettingsService.ResolveConnectionsRoot(StorageLocation.CustomDirectory, noMoveTargetBase)),
              StringComparison.OrdinalIgnoreCase),
          "Choosing not to move Config data only changes the active setting");
    var expectedSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JeekRemoteManager",
        "Config",
        "settings.json");
    Check(string.Equals(
              Path.GetFullPath(SettingsService.DefaultSettingsPath),
              Path.GetFullPath(expectedSettingsPath),
              StringComparison.OrdinalIgnoreCase),
          "settings.json resolves under LocalAppData Config");
    var expectedRoamingSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JeekRemoteManager",
        "Config",
        "settings.json");
    Check(string.Equals(
              Path.GetFullPath(SettingsService.DefaultRoamingSettingsPath),
              Path.GetFullPath(expectedRoamingSettingsPath),
              StringComparison.OrdinalIgnoreCase),
          "Roaming settings.json resolves under AppData Config");
    var settingsWithRecent = new AppSettings
    {
        Language = "zh",
        Theme = "Dark",
        RecentConnectionPaths = { Path.Combine(root, "Servers", "web01.json") },
        LastSelectedConnectionPath = Path.Combine(root, "Servers", "web01.json"),
        RecentExpanded = false,
        MainWindowWidth = 1200,
        MainWindowHeight = 760,
        ConnectionPanelWidth = 360,
    };
    var machineSettingsJson = JsonSerializer.Serialize(new MachineAppSettings
    {
        RecentConnectionPaths = settingsWithRecent.RecentConnectionPaths,
        LastSelectedConnectionPath = settingsWithRecent.LastSelectedConnectionPath,
        RecentExpanded = settingsWithRecent.RecentExpanded,
        MainWindowWidth = settingsWithRecent.MainWindowWidth,
        MainWindowHeight = settingsWithRecent.MainWindowHeight,
        ConnectionPanelWidth = settingsWithRecent.ConnectionPanelWidth,
    });
    Check(machineSettingsJson.Contains(nameof(MachineAppSettings.RecentConnectionPaths))
          && machineSettingsJson.Contains(nameof(MachineAppSettings.LastSelectedConnectionPath))
          && machineSettingsJson.Contains(nameof(MachineAppSettings.RecentExpanded))
          && machineSettingsJson.Contains(nameof(MachineAppSettings.MainWindowWidth))
          && machineSettingsJson.Contains(nameof(MachineAppSettings.MainWindowHeight))
          && machineSettingsJson.Contains(nameof(MachineAppSettings.ConnectionPanelWidth))
          && !machineSettingsJson.Contains("ConnectionPanelCollapsed")
          && !machineSettingsJson.Contains(nameof(RoamingAppSettings.Language))
          && !machineSettingsJson.Contains(nameof(RoamingAppSettings.Theme)),
          "Machine settings persist local paths and window size only");
    var roamingSettingsJson = JsonSerializer.Serialize(new RoamingAppSettings
    {
        Language = settingsWithRecent.Language,
        Theme = settingsWithRecent.Theme,
        CheckUpdateOnStartup = settingsWithRecent.CheckUpdateOnStartup,
        UpdateCheckIntervalHours = settingsWithRecent.UpdateCheckIntervalHours,
    });
    Check(roamingSettingsJson.Contains(nameof(RoamingAppSettings.Language))
          && roamingSettingsJson.Contains(nameof(RoamingAppSettings.Theme))
          && !roamingSettingsJson.Contains(nameof(MachineAppSettings.RecentConnectionPaths))
          && !roamingSettingsJson.Contains(nameof(MachineAppSettings.MainWindowWidth)),
          "Roaming settings persist machine-independent preferences only");

    var tempMachineSettingsPath = Path.Combine(root, "LocalConfig", "settings.json");
    var tempRoamingSettingsPath = Path.Combine(root, "RoamingConfig", "settings.json");
    var tempSettings = new SettingsService(tempMachineSettingsPath, tempRoamingSettingsPath);
    Check(!File.Exists(tempMachineSettingsPath) && !File.Exists(tempRoamingSettingsPath),
          "Unchanged settings do not create settings.json");
    Check(tempSettings.SaveIfChanged(), "Unchanged settings flush succeeds");
    Check(!File.Exists(tempMachineSettingsPath) && !File.Exists(tempRoamingSettingsPath),
          "Unchanged settings flush does not write settings.json");
    tempSettings.Settings.Language = "zh";
    Check(!File.Exists(tempRoamingSettingsPath), "Settings changes stay in memory before flush");
    Check(tempSettings.SaveIfChanged()
          && File.Exists(tempRoamingSettingsPath)
          && !File.Exists(tempMachineSettingsPath),
          "Changed roaming settings flush writes roaming settings.json");
    var savedSettingsJson = File.ReadAllText(tempRoamingSettingsPath);
    Check(savedSettingsJson.Contains("\"Language\": \"zh\""), "Changed roaming settings are serialized after flush");
    File.SetLastWriteTimeUtc(tempRoamingSettingsPath, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    var unchangedWriteTime = File.GetLastWriteTimeUtc(tempRoamingSettingsPath);
    Check(tempSettings.SaveIfChanged(), "Second unchanged settings flush succeeds");
    Check(File.GetLastWriteTimeUtc(tempRoamingSettingsPath) == unchangedWriteTime
          && File.ReadAllText(tempRoamingSettingsPath) == savedSettingsJson,
          "Unchanged roaming settings flush does not rewrite the existing file");
    tempSettings.Settings.MainWindowWidth = 1200;
    Check(tempSettings.SaveIfChanged()
          && File.Exists(tempMachineSettingsPath)
          && File.ReadAllText(tempMachineSettingsPath).Contains("\"MainWindowWidth\": 1200"),
          "Changed machine settings flush writes machine settings.json");
    Check(!File.ReadAllText(tempMachineSettingsPath).Contains("\"Language\""),
          "Machine settings.json does not include roaming preferences");
    Check(!File.ReadAllText(tempRoamingSettingsPath).Contains("\"MainWindowWidth\""),
          "Roaming settings.json does not include machine-local state");

    var concurrentMachinePath = Path.Combine(root, "ConcurrentLocal", "settings.json");
    var concurrentRoamingPath = Path.Combine(root, "ConcurrentRoaming", "settings.json");
    var concurrentA = new SettingsService(concurrentMachinePath, concurrentRoamingPath);
    var concurrentB = new SettingsService(concurrentMachinePath, concurrentRoamingPath);
    concurrentA.Settings.Language = "zh";
    concurrentB.Settings.Theme = "Dark";
    Check(concurrentA.SaveIfChanged() && concurrentB.SaveIfChanged(),
          "Concurrent settings instances can save through the shared-data lock");
    var concurrentMerged = new SettingsService(concurrentMachinePath, concurrentRoamingPath);
    Check(concurrentMerged.Settings.Language == "zh" && concurrentMerged.Settings.Theme == "Dark",
          "Three-way settings merge preserves unrelated fields from different instances");
    var sameFieldA = new SettingsService(concurrentMachinePath, concurrentRoamingPath);
    var sameFieldB = new SettingsService(concurrentMachinePath, concurrentRoamingPath);
    sameFieldA.Settings.Language = "en";
    sameFieldB.Settings.Language = null;
    Check(sameFieldA.SaveIfChanged() && sameFieldB.SaveIfChanged()
          && new SettingsService(concurrentMachinePath, concurrentRoamingPath).Settings.Language is null,
          "Three-way settings merge uses the last completed write for the same field");
    Check(JsonNode.Parse(File.ReadAllText(concurrentRoamingPath)) is JsonObject
          && Directory.GetFiles(Path.GetDirectoryName(concurrentRoamingPath)!, "*.tmp").Length == 0,
          "Atomic settings replacement leaves valid JSON and no temporary file");

    var alternateDebugPath = Path.Combine(root, "other-worktree", "bin");
    Check(DebugInstanceContext.IsDebugBuild
          && DebugInstanceContext.InstanceId.Length == 12
          && DebugInstanceContext.CreateInstanceId(AppContext.BaseDirectory)
             != DebugInstanceContext.CreateInstanceId(alternateDebugPath)
          && DebugInstanceContext.RuntimeTempRoot.Contains(DebugInstanceContext.InstanceId,
              StringComparison.OrdinalIgnoreCase),
          "Debug worktrees receive stable distinct identities and runtime temp roots");
    Check(!Path.GetFullPath(DebugInstanceContext.Info.ConfigRoot).StartsWith(
              Path.GetFullPath(DebugInstanceContext.RuntimeTempRoot),
              StringComparison.OrdinalIgnoreCase)
          && !DebugInstanceContext.Info.ConfigRoot.Contains("DebugProfile", StringComparison.OrdinalIgnoreCase),
          "Debug instance metadata keeps Config outside the isolated runtime root");

    var legacyMachineSettingsPath = Path.Combine(root, "LegacyLocal", "settings.json");
    var missingLegacyRoamingPath = Path.Combine(root, "LegacyRoaming", "settings.json");
    Directory.CreateDirectory(Path.GetDirectoryName(legacyMachineSettingsPath)!);
    File.WriteAllText(
        legacyMachineSettingsPath,
        JsonSerializer.Serialize(settingsWithRecent));
    var legacySettings = new SettingsService(legacyMachineSettingsPath, missingLegacyRoamingPath);
    Check(legacySettings.Settings.Language == "zh"
          && legacySettings.Settings.MainWindowWidth == 1200
          && legacySettings.Settings.RecentConnectionPaths.Count == 1,
          "Legacy single-file settings seed both machine and roaming settings");

    Check(progRoot.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)
          && progRoot.EndsWith(Path.Combine("Config", "Connections"), StringComparison.OrdinalIgnoreCase),
          "Program-directory root resolves under app Config");
    Check(userRoot.Contains("JeekRemoteManager")
          && userRoot.EndsWith(Path.Combine("Config", "Connections"), StringComparison.OrdinalIgnoreCase),
          "User-directory root resolves under roaming Config");
    Check(progRoot != userRoot, "The two storage locations are distinct");

    var builtInScriptsRoot = SettingsService.ResolveBuiltInScriptsRoot();
    Check(builtInScriptsRoot.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)
          && builtInScriptsRoot.EndsWith(Path.Combine("Data", "Scripts"), StringComparison.OrdinalIgnoreCase),
          "Built-in scripts root resolves under app Data");
    var autoUpdateScriptPath = Path.Combine(FindRepoRoot(), "bin", "AutoUpdate.ps1");
    var autoUpdateScript = File.Exists(autoUpdateScriptPath)
        ? File.ReadAllText(autoUpdateScriptPath)
        : "";
    Check(autoUpdateScript.Contains("$preserveNames = @(\"Config\", \"Connections\", \"Scripts\", \"AutoUpdate.ps1\")"),
          "Auto-update preserves Config and legacy top-level user data");
    Check(autoUpdateScript.Contains("Download-FileWithProgress")
          && autoUpdateScript.Contains("Write-Progress -Activity $activity")
          && autoUpdateScript.Contains("MB/s"),
          "Auto-update reports download progress and speed");
    Check(!autoUpdateScript.Contains("Write-Host \"      $status\""),
          "Auto-update does not print periodic status lines while the progress bar is active");
    Check(autoUpdateScript.Contains("Download failed from this mirror")
          && autoUpdateScript.Contains("Trying next mirror")
          && autoUpdateScript.Contains("Download failed from all mirrors"),
          "Auto-update retries alternate mirrors before failing");
    Check(autoUpdateScript.Contains("No download data received for $IdleTimeoutSeconds seconds"),
          "Auto-update abandons stalled mirror downloads");
    Check(autoUpdateScript.Contains("$minimumDownloadSpeedBytesPerSecond = 512KB")
          && autoUpdateScript.Contains("$slowDownloadWindowSeconds = 10")
          && autoUpdateScript.Contains("Download speed stayed below $minimumSpeed/s")
          && autoUpdateScript.Contains("$i -lt $downloadUrls.Count - 1"),
          "Auto-update switches mirrors after sustained low download speed");
    var autoUpdateServicePath = Path.Combine(FindRepoRoot(), "JeekRemoteManager", "Services", "AutoUpdateService.cs");
    var autoUpdateService = File.Exists(autoUpdateServicePath)
        ? File.ReadAllText(autoUpdateServicePath)
        : "";
    Check(autoUpdateService.Contains("DownloadUrls")
          && autoUpdateService.Contains("BuildDownloadUrls")
          && autoUpdateService.Contains(".Concat(DownloadUrls)"),
          "Auto-update passes mirror fallback URLs to the updater");
    var runtimeBbrDir = Path.Combine(FindRepoRoot(), "bin", "Data", "Scripts", "BBR");
    Check(!Directory.Exists(runtimeBbrDir),
          "Standalone BBR script suite is removed");
    var runtimeSingBoxDir = Path.Combine(FindRepoRoot(), "bin", "Data", "Scripts", "sing-box reality server");
    var runtimeSingBoxSuite = RemoteScriptStore.LoadSuite(runtimeSingBoxDir, RemoteScriptSuiteSource.BuiltIn);
    var runtimeSingBoxInstallPath = Path.Combine(runtimeSingBoxDir, "install.sh");
    var runtimeSingBoxInstall = File.Exists(runtimeSingBoxInstallPath)
        ? File.ReadAllText(runtimeSingBoxInstallPath)
        : "";
    var runtimeSingBoxUninstallPath = Path.Combine(runtimeSingBoxDir, "uninstall.sh");
    var runtimeSingBoxUninstall = File.Exists(runtimeSingBoxUninstallPath)
        ? File.ReadAllText(runtimeSingBoxUninstallPath)
        : "";
    var runtimeSingBoxShowLinkPath = Path.Combine(runtimeSingBoxDir, "show-link.sh");
    var runtimeSingBoxShowLink = File.Exists(runtimeSingBoxShowLinkPath)
        ? File.ReadAllText(runtimeSingBoxShowLinkPath)
        : "";
    Check(runtimeSingBoxSuite.Errors.Count == 0
          && runtimeSingBoxSuite.Name == "sing-box reality server"
          && runtimeSingBoxSuite.RelativePath == "sing-box reality server"
          && runtimeSingBoxSuite.Scripts.Any(s => s.Name == "install.sh")
          && runtimeSingBoxSuite.Scripts.Any(s => s.Name == "show-link.sh")
          && runtimeSingBoxSuite.Scripts.Any(s => s.Name == "uninstall.sh"),
          "Bundled sing-box reality server install, show-link, and uninstall scripts are discoverable");
    Check(runtimeSingBoxSuite.Parameters.Count == 2
          && runtimeSingBoxSuite.Parameters[0].Name == "PORT"
          && runtimeSingBoxSuite.Parameters[0].Type == RemoteScriptParameterType.Number
          && runtimeSingBoxSuite.Parameters[1].Name == "SNI"
          && runtimeSingBoxSuite.Parameters[1].Type == RemoteScriptParameterType.String,
          "Bundled sing-box reality server script exposes only PORT and SNI");
    Check(runtimeSingBoxInstall.Contains("https://sing-box.app/install.sh")
          && runtimeSingBoxInstall.Contains("curl -fsSL")
          && runtimeSingBoxInstall.Contains("wget -qO")
          && runtimeSingBoxInstall.Contains("apt-get install -y curl ca-certificates")
          && runtimeSingBoxInstall.Contains("dnf install -y curl ca-certificates")
          && runtimeSingBoxInstall.Contains("yum install -y curl ca-certificates")
          && runtimeSingBoxInstall.Contains("zypper --non-interactive install curl ca-certificates")
          && runtimeSingBoxInstall.Contains("pacman -Sy --noconfirm --needed curl ca-certificates")
          && runtimeSingBoxInstall.Contains("curl is required by the official sing-box install script")
          && runtimeSingBoxInstall.Contains("ensure_official_installer_dependencies"),
          "Bundled sing-box reality server install/update script ensures official installer dependencies");
    Check(runtimeSingBoxInstall.Contains("/etc/sysctl.d/99-jeekremote-bbr.conf")
          && runtimeSingBoxInstall.Contains("modprobe tcp_bbr")
          && runtimeSingBoxInstall.Contains("net.core.default_qdisc=fq")
          && runtimeSingBoxInstall.Contains("net.ipv4.tcp_congestion_control=bbr")
          && runtimeSingBoxInstall.Contains("sysctl -w net.core.default_qdisc=fq")
          && runtimeSingBoxInstall.Contains("sysctl -w net.ipv4.tcp_congestion_control=bbr")
          && runtimeSingBoxInstall.Contains("BBR is enabled.")
          && !runtimeSingBoxInstall.Contains("set_sysctl_conf_value_strict /etc/sysctl.conf"),
          "Bundled sing-box reality server install script writes BBR settings to an independent sysctl file");
    Check(runtimeSingBoxInstall.Contains("\"type\": \"vless\"")
          && runtimeSingBoxInstall.Contains("\"listen\": \"0.0.0.0\"")
          && runtimeSingBoxInstall.Contains("xtls-rprx-vision")
          && runtimeSingBoxInstall.Contains("\"reality\"")
          && runtimeSingBoxInstall.Contains("\"server\": \"$SNI\"")
          && runtimeSingBoxInstall.Contains("Writing sing-box reality server config from current PORT and SNI parameters")
          && runtimeSingBoxInstall.Contains("\"$sing_box\" check -c")
          && runtimeSingBoxInstall.Contains("systemctl restart sing-box"),
          "Bundled sing-box reality server install/update script writes and checks a REALITY config");
    Check(runtimeSingBoxInstall.Contains("https://api.ipify.org")
          && runtimeSingBoxInstall.Contains("ufw allow \"${PORT}/tcp\"")
          && runtimeSingBoxInstall.Contains("firewall-cmd --permanent --add-port=\"${PORT}/tcp\"")
          && runtimeSingBoxInstall.Contains("YOUR_SERVER_ADDRESS")
          && runtimeSingBoxInstall.Contains("/etc/sing-box/jeekremote-reality-link.conf")
          && runtimeSingBoxInstall.Contains("PUBLIC_KEY=$public_key")
          && runtimeSingBoxInstall.Contains("SHORT_ID=$short_id")
          && runtimeSingBoxInstall.Contains("install/update completed")
          && runtimeSingBoxInstall.Contains("Repeated runs update sing-box and replace the config with the current PORT and SNI")
          && runtimeSingBoxInstall.Contains("cloud security group"),
          "Bundled sing-box reality server install/update script detects address and handles supported firewalls");
    Check(runtimeSingBoxShowLink.Contains("/etc/sing-box/jeekremote-reality-link.conf")
          && runtimeSingBoxShowLink.Contains("Run install.sh once")
          && runtimeSingBoxShowLink.Contains("https://api.ipify.org")
          && runtimeSingBoxShowLink.Contains("flow=xtls-rprx-vision")
          && runtimeSingBoxShowLink.Contains("fp=chrome")
          && runtimeSingBoxShowLink.Contains("pbk=${public_key}")
          && runtimeSingBoxShowLink.Contains("sid=${short_id}")
          && runtimeSingBoxShowLink.Contains("sing-box reality client link"),
          "Bundled sing-box reality server show-link script prints the saved client URI");
    Check(runtimeSingBoxUninstall.Contains("systemctl stop sing-box")
          && runtimeSingBoxUninstall.Contains("apt-get purge -y sing-box")
          && runtimeSingBoxUninstall.Contains("dnf remove -y sing-box")
          && runtimeSingBoxUninstall.Contains("zypper --non-interactive remove sing-box")
          && runtimeSingBoxUninstall.Contains("pacman -Rns --noconfirm sing-box"),
          "Bundled sing-box reality server uninstall script removes service and package");
    Check(runtimeSingBoxUninstall.Contains("ufw --force delete allow \"${PORT}/tcp\"")
          && runtimeSingBoxUninstall.Contains("firewall-cmd --permanent --remove-port=\"${PORT}/tcp\"")
          && runtimeSingBoxUninstall.Contains("sing-box-config-backup-before-uninstall")
          && runtimeSingBoxUninstall.Contains("jeekremote-reality-link.conf")
          && runtimeSingBoxUninstall.Contains("BBR settings were left unchanged")
          && !runtimeSingBoxUninstall.Contains("built-in BBR disable script"),
          "Bundled sing-box reality server uninstall script cleans local state without disabling BBR");
    var runtimeSingBoxClientDir = Path.Combine(FindRepoRoot(), "bin", "Data", "Scripts", "sing-box reality client");
    var runtimeSingBoxClientSuite = RemoteScriptStore.LoadSuite(runtimeSingBoxClientDir, RemoteScriptSuiteSource.BuiltIn);
    var runtimeSingBoxClientInstallPath = Path.Combine(runtimeSingBoxClientDir, "install.sh");
    var runtimeSingBoxClientInstall = File.Exists(runtimeSingBoxClientInstallPath)
        ? File.ReadAllText(runtimeSingBoxClientInstallPath)
        : "";
    Check(runtimeSingBoxClientSuite.Errors.Count == 0
          && runtimeSingBoxClientSuite.Name == "sing-box reality client"
          && runtimeSingBoxClientSuite.Scripts.Any(s => s.Name == "install.sh")
          && runtimeSingBoxClientSuite.Scripts.Any(s => s.Name == "uninstall.sh"),
          "Bundled sing-box reality client install and uninstall scripts are discoverable");
    const int runtimeSingBoxClientServerLinkCount = 9;
    Check(runtimeSingBoxClientSuite.Parameters.Count == runtimeSingBoxClientServerLinkCount + 4
          && Enumerable.Range(1, runtimeSingBoxClientServerLinkCount).All(i =>
              runtimeSingBoxClientSuite.Parameters.Single(p => p.Name == $"SERVER_LINK_{i}").Type == RemoteScriptParameterType.String)
          && runtimeSingBoxClientSuite.Parameters.Single(p => p.Name == "LISTEN_PORT").Type == RemoteScriptParameterType.Number
          && runtimeSingBoxClientSuite.Parameters.Single(p => p.Name == "LISTEN_PORT").DefaultValue == "1080"
          && runtimeSingBoxClientSuite.Parameters.Single(p => p.Name == "ALLOW_EXTERNAL").Type == RemoteScriptParameterType.Bool
          && runtimeSingBoxClientSuite.Parameters.Single(p => p.Name == "ALLOW_EXTERNAL").DefaultValue == "false"
          && runtimeSingBoxClientSuite.Parameters.Single(p => p.Name == "ENABLE_TUN").Type == RemoteScriptParameterType.Bool
          && runtimeSingBoxClientSuite.Parameters.Single(p => p.Name == "ENABLE_TUN").DefaultValue == "false"
          && runtimeSingBoxClientSuite.Parameters.Single(p => p.Name == "UPDATE_SING_BOX").Type == RemoteScriptParameterType.Bool
          && runtimeSingBoxClientSuite.Parameters.Single(p => p.Name == "UPDATE_SING_BOX").DefaultValue == "true",
          "Bundled sing-box reality client script exposes nine server links and update toggle parameters");
    Check(runtimeSingBoxClientInstall.Contains("https://api.github.com/repos/SagerNet/sing-box/releases/latest")
          && runtimeSingBoxClientInstall.Contains("https://github.com/SagerNet/sing-box/releases/download/v${version}/${package_name}")
          && runtimeSingBoxClientInstall.Contains("https://ghfast.top/${package_url}")
          && runtimeSingBoxClientInstall.Contains("https://gh-proxy.com/github.com/SagerNet/sing-box/releases/download/v${version}/${package_name}")
          && runtimeSingBoxClientInstall.Contains("download_first_available")
          && runtimeSingBoxClientInstall.Contains("Checking latest sing-box release")
          && runtimeSingBoxClientInstall.Contains("Trying sing-box download")
          && runtimeSingBoxClientInstall.Contains("download_connect_timeout_seconds=2")
          && runtimeSingBoxClientInstall.Contains("download_response_timeout_seconds=4")
          && runtimeSingBoxClientInstall.Contains("download_package_timeout_seconds=20")
          && runtimeSingBoxClientInstall.Contains("download_stall_timeout_seconds=3")
          && runtimeSingBoxClientInstall.Contains("download_min_speed_bytes=65536")
          && runtimeSingBoxClientInstall.Contains("--max-time \"$download_package_timeout_seconds\"")
          && runtimeSingBoxClientInstall.Contains("timeout \"$download_package_timeout_seconds\"")
          && runtimeSingBoxClientInstall.Contains("--retry 0")
          && runtimeSingBoxClientInstall.Contains("--tries=1")
          && runtimeSingBoxClientInstall.Contains("--speed-time \"$download_stall_timeout_seconds\"")
          && !runtimeSingBoxClientInstall.Contains("https://sing-box.app/install.sh"),
          "Bundled sing-box reality client install script downloads sing-box releases with fast GitHub mirror fallback");
    Check(runtimeSingBoxClientInstall.Contains("UPDATE_SING_BOX=${UPDATE_SING_BOX:-true}")
          && runtimeSingBoxClientInstall.Contains("Skipping sing-box install/update because UPDATE_SING_BOX=false")
          && runtimeSingBoxClientInstall.Contains("Continuing with existing sing-box")
          && runtimeSingBoxClientInstall.Contains("install_or_update_sing_box")
          && runtimeSingBoxClientInstall.Contains("\"$sing_box\" check -c")
          && runtimeSingBoxClientInstall.Contains("systemctl restart sing-box")
          && !runtimeSingBoxClientInstall.Contains("SING_BOX_VERSION")
          && !runtimeSingBoxClientInstall.Contains("SING_BOX_PACKAGE_URL"),
          "Bundled sing-box reality client install script can skip updates or fall back to an existing sing-box binary");
    var runtimeServerOptimizationDir = Path.Combine(FindRepoRoot(), "bin", "Data", "Scripts", "Optimization");
    var runtimeServerOptimizationSuite = RemoteScriptStore.LoadSuite(runtimeServerOptimizationDir, RemoteScriptSuiteSource.BuiltIn);
    var runtimeServerOptimizationScriptPath = Path.Combine(runtimeServerOptimizationDir, "apply.sh");
    var runtimeServerOptimizationScript = File.Exists(runtimeServerOptimizationScriptPath)
        ? File.ReadAllText(runtimeServerOptimizationScriptPath)
        : "";
    Check(runtimeServerOptimizationSuite.Errors.Count == 0
          && runtimeServerOptimizationSuite.Name == RemoteScriptSuiteNames.Optimization
          && runtimeServerOptimizationSuite.RelativePath == RemoteScriptSuiteNames.Optimization
          && runtimeServerOptimizationSuite.Scripts.Count == 1
          && runtimeServerOptimizationSuite.Scripts[0].Name == "apply.sh",
          "Bundled server optimization script is discoverable");
    Check(runtimeServerOptimizationSuite.Parameters.Count == 7
          && runtimeServerOptimizationSuite.Parameters.All(p => p.Type == RemoteScriptParameterType.Bool)
          && runtimeServerOptimizationSuite.Parameters.Single(p => p.Name == "ENABLE_FAIL2BAN").DefaultValue == "true"
          && runtimeServerOptimizationSuite.Parameters.Single(p => p.Name == "ENABLE_FIREWALL").DefaultValue == "true"
          && runtimeServerOptimizationSuite.Parameters.Single(p => p.Name == "ENABLE_AUTO_UPDATES").DefaultValue == "true"
          && runtimeServerOptimizationSuite.Parameters.Single(p => p.Name == "ENABLE_BBR").DefaultValue == "true"
          && runtimeServerOptimizationSuite.Parameters.Single(p => p.Name == "ENABLE_TIME_SYNC").DefaultValue == "true"
          && runtimeServerOptimizationSuite.Parameters.Single(p => p.Name == "ENABLE_APT_AUTOREMOVE").DefaultValue == "false"
          && runtimeServerOptimizationSuite.Parameters.Single(p => p.Name == "ENABLE_COMMAND_COLORS").DefaultValue == "true"
          && runtimeServerOptimizationSuite.Parameters.Any(p => p.Name == "ENABLE_FAIL2BAN")
          && runtimeServerOptimizationSuite.Parameters.Any(p => p.Name == "ENABLE_FIREWALL")
          && runtimeServerOptimizationSuite.Parameters.Any(p => p.Name == "ENABLE_AUTO_UPDATES")
          && runtimeServerOptimizationSuite.Parameters.Any(p => p.Name == "ENABLE_BBR")
          && runtimeServerOptimizationSuite.Parameters.Any(p => p.Name == "ENABLE_TIME_SYNC")
          && runtimeServerOptimizationSuite.Parameters.Any(p => p.Name == "ENABLE_APT_AUTOREMOVE")
          && runtimeServerOptimizationSuite.Parameters.Any(p => p.Name == "ENABLE_COMMAND_COLORS"),
          "Bundled server optimization script exposes boolean feature toggles");
    Check(runtimeServerOptimizationScript.Contains("install_packages fail2ban")
          && runtimeServerOptimizationScript.Contains("systemctl enable fail2ban")
          && runtimeServerOptimizationScript.Contains("systemctl restart fail2ban")
          && runtimeServerOptimizationScript.Contains("/etc/fail2ban/jail.d/jeekremote-sshd.conf")
          && runtimeServerOptimizationScript.Contains("port = $ssh_port"),
          "Bundled server optimization script installs fail2ban with a jail bound to the detected SSH port");
    Check(runtimeServerOptimizationScript.Contains("\"$sshd_bin\" -T")
          && runtimeServerOptimizationScript.Contains("printf '22\\n'"),
          "Bundled server optimization script detects the SSH port with a 22 fallback");
    Check(runtimeServerOptimizationScript.Contains("ufw allow \"${ssh_port}/tcp\"")
          && runtimeServerOptimizationScript.Contains("ufw allow 443/tcp")
          && runtimeServerOptimizationScript.Contains("firewall-cmd --permanent --add-port=\"${ssh_port}/tcp\"")
          && runtimeServerOptimizationScript.Contains("firewall-cmd --permanent --add-port=443/tcp"),
          "Bundled server optimization script allows SSH and 443 on supported firewalls");
    Check(runtimeServerOptimizationScript.Contains("unattended-upgrades")
          && runtimeServerOptimizationScript.Contains("dnf-automatic")
          && runtimeServerOptimizationScript.Contains("yum-cron"),
          "Bundled server optimization script enables automatic updates on supported package managers");
    Check(runtimeServerOptimizationScript.Contains("modprobe tcp_bbr")
          && runtimeServerOptimizationScript.Contains("/etc/sysctl.d/99-jeekremote-bbr.conf")
          && runtimeServerOptimizationScript.Contains("net.core.default_qdisc = fq")
          && runtimeServerOptimizationScript.Contains("net.ipv4.tcp_congestion_control = bbr")
          && runtimeServerOptimizationScript.Contains("sysctl -w net.core.default_qdisc=fq")
          && runtimeServerOptimizationScript.Contains("sysctl -w net.ipv4.tcp_congestion_control=bbr")
          && runtimeServerOptimizationScript.Contains("BBR configuration written to ${bbr_config_file}.")
          && !runtimeServerOptimizationScript.Contains("set_sysctl_conf_value /etc/sysctl.conf"),
          "Bundled server optimization script writes BBR settings to an independent sysctl file and applies them immediately");
    Check(runtimeServerOptimizationScript.Contains("is_enabled \"$ENABLE_FIREWALL\"")
          && runtimeServerOptimizationScript.Contains("is_enabled \"$ENABLE_FAIL2BAN\"")
          && runtimeServerOptimizationScript.Contains("is_enabled \"$ENABLE_AUTO_UPDATES\"")
          && runtimeServerOptimizationScript.Contains("is_enabled \"$ENABLE_BBR\"")
          && runtimeServerOptimizationScript.Contains("is_enabled \"$ENABLE_TIME_SYNC\"")
          && runtimeServerOptimizationScript.Contains("is_enabled \"$ENABLE_APT_AUTOREMOVE\"")
          && runtimeServerOptimizationScript.Contains("is_enabled \"$ENABLE_COMMAND_COLORS\""),
          "Bundled server optimization script gates each feature behind a boolean parameter");
    Check(runtimeServerOptimizationScript.Contains("ENABLE_TIME_SYNC=${ENABLE_TIME_SYNC:-true}")
          && runtimeServerOptimizationScript.Contains("enable_time_sync")
          && runtimeServerOptimizationScript.Contains("enable_chrony_time_sync")
          && runtimeServerOptimizationScript.Contains("try_enable_timesyncd_time_sync")
          && runtimeServerOptimizationScript.Contains("install_packages chrony")
          && runtimeServerOptimizationScript.Contains("apt-get install -y systemd-timesyncd")
          && runtimeServerOptimizationScript.Contains("systemctl enable --now chronyd.service")
          && runtimeServerOptimizationScript.Contains("systemctl enable --now chrony.service")
          && runtimeServerOptimizationScript.Contains("systemctl enable --now systemd-timesyncd.service")
          && runtimeServerOptimizationScript.Contains("timedatectl set-ntp true")
          && runtimeServerOptimizationScript.Contains("stop_disable_service systemd-timesyncd.service")
          && runtimeServerOptimizationScript.Contains("stop_disable_service ntp.service")
          && runtimeServerOptimizationScript.Contains("stop_disable_service ntpd.service")
          && runtimeServerOptimizationScript.Contains("feature_done \"Time sync\"")
          && runtimeServerOptimizationScript.Contains("feature_skipped \"Time sync\""),
          "Bundled server optimization script enables automatic time sync via chrony or systemd-timesyncd");
    Check(!runtimeServerOptimizationScript.Contains("COLOR_FEATURE_START")
          && !runtimeServerOptimizationScript.Contains("feature_start")
          && runtimeServerOptimizationScript.Contains("COLOR_FEATURE_DONE")
          && runtimeServerOptimizationScript.Contains("COLOR_FEATURE_SKIPPED")
          && runtimeServerOptimizationScript.Contains("feature_done \"Firewall\"")
          && runtimeServerOptimizationScript.Contains("feature_skipped \"Firewall\"")
          && runtimeServerOptimizationScript.Contains("feature_done \"fail2ban\"")
          && runtimeServerOptimizationScript.Contains("feature_skipped \"fail2ban\"")
          && runtimeServerOptimizationScript.Contains("feature_done \"Automatic security updates\"")
          && runtimeServerOptimizationScript.Contains("feature_skipped \"Automatic security updates\"")
          && runtimeServerOptimizationScript.Contains("feature_done \"BBR\"")
          && runtimeServerOptimizationScript.Contains("feature_skipped \"BBR\"")
          && runtimeServerOptimizationScript.Contains("feature_done \"Time sync\"")
          && runtimeServerOptimizationScript.Contains("feature_skipped \"Time sync\"")
          && runtimeServerOptimizationScript.Contains("feature_done \"apt autoremove\"")
          && runtimeServerOptimizationScript.Contains("feature_skipped \"apt autoremove\"")
          && runtimeServerOptimizationScript.Contains("feature_done \"Command colors\"")
          && runtimeServerOptimizationScript.Contains("feature_skipped \"Command colors\""),
          "Bundled server optimization script separates every feature with colored end output");
    Check(runtimeServerOptimizationScript.Contains("apt-get autoremove -y")
          && runtimeServerOptimizationScript.Contains("/etc/apt/apt.conf.d/52unattended-upgrades-jeekremote-autoremove")
          && runtimeServerOptimizationScript.Contains("Unattended-Upgrade::Remove-Unused-Dependencies \"true\"")
          && runtimeServerOptimizationScript.Contains("Unattended-Upgrade::Remove-New-Unused-Dependencies \"true\"")
          && runtimeServerOptimizationScript.Contains("/var/run/reboot-required")
          && runtimeServerOptimizationScript.Contains("skipping immediate apt autoremove")
          && runtimeServerOptimizationScript.Contains("ENABLE_APT_AUTOREMOVE=${ENABLE_APT_AUTOREMOVE:-false}"),
          "Bundled server optimization script supports unattended-upgrades apt autoremove");
    Check(runtimeServerOptimizationScript.Contains("/etc/profile.d/jeekremote-command-colors.sh")
          && runtimeServerOptimizationScript.Contains("ENABLE_COMMAND_COLORS=${ENABLE_COMMAND_COLORS:-true}")
          && runtimeServerOptimizationScript.Contains("case \"$-\"")
          && runtimeServerOptimizationScript.Contains("dircolors -b")
          && runtimeServerOptimizationScript.Contains("alias ls='ls --color=auto'")
          && runtimeServerOptimizationScript.Contains("alias ll='ls -alF --color=auto'")
          && runtimeServerOptimizationScript.Contains("alias la='ls -A --color=auto'")
          && runtimeServerOptimizationScript.Contains("alias l='ls -CF --color=auto'")
          && runtimeServerOptimizationScript.Contains("alias grep='grep --color=auto'")
          && runtimeServerOptimizationScript.Contains("export LESS='-R'")
          && runtimeServerOptimizationScript.Contains("prompt_user_color='31'")
          && runtimeServerOptimizationScript.Contains("prompt_user_color='32'")
          && runtimeServerOptimizationScript.Contains("BASH_VERSION")
          && runtimeServerOptimizationScript.Contains("\\u")
          && runtimeServerOptimizationScript.Contains("\\h")
          && runtimeServerOptimizationScript.Contains("\\w")
          && runtimeServerOptimizationScript.Contains("export PS1"),
          "Bundled server optimization script installs interactive command color defaults");
    Check(runtimeServerOptimizationScript.Contains("JEEKREMOTE_CURRENT_SHELL_HOOK")
          && runtimeServerOptimizationScript.Contains(">> \"$JEEKREMOTE_CURRENT_SHELL_HOOK\"")
          && runtimeServerOptimizationScript.Contains(". /etc/profile.d/jeekremote-command-colors.sh >/dev/null 2>&1 || true"),
          "Bundled server optimization command colors use the generic current-shell hook");
    Check(!runtimeServerOptimizationScript.Contains("PasswordAuthentication")
          && !runtimeServerOptimizationScript.Contains("PermitRootLogin"),
          "Bundled server optimization script does not harden SSH login policy");

    var progScriptsRoot = SettingsService.ResolveScriptsRoot(StorageLocation.ProgramDirectory);
    var userScriptsRoot = SettingsService.ResolveScriptsRoot(StorageLocation.UserDirectory);
    var customBaseRoot = Path.Combine(root, "CustomStorage");
    var customConnectionsRoot = SettingsService.ResolveConnectionsRoot(StorageLocation.CustomDirectory, customBaseRoot);
    var customScriptsRoot = SettingsService.ResolveScriptsRoot(StorageLocation.CustomDirectory, customBaseRoot);
    Check(progScriptsRoot.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)
          && progScriptsRoot.EndsWith(Path.Combine("Config", "Scripts"), StringComparison.OrdinalIgnoreCase),
          "Program-directory custom scripts root resolves under app Config");
    Check(!string.Equals(Path.GetFullPath(builtInScriptsRoot), Path.GetFullPath(progScriptsRoot),
              StringComparison.OrdinalIgnoreCase),
          "Built-in scripts root is separate from program-directory custom scripts root");
    Check(userScriptsRoot.Contains("JeekRemoteManager")
          && userScriptsRoot.EndsWith(Path.Combine("Config", "Scripts"), StringComparison.OrdinalIgnoreCase),
          "User-directory custom scripts root resolves under roaming Config");
    Check(customConnectionsRoot.StartsWith(customBaseRoot, StringComparison.OrdinalIgnoreCase)
          && customConnectionsRoot.EndsWith(Path.Combine("Config", "Connections"), StringComparison.OrdinalIgnoreCase)
          && customScriptsRoot.StartsWith(customBaseRoot, StringComparison.OrdinalIgnoreCase)
          && customScriptsRoot.EndsWith(Path.Combine("Config", "Scripts"), StringComparison.OrdinalIgnoreCase),
          "Custom storage resolves under the chosen Config folder");
    var settingsOnlyChange = MainWindowViewModel.ClassifyPortableConfigChanges(new[]
    {
        SettingsService.ResolveSettingsPath(StorageLocation.ProgramDirectory),
    });
    Check(settingsOnlyChange.SettingsChanged
          && !settingsOnlyChange.ConnectionsChanged
          && !settingsOnlyChange.ScriptsChanged,
          "Portable watcher classifies settings.json changes without reloading data folders");
    var connectionsOnlyChange = MainWindowViewModel.ClassifyPortableConfigChanges(new[]
    {
        Path.Combine(SettingsService.ResolveConnectionsRoot(StorageLocation.ProgramDirectory), "Servers", "web01.json"),
    });
    Check(!connectionsOnlyChange.SettingsChanged
          && connectionsOnlyChange.ConnectionsChanged
          && !connectionsOnlyChange.ScriptsChanged,
          "Portable watcher classifies connection changes without reloading settings or scripts");
    var scriptsOnlyChange = MainWindowViewModel.ClassifyPortableConfigChanges(new[]
    {
        Path.Combine(SettingsService.ResolveScriptsRoot(StorageLocation.ProgramDirectory), "Deploy", RemoteScriptStore.ParameterFileName),
    });
    Check(!scriptsOnlyChange.SettingsChanged
          && !scriptsOnlyChange.ConnectionsChanged
          && scriptsOnlyChange.ScriptsChanged,
          "Portable watcher classifies script changes without reloading settings or connections");
    var unrelatedChange = MainWindowViewModel.ClassifyPortableConfigChanges(new[]
    {
        Path.Combine(SettingsService.ResolveConfigRoot(StorageLocation.ProgramDirectory), "Other", "ignored.txt"),
    });
    Check(!unrelatedChange.HasAnyChange, "Portable watcher ignores unchanged configuration areas");

    // --- File-system script suites and parameter bindings ---
    var scriptRoot = Path.Combine(root, "Scripts");
    var builtInScriptRoot = Path.Combine(root, "BuiltInScripts");
    var scriptStore = new RemoteScriptStore(scriptRoot, builtInScriptRoot);
    var builtInDeployDir = Path.Combine(builtInScriptRoot, "Deploy");
    Directory.CreateDirectory(builtInDeployDir);
    File.WriteAllText(Path.Combine(builtInDeployDir, RemoteScriptStore.ParameterFileName), "BUILTIN=string\n");
    File.WriteAllText(Path.Combine(builtInDeployDir, "builtin.sh"), "echo builtin\n");

    var builtInOnlyDir = Path.Combine(builtInScriptRoot, "BuiltInOnly");
    Directory.CreateDirectory(builtInOnlyDir);
    File.WriteAllText(Path.Combine(builtInOnlyDir, RemoteScriptStore.ParameterFileName), "VALUE=string\n");
    File.WriteAllText(Path.Combine(builtInOnlyDir, "show.sh"), "echo builtin-only\n");

    var deployDir = Path.Combine(scriptRoot, "Deploy");
    Directory.CreateDirectory(deployDir);
    File.WriteAllText(Path.Combine(deployDir, RemoteScriptStore.ParameterFileName), """
    # Deployment inputs
    TARGET=string=default host

    COUNT=number=3
    FORCE=bool=yes
    TOKEN=secret
    MODE=enum:fast|safe|debug=safe
    """);
    File.WriteAllText(Path.Combine(deployDir, "deploy.sh"),
        "printf '%s\\n' \"$TARGET\" \"$TOKEN\" \"$FORCE\" \"$MODE\"\n");
    File.WriteAllText(Path.Combine(deployDir, "restart.sh"), "echo restart\n");
    File.WriteAllText(Path.Combine(deployDir, "README.txt"), "not executable\n");

    var auditDir = Path.Combine(scriptRoot, "Audit");
    Directory.CreateDirectory(auditDir);
    File.WriteAllText(Path.Combine(auditDir, RemoteScriptStore.ParameterFileName), "LEVEL=enum:info|debug\n");
    File.WriteAllText(Path.Combine(auditDir, "check.sh"), "echo check\n");

    var suites = scriptStore.LoadAll();
    var loadedSuite = suites.Single(s => s.Name == "Deploy");
    var builtInOnlySuite = suites.Single(s => s.Name == "BuiltInOnly");
    Check(suites.Count == 3
          && loadedSuite.Parameters.Count == 5
          && loadedSuite.Errors.Count == 0
          && loadedSuite.Source == RemoteScriptSuiteSource.User
          && builtInOnlySuite.Source == RemoteScriptSuiteSource.BuiltIn,
          "RemoteScriptStore scans built-in and custom script suite directories");
    Check(loadedSuite.Parameters.All(p => p.Name != "BUILTIN")
          && loadedSuite.Scripts.All(s => s.Name != "builtin.sh"),
          "Custom script suites override built-in suites with the same name");
    Check(loadedSuite.Scripts.Count == 2
          && loadedSuite.Scripts.Any(s => s.Name == "deploy.sh")
          && loadedSuite.Scripts.All(s => s.Name.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)),
          "RemoteScriptStore discovers only .sh functions");
    Check(loadedSuite.Parameters.Single(p => p.Name == "TARGET").DefaultValue == "default host"
          && loadedSuite.Parameters.Single(p => p.Name == "COUNT").DefaultValue == "3"
          && loadedSuite.Parameters.Single(p => p.Name == "FORCE").DefaultValue == "true"
          && loadedSuite.Parameters.Single(p => p.Name == "MODE").DefaultValue == "safe",
          "params.conf supports default values for script parameters");

    var parsedErrors = new List<string>();
    _ = RemoteScriptStore.ParseParameterFile(new[]
    {
        "",
        "# comment",
        "bad-name=string",
        "COUNT=decimal",
        "MODE=enum:",
        "BAD_NUMBER=number=abc",
        "BAD_BOOL=bool=maybe",
        "BAD_MODE=enum:a|b=c",
    }, parsedErrors);
    Check(parsedErrors.Count >= 6,
          "params.conf parser supports blank lines/comments and rejects invalid names, types, and defaults");

    var missingParamsDir = Path.Combine(scriptRoot, "MissingParams");
    Directory.CreateDirectory(missingParamsDir);
    File.WriteAllText(Path.Combine(missingParamsDir, "run.sh"), "echo missing\n");
    Check(scriptStore.LoadSuite(missingParamsDir).Errors.Any(e => e.Contains(RemoteScriptStore.ParameterFileName)),
          "Script suites require readable params.conf");

    var oldConnectionPath = Path.Combine(folder, "legacy-json.json");
    File.WriteAllText(oldConnectionPath, """
    {
      "Type": "Ssh",
      "Name": "legacy-json",
      "Host": "legacy.example",
      "Port": 22,
      "Username": "root",
      "EncryptedPassword": ""
    }
    """);
    var migratedLegacyConnection = store.Load(oldConnectionPath);
    var migratedLegacyConnectionId = migratedLegacyConnection.ConnectionId;
    Check(migratedLegacyConnection.ScriptBindings.Count == 0,
          "Old connection JSON without ScriptBindings loads with an empty binding list");
    Check(Guid.TryParse(migratedLegacyConnectionId, out _)
          && store.Load(oldConnectionPath).ConnectionId == migratedLegacyConnectionId
          && File.ReadAllText(oldConnectionPath).Contains("ConnectionId", StringComparison.Ordinal),
          "Legacy connection JSON receives and persists a GUID on first load");

    var oldScriptBindingPath = Path.Combine(folder, "legacy-script-binding.json");
    File.WriteAllText(oldScriptBindingPath, $$"""
    {
      "Type": "Ssh",
      "Name": "legacy-script-binding",
      "Host": "legacy.example",
      "Port": 22,
      "Username": "root",
      "EncryptedPassword": "",
      "ScriptBindings": [
        {
          "SuitePath": "{{loadedSuite.RelativePath}}",
          "Values": [
            { "Name": "TARGET", "Value": "legacy" }
          ]
        }
      ]
    }
    """);
    var oldScriptBinding = store.Load(oldScriptBindingPath).ScriptBindings.Single();
    Check(oldScriptBinding.Name == loadedSuite.RelativePath
          && oldScriptBinding.Params.Single().Value == "legacy",
          "Old script binding JSON SuitePath/Values migrates to Name/Params");

    var defaultPanel = new ScriptSuitePanelViewModel(
        loadedSuite,
        new ConnectionScriptBinding { Name = loadedSuite.RelativePath },
        () => { });
    Check(defaultPanel.Parameters.Single(p => p.Name == "TARGET").Value == "default host"
          && defaultPanel.Parameters.Single(p => p.Name == "COUNT").Value == "3"
          && defaultPanel.Parameters.Single(p => p.Name == "FORCE").BoolValue
          && defaultPanel.Parameters.Single(p => p.Name == "MODE").SelectedEnumValue == "safe",
          "Script parameter panel fills missing values from params.conf defaults");
    defaultPanel.Parameters.Single(p => p.Name == "TARGET").Value = "changed";
    defaultPanel.Parameters.Single(p => p.Name == "FORCE").BoolValue = false;
    defaultPanel.Parameters.Single(p => p.Name == "MODE").SelectedEnumValue = "debug";
    defaultPanel.ClearParameters();
    Check(defaultPanel.Parameters.Single(p => p.Name == "TARGET").Value == "default host"
          && defaultPanel.Parameters.Single(p => p.Name == "FORCE").BoolValue
          && defaultPanel.Parameters.Single(p => p.Name == "MODE").SelectedEnumValue == "safe",
          "Clearing script parameters restores params.conf defaults");

    var invalidBinding = new ConnectionScriptBinding
    {
        Name = loadedSuite.RelativePath,
        Params =
        {
            new ConnectionScriptParameterValue { Name = "COUNT", Value = "many" },
            new ConnectionScriptParameterValue { Name = "FORCE", Value = "maybe" },
            new ConnectionScriptParameterValue { Name = "MODE", Value = "unsafe" },
        },
    };
    Check(RemoteScriptLauncher.ValidateBinding(loadedSuite, invalidBinding).Count >= 4,
          "Script binding validation catches number, bool, enum, and missing secret errors");

    const string secretToken = "tok'en line1\nline2 密码";
    var validBinding = new ConnectionScriptBinding
    {
        Name = loadedSuite.RelativePath,
        Params =
        {
            new ConnectionScriptParameterValue { Name = "TARGET", Value = "web api" },
            new ConnectionScriptParameterValue { Name = "COUNT", Value = "2.5" },
            new ConnectionScriptParameterValue { Name = "FORCE", Value = "yes" },
            new ConnectionScriptParameterValue { Name = "MODE", Value = "fast" },
            new ConnectionScriptParameterValue { Name = "TOKEN", Value = secretToken },
        },
    };

    var protectedBinding = RemoteScriptLauncher.ProtectSecretValues(loadedSuite, validBinding);
    var protectedToken = protectedBinding.Params.Single(v => v.Name == "TOKEN").Value;
    Check(MasterKeyService.IsPasswordBlob(protectedToken), "Secret script parameters are encrypted");
    Check(!JsonSerializer.Serialize(protectedBinding).Contains(secretToken),
          "Protected script binding JSON does not contain the clear secret");
    var protectedBindingJson = JsonSerializer.Serialize(protectedBinding);
    Check(protectedBindingJson.Contains("\"Name\"")
          && protectedBindingJson.Contains("\"Params\"")
          && !protectedBindingJson.Contains("SuitePath")
          && !protectedBindingJson.Contains("Values"),
          "Protected script binding JSON uses Name and Params fields");
    Check(RemoteScriptLauncher.ValidateBinding(loadedSuite, protectedBinding).Count == 0,
          "Protected script binding validates with the current master password");

    var deployFile = loadedSuite.Scripts.Single(s => s.Name == "deploy.sh");
    var defaultValueBinding = new ConnectionScriptBinding
    {
        Name = loadedSuite.RelativePath,
        Params =
        {
            new ConnectionScriptParameterValue { Name = "TOKEN", Value = secretToken },
        },
    };
    Check(RemoteScriptLauncher.ValidateBinding(loadedSuite, defaultValueBinding).Count == 0,
          "Script binding validation accepts params.conf defaults for missing values");
    var defaultPayload = RemoteScriptLauncher.BuildPayload(loadedSuite, deployFile, defaultValueBinding);
    Check(defaultPayload.Contains("export TARGET='default host'")
          && defaultPayload.Contains("export COUNT='3'")
          && defaultPayload.Contains("export FORCE='true'")
          && defaultPayload.Contains("export MODE='safe'"),
          "Script payload exports params.conf defaults for missing values");

    var payload = RemoteScriptLauncher.BuildPayload(loadedSuite, deployFile, protectedBinding);
    Check(payload.Contains("export TARGET='web api'")
          && payload.Contains("export COUNT='2.5'")
          && payload.Contains("export FORCE='true'")
          && payload.Contains("export MODE='fast'")
          && !payload.Contains("JRM_"),
          "Script payload exports original environment variable names");
    Check(!payload.Contains('\r'), "Script payload uses LF line endings for remote sh");
    Check(payload.Contains("tok'\"'\"'en line1") && payload.Contains("line2 密码"),
          "Script payload shell-quotes quotes, newlines, and Unicode values");
    Check(!payload.Contains("JRM_SCRIPT_", StringComparison.Ordinal)
          && !payload.Contains("stty -echo", StringComparison.Ordinal)
          && !payload.Contains("__JRM_PAYLOAD_", StringComparison.Ordinal)
          && !payload.Contains("__jrm_payload", StringComparison.Ordinal),
          "Script execution payload contains only the script body, not terminal control wrappers");
    var interactivePayload = InteractiveShellPayloadRunner.Build(payload, "SMOKETOKEN");
    var encodedPayload = InteractiveShellPayloadRunner.EncodePayloadForShell(payload);
    var encodedPayloadFirstLine = encodedPayload[..Math.Min(
        InteractiveShellPayloadRunner.EncodedPayloadLineLength,
        encodedPayload.Length)];
    var encodedPayloadLines = interactivePayload.ExecuteCommand
        .Split('\n')
        .Where(line => line.Length > 0 && line.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '+' or '/' or '='))
        .ToArray();
    var executeCommandFirstLine = interactivePayload.ExecuteCommand.Split('\n')[0];
    Check(interactivePayload.PrepareCommand.Contains("stty -echo", StringComparison.Ordinal)
          && interactivePayload.PrepareCommand.Contains("'__JRM_READY_' 'SMOKETOKEN__'", StringComparison.Ordinal)
          && interactivePayload.PrepareCommand.Contains("jeekremote-current-shell-SMOKETOKEN-$$.sh", StringComparison.Ordinal)
          && interactivePayload.ExecuteCommand.Contains(
              "base64 -d <<'__JRM_PAYLOAD_SMOKETOKEN__' | gzip -dc | { printf '\\n%s%s\\n' '__JRM_BEGIN_' 'SMOKETOKEN__'; sh -s; }; ",
              StringComparison.Ordinal)
          && interactivePayload.ExecuteCommand.Contains(interactivePayload.PayloadDelimiter, StringComparison.Ordinal)
          && interactivePayload.ExecuteCommand.Contains(encodedPayloadFirstLine, StringComparison.Ordinal)
          && encodedPayloadLines.All(line => line.Length <= InteractiveShellPayloadRunner.EncodedPayloadLineLength)
          && executeCommandFirstLine.Contains("'__JRM_BEGIN_' 'SMOKETOKEN__'", StringComparison.Ordinal)
          && executeCommandFirstLine.Contains("'__JRM_EXIT_' 'SMOKETOKEN__'", StringComparison.Ordinal)
          && !interactivePayload.ExecuteCommand.Contains(payload, StringComparison.Ordinal)
          && executeCommandFirstLine.Contains(InteractiveShellPayloadRunner.CurrentShellHookVariable, StringComparison.Ordinal)
          && executeCommandFirstLine.Contains(". \"$__jrm_current_shell_hook\" >/dev/null 2>&1 || true", StringComparison.Ordinal)
          && executeCommandFirstLine.Contains("rm -f \"$__jrm_current_shell_hook\" 2>/dev/null || true", StringComparison.Ordinal)
          && !interactivePayload.ExecuteCommand.Contains('\r'),
          "Interactive shell runner keeps the whole epilogue on the heredoc command line so the shell reads a single command");
    var interactiveMonitor = new InteractiveShellPayloadMonitor(interactivePayload);
    var hiddenOutput = interactiveMonitor.Append(Encoding.UTF8.GetBytes("echoed command\n__JRM_READY_SMOKE"));
    hiddenOutput = hiddenOutput.Concat(interactiveMonitor.Append(Encoding.UTF8.GetBytes("TOKEN__\nechoed payload\n"))).ToArray();
    await interactiveMonitor.WaitForReadyAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);
    var visibleOutput = interactiveMonitor.Append(Encoding.UTF8.GetBytes("__JRM_BEGIN_SMOKETOKEN__\nscript output"));
    visibleOutput = visibleOutput.Concat(interactiveMonitor.Append(Encoding.UTF8.GetBytes("\n__JRM_EXIT_SMOKETOKEN__:105\n[root@gz-rocky ~]# "))).ToArray();
    var parsedInteractiveResult = await interactiveMonitor.WaitForExitAsync(CancellationToken.None);
    Check(parsedInteractiveResult.ExitCode == 105
          && parsedInteractiveResult.Output.Contains("script output", StringComparison.Ordinal)
          && Encoding.UTF8.GetString(hiddenOutput).Length == 0
          && Encoding.UTF8.GetString(visibleOutput).Contains("script output", StringComparison.Ordinal)
          && !Encoding.UTF8.GetString(visibleOutput).Contains("[root@gz-rocky ~]# ", StringComparison.Ordinal)
          && !Encoding.UTF8.GetString(visibleOutput).Contains(interactivePayload.ExitMarkerPrefix, StringComparison.Ordinal),
          "Interactive shell monitor hides injection echo, parses exit markers, and hides the stale prompt after exit");
    var partialExitMonitor = new InteractiveShellPayloadMonitor(interactivePayload);
    partialExitMonitor.Append(Encoding.UTF8.GetBytes("__JRM_READY_SMOKETOKEN__\n__JRM_BEGIN_SMOKETOKEN__\n"));
    await partialExitMonitor.WaitForReadyAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);
    var partialExitVisible = partialExitMonitor.Append(Encoding.UTF8.GetBytes("partial output\n__JRM_EXIT_SMOKETOKEN__:0"));
    var pendingExit = partialExitMonitor.WaitForExitAsync(CancellationToken.None);
    var exitWasPendingBeforeMarkerLineEnd = !pendingExit.IsCompleted;
    var finalExitVisible = partialExitMonitor.Append(Encoding.UTF8.GetBytes("\n[root@gz-rocky ~]# "));
    var finalExit = await pendingExit;
    Check(exitWasPendingBeforeMarkerLineEnd
          && finalExit.ExitCode == 0
          && Encoding.UTF8.GetString(partialExitVisible).Contains("partial output", StringComparison.Ordinal)
          && !Encoding.UTF8.GetString(partialExitVisible).Contains(interactivePayload.ExitMarkerPrefix, StringComparison.Ordinal)
          && Encoding.UTF8.GetString(finalExitVisible).Length == 0,
          "Interactive shell monitor waits for a full exit marker line before completing and hides the following stale prompt");

    var interruptedPayload = InteractiveShellPayloadRunner.Build("sleep 30", "INTERRUPTTOKEN");
    var interruptedMonitor = new InteractiveShellPayloadMonitor(interruptedPayload);
    interruptedMonitor.Append(Encoding.UTF8.GetBytes(
        interruptedPayload.ReadyMarker + "\n" + interruptedPayload.BeginMarker + "\n"));
    var interruptedWait = interruptedMonitor.WaitForExitAsync(CancellationToken.None);
    interruptedMonitor.Fail(new OperationCanceledException("manual recovery"));
    var manualInterruptReleasedWait = false;
    try
    {
        await interruptedWait;
    }
    catch (OperationCanceledException)
    {
        manualInterruptReleasedWait = true;
    }
    Check(manualInterruptReleasedWait,
          "Manual terminal recovery releases the active payload wait without a shell response");

    var repeatedCommandsCompleted = true;
    for (var iteration = 0; iteration < 100; iteration++)
    {
        var token = $"REPEAT{iteration}";
        var repeatedPayload = InteractiveShellPayloadRunner.Build("printf ok", token);
        var repeatedMonitor = new InteractiveShellPayloadMonitor(repeatedPayload);
        try
        {
            var repeatedResult = await InteractiveShellPayloadRunner.RunAsync(
                repeatedPayload,
                repeatedMonitor,
                text =>
                {
                    var response = text == repeatedPayload.PrepareCommand
                        ? repeatedPayload.ReadyMarker + "\n"
                        : repeatedPayload.BeginMarker + "\nok\n" + repeatedPayload.ExitMarkerPrefix + "0\n";
                    repeatedMonitor.Append(Encoding.UTF8.GetBytes(response));
                },
                CancellationToken.None);
            repeatedCommandsCompleted &= repeatedResult.ExitCode == 0;
        }
        catch
        {
            repeatedCommandsCompleted = false;
            break;
        }
    }

    Check(repeatedCommandsCompleted,
          "Interactive shell runner remains usable across frequent commands without an automatic command-completion timeout");
    var terminalPublicKeyPayload = PublicKeyInstaller.BuildTerminalPayload("ssh-ed25519 AAAATEST test");
    Check(terminalPublicKeyPayload.Contains(PublicKeyInstaller.TerminalAlreadyPresentLine)
          && terminalPublicKeyPayload.Contains(PublicKeyInstaller.TerminalAddedLine)
          && !terminalPublicKeyPayload.Contains("__JRM_KEY_PRESENT__"),
          "Terminal public key payload prints user-facing status lines");

    var zmodemDetector = new ZmodemTriggerDetector();
    _ = zmodemDetector.Append(Encoding.ASCII.GetBytes("prompt **"), out var zmodemPromptBytes);
    var zmodemDetected = zmodemDetector.Append(
        new byte[] { 0x18, (byte)'B', (byte)'0', (byte)'0', (byte)'0', (byte)'0' },
        out var zmodemDisplayBytes);
    var zmodemVisiblePrefix = zmodemPromptBytes.Concat(zmodemDetected?.DisplayBytes ?? []).ToArray();
    Check(zmodemDetected is { Direction: ZmodemTransferDirection.Download }
          && Encoding.ASCII.GetString(zmodemVisiblePrefix).EndsWith("prompt ", StringComparison.Ordinal)
          && zmodemDetected.ProtocolBytes.Length >= 6
          && zmodemDisplayBytes.Length == 0
          && zmodemDetected.ProtocolBytes[0] == (byte)'*',
          "ZMODEM detector recognizes a split sz download trigger and keeps protocol bytes out of the terminal");

    var zmodemInitWrites = new List<byte[]>();
    var zmodemInitQueue = new ZmodemByteQueue();
    using (var zmodemInitCancel = new CancellationTokenSource())
    {
        var zmodemInitReceiver = new ZmodemSession(
            (bytes, _) =>
            {
                zmodemInitWrites.Add(bytes);
                zmodemInitCancel.Cancel();
                return Task.CompletedTask;
            },
            zmodemInitQueue.ReadByteAsync);
        try
        {
            await zmodemInitReceiver.ReceiveAsync(root, zmodemInitCancel.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected after capturing the initial receiver header.
        }
    }
    var zmodemReceiverInitText = Encoding.ASCII.GetString(zmodemInitWrites.Single());
    Check(zmodemReceiverInitText.Contains($"**{(char)0x18}B0100000027fed4", StringComparison.Ordinal),
          "ZMODEM receiver advertises capability flags in ZF0 with a compatible CRC16");

    var zmodemSource = Path.Combine(root, "zmodem-source.bin");
    var zmodemDest = Path.Combine(root, "zmodem-dest");
    Directory.CreateDirectory(zmodemDest);
    var zmodemPayload = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
    zmodemPayload[17] = 0x18;
    zmodemPayload[18] = 0x11;
    zmodemPayload[19] = 0x13;
    File.WriteAllBytes(zmodemSource, zmodemPayload);

    var senderToReceiver = new ZmodemByteQueue();
    var receiverToSender = new ZmodemByteQueue();
    var zmodemSender = new ZmodemSession(
        (bytes, _) => { senderToReceiver.Append(bytes); return Task.CompletedTask; },
        receiverToSender.ReadByteAsync);
    var zmodemReceiver = new ZmodemSession(
        (bytes, _) => { receiverToSender.Append(bytes); return Task.CompletedTask; },
        senderToReceiver.ReadByteAsync);
    var zmodemSendTask = zmodemSender.SendAsync([zmodemSource], CancellationToken.None);
    var zmodemReceiveTask = zmodemReceiver.ReceiveAsync(zmodemDest, CancellationToken.None);
    await Task.WhenAll(zmodemSendTask, zmodemReceiveTask);
    var zmodemReceivedFile = zmodemReceiveTask.Result.Files.Single();
    Check(zmodemSendTask.Result.Files.Single() == zmodemSource
          && Path.GetFileName(zmodemReceivedFile) == Path.GetFileName(zmodemSource)
          && File.ReadAllBytes(zmodemReceivedFile).SequenceEqual(zmodemPayload),
          "ZMODEM sender and receiver transfer binary data through the SSH byte-stream adapter");

    var auditSuite = suites.Single(s => s.Name == "Audit");
    var sortedChoices = MainWindowViewModel.SortScriptSuiteChoices(
        suites,
        new[] { new ConnectionScriptBinding { Name = auditSuite.RelativePath } });
    Check(sortedChoices[0].Suite.RelativePath == auditSuite.RelativePath
          && sortedChoices[0].HasParameters
          && sortedChoices.Skip(1).All(c => !c.HasParameters),
          "Script suite picker sorts suites with saved parameters first");

    var staleBindingConnection = new Connection
    {
        Type = ConnectionType.Ssh,
        Name = "stale-script-binding",
        Host = "x",
        ScriptBindings =
        {
            new ConnectionScriptBinding { Name = auditSuite.RelativePath },
            new ConnectionScriptBinding { Name = "MissingSuite" },
            new ConnectionScriptBinding { Name = "" },
        },
    };
    var removedStaleBindings =
        MainWindowViewModel.PruneMissingScriptBindings(staleBindingConnection.ScriptBindings, suites);
    Check(removedStaleBindings == 2
          && staleBindingConnection.ScriptBindings.Count == 1
          && staleBindingConnection.ScriptBindings[0].Name == auditSuite.RelativePath,
          "Missing script suite bindings are pruned when scripts are rescanned");

    var legacyOptimizationBindingConnection = new Connection
    {
        Type = ConnectionType.Ssh,
        Name = "legacy-optimization-script-binding",
        Host = "x",
        ScriptBindings =
        {
            new ConnectionScriptBinding
            {
                Name = "Security",
                Params = { new ConnectionScriptParameterValue { Name = "ENABLE_FIREWALL", Value = "false" } },
            },
        },
    };
    var removedLegacyOptimizationBindings =
        MainWindowViewModel.PruneMissingScriptBindings(
            legacyOptimizationBindingConnection.ScriptBindings,
            new[] { runtimeServerOptimizationSuite });
    Check(removedLegacyOptimizationBindings == 0
          && legacyOptimizationBindingConnection.ScriptBindings.Count == 1
          && legacyOptimizationBindingConnection.ScriptBindings[0].Name == RemoteScriptSuiteNames.Optimization
          && legacyOptimizationBindingConnection.ScriptBindings[0].Params.Single().Value == "false",
          "Legacy Security script bindings are renamed to Optimization");

    var editorForDedupe = new ConnectionEditorViewModel();
    editorForDedupe.ScriptBindings.Add(ConnectionScriptBindingViewModel.FromModel(new ConnectionScriptBinding
    {
        Name = loadedSuite.RelativePath,
        Params = { new ConnectionScriptParameterValue { Name = "TARGET", Value = "old" } },
    }));
    editorForDedupe.ScriptBindings.Add(ConnectionScriptBindingViewModel.FromModel(new ConnectionScriptBinding
    {
        Name = loadedSuite.RelativePath,
        Params = { new ConnectionScriptParameterValue { Name = "TARGET", Value = "new" } },
    }));
    var dedupedConnection = new Connection { Type = ConnectionType.Ssh, Name = "deduped", Host = "x" };
    editorForDedupe.ApplyTo(dedupedConnection);
    Check(dedupedConnection.ScriptBindings.Count == 1
          && dedupedConnection.ScriptBindings[0].Params.Single().Value == "new",
          "Each connection keeps only one binding per script suite");

    var copiedScriptRoot = Path.Combine(root, "CopiedScripts");
    scriptStore.CopyTreeContents(scriptRoot, copiedScriptRoot);
    Check(Directory.Exists(copiedScriptRoot)
          && File.Exists(Path.Combine(copiedScriptRoot, "Deploy", RemoteScriptStore.ParameterFileName))
          && File.Exists(Path.Combine(copiedScriptRoot, "Deploy", "deploy.sh")),
          "RemoteScriptStore copies script suites during storage migration");

    // --- SetRoot ---
    var altRoot = Path.Combine(root, "Alt");
    var store2 = new ConnectionStore(root);
    store2.SetRoot(altRoot);
    Check(string.Equals(Path.GetFullPath(store2.RootPath), Path.GetFullPath(altRoot),
          StringComparison.OrdinalIgnoreCase) && Directory.Exists(altRoot),
          "SetRoot switches the store root and creates it");

    // --- Delete ---
    var delConn = new Connection { Type = ConnectionType.Ssh, Name = "to-delete", Host = "x" };
    var delPath = store.Save(delConn, store.RootPath);
    Check(File.Exists(delPath), "Created a file to delete");
    store.DeleteFile(delPath);
    Check(!File.Exists(delPath), "DeleteFile removes the file");

    var delFolder = store.CreateFolder(store.RootPath, "ToDelete");
    store.Save(new Connection { Name = "inside", Host = "y" }, delFolder);
    store.DeleteFolder(delFolder);
    Check(!Directory.Exists(delFolder), "DeleteFolder removes the folder recursively");

    Console.WriteLine();
    Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
}
finally
{
    try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
}

return failures;

sealed class FakeAgentChatSession(bool supportsSteering = false) : IAgentChatSession
{
    public string? SessionId => "fake";

    public bool SupportsSteering => supportsSteering;

    public int DisposeCount { get; private set; }

    public List<string> SteeredTexts { get; } = [];

    public event Action<string>? SessionInitialized { add { } remove { } }

    public event Action<string>? TextDelta { add { } remove { } }

    public event Action<string>? TextReplaced { add { } remove { } }

    public event Action<AgentTurnResult>? TurnCompleted { add { } remove { } }

    public event Action<string>? Errored { add { } remove { } }

    public event Action? Exited { add { } remove { } }

    public void Start()
    {
    }

    public Task SendAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SteerAsync(string text, CancellationToken cancellationToken = default)
    {
        SteeredTexts.Add(text);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}
