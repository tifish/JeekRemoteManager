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

/// <summary>Refresh, updates, language, imports, settings, storage moves and master-password change.</summary>
public partial class MainWindowViewModel
{
    // --- Misc ---

    [RelayCommand]
    private void Refresh() => ReloadTree(SelectedNode?.FullPath);

    /// <summary>Clears the tree selection so subsequent new/paste operations target the root.</summary>
    // Disabled while renaming so the tree's Escape key binding doesn't swallow
    // the key before the name editor can cancel the edit.
    [RelayCommand(CanExecute = nameof(CanClearSelection))]
    private void ClearSelection() => SelectedNode = null;

    private bool CanClearSelection() => _renamingNode is null;

    [RelayCommand]
    private void OpenStorageFolder()
    {
        try
        {
            Directory.CreateDirectory(_store.RootPath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _store.RootPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusOpenFolderFailed", ex.Message);
        }
    }

    // Cancelled when the user changes the periodic-check interval, so the loop
    // wakes from its long sleep and picks up the new cadence immediately.
    private CancellationTokenSource _updateIntervalChanged = new();

    // Guards against overlapping downloads when the periodic silent check and
    // the manual menu command race each other. Only touched on the UI thread.
    private bool _updateDownloadInProgress;

    private static string FormatUpdateDownloadProgress(UpdateDownloadProgress p)
    {
        var speed = $"{p.BytesPerSecond / (1024 * 1024):0.0} MB/s";
        if (p.TotalBytes is > 0)
        {
            var percent = Math.Min(100, (int)(p.ReceivedBytes * 100 / p.TotalBytes.Value));
            return $"{percent}% ({FileBrowserViewModel.FormatSize(p.ReceivedBytes)} / " +
                   $"{FileBrowserViewModel.FormatSize(p.TotalBytes.Value)}, {speed})";
        }

        return $"{FileBrowserViewModel.FormatSize(p.ReceivedBytes)}, {speed}";
    }

    /// <summary>
    /// Background task driving auto-updates: an optional silent check shortly
    /// after launch (per <see cref="AppSettings.CheckUpdateOnStartup"/>) and an
    /// optional periodic check (per <see cref="AppSettings.UpdateCheckIntervalHours"/>).
    /// Both gates are re-read each iteration so settings changes take effect
    /// without a restart. Failures are swallowed.
    /// </summary>
    public async Task RunBackgroundUpdateChecksAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            if (_settings.Settings.CheckUpdateOnStartup)
                await CheckOnceSilentlyAsync().ConfigureAwait(false);

            while (true)
            {
                var hours = _settings.Settings.UpdateCheckIntervalHours;
                // Idle (1h) re-poll when periodic is disabled, so enabling it
                // from Settings starts taking effect within the hour.
                var delay = hours > 0 ? TimeSpan.FromHours(hours) : TimeSpan.FromHours(1);

                var waker = _updateIntervalChanged;
                try
                {
                    await Task.Delay(delay, waker.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Interval changed mid-wait; loop around to read the new value.
                    _updateIntervalChanged = new CancellationTokenSource();
                    continue;
                }

                if (_settings.Settings.UpdateCheckIntervalHours > 0)
                    await CheckOnceSilentlyAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort: never let an update-check error tear the app down.
        }
    }

    private async Task CheckOnceSilentlyAsync()
    {
        try
        {
            var outcome = await AutoUpdateService.HasUpdateAsync().ConfigureAwait(false);
            if (outcome != UpdateCheckOutcome.Available)
                return;

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await PromptUpdateAsync(silentIfUpToDate: true);
            });
        }
        catch
        {
            // Best-effort.
        }
    }

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        StatusMessage = L("StatusCheckingUpdates");
        var outcome = await AutoUpdateService.HasUpdateAsync();
        await PromptUpdateAsync(silentIfUpToDate: false, outcome);
    }

    private void ApplyLanguage(string? language)
    {
        // Empty / null means "follow system": clear the stored preference and
        // resolve from the current OS culture (falling back to en if unsupported).
        if (string.IsNullOrEmpty(language))
        {
            _settings.Settings.Language = null;

            var system = System.Globalization.CultureInfo.CurrentCulture.TwoLetterISOLanguageName;
            Localizer.Language = Localizer.Languages.Contains(system) ? system : "en";
            return;
        }

        if (!Localizer.Languages.Contains(language))
            return;

        Localizer.Language = language;
        _settings.Settings.Language = language;
    }

    private void ApplyTheme(string? theme)
    {
        // Empty / null means "follow system": clear the stored preference and
        // let Avalonia resolve the variant from the OS theme.
        _settings.Settings.Theme = string.IsNullOrEmpty(theme) ? null : theme;

        if (Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = App.ThemeVariantFor(_settings.Settings.Theme);
    }

    private async Task PromptUpdateAsync(bool silentIfUpToDate, UpdateCheckOutcome? known = null)
    {
        var outcome = known ?? await AutoUpdateService.HasUpdateAsync();
        switch (outcome)
        {
            case UpdateCheckOutcome.Available:
                if (ConfirmAsync is null || _updateDownloadInProgress)
                    return;

                // Prefer a previously postponed package when its sidecar still
                // matches remote version.txt; otherwise download into LocalAppData.
                string? stagedDir = AutoUpdateService.TryGetReusableStagedPackageDir();
                if (stagedDir is null)
                {
                    _updateDownloadInProgress = true;
                    try
                    {
                        var progress = new Progress<UpdateDownloadProgress>(p =>
                            StatusMessage = L("StatusDownloadingUpdate", FormatUpdateDownloadProgress(p)));
                        StatusMessage = L("StatusDownloadingUpdate", "");
                        stagedDir = await AutoUpdateService.DownloadAndStageAsync(progress: progress);
                    }
                    finally
                    {
                        _updateDownloadInProgress = false;
                    }

                    if (stagedDir is null)
                    {
                        StatusMessage = L("StatusUpdateDownloadFailed", AutoUpdateService.FailureReason ?? "");
                        return;
                    }
                }

                // Package is local; ask once before restart so live sessions
                // are never torn down unannounced. Declining keeps the stage
                // for the next check (sidecar still matches remote version).
                var restart = await ConfirmAsync(
                    L("DialogUpdateReadyTitle"),
                    L("DialogUpdateReadyMessage",
                        AutoUpdateService.LocalCommitCount,
                        AutoUpdateService.RemoteCommitCount));
                if (!restart)
                {
                    StatusMessage = L("StatusUpdatePostponed");
                    return;
                }

                FlushPendingAutoSave();
                FlushSettings();

                if (!AutoUpdateService.LaunchInstall(stagedDir))
                {
                    StatusMessage = L("StatusUpdateLauncherFail");
                    return;
                }

                // Hand off to the PowerShell updater: it waits for our exit,
                // replaces the install, then relaunches the app.
                if (Avalonia.Application.Current?.ApplicationLifetime
                    is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
                break;

            case UpdateCheckOutcome.UpToDate:
                if (!silentIfUpToDate)
                    StatusMessage = L("StatusUpToDate", AutoUpdateService.LocalCommitCount);
                break;

            case UpdateCheckOutcome.Failed:
                if (!silentIfUpToDate)
                    StatusMessage = L("StatusUpdateFailed", AutoUpdateService.FailureReason ?? "");
                break;
        }
    }

    [RelayCommand]
    private async Task ImportFinalShell()
    {
        if (PickFolderAsync is null)
            return;

        FlushPendingAutoSave();

        var defaultHint = @"C:\Library\Software\Net\RemoteControl\FinalShell\conn";
        var picked = await PickFolderAsync(defaultHint, L("DialogPickFinalShellTitle"));
        if (string.IsNullOrEmpty(picked))
            return;

        try
        {
            var importer = new FinalShellImporter(_store);
            var result = importer.Import(picked);
            ReloadTree();
            StatusMessage = L("StatusImported", result.Imported, result.Folders, result.Skipped);
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusImportFailed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ImportSecureCrt()
    {
        if (PickFolderAsync is null)
            return;

        FlushPendingAutoSave();

        var defaultHint = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VanDyke", "Config", "Sessions");
        if (!Directory.Exists(defaultHint))
            defaultHint = @"C:\Library\Software\Net\RemoteControl\SecureCRT\Data\settings\config\Sessions";

        var picked = await PickFolderAsync(defaultHint, L("DialogPickSecureCrtTitle"));
        if (string.IsNullOrEmpty(picked))
            return;

        try
        {
            var result = ImportSecureCrtFromDirectory(picked);
            StatusMessage = FormatSessionImportResult(
                result.Imported, result.Folders, result.Skipped, result.PasswordsImported);
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusImportFailed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ImportXshell()
    {
        if (PickFolderAsync is null)
            return;

        FlushPendingAutoSave();

        var defaultHint = FindDefaultXshellSessionsPath()
                          ?? Path.Combine(
                              Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                              "NetSarang Computer");

        var picked = await PickFolderAsync(defaultHint, L("DialogPickXshellTitle"));
        if (string.IsNullOrEmpty(picked))
            return;

        try
        {
            var result = ImportXshellFromDirectory(picked);
            StatusMessage = FormatSessionImportResult(
                result.Imported, result.Folders, result.Skipped, result.PasswordsImported);
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusImportFailed", ex.Message);
        }
    }

    /// <summary>
    /// Imports SecureCRT sessions from a Sessions directory. Used by the UI
    /// command and by the Debug MCP for automated testing.
    /// </summary>
    public SecureCrtImporter.ImportResult ImportSecureCrtFromDirectory(string sessionsRoot)
    {
        FlushPendingAutoSave();
        var result = new SecureCrtImporter(_store).Import(sessionsRoot);
        ReloadTree();
        return result;
    }

    /// <summary>
    /// Imports Xshell sessions from a Sessions directory. Used by the UI
    /// command and by the Debug MCP for automated testing.
    /// </summary>
    public XshellImporter.ImportResult ImportXshellFromDirectory(string sessionsRoot)
    {
        FlushPendingAutoSave();
        var result = new XshellImporter(_store).Import(sessionsRoot);
        ReloadTree();
        return result;
    }

    private string FormatSessionImportResult(
        int imported, int folders, int skipped, int passwordsImported)
    {
        var message = L("StatusImportedConnections", imported, folders, skipped);
        if (imported <= 0)
            return message;

        message += " " + (passwordsImported > 0
            ? L("StatusImportedPasswords", passwordsImported)
            : L("StatusImportedNoPasswords"));
        return message;
    }

    private static string? FindDefaultXshellSessionsPath()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var root = Path.Combine(documents, "NetSarang Computer");
        if (!Directory.Exists(root))
            return null;

        // Prefer the highest versioned Xshell\Sessions folder.
        string? best = null;
        var bestVersion = -1.0;
        foreach (var versionDir in Directory.GetDirectories(root))
        {
            var sessions = Path.Combine(versionDir, "Xshell", "Sessions");
            if (!Directory.Exists(sessions))
                continue;

            var name = Path.GetFileName(versionDir);
            if (!double.TryParse(name, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var version))
                version = 0;

            if (best is null || version >= bestVersion)
            {
                best = sessions;
                bestVersion = version;
            }
        }

        return best;
    }

    [RelayCommand]
    private async Task OpenSettings()
    {
        // Flush in case the user was mid-edit when changing storage location.
        FlushPendingAutoSave();

        if (PickSettingsAsync is null)
            return;

        var current = _settings.CurrentStorageLocation;
        var currentCustomPath = _settings.Settings.CustomStoragePath;
        var result = await PickSettingsAsync(
            current,
            currentCustomPath,
            _settings.Settings.Language,
            _settings.Settings.Theme,
            _settings.Settings.CheckUpdateOnStartup,
            _settings.Settings.UpdateCheckIntervalHours,
            _settings.Settings.FileBrowserEditorPath,
            TerminalAppearance);
        if (result is null)
            return;

        // Font and colors apply to every open terminal at once; scrollback to new tabs.
        var terminal = new TerminalAppearanceSettings(
            string.IsNullOrWhiteSpace(result.Terminal.FontFamily) ? null : result.Terminal.FontFamily.Trim(),
            Services.TerminalAppearance.NormalizeSchemeName(result.Terminal.ColorScheme),
            Services.TerminalAppearance.NormalizeScrollback(result.Terminal.ScrollbackLines));
        if (terminal != TerminalAppearance)
        {
            _settings.Settings.TerminalFontFamily = terminal.FontFamily;
            _settings.Settings.TerminalColorScheme = terminal.ColorScheme;
            _settings.Settings.TerminalScrollbackLines = terminal.ScrollbackLines;
            _settings.SaveIfChanged();
            ApplyTerminalAppearance?.Invoke(TerminalAppearance);
        }

        // Apply the remote-editing editor; takes effect on the next F4 open.
        var editorPath = string.IsNullOrWhiteSpace(result.FileBrowserEditorPath)
            ? null
            : result.FileBrowserEditorPath.Trim();
        if (editorPath != _settings.Settings.FileBrowserEditorPath)
        {
            _settings.Settings.FileBrowserEditorPath = editorPath;
            _settings.SaveIfChanged();
        }
        // Apply the language choice (no-op if unchanged); takes effect immediately.
        if (result.Language != _settings.Settings.Language)
            ApplyLanguage(result.Language);

        // Apply the theme choice (no-op if unchanged); takes effect immediately.
        if (result.Theme != _settings.Settings.Theme)
            ApplyTheme(result.Theme);

        // Apply auto-update preferences. A changed interval wakes the periodic
        // loop so the new cadence kicks in without a restart.
        var intervalChanged = result.UpdateCheckIntervalHours != _settings.Settings.UpdateCheckIntervalHours;
        if (result.CheckUpdateOnStartup != _settings.Settings.CheckUpdateOnStartup
            || intervalChanged)
        {
            _settings.Settings.CheckUpdateOnStartup = result.CheckUpdateOnStartup;
            _settings.Settings.UpdateCheckIntervalHours = result.UpdateCheckIntervalHours;
            if (intervalChanged)
                _updateIntervalChanged.Cancel();
        }

        // A no-op when neither the location nor (for a custom location) its path changed.
        if (result.StorageLocation == current
            && (result.StorageLocation != StorageLocation.CustomDirectory
                || string.Equals(result.CustomStoragePath, currentCustomPath, StringComparison.OrdinalIgnoreCase)))
            return;

        var oldConfigRoot = _settings.ResolveConfigRoot();
        var newRoot = SettingsService.ResolveConnectionsRoot(result.StorageLocation, result.CustomStoragePath);
        var newScriptsRoot = SettingsService.ResolveScriptsRoot(result.StorageLocation, result.CustomStoragePath);
        var newConfigRoot = SettingsService.ResolveConfigRoot(result.StorageLocation, result.CustomStoragePath);

        var moveConfigData = ConfirmAsync is null
            || await ConfirmAsync(
                L("DialogMoveConfigTitle"),
                L("DialogMoveConfigMessage", oldConfigRoot, newConfigRoot));

        if (moveConfigData)
        {
            try
            {
                StopWatching();
                SettingsService.MoveConfigRoot(oldConfigRoot, newConfigRoot);
            }
            catch (Exception ex)
            {
                StatusMessage = L("StatusStorageCopyFailed", ex.Message);
                return;
            }
        }
        else
        {
            StopWatching();
        }

        _settings.Settings.StorageLocation = result.StorageLocation;
        _settings.Settings.CustomStoragePath = result.CustomStoragePath;
        var settingsSaved = _settings.SaveIfChanged();
        DebugInstanceContext.SetConfigRoot(_settings.ResolveConfigRoot());
        DebugMcpServer.RefreshDiscovery();
        _store.SetRoot(newRoot);
        _scriptStore.SetRoot(newScriptsRoot);
        ReloadScripts();
        StartWatchingCurrentStorage();
        ClearClipboard();
        ReloadTree();
        OnPropertyChanged(nameof(RootPath));
        OnPropertyChanged(nameof(TargetDescription));

        if (!settingsSaved)
            StatusMessage = L("StatusStorageNotSaved", _settings.SettingsPath);
        else if (HasData(newConfigRoot))
            StatusMessage = L("StatusStorageLocationWithData", result.StorageLocation, newConfigRoot);
        else
            StatusMessage = L("StatusStorageLocationOnly", result.StorageLocation, newConfigRoot);
    }

    /// <summary>
    /// Switches the master password by re-encrypting every stored secret on every
    /// connection — the login password, the private-key passphrase, and any
    /// script-binding secret parameters — each decrypted with the current master
    /// password and re-encrypted as a fresh jrm1 blob under <paramref name="newPassword"/>.
    /// If any secret on a connection cannot be decrypted, the whole change aborts
    /// before anything is written, so we never strand data. The new password
    /// replaces the cached one only after the sweep succeeds.
    /// </summary>
    public void ChangeMasterPassword(string newPassword)
    {
        try
        {
            FlushPendingAutoSave();

            var current = MasterKeyService.Current
                          ?? throw new InvalidOperationException("Master password not initialised.");

            var pending = new List<(
                string File,
                Connection Connection,
                string? ClearPassword,
                string? ClearPassphrase,
                List<(ConnectionScriptParameterValue Param, string Clear)> ScriptSecrets)>();
            var unreadable = 0;

            foreach (var file in _store.AllConnectionFiles())
            {
                try
                {
                    var c = _store.Load(file);

                    string? clearPassword = null;
                    string? clearPassphrase = null;
                    var scriptSecrets = new List<(ConnectionScriptParameterValue Param, string Clear)>();
                    var failed = false;

                    // Every master-password-encrypted secret on the connection must
                    // decrypt with the CURRENT master password; if any one can't, skip
                    // the whole connection so we never clobber it under a new password.
                    if (!string.IsNullOrEmpty(c.EncryptedPassword))
                    {
                        if (current.TryDecryptPassword(c.EncryptedPassword, out var clear))
                            clearPassword = clear;
                        else
                            failed = true;
                    }

                    if (!failed && !string.IsNullOrEmpty(c.EncryptedPrivateKeyPassphrase))
                    {
                        if (current.TryDecryptPassword(c.EncryptedPrivateKeyPassphrase, out var clearPp))
                            clearPassphrase = clearPp;
                        else
                            failed = true;
                    }

                    // Script-binding secret parameters are jrm1 blobs too (detected by
                    // prefix, no suite definition needed).
                    if (!failed)
                    {
                        foreach (var param in c.ScriptBindings.SelectMany(b => b.Params))
                        {
                            if (string.IsNullOrEmpty(param.Value) || !MasterKeyService.IsPasswordBlob(param.Value))
                                continue;
                            if (current.TryDecryptPassword(param.Value, out var clearSecret))
                                scriptSecrets.Add((param, clearSecret));
                            else
                            {
                                failed = true;
                                break;
                            }
                        }
                    }

                    if (failed)
                    {
                        unreadable++;
                        continue;
                    }

                    // Nothing encrypted on this connection -> nothing to migrate.
                    if (clearPassword is null && clearPassphrase is null && scriptSecrets.Count == 0)
                        continue;

                    pending.Add((file, c, clearPassword, clearPassphrase, scriptSecrets));
                }
                catch
                {
                    unreadable++;
                }
            }

            if (unreadable > 0)
                throw new InvalidOperationException(L("MasterChangeUnreadablePasswords", unreadable));

            _store.RunBatch(() =>
            {
                foreach (var item in pending)
                {
                    if (item.ClearPassword is not null)
                        item.Connection.EncryptedPassword =
                            MasterKeyService.EncryptWithPassword(newPassword, item.ClearPassword);
                    if (item.ClearPassphrase is not null)
                        item.Connection.EncryptedPrivateKeyPassphrase =
                            MasterKeyService.EncryptWithPassword(newPassword, item.ClearPassphrase);
                    foreach (var (param, clear) in item.ScriptSecrets)
                        param.Value = MasterKeyService.EncryptWithPassword(newPassword, clear);
                    _store.SaveInPlace(item.Connection, item.File);
                }
            });

            current.SetPassword(newPassword);
            StatusMessage = L("StatusMasterChanged");
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusMasterChangeFailed", ex.Message);
        }
    }

    private static bool HasData(string folder) =>
        Directory.Exists(folder) &&
        (Directory.GetFiles(folder, "*" + ConnectionStore.FileExtension).Length > 0 ||
         Directory.GetDirectories(folder).Length > 0);
}
