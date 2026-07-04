using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jeek.Avalonia.Localization;
using JeekRemoteManager.Services;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace JeekRemoteManager.ViewModels;

/// <summary>One row in the remote file list.</summary>
public sealed partial class RemoteFileEntry : ObservableObject
{
    public RemoteFileEntry(
        string name, string fullPath, bool isDirectory, bool isSymlink,
        long length, DateTime modified, string permissions)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        IsSymlink = isSymlink;
        Length = length;
        Modified = modified;
        Permissions = permissions;
        _editName = name;
    }

    /// <summary>True while this row shows the inline rename editor.</summary>
    [ObservableProperty]
    private bool _isNameEditing;

    [ObservableProperty]
    private string _editName;

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public bool IsSymlink { get; }
    public long Length { get; }
    public DateTime Modified { get; }
    public string Permissions { get; }

    public string SizeText => IsDirectory ? "" : FileBrowserViewModel.FormatSize(Length);
    public string ModifiedText => Modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    /// <summary>Color emoji, like the connection tree's folder icons — the colored
    /// folder reads far better than same-color monochrome glyphs.</summary>
    public string IconGlyph => IsSymlink ? "🔗" : IsDirectory ? "📁" : "📄";
}

/// <summary>One queued/running/finished SFTP transfer shown in the transfer strip.</summary>
public sealed partial class FileTransferItem : ObservableObject
{
    public FileTransferItem(string fileName, bool isUpload)
    {
        FileName = fileName;
        IsUpload = isUpload;
        _statusText = Localizer.Get("TransferQueued");
    }

    public string FileName { get; }
    public bool IsUpload { get; }
    public string DirectionGlyph => IsUpload ? "" : "";

    internal string LocalPath = "";
    internal string RemotePath = "";
    /// <summary>Remote directory chain created before an upload (for folder uploads).</summary>
    internal string? EnsureRemoteDirectory;
    /// <summary>Remote directory the upload lands in, to auto-refresh the listing.</summary>
    internal string? TargetRemoteDirectory;
    internal long TotalBytes;
    internal bool OpenWhenDone;
    internal readonly CancellationTokenSource Cancellation = new();

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _statusText;

    /// <summary>True once completed/failed/canceled — hides the cancel button.</summary>
    [ObservableProperty]
    private bool _isFinished;

    [RelayCommand]
    private void Cancel() => Cancellation.Cancel();
}

/// <summary>
/// Drives the SFTP file browser panel that lives under a terminal tab. Uses two
/// lazily-dialed <see cref="SftpSession"/>s: one for browsing (listings, rename,
/// delete — always responsive) and one for transfers (uploads/downloads run
/// sequentially on it without blocking the listing).
/// </summary>
public partial class FileBrowserViewModel : ViewModelBase, IDisposable
{
    private readonly Func<ConnectionInfo> _buildConnectionInfo;
    private readonly Action<string> _openDirectoryInTerminal;
    private readonly SemaphoreSlim _transferPump = new(1, 1);
    // Listings of directories visited this session: navigation into a cached
    // directory renders instantly and revalidates in the background.
    private readonly Dictionary<string, List<RemoteFileEntry>> _listingCache = new();
    private SftpSession? _browseSession;
    private SftpSession? _transferSession;
    private bool _loadedOnce;
    private bool _disposed;

    private const int ListingCacheCapacity = 256;

    public FileBrowserViewModel(
        Func<ConnectionInfo> buildConnectionInfo,
        Action<string> openDirectoryInTerminal)
    {
        _buildConnectionInfo = buildConnectionInfo;
        _openDirectoryInTerminal = openDirectoryInTerminal;
    }

    public ObservableCollection<RemoteFileEntry> Items { get; } = new();
    public ObservableCollection<FileTransferItem> Transfers { get; } = new();

    [ObservableProperty]
    private string _currentPath = "";

