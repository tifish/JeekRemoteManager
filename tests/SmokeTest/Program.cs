using System.Text;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;

int failures = 0;
void Check(bool cond, string label)
{
    Console.WriteLine((cond ? "PASS  " : "FAIL  ") + label);
    if (!cond) failures++;
}

// Isolated temp root so we don't touch the user's real data.
var root = Path.Combine(Path.GetTempPath(), "jrm_smoke_" + Guid.NewGuid().ToString("N"));
var store = new ConnectionStore(root);

try
{
    Check(Directory.Exists(root), "Store creates its root folder");

    // --- Password encryption round-trip (DPAPI) ---
    const string secret = "S3cr3t!™密码";
    var enc = PasswordProtector.Encrypt(secret);
    Check(enc != secret && enc.Length > 0, "Encrypt produces non-plaintext blob");
    Check(PasswordProtector.Decrypt(enc) == secret, "Decrypt round-trips the password");
    Check(PasswordProtector.Encrypt("") == "", "Empty password encrypts to empty");

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
    Check(progRoot.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)
          && progRoot.EndsWith("Connections"), "Program-directory root resolves next to the exe");
    Check(userRoot.Contains("JeekRemoteManager") && userRoot.EndsWith("Connections"),
          "User-directory root resolves under the user profile");
    Check(progRoot != userRoot, "The two storage locations are distinct");

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
