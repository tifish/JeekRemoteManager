using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit.
            DisableAvaloniaDataAnnotationValidation();

            var settings = new SettingsService();
            var store = new ConnectionStore();
            var launcher = new ConnectionLauncher();

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

    private static void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