    /// <summary>The editable path box; reset to <see cref="CurrentPath"/> when navigation fails.</summary>
    [ObservableProperty]
    private string _pathInput = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusText;

    public bool HasStatus => !string.IsNullOrEmpty(StatusText);

    partial void OnStatusTextChanged(string? value) => OnPropertyChanged(nameof(HasStatus));

    [ObservableProperty]
    private bool _hasTransfers;

    // Interactions supplied by the view (pickers and dialogs need a TopLevel).
    public Func<Task<IReadOnlyList<string>>>? PickUploadFilesAsync { get; set; }
    public Func<Task<string?>>? PickDownloadFolderAsync { get; set; }
    public Func<string, string, string, Task<string?>>? PromptAsync { get; set; }
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }
    public Func<IReadOnlyList<RemoteFileEntry>>? GetSelection { get; set; }
    public Func<string, Task>? SetClipboardTextAsync { get; set; }

    /// <summary>True when keyboard focus is currently inside the file list (view-supplied).</summary>
    public Func<bool>? IsListFocused { get; set; }

    /// <summary>Puts keyboard focus back on the file list (view-supplied). Reloading the
    /// listing rebuilds every row, which silently drops focus if it was on a row.</summary>
    public Action? RequestFocusList { get; set; }

    /// <summary>Selects an entry in the list and scrolls it into view (view-supplied).</summary>
    public Action<RemoteFileEntry>? RequestSelectEntry { get; set; }

    /// <summary>Focuses the path box and selects its text (view-supplied), so a failed
    /// navigation leaves the bad path ready to correct instead of dropping focus.</summary>
    public Action? RequestFocusPathInput { get; set; }

    /// <summary>Focuses the row's inline rename editor and selects the name stem
    /// (view-supplied).</summary>
    public Action<RemoteFileEntry>? RequestBeginRename { get; set; }

    private IReadOnlyList<RemoteFileEntry> Selection =>
        GetSelection?.Invoke() ?? Array.Empty<RemoteFileEntry>();

    /// <summary>First open of the panel: connect and show the home directory.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_loadedOnce || _disposed)
            return;
        await LoadDirectoryAsync(null);
    }

    // ---- Navigation ----

    [RelayCommand]
    private Task GoUpAsync()
    {
        if (string.IsNullOrEmpty(CurrentPath) || CurrentPath == "/")
            return Task.CompletedTask;

        // Land with the directory we just left selected, the file-manager convention.
        return LoadDirectoryAsync(ParentOf(CurrentPath), selectName: NameOf(CurrentPath));
    }

    [RelayCommand]
    private Task GoHomeAsync() => LoadDirectoryAsync(_browseSession?.HomePath);

    [RelayCommand]
    private Task RefreshAsync() =>
        string.IsNullOrEmpty(CurrentPath)
            ? EnsureLoadedAsync()
            : LoadDirectoryAsync(CurrentPath, bypassCache: true);

    [RelayCommand]
    private async Task NavigateToInputAsync()
    {
        var input = PathInput?.Trim();
        if (string.IsNullOrEmpty(input))
            return;

        if (input == "~" || input.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = _browseSession?.HomePath;
            if (home is null)
                return;
            input = input.Length == 1 ? home : CombineRemote(home, input[2..]);
        }

        if (!await LoadDirectoryAsync(NormalizeRemotePath(input, CurrentPath)))
            RequestFocusPathInput?.Invoke();
    }

    [RelayCommand]
    private async Task OpenSelectedAsync()
    {
        if (Selection.FirstOrDefault() is not { } entry)
            return;

        if (entry.IsDirectory)
        {
            await LoadDirectoryAsync(entry.FullPath);
            return;
        }

        // A symlink's readdir attributes describe the link itself, so its target
        // kind is unknown: try to enter it as a directory, fall back to a file.
        if (entry.IsSymlink && await LoadDirectoryAsync(entry.FullPath))
            return;

        DownloadAndOpen(entry);
    }

    /// <summary>Loads <paramref name="path"/> (null = remote home) into the list,
    /// selecting the entry named <paramref name="selectName"/> when given. A cached
    /// listing renders instantly and revalidates in the background; explicit
    /// refreshes and post-mutation reloads set <paramref name="bypassCache"/>.
    /// Returns false and keeps the current listing when it fails.</summary>
    private async Task<bool> LoadDirectoryAsync(
        string? path, string? selectName = null, bool bypassCache = false)
    {
        if (_disposed)
            return false;

        // Rebuilding the rows destroys a focused row's container and loses
        // keyboard focus, so remember whether the list had it and restore after.
        var hadFocus = IsListFocused?.Invoke() == true;

        if (path is not null && !bypassCache && _listingCache.TryGetValue(path, out var cached))
        {
            ApplyListing(path, cached, selectName);
            if (hadFocus)
                RequestFocusList?.Invoke();
            QueueRevalidate(path);
            return true;
        }

        IsBusy = true;
        if (!_loadedOnce)
            StatusText = L("FileBrowserConnecting");

        try
        {
            var session = _browseSession ??= new SftpSession(_buildConnectionInfo);
            var (target, entries) = await session.RunAsync(client =>
            {
                var dir = path ?? session.HomePath ?? client.WorkingDirectory;
                return (dir, ListEntries(client, dir));
            });

            if (_disposed)
                return false;

            CacheListing(target, entries);
            ApplyListing(target, entries, selectName);

            if (hadFocus)
                RequestFocusList?.Invoke();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (!_disposed)
                StatusText = ex.Message;
            return false;
        }
        finally
        {
            if (!_disposed)
                IsBusy = false;
        }
    }

    private void ApplyListing(string path, List<RemoteFileEntry> entries, string? selectName)
    {
        // Don't clobber a path the user is in the middle of typing.
        var pathBoxUntouched = PathInput == CurrentPath;

        Items.Clear();
        foreach (var entry in entries)
            Items.Add(entry);

        CurrentPath = path;
        if (pathBoxUntouched)
            PathInput = path;
        _loadedOnce = true;
        StatusText = entries.Count == 0 ? L("FileBrowserEmptyDir") : null;

        if (selectName is not null
            && Items.FirstOrDefault(e => e.Name == selectName) is { } match)
        {
            RequestSelectEntry?.Invoke(match);
        }
    }

    private void CacheListing(string path, List<RemoteFileEntry> entries)
    {
        // Crude cap: visiting this many distinct directories in one session is
        // rare, and a full reset is simpler than LRU bookkeeping.
        if (_listingCache.Count >= ListingCacheCapacity)
            _listingCache.Clear();
        _listingCache[path] = entries;
    }

    // Background revalidation is coalesced: rapid navigation through several cached
    // directories must not pile one listing request per hop onto the serialized
    // session queue. Only the most recent target is (re)validated; intermediate
    // stops keep their cached listing until the next visit. Both fields are touched
    // on the UI thread only.
    private string? _revalidateTarget;
    private bool _revalidateRunning;

    private void QueueRevalidate(string path)
    {
        _revalidateTarget = path;
        if (_revalidateRunning)
            return;
        _revalidateRunning = true;
        _ = RunRevalidateLoopAsync();
    }

    private async Task RunRevalidateLoopAsync()
    {
        try
        {
            while (!_disposed && _revalidateTarget is { } path)
            {
                _revalidateTarget = null;
                await RevalidateListingAsync(path);
            }
        }
        finally
        {
            _revalidateRunning = false;
        }
    }

    /// <summary>Refreshes a cached listing in the background; when the fresh data
    /// differs and the user is still in that directory, swaps it in while keeping
    /// selection and focus.</summary>
    private async Task RevalidateListingAsync(string path)
    {
        try
        {
            var session = _browseSession ??= new SftpSession(_buildConnectionInfo);
            var entries = await session.RunAsync(client => ListEntries(client, path));
            if (_disposed)
                return;

            CacheListing(path, entries);
            if (CurrentPath != path || ListingMatchesItems(entries))
                return;

            var hadFocus = IsListFocused?.Invoke() == true;
            ApplyListing(path, entries, Selection.FirstOrDefault()?.Name);
            if (hadFocus)
                RequestFocusList?.Invoke();
        }
        catch
        {
            // Best-effort refresh: the cached view stays; a real failure will
            // surface on the next explicit operation.
        }
    }

    private bool ListingMatchesItems(List<RemoteFileEntry> entries)
    {
        if (entries.Count != Items.Count)
            return false;

        for (var i = 0; i < entries.Count; i++)
        {
            var a = entries[i];
            var b = Items[i];
            if (a.Name != b.Name
                || a.IsDirectory != b.IsDirectory
                || a.IsSymlink != b.IsSymlink
                || a.Length != b.Length
                || a.Modified != b.Modified
                || a.Permissions != b.Permissions)
            {
                return false;
            }
        }

        return true;
    }

    private static List<RemoteFileEntry> ListEntries(SftpClient client, string path)
    {
        var entries = new List<RemoteFileEntry>();
        foreach (var file in client.ListDirectory(path))
        {
            if (file.Name is "." or "..")
                continue;

            entries.Add(new RemoteFileEntry(
                file.Name,
                file.FullName,
                file.IsDirectory,
                file.IsSymbolicLink,
                file.Length,
                file.LastWriteTime,
                BuildPermissionString(file)));
        }

        entries.Sort((a, b) =>
        {
            var dirs = b.IsDirectory.CompareTo(a.IsDirectory);
            return dirs != 0 ? dirs : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return entries;
    }

    private static string BuildPermissionString(Renci.SshNet.Sftp.ISftpFile file)
    {
        Span<char> chars = stackalloc char[10];
        chars[0] = file.IsSymbolicLink ? 'l' : file.IsDirectory ? 'd' : '-';
        chars[1] = file.OwnerCanRead ? 'r' : '-';
        chars[2] = file.OwnerCanWrite ? 'w' : '-';
        chars[3] = file.OwnerCanExecute ? 'x' : '-';
        chars[4] = file.GroupCanRead ? 'r' : '-';
        chars[5] = file.GroupCanWrite ? 'w' : '-';
        chars[6] = file.GroupCanExecute ? 'x' : '-';
        chars[7] = file.OthersCanRead ? 'r' : '-';
        chars[8] = file.OthersCanWrite ? 'w' : '-';
        chars[9] = file.OthersCanExecute ? 'x' : '-';
        return new string(chars);
    }

    // ---- File operations ----

    [RelayCommand]
    private async Task NewFolderAsync()
    {
        if (PromptAsync is null || string.IsNullOrEmpty(CurrentPath))
            return;

        var hadFocus = IsListFocused?.Invoke() == true;
        var name = (await PromptAsync(L("NewFolder"), L("FileBrowserNewFolderPrompt"), ""))?.Trim();
        if (string.IsNullOrEmpty(name) || name.Contains('/'))
        {
            // A closing dialog drops focus to the window's first focusable control;
            // the cancel path must hand it back to the list too.
            RestoreListFocus(hadFocus);
            return;
        }

        await RunBrowseOperationAsync(
            client => client.CreateDirectory(CombineRemote(CurrentPath, name)),
            refocusList: hadFocus,
            selectName: name);
    }

    /// <summary>Starts Explorer-style inline renaming on the selected row; the view
    /// focuses the row's editor via <see cref="RequestBeginRename"/>.</summary>
    [RelayCommand]
    private void RenameSelected()
    {
        if (Selection.FirstOrDefault() is not { } entry)
            return;

        foreach (var item in Items)
            item.IsNameEditing = false;

        entry.EditName = entry.Name;
        entry.IsNameEditing = true;
        RequestBeginRename?.Invoke(entry);
    }

    /// <summary>Applies the inline rename editor's text. No-op when editing already
    /// ended (Escape hides the editor, which also fires its LostFocus).</summary>
    public async Task CommitRenameAsync(RemoteFileEntry entry)
    {
        if (!entry.IsNameEditing)
            return;
        entry.IsNameEditing = false;

        var name = entry.EditName?.Trim();
        if (string.IsNullOrEmpty(name) || name == entry.Name || name.Contains('/'))
        {
            RestoreListFocus(true);
            return;
        }

        var target = CombineRemote(ParentOf(entry.FullPath), name);
        await RunBrowseOperationAsync(
            client => client.RenameFile(entry.FullPath, target),
            refocusList: true,
            selectName: name);
    }

    public void CancelRename(RemoteFileEntry entry)
    {
        entry.IsNameEditing = false;
        RestoreListFocus(true);
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var selection = Selection;
        if (ConfirmAsync is null || selection.Count == 0)
            return;

        var hadFocus = IsListFocused?.Invoke() == true;
        var what = selection.Count == 1
            ? $"'{selection[0].Name}'"
            : L("FileBrowserDeleteMany", selection.Count);
        if (!await ConfirmAsync(L("DialogDeleteTitle"), L("FileBrowserDeletePrompt", what)))
        {
            RestoreListFocus(hadFocus);
            return;
        }

        await RunBrowseOperationAsync(client =>
        {
            foreach (var entry in selection)
                DeleteRecursive(client, entry.FullPath, entry.IsDirectory, entry.IsSymlink);
        }, refocusList: hadFocus);
    }

    [RelayCommand]
    private async Task ChangePermissionsAsync()
    {
        var selection = Selection;
        if (PromptAsync is null || selection.Count == 0)
            return;

        var hadFocus = IsListFocused?.Invoke() == true;
        var initial = OctalFromPermissions(selection[0].Permissions);
        var input = (await PromptAsync(L("FileBrowserPermissions"), L("FileBrowserPermissionsPrompt"), initial))?.Trim();
        if (string.IsNullOrEmpty(input) || !Regex.IsMatch(input, "^[0-7]{3,4}$"))
        {
            RestoreListFocus(hadFocus);
            return;
        }

        var mode = Convert.ToInt16(input, 8);
        await RunBrowseOperationAsync(client =>
        {
            foreach (var entry in selection)
                client.ChangePermissions(entry.FullPath, mode);
        }, refocusList: hadFocus, selectName: selection[0].Name);
    }

    [RelayCommand]
    private async Task CopyPathAsync()
    {
        var selection = Selection;
        if (SetClipboardTextAsync is null)
            return;

        var text = selection.Count > 0
            ? string.Join(Environment.NewLine, selection.Select(e => e.FullPath))
            : CurrentPath;
        if (!string.IsNullOrEmpty(text))
            await SetClipboardTextAsync(text);
    }

    [RelayCommand]
    private void OpenInTerminal()
    {
        var entry = Selection.FirstOrDefault();
        var path = entry is null ? CurrentPath
            : entry.IsDirectory ? entry.FullPath
            : ParentOf(entry.FullPath);
        if (!string.IsNullOrEmpty(path))
            _openDirectoryInTerminal(path);
    }

    private void RestoreListFocus(bool hadFocus)
    {
        if (hadFocus)
            RequestFocusList?.Invoke();
    }

    /// <summary>Runs a mutation on the browse session and refreshes the listing,
    /// optionally selecting <paramref name="selectName"/> and putting focus back on
    /// the list (dialogs shown before the operation take focus away from it);
    /// failures land in the status line.</summary>
    private async Task RunBrowseOperationAsync(
        Action<SftpClient> operation, bool refocusList = false, string? selectName = null)
    {
        if (_disposed)
            return;

        IsBusy = true;
        try
        {
            var session = _browseSession ??= new SftpSession(_buildConnectionInfo);
            await session.RunAsync(client =>
            {
                operation(client);
                return true;
            });
        }
        catch (Exception ex)
        {
            if (!_disposed)
                StatusText = ex.Message;
            return;
        }
        finally
        {
            if (!_disposed)
                IsBusy = false;
        }

        if (string.IsNullOrEmpty(CurrentPath))
            await EnsureLoadedAsync();
        else
            await LoadDirectoryAsync(CurrentPath, selectName, bypassCache: true);

        if (refocusList)
            RequestFocusList?.Invoke();
    }

    private static void DeleteRecursive(SftpClient client, string path, bool isDirectory, bool isSymlink)
    {
        // A symlink is removed as a file (never followed), so a linked directory's
        // contents survive.
        if (!isDirectory || isSymlink)
        {
            client.DeleteFile(path);
            return;
        }

        foreach (var child in client.ListDirectory(path))
        {
            if (child.Name is "." or "..")
                continue;
            DeleteRecursive(client, child.FullName, child.IsDirectory, child.IsSymbolicLink);
        }

        client.DeleteDirectory(path);
    }

    // ---- Transfers ----

    [RelayCommand]
    private async Task UploadAsync()
    {
        if (PickUploadFilesAsync is null || string.IsNullOrEmpty(CurrentPath))
            return;

        var files = await PickUploadFilesAsync();
        if (files.Count > 0)
            await QueueUploadLocalPathsAsync(files);
    }

    /// <summary>Queues local files (and folders, recursively) for upload into the
    /// current remote directory. Also the drop target handler.</summary>
    public async Task QueueUploadLocalPathsAsync(IReadOnlyList<string> localPaths)
    {
        var remoteDir = CurrentPath;
        if (_disposed || string.IsNullOrEmpty(remoteDir))
            return;

        foreach (var path in localPaths)
        {
            if (File.Exists(path))
            {
                EnqueueUpload(path, CombineRemote(remoteDir, Path.GetFileName(path)), null, remoteDir);
                continue;
            }

            if (!Directory.Exists(path))
                continue;

            // Expand the folder off the UI thread; big trees can take a while.
            var root = Path.TrimEndingDirectorySeparator(path);
            var rootName = Path.GetFileName(root);
            List<(string Local, string Remote, string RemoteDir)> expanded;
            try
            {
                expanded = await Task.Run(() =>
                    Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                        .Select(file =>
                        {
                            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                            var remote = CombineRemote(remoteDir, rootName + "/" + relative);
                            return (file, remote, ParentOf(remote));
                        })
                        .ToList());
            }
            catch (Exception ex)
            {
                StatusText = ex.Message;
                continue;
            }

            foreach (var (local, remote, dir) in expanded)
                EnqueueUpload(local, remote, dir, remoteDir);
        }
    }

    private void EnqueueUpload(string localPath, string remotePath, string? ensureRemoteDir, string targetRemoteDir)
    {
        var item = new FileTransferItem(Path.GetFileName(localPath), isUpload: true)
        {
            LocalPath = localPath,
            RemotePath = remotePath,
            EnsureRemoteDirectory = ensureRemoteDir,
            TargetRemoteDirectory = targetRemoteDir,
            TotalBytes = TryGetFileLength(localPath),
        };
        Enqueue(item);
    }

    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        var selection = Selection;
        if (PickDownloadFolderAsync is null || selection.Count == 0)
            return;

        var folder = await PickDownloadFolderAsync();
        if (string.IsNullOrEmpty(folder))
            return;

        foreach (var entry in selection)
        {
            if (!entry.IsDirectory)
            {
                EnqueueDownload(entry.FullPath, Path.Combine(folder, entry.Name), entry.Length, openWhenDone: false);
                continue;
            }

            // Expand the remote folder on the transfer session so the listing
            // session stays free for browsing.
            var remoteRoot = entry.FullPath;
            var localRoot = Path.Combine(folder, entry.Name);
            try
            {
                var session = _transferSession ??= new SftpSession(_buildConnectionInfo);
                var files = await session.RunAsync(client =>
                {
                    var found = new List<(string Remote, string Relative, long Length)>();
                    CollectFilesRecursive(client, remoteRoot, "", found);
                    return found;
                });

                foreach (var (remote, relative, length) in files)
                {
                    var local = Path.Combine(localRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                    EnqueueDownload(remote, local, length, openWhenDone: false);
                }
            }
            catch (Exception ex)
            {
                if (!_disposed)
                    StatusText = ex.Message;
            }
        }
    }

    private static void CollectFilesRecursive(
        SftpClient client, string path, string relativePrefix,
        List<(string Remote, string Relative, long Length)> found)
    {
        foreach (var child in client.ListDirectory(path))
        {
            if (child.Name is "." or "..")
                continue;

            var relative = relativePrefix.Length == 0 ? child.Name : relativePrefix + "/" + child.Name;
            if (child.IsDirectory)
                CollectFilesRecursive(client, child.FullName, relative, found);
            else if (!child.IsSymbolicLink)
                found.Add((child.FullName, relative, child.Length));
        }
    }

    /// <summary>Double-clicked file: download to a unique temp folder, then open it
    /// with the local shell association.</summary>
    private void DownloadAndOpen(RemoteFileEntry entry)
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(), "JeekRemoteManager", "sftp", Guid.NewGuid().ToString("N"));
        EnqueueDownload(entry.FullPath, Path.Combine(tempDir, entry.Name), entry.Length, openWhenDone: true);
    }

    private void EnqueueDownload(string remotePath, string localPath, long length, bool openWhenDone)
    {
        var item = new FileTransferItem(Path.GetFileName(localPath), isUpload: false)
        {
            RemotePath = remotePath,
            LocalPath = localPath,
            TotalBytes = length,
            OpenWhenDone = openWhenDone,
        };
        Enqueue(item);
    }

    private void Enqueue(FileTransferItem item)
    {
        Transfers.Add(item);
        HasTransfers = true;
        _ = RunTransferAsync(item);
    }

    private async Task RunTransferAsync(FileTransferItem item)
    {
        await _transferPump.WaitAsync();
        try
        {
            if (_disposed || item.Cancellation.IsCancellationRequested)
            {
                SetTransferState(item, L("TransferCanceled"), finished: true);
                return;
            }

            var session = _transferSession ??= new SftpSession(_buildConnectionInfo);
            await session.RunAsync(client =>
            {
                ExecuteTransfer(client, item);
                return true;
            }, item.Cancellation.Token);

            SetTransferState(item, L("TransferCompleted"), finished: true, progress: 100);

            if (item.IsUpload)
            {
                if (item.TargetRemoteDirectory == CurrentPath && !_disposed)
                    await Dispatcher.UIThread.InvokeAsync(() => _ = RefreshAsync());
            }
            else if (item.OpenWhenDone)
            {
                TryOpenLocalFile(item.LocalPath);
            }
        }
        catch (OperationCanceledException)
        {
            SetTransferState(item, L("TransferCanceled"), finished: true);
            if (!item.IsUpload)
                TryDeleteLocalFile(item.LocalPath);
        }
        catch (Exception ex)
        {
            SetTransferState(item, L("TransferFailed", ex.Message), finished: true);
            if (!item.IsUpload)
                TryDeleteLocalFile(item.LocalPath);
        }
        finally
        {
            _transferPump.Release();
        }
    }

    /// <summary>Runs on the transfer session's worker thread. Cancellation is delivered
    /// by throwing out of the progress callback — SSH.NET's transfer loops have no
    /// token parameter.</summary>
    private void ExecuteTransfer(SftpClient client, FileTransferItem item)
    {
        var throttle = Stopwatch.StartNew();
        var total = item.TotalBytes;

        void Report(ulong transferred)
        {
            item.Cancellation.Token.ThrowIfCancellationRequested();
            if (throttle.ElapsedMilliseconds < 100)
                return;
            throttle.Restart();

            var done = (long)transferred;
            Dispatcher.UIThread.Post(() =>
            {
                item.Progress = total > 0 ? Math.Min(100, done * 100.0 / total) : 0;
                item.StatusText = $"{FormatSize(done)} / {FormatSize(total)}";
            });
        }

        if (item.IsUpload)
        {
            if (item.EnsureRemoteDirectory is not null)
                EnsureRemoteDirectories(client, item.EnsureRemoteDirectory);

            using var local = File.OpenRead(item.LocalPath);
            item.TotalBytes = total = local.Length;
            client.UploadFile(local, item.RemotePath, canOverride: true, Report);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(item.LocalPath)!);
            using var local = File.Create(item.LocalPath);
            client.DownloadFile(item.RemotePath, local, Report);
        }
    }

    private static void EnsureRemoteDirectories(SftpClient client, string path)
    {
        var current = "";
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + part;
            if (client.Exists(current))
                continue;
            try
            {
                client.CreateDirectory(current);
            }
            catch (SshException)
            {
                // Lost a creation race, or the parent chain exists with odd
                // permissions — the upload itself will surface a real failure.
            }
        }
    }

    private static void SetTransferState(FileTransferItem item, string status, bool finished, double? progress = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            item.StatusText = status;
            item.IsFinished = finished;
            if (progress is { } value)
                item.Progress = value;
        });
    }

    [RelayCommand]
    private void ClearFinishedTransfers()
    {
        for (var i = Transfers.Count - 1; i >= 0; i--)
        {
            if (Transfers[i].IsFinished)
                Transfers.RemoveAt(i);
        }

        HasTransfers = Transfers.Count > 0;
    }

    private static void TryOpenLocalFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // No association or the file vanished; the transfer row already shows
            // where it went.
        }
    }

    private static void TryDeleteLocalFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of a partial download.
        }
    }

    private static long TryGetFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    // ---- Path helpers ----

    internal static string CombineRemote(string directory, string name) =>
        directory == "/" ? "/" + name : directory.TrimEnd('/') + "/" + name;

    internal static string ParentOf(string path)
    {
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index <= 0 ? "/" : trimmed[..index];
    }

    /// <summary>Last path segment ("/home/user/docs" → "docs").</summary>
    internal static string NameOf(string path)
    {
        var trimmed = path.TrimEnd('/');
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }

    /// <summary>Resolves "." and ".." textually; a relative input is taken against
    /// <paramref name="current"/>.</summary>
    internal static string NormalizeRemotePath(string input, string current)
    {
        var combined = input.StartsWith('/') ? input : CombineRemote(
            string.IsNullOrEmpty(current) ? "/" : current, input);

        var stack = new List<string>();
        foreach (var part in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
                continue;
            if (part == "..")
            {
                if (stack.Count > 0)
                    stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(part);
        }

        return stack.Count == 0 ? "/" : "/" + string.Join('/', stack);
    }

    internal static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return bytes + " B";
        double value = bytes;
        string[] units = { "KB", "MB", "GB", "TB" };
        var unit = -1;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value.ToString(value >= 100 ? "F0" : "F1", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>"rwxr-xr-x" → "755", used to prefill the permissions prompt.</summary>
    internal static string OctalFromPermissions(string permissions)
    {
        if (permissions.Length != 10)
            return "644";

        var digits = new char[3];
        for (var group = 0; group < 3; group++)
        {
            var value = 0;
            var offset = 1 + group * 3;
            if (permissions[offset] == 'r')
                value += 4;
            if (permissions[offset + 1] == 'w')
                value += 2;
            if (permissions[offset + 2] != '-')
                value += 1;
            digits[group] = (char)('0' + value);
        }
        return new string(digits);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var transfer in Transfers)
            transfer.Cancellation.Cancel();

        _browseSession?.Dispose();
        _transferSession?.Dispose();
        _browseSession = null;
        _transferSession = null;
    }
}
