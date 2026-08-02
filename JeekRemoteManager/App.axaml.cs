using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Jeek.Avalonia.Localization;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;
using JeekRemoteManager.Views;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekRemoteManager;

public partial class App : Application
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(App));
    private bool _exitRequested;
    private MainWindowViewModel? _vm;

    /// <summary>Current worktree/process identity, exposed for Debug MCP and SmokeTest.</summary>
    public DebugInstanceInfo DebugInstanceInfo => DebugInstanceContext.Info;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Localizer.SetLocalizer(
            new TabLocalizer(Path.Combine(AppContext.BaseDirectory, "Data", "Languages.tab"))
        );

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                McpAdapterRegistration.RegisterCurrentInstance();
            }
            catch (Exception ex)
            {
                // The GUI remains usable, but generated agent configs will report the missing
                // fixed adapter through the Debug MCP verification tool.
                Log.ZLogWarning(ex, $"Could not register the fixed MCP adapter");
            }

            // Closing the main window only hides it to the tray; exit happens
            // via the tray's Exit menu.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) => FlushSettingsState();
            if (desktop is IActivatableLifetime activatable)
                activatable.Deactivated += (_, _) => FlushSettingsState();
            // Best-effort last save when the process is about to die on an
            // unhandled exception, so recent UI-state changes are not lost.
            AppDomain.CurrentDomain.UnhandledException += (_, _) =>
            {
                try
                {
                    FlushSettingsState();
                }
                catch
                {
                    // The process is already crashing; never mask the original error.
                }
            };

            var settings = new SettingsService();
            DebugInstanceContext.SetConfigRoot(settings.ResolveConfigRoot());
            var store = new ConnectionStore();
            var launcher = new ConnectionLauncher();
            var master = new MasterKeyService();
            MasterKeyService.Current = master;
            store.SetRoot(settings.ResolveConnectionsRoot());

            ApplyStoredLanguage(settings);
            ApplyStoredTheme(settings);

            // No-op in Release builds: the server only listens in Debug.
            DebugMcpServer.Start();
            desktop.Exit += (_, _) => DebugMcpServer.Stop();

            // Product surface (all builds), on its own named pipe. Agents reach it through the
            // fixed per-user JeekRemoteManagerMcp.exe; tools that need the window report
            // awaiting_user until it is up.
            ProductMcpServer.Start();
            desktop.Exit += (_, _) => ProductMcpServer.Stop();

            // Gate the main window behind the master-password setup/unlock flow.
            _ = StartupAsync(desktop, settings, store, launcher, master);
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Fire-and-forget wrapper: a failure inside the startup sequence used to be
    // swallowed silently, leaving the process alive with no window. Log it and
    // exit instead.
    private async Task StartupAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        SettingsService settings,
        ConnectionStore store,
        ConnectionLauncher launcher,
        MasterKeyService master)
    {
        try
        {
            await UnlockThenStartAsync(desktop, settings, store, launcher, master);
        }
        catch (Exception ex)
        {
            Log.ZLogCritical(ex, $"Startup failed");
            desktop.Shutdown(1);
        }
    }

    /// <summary>
    /// Ensures the master password is unlocked, then builds and shows the main
    /// window. Tries a silent unlock from the local DPAPI cache first; otherwise
    /// asks for the master password (always with confirmation). When saved
    /// encrypted passwords exist, the entered password must decrypt at least one
    /// jrm1 blob. Exits the app if the user cancels.
    /// </summary>
    private async Task UnlockThenStartAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        SettingsService settings,
        ConnectionStore store,
        ConnectionLauncher launcher,
        MasterKeyService master)
    {
        var unlocked = master.TryUnlockFromCache();
        if (unlocked && !UnlockedKeyCanReadStoredPasswords(store, master))
        {
            master.Lock();
            MasterKeyService.ClearCache();
            unlocked = false;
        }

        if (!unlocked)
        {
            // The dialog has a single mode (password + confirmation); the title
            // is just a hint. We call it "unlock" when connection files already
            // exist, "setup" otherwise.
            var firstRun = store.AllConnectionFiles().Count == 0;
            var (title, prompt) = firstRun
                ? (Localizer.Get("MasterSetupTitle"), Localizer.Get("MasterSetupPrompt"))
                : (Localizer.Get("MasterUnlockTitle"), Localizer.Get("MasterUnlockPrompt"));

            unlocked = await MasterPasswordDialog.ShowAsync(null, title, prompt, password =>
            {
                if (!PasswordCanReadStoredPasswords(store, password))
                    return false;

                master.SetPassword(password);
                return true;
            });
        }

        if (!unlocked)
        {
            desktop.Shutdown();
            return;
        }

        var vm = new MainWindowViewModel(store, launcher, settings);
        var window = new MainWindow
        {
            DataContext = vm,
        };
        window.Closing += OnMainWindowClosing;
        desktop.MainWindow = window;
        window.Show();

        _vm = vm;
        BuildTrayMenu();
        Program.ActivationRequested += ShowMainWindow;

        // Rebuild the tray menu on language change so its localized labels
        // stay current.
        Localizer.LanguageChanged += (_, _) => BuildTrayMenu();

        // Silent startup check + periodic re-check (both gated by user settings).
        _ = vm.RunBackgroundUpdateChecksAsync();
    }

    private static bool UnlockedKeyCanReadStoredPasswords(ConnectionStore store, MasterKeyService master) =>
        CanReadStoredPasswords(store, blob => master.TryDecryptPassword(blob, out _));

    private static bool PasswordCanReadStoredPasswords(ConnectionStore store, string password) =>
        CanReadStoredPasswords(store, blob => MasterKeyService.DecryptWithPassword(password, blob) is not null);

    /// <summary>
    /// True when <paramref name="canDecrypt"/> reads at least one stored password, or
    /// when nothing is encrypted at all. Stops at the first success, so the usual case
    /// costs a single key derivation.
    /// </summary>
    private static bool CanReadStoredPasswords(ConnectionStore store, Func<string, bool> canDecrypt)
    {
        var sawEncrypted = false;
        foreach (var blob in store.EnumerateStoredPasswordBlobs())
        {
            sawEncrypted = true;
            if (canDecrypt(blob))
                return true;
        }

        return !sawEncrypted;
    }


    private void OnMainWindowClosing(object? sender, Avalonia.Controls.WindowClosingEventArgs e)
    {
        if (_exitRequested || sender is not Window window)
            return;

        e.Cancel = true;
        window.Hide();
    }

    private void OnTrayIconClicked(object? sender, EventArgs e) => ToggleMainWindow();

    private void OnTrayShowClicked(object? sender, EventArgs e) => ShowMainWindow();

    public void RequestExit()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _exitRequested = true;
            FlushSettingsState();
            desktop.Shutdown();
        }
    }

    private void FlushSettingsState()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is MainWindow window)
        {
            window.FlushCurrentSettingsState();
            return;
        }

        _vm?.SaveLastSelectedConnection();
        _vm?.FlushSettings();
    }

    /// <summary>Localized tray labels exposed for Debug MCP verification.</summary>
    public IReadOnlyList<string> TrayMenuHeaders =>
        TrayIcon.GetIcons(this)?.FirstOrDefault()?.Menu?.Items
        .OfType<NativeMenuItem>()
        .Where(item => item is not NativeMenuItemSeparator)
        .Select(item => item.Header ?? string.Empty)
        .ToArray() ?? [];

    private void ShowMainWindow()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (desktop.MainWindow is not Window window)
                return;

            window.Show();
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Activate();
        });
    }

    private void ToggleMainWindow()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (desktop.MainWindow is not Window window)
                return;

            if (window.IsVisible && window.WindowState != WindowState.Minimized)
            {
                window.Hide();
            }
            else
            {
                window.Show();
                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;
                window.Activate();
            }
        });
    }

    /// <summary>
    /// Builds the tray icon's right-click menu from the same shared action list
    /// as the main window, with a tray-specific Show item prepended. Rebuilt on
    /// language changes so the labels stay current.
    /// </summary>
    private void BuildTrayMenu()
    {
        if (_vm is null)
            return;

        var icon = TrayIcon.GetIcons(this)?.FirstOrDefault();
        if (icon == null)
            return;

        icon.ToolTipText = DebugInstanceContext.DecorateTitle(Localizer.Get("WindowTitle"));

        var menu = new NativeMenu();

        var show = new NativeMenuItem { Header = Localizer.Get("TrayShow") };
        show.Click += OnTrayShowClicked;
        menu.Items.Add(show);

        foreach (var entry in ApplicationMenuDefinition.Items)
        {
            menu.Items.Add(new NativeMenuItemSeparator());

            var action = entry.Action;
            var item = new NativeMenuItem { Header = Localizer.Get(entry.LocalizationKey) };
            item.Click += (_, _) => InvokeSharedMenuAction(action);
            menu.Items.Add(item);
        }

        icon.Menu = menu;
    }

    /// <summary>
    /// Invokes a shared application-menu action from the tray. Surfaces the main
    /// window first for dialog-owned actions (ShowDialog hangs on a hidden owner).
    /// Exit does not need the window and runs immediately.
    /// </summary>
    private void InvokeSharedMenuAction(ApplicationMenuAction action)
    {
        if (action == ApplicationMenuAction.Exit)
        {
            RequestExit();
            return;
        }

        // Tray-invoked commands open dialogs owned by the main window;
        // ShowDialog over a hidden owner hangs, so surface the window first.
        ShowMainWindow();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is MainWindow window)
        {
            window.ExecuteApplicationMenuAction(action);
        }
    }

    private static void ApplyStoredLanguage(SettingsService settings)
    {
        var language = settings.Settings.Language;
        if (string.IsNullOrEmpty(language))
            return;

        if (Localizer.Languages.Contains(language))
            Localizer.Language = language;
    }

    private void ApplyStoredTheme(SettingsService settings)
    {
        RequestedThemeVariant = ThemeVariantFor(settings.Settings.Theme);
    }

    /// <summary>Maps a stored theme string to an Avalonia <see cref="ThemeVariant"/>.
    /// Null / empty / unrecognized values fall back to <see cref="ThemeVariant.Default"/>
    /// (follow system).</summary>
    public static ThemeVariant ThemeVariantFor(string? theme) => theme switch
    {
        "Light" => ThemeVariant.Light,
        "Dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

}
