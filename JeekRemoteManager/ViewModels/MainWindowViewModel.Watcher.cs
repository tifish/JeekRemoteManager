using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jeek.Avalonia.Localization;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;
using JeekTools;

namespace JeekRemoteManager.ViewModels;

/// <summary>Watching the storage folder and reloading what changed outside the app.</summary>
public partial class MainWindowViewModel
{
    // --- Watching the connections folder ---

    /// <summary>(Re)starts the file-system watcher for the active storage mode.</summary>
    private void StartWatchingCurrentStorage()
    {
        var watchPortableConfig = _settings.CurrentStorageLocation == StorageLocation.ProgramDirectory;
        var path = watchPortableConfig ? _settings.ResolveConfigRoot() : _store.RootPath;
        StartWatching(path, watchPortableConfig);
    }

    private void StartWatching(string path, bool watchPortableConfig)
    {
        StopWatching();
        _pendingWatchedPaths.Clear();

        if (!Directory.Exists(path))
            return;

        try
        {
            _watchingPortableConfig = watchPortableConfig;
            _watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size,
            };
            _watcher.Created += OnWatchedChange;
            _watcher.Deleted += OnWatchedChange;
            _watcher.Renamed += OnWatchedChange;
            _watcher.Changed += OnWatchedChange;
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            // If watching can't be set up (e.g. unsupported path), the app still
            // works; it just won't pick up external changes automatically.
            _watcher = null;
        }
    }

    private void StopWatching()
    {
        if (_watcher is null)
            return;

        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnWatchedChange;
        _watcher.Deleted -= OnWatchedChange;
        _watcher.Renamed -= OnWatchedChange;
        _watcher.Changed -= OnWatchedChange;
        _watcher.Dispose();
        _watcher = null;
        _pendingWatchedPaths.Clear();
        _watchingPortableConfig = false;
    }

    // Events arrive on a background thread; hop to the UI thread and debounce. Whether a
    // change was the app's own is decided when the debounce fires, by comparing the disk
    // with what the tree reflects — not by dropping events for a while after each own
    // write, which also dropped external changes that happened to land in that window.
    private void OnWatchedChange(object? sender, FileSystemEventArgs e)
    {
        var changedPath = e.FullPath;
        var oldPath = e is RenamedEventArgs renamed ? renamed.OldFullPath : null;

        Dispatcher.UIThread.Post(() => ScheduleWatchReload(changedPath, oldPath));
    }

    private void ScheduleWatchReload(string changedPath, string? oldPath = null)
    {
        if (_watchingPortableConfig)
        {
            AddPendingWatchedPath(changedPath);
            if (!string.IsNullOrWhiteSpace(oldPath))
                AddPendingWatchedPath(oldPath);
        }

        if (_watchReloadTimer is null)
        {
            _watchReloadTimer = new DispatcherTimer();
            _watchReloadTimer.Tick += (_, _) =>
            {
                _watchReloadTimer!.Stop();
                var changedPaths = _pendingWatchedPaths.ToList();
                _pendingWatchedPaths.Clear();

                ReloadWatchedData(changedPaths);
            };
        }

        _watchReloadTimer.Interval = _watchingPortableConfig
            ? PortableConfigReloadDelay
            : ConnectionWatchReloadDelay;
        _watchReloadTimer.Stop();
        _watchReloadTimer.Start();
    }

    private void AddPendingWatchedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            _pendingWatchedPaths.Add(Path.GetFullPath(path));
        }
        catch
        {
            _pendingWatchedPaths.Add(path);
        }
    }

    private void ReloadWatchedData(IReadOnlyCollection<string> changedPaths)
    {
        if (!_watchingPortableConfig)
        {
            // Don't clobber an in-progress edit; flush it first so the reload
            // reflects the user's latest changes too.
            FlushPendingAutoSave();
            _ = ReloadTreeIfChangedAsync();
            return;
        }

        var changes = ClassifyPortableConfigChanges(changedPaths);
        if (!changes.HasAnyChange)
            return;

        if (changes.SettingsChanged && !_settings.RoamingFileMatchesLastSave())
        {
            var previousInterval = _settings.Settings.UpdateCheckIntervalHours;
            _settings.ReloadRoamingSettings();
            ApplyLanguage(_settings.Settings.Language);
            ApplyTheme(_settings.Settings.Theme);
            ApplyTerminalAppearance?.Invoke(TerminalAppearance);
            if (previousInterval != _settings.Settings.UpdateCheckIntervalHours)
                _updateIntervalChanged.Cancel();
        }

        if (changes.ScriptsChanged)
            ReloadScripts();

        if (changes.ConnectionsChanged)
        {
            FlushPendingAutoSave();
            _ = ReloadPortableConnectionsIfChangedAsync();
        }
    }

    private async Task ReloadPortableConnectionsIfChangedAsync()
    {
        if (!await TreeDiffersFromDiskAsync())
            return;

        ClearClipboard();
        _ = ReloadTreeAsync(_settings.Settings.LastSelectedConnectionPath);
        OnPropertyChanged(nameof(RootPath));
        OnPropertyChanged(nameof(TargetDescription));
    }

    public static PortableConfigChangeSet ClassifyPortableConfigChanges(IEnumerable<string> changedPaths)
    {
        var settingsPath = SettingsService.ResolveSettingsPath(StorageLocation.ProgramDirectory);
        var connectionsRoot = SettingsService.ResolveConnectionsRoot(StorageLocation.ProgramDirectory);
        var scriptsRoot = SettingsService.ResolveScriptsRoot(StorageLocation.ProgramDirectory);

        var result = new PortableConfigChangeSet();
        foreach (var path in changedPaths)
        {
            if (PathEquals(path, settingsPath))
            {
                result.SettingsChanged = true;
            }
            else if (ConnectionStore.IsSameOrInside(connectionsRoot, path))
            {
                result.ConnectionsChanged = true;
            }
            else if (ConnectionStore.IsSameOrInside(scriptsRoot, path))
            {
                result.ScriptsChanged = true;
            }
        }

        return result;
    }

    public sealed class PortableConfigChangeSet
    {
        public bool SettingsChanged { get; set; }

        public bool ConnectionsChanged { get; set; }

        public bool ScriptsChanged { get; set; }

        public bool HasAnyChange => SettingsChanged || ConnectionsChanged || ScriptsChanged;
    }
}
