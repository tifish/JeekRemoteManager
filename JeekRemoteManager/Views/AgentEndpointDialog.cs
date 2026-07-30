using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Jeek.Avalonia.Localization;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;

namespace JeekRemoteManager.Views;

/// <summary>
/// Edits one saved API endpoint, so Claude or Codex can run against an Anthropic- or
/// OpenAI-compatible gateway instead of the vendor's own API.
///
/// The key is masked and write-only from the dialog's point of view: an existing key is never
/// shown back, only reported as set, and leaving the box empty keeps whatever is stored. That
/// way opening the dialog to change the URL cannot accidentally reveal or clear the secret.
/// </summary>
public static class AgentEndpointDialog
{
    private const char Mask = '●';

    /// <summary>
    /// Shows the editor for <paramref name="endpoint"/>. Returns true when the user accepted,
    /// with the changes already written into the object; the caller persists them.
    /// </summary>
    public static Task<bool> ShowAsync(Window? owner, string agentLabel, AgentEndpointProfile endpoint)
    {
        var tcs = new TaskCompletionSource<bool>();

        var name = new TextBox
        {
            Text = endpoint.Name,
            PlaceholderText = Localizer.Get("AiEndpointNamePlaceholder"),
        };
        var baseUrl = new TextBox
        {
            Text = endpoint.BaseUrl,
            PlaceholderText = "https://example.com/api",
        };
        var hasKey = endpoint.EncryptedApiKey.Length > 0;
        var apiKey = new TextBox
        {
            PasswordChar = Mask,
            PlaceholderText = Localizer.Get(hasKey ? "AiEndpointKeyKeep" : "AiEndpointKeyEmpty"),
        };
        var reveal = new CheckBox { Content = Localizer.Get("MasterShowPassword") };
        reveal.IsCheckedChanged += (_, _) => apiKey.RevealPassword = reveal.IsChecked == true;

        var clearKey = new Button { Content = Localizer.Get("AiEndpointKeyClear"), IsEnabled = hasKey };
        var keyCleared = false;
        clearKey.Click += (_, _) =>
        {
            keyCleared = true;
            apiKey.Text = "";
            clearKey.IsEnabled = false;
            apiKey.PlaceholderText = Localizer.Get("AiEndpointKeyEmpty");
        };

        var model = new TextBox
        {
            Text = endpoint.Model,
            PlaceholderText = Localizer.Get("AiEndpointModelDefault"),
        };

        var error = new TextBlock
        {
            Foreground = Brushes.IndianRed,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        var ok = new Button { Content = Localizer.Get("DialogOk"), MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = Localizer.Get("DialogCancel"), MinWidth = 80, IsCancel = true };

        var dialog = new Window
        {
            Title = string.Format(Localizer.Get("AiEndpointTitle"), agentLabel),
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = Localizer.Get("AiEndpointHint"),
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.8,
                    },
                    new TextBlock { Text = Localizer.Get("AiEndpointName") },
                    name,
                    new TextBlock { Text = Localizer.Get("AiEndpointBaseUrl") },
                    baseUrl,
                    new TextBlock { Text = Localizer.Get("AiEndpointKey") },
                    apiKey,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 12,
                        Children = { reveal, clearKey },
                    },
                    new TextBlock { Text = Localizer.Get("AiEndpointModel") },
                    model,
                    error,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { ok, cancel },
                    },
                },
            },
        };

        ok.Click += (_, _) =>
        {
            var url = (baseUrl.Text ?? "").Trim();
            if (url.Length == 0)
            {
                error.Text = Localizer.Get("AiEndpointBaseUrlRequired");
                error.IsVisible = true;
                return;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                error.Text = Localizer.Get("AiEndpointBaseUrlInvalid");
                error.IsVisible = true;
                return;
            }

            // An unnamed endpoint would show as a bare URL in the picker; the host falls back
            // to that, so a blank name is allowed rather than blocking the save.
            endpoint.Name = (name.Text ?? "").Trim();
            endpoint.BaseUrl = url;
            endpoint.Model = (model.Text ?? "").Trim();

            // Empty box keeps the stored key, unless the user explicitly cleared it.
            var typed = apiKey.Text ?? "";
            if (typed.Length > 0)
                endpoint.EncryptedApiKey = PasswordProtector.Encrypt(typed);
            else if (keyCleared)
                endpoint.EncryptedApiKey = "";

            tcs.TrySetResult(true);
            dialog.Close();
        };

        cancel.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);
        dialog.Opened += (_, _) => name.Focus();

        if (owner is null)
            dialog.Show();
        else
            dialog.ShowDialog(owner);
        return tcs.Task;
    }
}
