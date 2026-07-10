using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Reflection;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;
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
    aiVm.InputText = "draft";
    Check(aiVm.NewConversationCommand.CanExecute(null),
          "AI new conversation command is available while idle");
    aiVm.NewConversationCommand.Execute(null);
    Check(aiVm.Messages.Count == 0 && aiVm.InputText == "draft" && aiVm.StatusText == "",
          "AI new conversation clears the transcript and keeps the draft");
    Check(activeAiSession.DisposeCount == 1,
          "AI new conversation disposes the active session");

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

    var thinkingMessage = new ChatMessageViewModel(ChatRole.Assistant, "")
    {
        IsThinking = true,
        ThinkingText = "Thinking...",
    };
    Check(thinkingMessage.ShowsThinking && !thinkingMessage.ShowsAssistantMarkdown,
          "AI assistant empty response shows the thinking placeholder");
    thinkingMessage.Text = "answer";
    Check(!thinkingMessage.ShowsThinking && thinkingMessage.ShowsAssistantMarkdown,
          "AI thinking placeholder hides after response text appears");

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
    Check(store.GetConnectionFiles(folder).Count == 2, "Folder lists exactly two connection files");

    // Plaintext password must never appear on disk.
    var sshOnDisk = File.ReadAllText(sshPath);
    Check(!sshOnDisk.Contains(secret), "Plaintext password is not present in the saved file");

    // --- Load round-trip ---
    var loaded = store.Load(sshPath);
    Check(loaded.Type == ConnectionType.Ssh && loaded.Host == "10.0.0.1"
          && loaded.Name == "web01", "Loaded connection matches what was saved");
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

    // --- Rename via Save (file follows the name) ---
    loaded.Name = "web01-renamed";
    var renamedPath = store.Save(loaded, folder, sshPath);
    Check(!File.Exists(sshPath) && File.Exists(renamedPath), "Renaming moves the file, old one removed");

    // --- Name collision disambiguation ---
    var dup = new Connection { Type = ConnectionType.Ssh, Name = "win-box", Host = "x" };
    var dupPath = store.Save(dup, folder);
    Check(dupPath != rdpPath && File.Exists(dupPath), "Colliding name gets a unique file");

    // --- Copy / move files ---
    var sub = store.CreateFolder(store.RootPath, "Sub");

    var copied = store.CopyFileInto(rdpPath, sub);
    Check(File.Exists(copied) && File.Exists(rdpPath) && Path.GetDirectoryName(copied) == sub,
          "CopyFileInto copies and keeps the original");

    var movedFile = store.MoveFileInto(dupPath, sub);
    Check(File.Exists(movedFile) && !File.Exists(dupPath), "MoveFileInto moves the file");

    var noop = store.MoveFileInto(movedFile, sub);
    Check(noop == movedFile && File.Exists(movedFile), "MoveFileInto into same folder is a no-op");

    // --- Copy / move folders ---
    var copiedFolder = store.CopyFolderInto(folder, store.RootPath);
    Check(Directory.Exists(copiedFolder) && copiedFolder != folder
          && store.GetConnectionFiles(copiedFolder).Count == store.GetConnectionFiles(folder).Count,
          "CopyFolderInto recursively copies with a unique name");

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
        store.CopyTreeContents(store.RootPath, migrateDest);
        Check(Directory.Exists(migrateDest) && Directory.GetDirectories(migrateDest).Length >= 1,
              "CopyTreeContents migrates the tree to a separate root");

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
    };
    var machineSettingsJson = JsonSerializer.Serialize(new MachineAppSettings
    {
        RecentConnectionPaths = settingsWithRecent.RecentConnectionPaths,
        LastSelectedConnectionPath = settingsWithRecent.LastSelectedConnectionPath,
        RecentExpanded = settingsWithRecent.RecentExpanded,
        MainWindowWidth = settingsWithRecent.MainWindowWidth,
        MainWindowHeight = settingsWithRecent.MainWindowHeight,
    });
    Check(machineSettingsJson.Contains(nameof(MachineAppSettings.RecentConnectionPaths))
          && machineSettingsJson.Contains(nameof(MachineAppSettings.LastSelectedConnectionPath))
          && machineSettingsJson.Contains(nameof(MachineAppSettings.RecentExpanded))
          && machineSettingsJson.Contains(nameof(MachineAppSettings.MainWindowWidth))
          && machineSettingsJson.Contains(nameof(MachineAppSettings.MainWindowHeight))
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
    Check(runtimeSingBoxClientSuite.Parameters.Count == 10
          && Enumerable.Range(1, 6).All(i =>
              runtimeSingBoxClientSuite.Parameters.Single(p => p.Name == $"SERVER_LINK_{i}").Type == RemoteScriptParameterType.String)
          && runtimeSingBoxClientSuite.Parameters.Single(p => p.Name == "LISTEN_PORT").Type == RemoteScriptParameterType.Number
          && runtimeSingBoxClientSuite.Parameters.Single(p => p.Name == "LISTEN_PORT").DefaultValue == "1080"
          && runtimeSingBoxClientSuite.Parameters.Single(p => p.Name == "ALLOW_EXTERNAL").Type == RemoteScriptParameterType.Bool
          && runtimeSingBoxClientSuite.Parameters.Single(p => p.Name == "ALLOW_EXTERNAL").DefaultValue == "false"
          && runtimeSingBoxClientSuite.Parameters.Single(p => p.Name == "ENABLE_TUN").Type == RemoteScriptParameterType.Bool
          && runtimeSingBoxClientSuite.Parameters.Single(p => p.Name == "ENABLE_TUN").DefaultValue == "false"
          && runtimeSingBoxClientSuite.Parameters.Single(p => p.Name == "UPDATE_SING_BOX").Type == RemoteScriptParameterType.Bool
          && runtimeSingBoxClientSuite.Parameters.Single(p => p.Name == "UPDATE_SING_BOX").DefaultValue == "true",
          "Bundled sing-box reality client script exposes six server links and update toggle parameters");
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
    Check(runtimeServerOptimizationSuite.Parameters.Count == 6
          && runtimeServerOptimizationSuite.Parameters.All(p => p.Type == RemoteScriptParameterType.Bool)
          && runtimeServerOptimizationSuite.Parameters.Single(p => p.Name == "ENABLE_FAIL2BAN").DefaultValue == "true"
          && runtimeServerOptimizationSuite.Parameters.Single(p => p.Name == "ENABLE_FIREWALL").DefaultValue == "true"
          && runtimeServerOptimizationSuite.Parameters.Single(p => p.Name == "ENABLE_AUTO_UPDATES").DefaultValue == "true"
          && runtimeServerOptimizationSuite.Parameters.Single(p => p.Name == "ENABLE_BBR").DefaultValue == "true"
          && runtimeServerOptimizationSuite.Parameters.Single(p => p.Name == "ENABLE_APT_AUTOREMOVE").DefaultValue == "false"
          && runtimeServerOptimizationSuite.Parameters.Single(p => p.Name == "ENABLE_COMMAND_COLORS").DefaultValue == "true"
          && runtimeServerOptimizationSuite.Parameters.Any(p => p.Name == "ENABLE_FAIL2BAN")
          && runtimeServerOptimizationSuite.Parameters.Any(p => p.Name == "ENABLE_FIREWALL")
          && runtimeServerOptimizationSuite.Parameters.Any(p => p.Name == "ENABLE_AUTO_UPDATES")
          && runtimeServerOptimizationSuite.Parameters.Any(p => p.Name == "ENABLE_BBR")
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
          && runtimeServerOptimizationScript.Contains("is_enabled \"$ENABLE_APT_AUTOREMOVE\"")
          && runtimeServerOptimizationScript.Contains("is_enabled \"$ENABLE_COMMAND_COLORS\""),
          "Bundled server optimization script gates each feature behind a boolean parameter");
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
    Check(store.Load(oldConnectionPath).ScriptBindings.Count == 0,
          "Old connection JSON without ScriptBindings loads with an empty binding list");

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

sealed class FakeAgentChatSession : IAgentChatSession
{
    public string? SessionId => "fake";

    public int DisposeCount { get; private set; }

    public event Action<string>? SessionInitialized { add { } remove { } }

    public event Action<string>? TextDelta { add { } remove { } }

    public event Action<AgentTurnResult>? TurnCompleted { add { } remove { } }

    public event Action<string>? Errored { add { } remove { } }

    public event Action? Exited { add { } remove { } }

    public void Start()
    {
    }

    public Task SendAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}
