using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Jeek.Avalonia.Localization;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;
using JeekRemoteManager.Views;

namespace JeekRemoteManager;

public partial class App : Application
{
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

            var settings = new SettingsService();
            var store = new ConnectionStore();
            var launcher = new ConnectionLauncher();

            ApplyStoredLanguage(settings);

            var vm = new MainWindowViewModel(store, launcher, settings);
            desktop.MainWindow = new MainWindow
            {
                DataContext = vm,
            };

            // Silent background check shortly after startup.
            _ = vm.CheckForUpdatesOnStartupAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ApplyStoredLanguage(SettingsService settings)
    {
        var language = settings.Settings.Language;
        if (string.IsNullOrEmpty(language))
            return;

        if (Localizer.Languages.Contains(language))
            Localizer.Language = language;
    }

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
