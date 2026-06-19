using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;

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

    // --- SSH command-line shape ---
    var sshArgs = ConnectionLauncher.BuildSshArguments(new Connection
    {
        Type = ConnectionType.Ssh,
        Host = "example.com",
        Username = "root",
        Port = 2200,
        PrivateKeyPath = @"C:\keys\id_rsa",
        ExtraSshArguments = "-X -L 8080:localhost:80",
    });
    Check(sshArgs.SequenceEqual(new[]
    {
        "-i", @"C:\keys\id_rsa",
        "-p", "2200",
        "-X",
        "-L", "8080:localhost:80",
        "root@example.com",
    }), "SSH options are emitted before the target host");
    Check(ConnectionType.Ssh.ToDisplayName() == "SSH" && ConnectionType.Rdp.ToDisplayName() == "RDP",
          "Connection types display as uppercase acronyms");

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
    var expectedSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JeekRemoteManager",
        "settings.json");
    Check(string.Equals(
              Path.GetFullPath(SettingsService.DefaultSettingsPath),
              Path.GetFullPath(expectedSettingsPath),
              StringComparison.OrdinalIgnoreCase),
          "settings.json resolves under LocalAppData");
    var settingsWithRecent = new AppSettings
    {
        RecentConnectionPaths = { Path.Combine(root, "Servers", "web01.json") },
        LastSelectedConnectionPath = Path.Combine(root, "Servers", "web01.json"),
        RecentExpanded = false,
        MainWindowWidth = 1200,
        MainWindowHeight = 760,
    };
    var settingsJson = JsonSerializer.Serialize(settingsWithRecent);
    Check(settingsJson.Contains(nameof(AppSettings.RecentConnectionPaths))
          && settingsJson.Contains(nameof(AppSettings.LastSelectedConnectionPath))
          && settingsJson.Contains(nameof(AppSettings.RecentExpanded))
          && settingsJson.Contains(nameof(AppSettings.MainWindowWidth))
          && settingsJson.Contains(nameof(AppSettings.MainWindowHeight)),
          "Recent list, selected connection, and main window size are persisted inside AppSettings");

    var tempSettingsPath = Path.Combine(root, "Config", "settings.json");
    var tempSettings = new SettingsService(tempSettingsPath);
    Check(!File.Exists(tempSettingsPath), "Unchanged settings do not create settings.json");
    Check(tempSettings.SaveIfChanged(), "Unchanged settings flush succeeds");
    Check(!File.Exists(tempSettingsPath), "Unchanged settings flush does not write settings.json");
    tempSettings.Settings.Language = "zh";
    Check(!File.Exists(tempSettingsPath), "Settings changes stay in memory before flush");
    Check(tempSettings.SaveIfChanged() && File.Exists(tempSettingsPath), "Changed settings flush writes settings.json");
    var savedSettingsJson = File.ReadAllText(tempSettingsPath);
    Check(savedSettingsJson.Contains("\"Language\": \"zh\""), "Changed settings are serialized after flush");
    File.SetLastWriteTimeUtc(tempSettingsPath, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    var unchangedWriteTime = File.GetLastWriteTimeUtc(tempSettingsPath);
    Check(tempSettings.SaveIfChanged(), "Second unchanged settings flush succeeds");
    Check(File.GetLastWriteTimeUtc(tempSettingsPath) == unchangedWriteTime
          && File.ReadAllText(tempSettingsPath) == savedSettingsJson,
          "Unchanged settings flush does not rewrite the existing file");

    Check(progRoot.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)
          && progRoot.EndsWith("Connections"), "Program-directory root resolves next to the exe");
    Check(userRoot.Contains("JeekRemoteManager") && userRoot.EndsWith("Connections"),
          "User-directory root resolves under the user profile");
    Check(progRoot != userRoot, "The two storage locations are distinct");

    var builtInScriptsRoot = SettingsService.ResolveBuiltInScriptsRoot();
    Check(builtInScriptsRoot.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)
          && builtInScriptsRoot.EndsWith(Path.Combine("Data", "Scripts"), StringComparison.OrdinalIgnoreCase),
          "Built-in scripts root resolves under app Data");
    var runtimeBbrDir = Path.Combine(FindRepoRoot(), "bin", "Data", "Scripts", "BBR");
    Check(File.Exists(Path.Combine(runtimeBbrDir, "enable-bbr.sh"))
          && File.Exists(Path.Combine(runtimeBbrDir, "disable-bbr.sh")),
          "Bundled BBR scripts include enable and disable actions");

    var progScriptsRoot = SettingsService.ResolveScriptsRoot(StorageLocation.ProgramDirectory);
    var userScriptsRoot = SettingsService.ResolveScriptsRoot(StorageLocation.UserDirectory);
    Check(progScriptsRoot.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)
          && progScriptsRoot.EndsWith("Scripts"), "Program-directory custom scripts root resolves next to the exe");
    Check(!string.Equals(Path.GetFullPath(builtInScriptsRoot), Path.GetFullPath(progScriptsRoot),
              StringComparison.OrdinalIgnoreCase),
          "Built-in scripts root is separate from program-directory custom scripts root");
    Check(userScriptsRoot.Contains("JeekRemoteManager") && userScriptsRoot.EndsWith("Scripts"),
          "User-directory custom scripts root resolves under the user profile");

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
