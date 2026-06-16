using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Jeek.Avalonia.Localization;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;
using JeekRemoteManager.Views;

namespace JeekRemoteManager;

public partial class App : Application
{
    private bool _exitRequested;
    private MainWindowViewModel? _vm;

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
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit.
            DisableAvaloniaDataAnnotationValidation();

            // Closing the main window only hides it to the tray; exit happens
            // via the tray's Exit menu.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var settings = new SettingsService();
            var store = new ConnectionStore();
            var launcher = new ConnectionLauncher();
            var master = new MasterKeyService(settings);
            MasterKeyService.Current = master;
            store.SetRoot(settings.ResolveConnectionsRoot());

            ApplyStoredLanguage(settings);
            ApplyStoredTheme(settings);

            // Gate the main window behind the master-password setup/unlock flow.
            _ = UnlockThenStartAsync(desktop, settings, store, launcher, master);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Ensures the master key is unlocked (set up on first run, unlocked silently
    /// from the local cache, or prompted for), then builds and shows the main window.
    /// Exits the app if the user cancels the setup/unlock prompt.
    /// </summary>
    private async Task UnlockThenStartAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        SettingsService settings,
        ConnectionStore store,
        ConnectionLauncher launcher,
        MasterKeyService master)
    {
        bool unlocked;
        if (master.IsConfigured)
        {
            // Silent cache unlock on the same machine; otherwise ask once.
            unlocked = master.TryUnlockFromCache()
                || await MasterPasswordDialog.ShowUnlockAsync(null, master.TryUnlock);
        }
        else
        {
            var password = await MasterPasswordDialog.ShowSetupAsync(null);
            if (password is null)
            {
                unlocked = false;
            }
            else
            {
                master.Initialize(password);
                MigrateLegacyPasswords(store);
                settings.Save();
                unlocked = true;
            }
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

        // Rebuild the tray menu on language change so its localized labels
        // stay current.
        Localizer.LanguageChanged += (_, _) => BuildTrayMenu();

        // Silent background check shortly after startup.
        _ = vm.CheckForUpdatesOnStartupAsync();
    }

    /// <summary>
    /// One-time migration when a master password is first adopted: re-encrypts every
    /// connection password that was previously stored with DPAPI so it is protected
    /// by the new master key instead. Passwords that cannot be decrypted on this
    /// machine (e.g. copied from another account) are left untouched.
    /// </summary>
    private static void MigrateLegacyPasswords(ConnectionStore store)
    {
        foreach (var file in store.AllConnectionFiles())
        {
            try
            {
                var connection = store.Load(file);
                if (string.IsNullOrEmpty(connection.EncryptedPassword))
                    continue;

                var clear = PasswordProtector.DpapiDecryptLegacy(connection.EncryptedPassword);
                if (clear.Length == 0)
                    continue;

                connection.EncryptedPassword = PasswordProtector.Encrypt(clear);
                store.SaveInPlace(connection, file);
            }
            catch
            {
                // Skip any single file that fails; the rest still migrate.
            }
        }
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

    private void OnTrayExitClicked(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _exitRequested = true;
            desktop.Shutdown();
        }
    }

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
    /// Builds the tray icon's right-click menu so it mirrors the main window's
    /// overflow (⋮) menu: Settings, Import and Check for Updates, bracketed by
    /// Show/Exit. Rebuilt on language changes so the labels stay current.
    /// </summary>
    private void BuildTrayMenu()
    {
        if (_vm is not { } vm)
            return;

        var icon = TrayIcon.GetIcons(this)?.FirstOrDefault();
        if (icon == null)
            return;

        icon.ToolTipText = Localizer.Get("WindowTitle");

        var menu = new NativeMenu();

        var show = new NativeMenuItem { Header = Localizer.Get("TrayShow") };
        show.Click += OnTrayShowClicked;
        menu.Items.Add(show);

        menu.Items.Add(new NativeMenuItemSeparator());

        menu.Items.Add(CommandItem(Localizer.Get("Settings"), vm.OpenSettingsCommand));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(CommandItem(Localizer.Get("ImportFromFinalShell"), vm.ImportFinalShellCommand));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(CommandItem(Localizer.Get("CheckForUpdates"), vm.CheckForUpdatesCommand));

        menu.Items.Add(new NativeMenuItemSeparator());

        var exit = new NativeMenuItem { Header = Localizer.Get("TrayExit") };
        exit.Click += OnTrayExitClicked;
        menu.Items.Add(exit);

        icon.Menu = menu;
    }

    private static NativeMenuItem CommandItem(string header, System.Windows.Input.ICommand command)
    {
        var item = new NativeMenuItem { Header = header };
        item.Click += (_, _) =>
        {
            if (command.CanExecute(null))
                command.Execute(null);
        };
        return item;
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

    private static void DisableAvaloniaDataAnnotationValidation()
    {
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
