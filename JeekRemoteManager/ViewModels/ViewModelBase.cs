using CommunityToolkit.Mvvm.ComponentModel;
using Jeek.Avalonia.Localization;

namespace JeekRemoteManager.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    /// <summary>Short alias for <see cref="Localizer.Get"/>.</summary>
    protected static string L(string key) => Localizer.Get(key);

    /// <summary>Localized format-string helper.</summary>
    protected static string L(string key, params object?[] args) =>
        string.Format(Localizer.Get(key), args);
}
