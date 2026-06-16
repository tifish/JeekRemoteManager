using System;
using System.IO;
using System.Linq;
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

            ApplyStoredLanguage(settings);
            ApplyStoredTheme(settings);

            var vm = new MainWindowViewModel(store, launcher, settings);
            var window = new MainWindow
            {
                DataContext = vm,
            };
            window.Closing += OnMainWindowClosing;
            desktop.MainWindow = window;

            LocalizeTrayMenu();

            // Silent background check shortly after startup.
            _ = vm.CheckForUpdatesOnStartupAsync();
        }

        base.OnFrameworkInitializationCompleted();
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

    private void LocalizeTrayMenu()
    {
        var icons = TrayIcon.GetIcons(this);
        if (icons == null)
            return;

        foreach (var icon in icons)
        {
            icon.ToolTipText = Localizer.Get("WindowTitle");
            if (icon.Menu is not { } menu)
                continue;

            foreach (var item in menu.Items)
            {
                if (item is not NativeMenuItem nmi)
                    continue;

                nmi.Header = nmi.Header switch
                {
                    "Show" => Localizer.Get("TrayShow"),
                    "Exit" => Localizer.Get("TrayExit"),
                    _ => nmi.Header,
                };
            }
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
