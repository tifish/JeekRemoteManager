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
              ApplicationMenuAction.ImportFromSecureCrt,
              ApplicationMenuAction.ImportFromXshell,
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

    var functionKeySequences = new[]
    {
        "\u001bOP", "\u001bOQ", "\u001bOR", "\u001bOS",
        "\u001b[15~", "\u001b[17~", "\u001b[18~", "\u001b[19~",
        "\u001b[20~", "\u001b[21~", "\u001b[23~", "\u001b[24~",
        "\u001b[25~", "\u001b[26~", "\u001b[28~", "\u001b[29~",
        "\u001b[31~", "\u001b[32~", "\u001b[33~", "\u001b[34~",
        "\u001b[42~", "\u001b[43~", "\u001b[44~", "\u001b[45~",
    };
    var allFunctionKeysEncoded = true;
    for (var i = 0; i < functionKeySequences.Length; i++)
    {
        var key = (Avalonia.Input.Key)((int)Avalonia.Input.Key.F1 + i);
        allFunctionKeysEncoded &= TerminalFunctionKeySequence.TryEncode(
            key,
            Avalonia.Input.KeyModifiers.None,
            out var number,
            out var sequence)
            && number == i + 1
            && sequence == functionKeySequences[i];
    }
    Check(allFunctionKeysEncoded, "Terminal input encodes every function key from F1 through F24");
    Check(TerminalFunctionKeySequence.TryEncode(
              Avalonia.Input.Key.F2,
              Avalonia.Input.KeyModifiers.Shift | Avalonia.Input.KeyModifiers.Control,
              out var modifiedFunctionKeyNumber,
              out var modifiedFunctionKeySequence)
          && modifiedFunctionKeyNumber == 2
          && modifiedFunctionKeySequence == "\u001b[1;6Q",
          "Terminal input preserves function-key modifier combinations");
    Check(!TerminalFunctionKeySequence.TryEncode(
              Avalonia.Input.Key.Enter,
              Avalonia.Input.KeyModifiers.None,
              out _,
              out _),
          "Function-key forwarding leaves non-function keys to their focused control");
    Check(!TerminalFunctionKeySequence.TryEncode(
              Avalonia.Input.Key.F4,
              Avalonia.Input.KeyModifiers.Alt,
              out _,
              out _),
          "Function-key forwarding preserves the host Alt+F4 shortcut");

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

    // --- SecureCRT / Xshell session import ---
    {
        static string EncryptSecureCrtPasswordV2(string plaintext)
        {
            var plainBytes = Encoding.UTF8.GetBytes(plaintext);
            var key = SHA256.HashData(Encoding.UTF8.GetBytes(""));
            var lvc = new byte[4 + plainBytes.Length + 32];
            BitConverter.GetBytes(plainBytes.Length).CopyTo(lvc, 0);
            plainBytes.CopyTo(lvc, 4);
            SHA256.HashData(plainBytes).CopyTo(lvc, 4 + plainBytes.Length);
            var padLen = 16 - (lvc.Length % 16);
            if (padLen < 8) padLen += 16;
            var padded = new byte[lvc.Length + padLen];
            lvc.CopyTo(padded, 0);
            RandomNumberGenerator.Fill(padded.AsSpan(lvc.Length));
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = new byte[16];
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            var cipher = aes.CreateEncryptor().TransformFinalBlock(padded, 0, padded.Length);
            return "02:" + Convert.ToHexString(cipher).ToLowerInvariant();
        }

        static byte[] Rc4(byte[] key, byte[] data)
        {
            var s = new byte[256];
            for (var i = 0; i < 256; i++) s[i] = (byte)i;
            var j = 0;
            for (var i = 0; i < 256; i++)
            {
                j = (j + s[i] + key[i % key.Length]) & 0xFF;
                (s[i], s[j]) = (s[j], s[i]);
            }
            var output = new byte[data.Length];
            var x = 0; var y = 0;
            for (var k = 0; k < data.Length; k++)
            {
                x = (x + 1) & 0xFF;
                y = (y + s[x]) & 0xFF;
                (s[x], s[y]) = (s[y], s[x]);
                output[k] = (byte)(data[k] ^ s[(s[x] + s[y]) & 0xFF]);
            }
            return output;
        }

        static string EncryptXshellPasswordLegacy(string plaintext)
        {
            var key = MD5.HashData(Encoding.ASCII.GetBytes("!X@s#h$e%l^l&"));
            return Convert.ToBase64String(Rc4(key, Encoding.UTF8.GetBytes(plaintext)));
        }

        var scrtRoot = Path.Combine(root, "securecrt_sessions");
        var scrtGroup = Path.Combine(scrtRoot, "Prod");
        Directory.CreateDirectory(scrtGroup);
        File.WriteAllText(Path.Combine(scrtRoot, "Default.ini"), "S:\"Protocol Name\"=SSH2\r\n");
        File.WriteAllText(Path.Combine(scrtRoot, "__FolderData__.ini"), "D:\"Is Session\"=00000000\r\n");
        File.WriteAllText(Path.Combine(scrtRoot, "skip-telnet.ini"),
            "S:\"Protocol Name\"=Telnet\r\nS:\"Hostname\"=old.example\r\n");
        File.WriteAllText(Path.Combine(scrtGroup, "web-1.ini"),
            "S:\"Protocol Name\"=SSH2\r\n" +
            "S:\"Hostname\"=web1.example.com\r\n" +
            "S:\"Username\"=deploy\r\n" +
            "D:\"[SSH2] Port\"=00001f90\r\n" + // 8080
            "S:\"Emulation\"=Xterm\r\n" +
            "S:\"Password V2\"=" + EncryptSecureCrtPasswordV2("scrt-secret") + "\r\n");
        File.WriteAllText(Path.Combine(scrtRoot, "no-host.ini"),
            "S:\"Protocol Name\"=SSH2\r\nS:\"Username\"=root\r\n");

        var scrtImportStore = new ConnectionStore(Path.Combine(root, "import_scrt"));
        var scrtResult = new SecureCrtImporter(scrtImportStore).Import(scrtRoot);
        Check(scrtResult.Imported == 1 && scrtResult.Folders == 1 && scrtResult.Skipped >= 3,
              "SecureCRT import keeps SSH sessions, folders, and skips noise");
        Check(scrtResult.PasswordsImported == 1,
              "SecureCRT import recovers Password V2 with empty config passphrase");
        var scrtConn = scrtImportStore.Load(scrtImportStore.GetConnectionFiles(
            scrtImportStore.GetSubFolders(scrtImportStore.RootPath)[0])[0]);
        Check(scrtConn.Name == "web-1"
              && scrtConn.Host == "web1.example.com"
              && scrtConn.Username == "deploy"
              && scrtConn.Port == 8080
              && scrtConn.TerminalType == "xterm-256color"
              && PasswordProtector.Decrypt(scrtConn.EncryptedPassword) == "scrt-secret",
              "SecureCRT import maps host, user, hex port, emulation, and password");

        var xshRoot = Path.Combine(root, "xshell_sessions");
        var xshGroup = Path.Combine(xshRoot, "Labs");
        Directory.CreateDirectory(xshGroup);
        File.WriteAllText(Path.Combine(xshGroup, "lab-box.xsh"),
            "[SessionInfo]\r\nVersion=4.0\r\n" +
            "[CONNECTION]\r\nHost=10.0.0.9\r\nPort=2222\r\nProtocol=SSH\r\nDescription=lab note\r\n" +
            "[CONNECTION:AUTHENTICATION]\r\nUserName=admin\r\nPassword=" +
            EncryptXshellPasswordLegacy("xsh-secret") + "\r\n");
        File.WriteAllText(Path.Combine(xshRoot, "serial.xsh"),
            "[SessionInfo]\r\nVersion=8.1\r\n[CONNECTION]\r\nHost=\r\nPort=0\r\nProtocol=SERIAL\r\n" +
            "[CONNECTION:AUTHENTICATION]\r\nUserName=\r\n");
        File.WriteAllText(Path.Combine(xshRoot, "folder.cnf"), "not a session\r\n");

        var xshImportStore = new ConnectionStore(Path.Combine(root, "import_xsh"));
        var xshResult = new XshellImporter(xshImportStore).Import(xshRoot);
        Check(xshResult.Imported == 1 && xshResult.Folders == 1 && xshResult.Skipped >= 1,
              "Xshell import keeps SSH sessions and skips non-SSH files");
        Check(xshResult.PasswordsImported == 1,
              "Xshell import recovers legacy fixed-key passwords");
        var xshConn = xshImportStore.Load(xshImportStore.GetConnectionFiles(
            xshImportStore.GetSubFolders(xshImportStore.RootPath)[0])[0]);
        Check(xshConn.Name == "lab-box"
              && xshConn.Host == "10.0.0.9"
              && xshConn.Port == 2222
              && xshConn.Username == "admin"
              && xshConn.Notes == "lab note"
              && PasswordProtector.Decrypt(xshConn.EncryptedPassword) == "xsh-secret",
              "Xshell import maps host, port, user, notes, and password");
    }

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
    Check(TerminalView.ShouldHideSshTerminal(
              aiPanelVisible: true,
              hideSshTerminalRequested: true,
              loginManualInputPending: false),
          "Hide SSH terminal preference applies when no login input is pending");
    Check(!TerminalView.ShouldHideSshTerminal(
              aiPanelVisible: true,
              hideSshTerminalRequested: true,
              loginManualInputPending: true),
          "Pending login input temporarily shows the SSH terminal");
    Check(!TerminalView.ShouldHideSshTerminal(
              aiPanelVisible: true,
              hideSshTerminalRequested: false,
              loginManualInputPending: false),
          "Disabled preference keeps the SSH terminal visible");
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

    // --- Streaming UTF-8 (Chinese split across packets must not become U+FFFD) ---
    var chineseUtf8 = Encoding.UTF8.GetBytes("中文测试");
    Check(chineseUtf8.Length >= 6, "Chinese UTF-8 sample is multi-byte");
    // Library path: per-packet GetString turns incomplete sequences into replacement chars.
    var brokenModel = new TerminalControlModel(new TerminalOptions { Cols = 40, Rows = 5, Scrollback = 10 });
    brokenModel.Feed(chineseUtf8.AsSpan(0, 2).ToArray(), 2);
    brokenModel.Feed(chineseUtf8.AsSpan(2).ToArray(), chineseUtf8.Length - 2);
    var brokenText = brokenModel.Terminal.Buffer.GetLine(0)?.TranslateToString(true) ?? "";
    Check(brokenText.Contains('\uFFFD'),
          "TerminalControlModel.Feed(byte[]) produces U+FFFD when UTF-8 is split mid-character");

    var streamDecoder = new Utf8StreamDecoder();
    var streamed = streamDecoder.Decode(chineseUtf8.AsSpan(0, 2))
                   + streamDecoder.Decode(chineseUtf8.AsSpan(2));
    Check(streamed == "中文测试" && !streamed.Contains('\uFFFD'),
          "Utf8StreamDecoder reassembles Chinese split across packets");

    var fixedModel = new TerminalControlModel(new TerminalOptions { Cols = 40, Rows = 5, Scrollback = 10 });
    // One byte at a time through the decoder (worst-case packet split).
    var perByteDecoder = new Utf8StreamDecoder();
    var rebuilt = new StringBuilder();
    foreach (var b in chineseUtf8)
        rebuilt.Append(perByteDecoder.Decode([b]));
    fixedModel.Feed(rebuilt.ToString());
    var fixedText = fixedModel.Terminal.Buffer.GetLine(0)?.TranslateToString(true) ?? "";
    Check(fixedText.Contains("中文测试", StringComparison.Ordinal) && !fixedText.Contains('\uFFFD'),
          "Terminal receives intact Chinese when fed via Utf8StreamDecoder + Feed(string)");

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

    var sessionOutput = new TerminalSessionOutputBuffer();
    Check(sessionOutput.Append("\u001b[10;2H"u8, generation: 7)
          && !sessionOutput.Append("working"u8, generation: 7)
          && !sessionOutput.Append("\u001b[12;4H"u8, generation: 6)
          && sessionOutput.PendingPacketCount == 3,
          "AI terminal output schedules one UI drain for a burst of ConPTY packets");
    Check(Encoding.UTF8.GetString(sessionOutput.Drain(generation: 7)) == "\u001b[10;2Hworking"
          && sessionOutput.PendingPacketCount == 0
          && sessionOutput.Append("done"u8, generation: 7),
          "AI terminal output coalesces current-session packets and drops stale output");
    sessionOutput.Clear();

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

    var resetScrollbackModel = FeedScrollbackPrompt(10);
    resetScrollbackModel.ScrollLines(-5);
    Check(resetScrollbackModel.MaxScrollback > 0
          && !resetScrollbackModel.Terminal.Buffer.IsAtBottom,
          "Terminal scrollback reset test starts with history above the viewport");
    TerminalScrollbackReset.Reset(resetScrollbackModel);
    Check(resetScrollbackModel.MaxScrollback == 0
          && resetScrollbackModel.ScrollOffset == 0
          && resetScrollbackModel.Terminal.Buffer.Lines.Length == resetScrollbackModel.Terminal.Rows
          && resetScrollbackModel.Terminal.Buffer.IsAtBottom
          && !resetScrollbackModel.CanScroll,
          "Terminal scrollback reset restores one initial-size viewport");

    // --- AI CLI workspace + dim color filter ---
    var connectionsRoot = Path.Combine(Path.GetTempPath(), "jrm-agent-ws-smoke", "Connections");
    Directory.CreateDirectory(Path.Combine(connectionsRoot, "vps"));
    var bwgFile = Path.Combine(connectionsRoot, "vps", "bwg.json");
    File.WriteAllText(bwgFile, "{}");
    var relative = AgentCliWorkspace.ResolveRelativePath(connectionsRoot, bwgFile, new Connection { Name = "bwg" });
    var localRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JeekRemoteManager",
        "AgentWorkspaces");
    Check(string.Equals(
              Path.GetFullPath(AgentCliWorkspace.RootPath),
              Path.GetFullPath(localRoot),
              StringComparison.OrdinalIgnoreCase),
          "AI CLI workspace root is %LOCALAPPDATA%\\JeekRemoteManager\\AgentWorkspaces");
    const string smokeMcpUrl = "http://127.0.0.1:1234/agent/smoketest/mcp";
    var workspace = AgentCliWorkspace.Ensure(
        connectionsRoot,
        bwgFile,
        new Connection
        {
            Name = "bwg",
            Type = ConnectionType.Ssh,
            Host = "1.2.3.4",
            Port = 22,
            Username = "root",
            Notes = "edge VPS",
        },
        smokeMcpUrl);
    var agentsMd = File.ReadAllText(Path.Combine(workspace, "AGENTS.md"));
    var claudeMd = File.ReadAllText(Path.Combine(workspace, "CLAUDE.md")).Trim();
    var mcpJson = File.Exists(Path.Combine(workspace, ".mcp.json"))
        ? File.ReadAllText(Path.Combine(workspace, ".mcp.json"))
        : "";
    var codexToml = File.Exists(Path.Combine(workspace, ".codex", "config.toml"))
        ? File.ReadAllText(Path.Combine(workspace, ".codex", "config.toml"))
        : "";
    var grokToml = File.Exists(Path.Combine(workspace, ".grok", "config.toml"))
        ? File.ReadAllText(Path.Combine(workspace, ".grok", "config.toml"))
        : "";
    Check(relative.Replace('\\', '/') == "vps/bwg"
          && workspace.Replace('\\', '/').EndsWith("AgentWorkspaces/vps/bwg", StringComparison.OrdinalIgnoreCase)
          && workspace.StartsWith(Path.GetFullPath(localRoot), StringComparison.OrdinalIgnoreCase)
          && File.Exists(Path.Combine(workspace, "CLAUDE.md"))
          && File.Exists(Path.Combine(workspace, "AGENTS.md"))
          && claudeMd == "@AGENTS.md"
          && !claudeMd.Contains("jrm-remote", StringComparison.Ordinal)
          && agentsMd.Contains("jrm-remote", StringComparison.Ordinal)
          && agentsMd.Contains("edge VPS", StringComparison.Ordinal)
          && agentsMd.Contains(smokeMcpUrl, StringComparison.Ordinal)
          && agentsMd.Contains(".mcp.json", StringComparison.Ordinal)
          && !agentsMd.Contains("--append-system-prompt", StringComparison.Ordinal)
          && mcpJson.Contains(smokeMcpUrl, StringComparison.Ordinal)
          && codexToml.Contains(smokeMcpUrl, StringComparison.Ordinal)
          && grokToml.Contains(smokeMcpUrl, StringComparison.Ordinal)
          && grokToml.Contains("transport = \"http\"", StringComparison.Ordinal),
          "AI workspace writes AGENTS.md (full) + CLAUDE.md include + project MCP configs");

    var claudeAutoArgs = AgentCliCatalog.BuildInteractiveArguments(AgentCliKind.Claude, autoRun: true);
    var claudePromptArgs = AgentCliCatalog.BuildInteractiveArguments(AgentCliKind.Claude, autoRun: false);
    var codexAutoArgs = AgentCliCatalog.BuildInteractiveArguments(AgentCliKind.Codex, autoRun: true);
    var codexPromptArgs = AgentCliCatalog.BuildInteractiveArguments(AgentCliKind.Codex, autoRun: false);
    var grokAutoArgs = AgentCliCatalog.BuildInteractiveArguments(AgentCliKind.Grok, autoRun: true);
    Check(claudeAutoArgs.Contains("--allowedTools")
          && !claudeAutoArgs.Contains("--mcp-config")
          && !claudeAutoArgs.Contains("--append-system-prompt")
          && !claudeAutoArgs.Contains("--strict-mcp-config")
          && !claudePromptArgs.Contains("--allowedTools")
          && claudeAutoArgs.Any(a => a.Contains("mcp__jrm-remote__terminal_status", StringComparison.Ordinal)
                                     && a.Contains("mcp__jrm-remote__terminal_run", StringComparison.Ordinal))
          && codexAutoArgs.Contains("--no-alt-screen")
          && !codexAutoArgs.Any(a => a.Contains("mcp_servers.jrm-remote.url=", StringComparison.Ordinal))
          && codexAutoArgs.Any(a => a.Contains("terminal_run.approval_mode=\"approve\"", StringComparison.Ordinal))
          && codexAutoArgs.Any(a => a.Contains("terminal_status.approval_mode=\"approve\"", StringComparison.Ordinal))
          && codexPromptArgs.Any(a => a.Contains("terminal_run.approval_mode=\"prompt\"", StringComparison.Ordinal))
          && grokAutoArgs.Contains("MCPTool(jrm-remote__terminal_run)")
          && grokAutoArgs.Contains("MCPTool(jrm-remote__terminal_status)")
          && grokAutoArgs.Contains("MCPTool(jrm-remote__terminal_run_danger)"),
          "AI CLI args are runtime-only; MCP URL/context live in workspace; auto-run allows expanded jrm-remote tools");

    await using (var safetyServer = new AgentRemoteMcpServer(new SmokeAgentRemoteTools()))
    {
        Check(safetyServer.RequiresDangerConfirmation("rm -rf /tmp/jrm-smoke", false)
              && safetyServer.RequiresDangerConfirmation("echo safe", true),
              "AI dangerous-command confirmation remains enabled by default");
        safetyServer.AutoApproveDangerousCommands = true;
        Check(!safetyServer.RequiresDangerConfirmation("rm -rf /tmp/jrm-smoke", false)
              && !safetyServer.RequiresDangerConfirmation("echo safe", true),
              "AI auto-approve bypasses detector and agent-tagged dangerous-command confirmations");

        // tools/list must succeed even when terminal_run and terminal_run_danger share schema props.
        // (JsonNode "already has a parent" regressed Codex MCP startup.)
        safetyServer.Start();
        var toolsListOk = false;
        var toolsListNames = "";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var listBody = """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""";
            using var listResp = await http.PostAsync(
                safetyServer.EndpointUrl,
                new StringContent(listBody, Encoding.UTF8, "application/json"));
            var listJson = await listResp.Content.ReadAsStringAsync();
            var listNode = JsonNode.Parse(listJson) as JsonObject;
            var tools = listNode?["result"]?["tools"] as JsonArray;
            toolsListOk = listResp.IsSuccessStatusCode
                          && listNode?["error"] is null
                          && tools is { Count: > 0 }
                          && tools.Any(t => t?["name"]?.GetValue<string>() == "terminal_run")
                          && tools.Any(t => t?["name"]?.GetValue<string>() == "terminal_run_danger");
            toolsListNames = tools is null
                ? listJson
                : string.Join(',', tools.Select(t => t?["name"]?.GetValue<string>() ?? "?"));
        }
        catch (Exception ex)
        {
            toolsListNames = ex.Message;
        }

        Check(toolsListOk, $"Agent MCP tools/list returns shared-schema tools ({toolsListNames})");
    }

    var dimFilter = new TerminalDimColorFilter();
    var dimRewritten = Encoding.ASCII.GetString(dimFilter.Process(Encoding.ASCII.GetBytes("\u001b[2msecondary\u001b[22m")));
    var dimWithColor = Encoding.ASCII.GetString(dimFilter.Process(Encoding.ASCII.GetBytes("\u001b[0m\u001b[2;32mkeep green\u001b[0m")));
    // True-color bg uses 48;2;r;g;b — the '2' must not be stripped as dim.
    var trueColorBg = Encoding.ASCII.GetString(dimFilter.Process(Encoding.ASCII.GetBytes("\u001b[0m\u001b[48;2;40;40;40m\u001b[97mx\u001b[0m")));
    Check(dimRewritten.Contains("\u001b[38;2;168;168;168m", StringComparison.Ordinal)
          && dimRewritten.Contains("secondary", StringComparison.Ordinal)
          && dimRewritten.Contains("\u001b[22;39m", StringComparison.Ordinal)
          && dimWithColor.Contains("\u001b[32m", StringComparison.Ordinal)
          && !dimWithColor.Contains("\u001b[2;32m", StringComparison.Ordinal)
          && trueColorBg.Contains("\u001b[48;2;40;40;40m", StringComparison.Ordinal)
          && trueColorBg.Contains("\u001b[97m", StringComparison.Ordinal),
          "AI CLI dim (SGR 2) becomes soft gray; true-color 38/48;2 sequences stay intact");

    Check(AgentTerminalAction.ForceInterrupt != AgentTerminalAction.Reconnect
          && new AgentFileTransfer(true, ["a.txt"], "/tmp") is { IsUpload: true },
          "AI remote tool types remain available for MCP transfers and recovery");

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
    var autoSaveC = new Connection
    {
        Type = ConnectionType.Ssh,
        Name = "autosave-c",
        Host = "c.example",
        Port = 22,
        Username = "root",
        EncryptedPassword = PasswordProtector.Encrypt("autosave-password"),
    };
    var autoSaveAPath = vmStore.Save(autoSaveA, vmStore.RootPath);
    var autoSaveBPath = vmStore.Save(autoSaveB, vmStore.RootPath);
    var autoSaveCPath = vmStore.Save(autoSaveC, vmStore.RootPath);
    var autoSaveAPasswordBeforeEdit = autoSaveA.EncryptedPassword;

    vmSettings.Settings.RecentConnectionPaths.Clear();
    vmSettings.Settings.RecentConnectionPaths.Add(autoSaveAPath);
    vmSettings.Settings.RecentConnectionPaths.Add(autoSaveBPath);
    vmSettings.Settings.RecentExpanded = true;
    var recentVm = new MainWindowViewModel(vmStore, new ConnectionLauncher(), vmSettings);
    var recentChildrenBefore = recentVm.Nodes.Single(n => n.IsRecent).Children
        .Select(n => n.FullPath)
        .ToList();
    Check(recentChildrenBefore.SequenceEqual(new[] { autoSaveAPath, autoSaveBPath }),
          "Recent group initially lists paths most-recent first");
    var recentNode = recentVm.Nodes.Single(n => n.IsRecent).Children.Single(n => n.FullPath == autoSaveBPath);
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
    Check(recentLaunchPath == autoSaveBPath, "Recent context-menu Connect launches the selected connection");
    Check(recentVm.SelectedNode is null, "Recent context-menu Connect clears the stale shadow selection");
    Check(vmSettings.Settings.RecentConnectionPaths.SequenceEqual(new[] { autoSaveBPath, autoSaveAPath }),
          "Recording a recent connection updates the settings order immediately");
    var recentChildrenAfterReorder = recentVm.Nodes.Single(n => n.IsRecent).Children
        .Select(n => n.FullPath)
        .ToList();
    Check(recentChildrenAfterReorder.SequenceEqual(recentChildrenBefore),
          "Recent tree UI does not jump immediately when reordering an already-listed connection");
    recentVm.FlushPendingRecentRebuild();
    var recentChildrenAfterFlush = recentVm.Nodes.Single(n => n.IsRecent).Children
        .Select(n => n.FullPath)
        .ToList();
    Check(recentChildrenAfterFlush.SequenceEqual(new[] { autoSaveBPath, autoSaveAPath }),
          "Flushing the delayed Recent rebuild applies the updated order");

    // A brand-new recent entry is also delayed, then appears after flush.
    var nodeC = recentVm.Nodes.Single(n => n.Name == "autosave-c");
    recentLaunchPath = null;
    recentVm.SelectedNode = nodeC;
    await recentVm.ConnectCommand.ExecuteAsync(null);
    Check(recentLaunchPath == autoSaveCPath, "Connecting a non-recent connection launches it");
    Check(vmSettings.Settings.RecentConnectionPaths[0] == autoSaveCPath,
          "A newly recorded connection is stored first in settings");
    var recentChildrenBeforeNewFlush = recentVm.Nodes.Single(n => n.IsRecent).Children
        .Select(n => n.FullPath)
        .ToList();
    Check(recentChildrenBeforeNewFlush.SequenceEqual(new[] { autoSaveBPath, autoSaveAPath }),
          "Adding a new recent entry does not rebuild the Recent tree immediately");
    recentVm.FlushPendingRecentRebuild();
    var recentChildrenAfterAdd = recentVm.Nodes.Single(n => n.IsRecent).Children
        .Select(n => n.FullPath)
        .ToList();
    Check(recentChildrenAfterAdd.SequenceEqual(new[] { autoSaveCPath, autoSaveBPath, autoSaveAPath }),
          "Flushing after a new recent entry rebuilds the tree with the updated order");

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
        AiAutoRun = false,
        AiAutoApproveDangerousCommands = true,
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
        AiAutoRun = settingsWithRecent.AiAutoRun,
        AiAutoApproveDangerousCommands = settingsWithRecent.AiAutoApproveDangerousCommands,
    });
    Check(roamingSettingsJson.Contains(nameof(RoamingAppSettings.Language))
          && roamingSettingsJson.Contains(nameof(RoamingAppSettings.Theme))
          && roamingSettingsJson.Contains(nameof(RoamingAppSettings.AiAutoRun))
          && roamingSettingsJson.Contains(nameof(RoamingAppSettings.AiAutoApproveDangerousCommands))
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
    tempSettings.Settings.AiAutoRun = false;
    tempSettings.Settings.AiAutoApproveDangerousCommands = true;
    Check(!File.Exists(tempRoamingSettingsPath), "Settings changes stay in memory before flush");
    Check(tempSettings.SaveIfChanged()
          && File.Exists(tempRoamingSettingsPath)
          && !File.Exists(tempMachineSettingsPath),
          "Changed roaming settings flush writes roaming settings.json");
    var savedSettingsJson = File.ReadAllText(tempRoamingSettingsPath);
    Check(savedSettingsJson.Contains("\"Language\": \"zh\"")
          && savedSettingsJson.Contains("\"AiAutoRun\": false")
          && savedSettingsJson.Contains("\"AiAutoApproveDangerousCommands\": true"),
          "Changed roaming settings are serialized after flush");
    var reloadedAiSettings = new SettingsService(tempMachineSettingsPath, tempRoamingSettingsPath);
    Check(!reloadedAiSettings.Settings.AiAutoRun
          && reloadedAiSettings.Settings.AiAutoApproveDangerousCommands,
          "AI command safety options round-trip through roaming settings");
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
          && interactivePayload.PrepareCommand.Contains("PAGER=cat", StringComparison.Ordinal)
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

    var cleanShellOutput = typeof(TerminalView)
        .GetMethod("CleanShellOutput", BindingFlags.Static | BindingFlags.NonPublic)!;
    var rawCapturedShell =
        "stty -echo 2>/dev/null || true; printf '__JRM_READY_TOK__'\n" +
        "__JRM_READY_TOK__\nroot@host:~# \n" +
        "__JRM_BEGIN_TOK__\n" +
        "=== OS / Kernel ===\nLinux test 6.1\n" +
        "__JRM_EXIT_TOK__:0\nroot@host:~# \n";
    var cleanedShell = (string)cleanShellOutput.Invoke(null, [rawCapturedShell])!;
    Check(cleanedShell == "=== OS / Kernel ===\nLinux test 6.1"
          && !cleanedShell.Contains("__JRM_", StringComparison.Ordinal)
          && !cleanedShell.Contains("stty -echo", StringComparison.Ordinal),
          "AI command capture keeps only the script body between BEGIN and EXIT markers");


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

sealed class SmokeAgentRemoteTools : IAgentRemoteTools
{
    public string ConnectionLabel => "smoke";
    public bool IsWsl => false;
    public Task<string> RunCommandAsync(
        string command,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(timeoutSeconds is { } t ? $"{command}|timeout={t}" : command);
    public Task<string> TransferFilesAsync(AgentFileTransfer transfer, CancellationToken cancellationToken = default) =>
        Task.FromResult("ok");
    public Task<string> RunTerminalActionAsync(AgentTerminalAction action, CancellationToken cancellationToken = default) =>
        Task.FromResult("ok");
    public Task<bool> ConfirmDangerousCommandAsync(string command, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
    public Task<string> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult("connected=true\ncommand_lock_available=true");
    public Task<string> GetConnectionInfoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult("type=SSH\ntarget=smoke");
    public Task<string> GetScrollbackAsync(int lines, CancellationToken cancellationToken = default) =>
        Task.FromResult($"[scrollback lines=0 requested={lines}]");
    public Task<string> SendKeysAsync(string text, CancellationToken cancellationToken = default) =>
        Task.FromResult($"[keys sent bytes={text.Length}]");
    public Task<string> AskUserAsync(
        string prompt,
        IReadOnlyList<string>? options,
        CancellationToken cancellationToken = default) =>
        Task.FromResult("[answer] smoke");
    public Task<string> GetMonitorSnapshotAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult("[monitor unavailable]");
}
