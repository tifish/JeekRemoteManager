using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Jeek.Avalonia.Localization;
using JeekRemoteManager.Services;

namespace JeekRemoteManager.Views;

internal sealed class BackgroundPortForwardWindow : Window
{
    internal sealed record ConnectionChoice(string Id, string Path, string Host)
    {
        public override string ToString() => $"{Path} ({Host})";
    }

    private readonly BackgroundPortForwards _forwards;
    private readonly Func<string, string, int, int, SshForwardTransport.Plan> _prepare;
    private readonly ComboBox _connection = new() { Name = "BackgroundForwardConnection", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _localPort = new() { Name = "BackgroundForwardLocalPort" };
    private readonly TextBox _remotePort = new() { Name = "BackgroundForwardRemotePort" };
    private readonly ListBox _list = new() { Name = "BackgroundForwardList", Padding = new Thickness(0) };
    private readonly TextBlock _empty = new() { Classes = { "hint" }, Margin = new Thickness(14) };
    private readonly TextBlock _message = new() { Name = "BackgroundForwardMessage", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _open = Button("BackgroundForwardOpen");
    private readonly Button _resume = Button("BackgroundForwardResume");
    private readonly Button _stop = Button("BackgroundForwardStop");
    private readonly Button _copy = Button("BackgroundForwardCopy");
    private BackgroundPortForwards.Overview[] _rows = [];
    private string? _selectOnRefresh;
    private bool _refreshing;
    private bool _opening;
    private bool _busy;
    private bool _closed;

    internal BackgroundPortForwardWindow(BackgroundPortForwards forwards, Func<IReadOnlyList<ConnectionChoice>> choices,
        Func<string, string, int, int, SshForwardTransport.Plan> prepare, string? selectedConnectionId = null)
    {
        _forwards = forwards;
        _prepare = prepare;
        Title = Localizer.Get("BackgroundForwardManager");
        Width = 850;
        Height = 490;
        MinWidth = 675;
        MinHeight = 330;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var connectionLabel = Localizer.Get("Connection");
        var localPortLabel = Localizer.Get("BackgroundForwardLocalPortLabel");
        var remotePortLabel = Localizer.Get("BackgroundForwardRemotePortLabel");
        AutomationProperties.SetName(_connection, connectionLabel);
        AutomationProperties.SetName(_localPort, localPortLabel);
        AutomationProperties.SetName(_remotePort, remotePortLabel);
        _connection.PlaceholderText = Localizer.Get("BackgroundForwardChooseConnection");
        _localPort.PlaceholderText = Localizer.Get("BackgroundForwardAutomaticPort");
        _remotePort.PlaceholderText = "1–65535";
        _open.Classes.Add("accent");
        _empty.Text = Localizer.Get("BackgroundForwardEmpty");
        _list.ItemTemplate = new FuncDataTemplate<BackgroundPortForwards.Overview>((row, _) =>
        {
            if (row is null)
                return null;
            var target = row.Target;
            var localPort = row.LocalPort > 0 ? row.LocalPort : target.RequestedLocalPort;
            return Columns(
                new StackPanel
                {
                    Children =
                    {
                        Text(string.IsNullOrEmpty(target.TreePath) ? target.Host : target.TreePath),
                        Text(target.Host, "hint"),
                    },
                },
                Text(localPort > 0 ? Endpoint(localPort) : "—", row.LocalPort == 0 ? "hint" : null),
                Text(Endpoint(target.RemotePort)),
                new StackPanel
                {
                    Children =
                    {
                        Text(Localizer.Get(StateLabel(row))),
                        Text(Localizer.Get(row.IsManual ? "BackgroundForwardManual" : "BackgroundForwardMcp"), "hint"),
                    },
                });
        });
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) },
        });

        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("*,135,160,Auto"), ColumnSpacing = 10 };
        form.Children.Add(new StackPanel { Spacing = 4, Children = { Text(connectionLabel, "label"), _connection } });
        var localPortInput = new StackPanel { Spacing = 4, Children = { Text(localPortLabel, "label"), _localPort } };
        Grid.SetColumn(localPortInput, 1);
        form.Children.Add(localPortInput);
        var remotePortInput = new StackPanel { Spacing = 4, Children = { Text(remotePortLabel, "label"), _remotePort } };
        Grid.SetColumn(remotePortInput, 2);
        form.Children.Add(remotePortInput);
        Grid.SetColumn(_open, 3);
        _open.VerticalAlignment = VerticalAlignment.Bottom;
        form.Children.Add(_open);

        var headers = Columns(Text(connectionLabel, "label"), Text(Localizer.Get("BackgroundForwardLocalAddress"), "label"),
            Text(Localizer.Get("BackgroundForwardRemoteAddress"), "label"), Text(Localizer.Get("BackgroundForwardStatus"), "label"));
        headers.Margin = new Thickness(12, 8, 12, 0);
        var table = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 4, Children = { headers } };
        var listArea = new Grid { Children = { _list, _empty } };
        Grid.SetRow(listArea, 1);
        table.Children.Add(listArea);

        var close = Button("Close");
        close.IsCancel = true;
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Children = { _resume, _stop, _copy },
        };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { actions } };
        Grid.SetColumn(close, 1);
        footer.Children.Add(close);
        var layout = new Grid
        {
            Margin = new Thickness(18), RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 8,
            Children = { form, table, _message, footer },
        };
        Grid.SetRow(table, 1);
        Grid.SetRow(_message, 2);
        Grid.SetRow(footer, 3);
        Content = layout;

        void LoadChoices(string? id)
        {
            var available = choices();
            _connection.ItemsSource = available;
            _connection.SelectedItem = available.FirstOrDefault(choice => choice.Id == id)
                ?? (available.Count == 1 ? available[0] : null);
        }

        _connection.DropDownOpened += (_, _) => LoadChoices((_connection.SelectedItem as ConnectionChoice)?.Id);
        _connection.SelectionChanged += (_, _) => UpdateActions();
        _list.SelectionChanged += (_, _) => UpdateActions();
        _open.Click += async (_, _) =>
        {
            try { await OpenAsync(); }
            catch { ShowMessage("BackgroundForwardRouteUnavailable"); }
        };
        _resume.Click += async (_, _) =>
        {
            if (_busy || _list.SelectedItem is not BackgroundPortForwards.Overview { CanStart: true } row)
                return;
            _busy = true;
            UpdateActions();
            try
            {
                await StartAsync(row.Target.ConnectionId, row.Target.Host, row.Target.RemotePort,
                    row.Target.RequestedLocalPort, row.Id);
            }
            catch
            {
                ShowMessage("BackgroundForwardRouteUnavailable");
            }
            finally
            {
                _busy = false;
                UpdateActions();
            }
        };
        _stop.Click += async (_, _) =>
        {
            if (_busy || _list.SelectedItem is not BackgroundPortForwards.Overview { CanStop: true } row)
                return;
            _busy = true;
            UpdateActions();
            try
            {
                ShowMessage(null);
                await Task.Run(() => _forwards.StopFromManager(row.Id));
                await RefreshAsync();
            }
            catch
            {
                ShowMessage("BackgroundForwardFailed");
            }
            finally
            {
                _busy = false;
                UpdateActions();
            }
        };
        _copy.Click += async (_, _) =>
        {
            if (_list.SelectedItem is not BackgroundPortForwards.Overview { LocalPort: > 0 } row || Clipboard is null)
                return;
            try
            {
                await Clipboard.SetTextAsync(Endpoint(row.LocalPort));
                ShowMessage("BackgroundForwardCopied");
            }
            catch
            {
                ShowMessage("BackgroundForwardCopyFailed");
            }
        };
        close.Click += (_, _) => Close();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += async (_, _) =>
        {
            try { await RefreshAsync(); }
            catch { /* best-effort periodic refresh */ }
        };
        Opened += async (_, _) =>
        {
            timer.Start();
            try { await RefreshAsync(); }
            catch { /* best-effort initial refresh */ }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            timer.Stop();
        };
        LoadChoices(selectedConnectionId);
        UpdateActions();
    }

    private async Task OpenAsync()
    {
        if (_opening || _connection.SelectedItem is not ConnectionChoice choice)
            return;
        var localPort = 0;
        if (!string.IsNullOrWhiteSpace(_localPort.Text) && !TryParsePort(_localPort.Text, out localPort))
        {
            ShowMessage("BackgroundForwardInvalidLocalPort");
            _localPort.Focus();
            return;
        }
        if (!TryParsePort(_remotePort.Text, out var remotePort))
        {
            ShowMessage("BackgroundForwardInvalidPort");
            _remotePort.Focus();
            return;
        }
        _opening = true;
        UpdateActions();
        try
        {
            await StartAsync(choice.Id, choice.Host, remotePort, localPort);
        }
        finally
        {
            _opening = false;
            UpdateActions();
        }
    }

    private static bool TryParsePort(string? text, out int port) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

    private async Task StartAsync(string connectionId, string host, int remotePort, int localPort, string? id = null)
    {
        ShowMessage(null);
        try
        {
            var report = await Task.Run(() =>
            {
                var plan = _prepare(connectionId, host, remotePort, localPort);
                return _forwards.StartFromManagerAsync(plan.Target, plan.Dial, id);
            });
            if (id is null)
                _selectOnRefresh = report["forward_id"]!.GetValue<string>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            ShowMessage("BackgroundForwardRouteUnavailable");
        }
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_closed || _refreshing)
            return;
        _refreshing = true;
        try
        {
            var rows = await Task.Run(_forwards.List);
            if (_closed)
                return;
            var selectedId = _selectOnRefresh ?? (_list.SelectedItem as BackgroundPortForwards.Overview)?.Id;
            if (!_rows.SequenceEqual(rows))
            {
                _rows = rows;
                _list.ItemsSource = rows;
                _empty.IsVisible = rows.Length == 0;
            }
            if (selectedId != (_list.SelectedItem as BackgroundPortForwards.Overview)?.Id)
                _list.SelectedItem = _rows.FirstOrDefault(row => row.Id == selectedId);
            _selectOnRefresh = null;
            UpdateActions();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void UpdateActions()
    {
        var selected = _list.SelectedItem as BackgroundPortForwards.Overview;
        _open.IsEnabled = !_opening && !_busy && _connection.SelectedItem is ConnectionChoice;
        _resume.IsEnabled = !_busy && selected?.CanStart == true;
        _stop.IsEnabled = !_busy && selected?.CanStop == true;
        _copy.IsEnabled = selected?.LocalPort > 0;
    }

    private void ShowMessage(string? key)
    {
        _message.Text = key is null ? null : Localizer.Get(key);
        _message.IsVisible = key is not null;
    }

    private static string StateLabel(BackgroundPortForwards.Overview row) => row.Error switch
    {
        "host_key_requires_user" => "BackgroundForwardTrustRequired",
        "complete_authentication_in_jrm" => "BackgroundForwardAuthRequired",
        "lease_expired" => "BackgroundForwardExpired",
        "ssh_or_listener_disconnected" => "BackgroundForwardDisconnected",
        "local_port_unavailable" => "BackgroundForwardLocalPortUnavailable",
        _ => row.State switch
        {
            "ready" => "BackgroundForwardReady",
            "closed" => "BackgroundForwardClosed",
            "failed" => "BackgroundForwardFailed",
            _ => "BackgroundForwardConnecting",
        },
    };

    private static string Endpoint(int port) => $"127.0.0.1:{port}";

    private static Grid Columns(Control connection, Control local, Control remote, Control state)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,132,132,188"),
            ColumnSpacing = 8,
            Children = { connection, local, remote, state },
        };
        Grid.SetColumn(local, 1);
        Grid.SetColumn(remote, 2);
        Grid.SetColumn(state, 3);
        return grid;
    }

    private static TextBlock Text(string content, string? style = null)
    {
        var text = new TextBlock { Text = content, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        if (style is not null)
            text.Classes.Add(style);
        ToolTip.SetTip(text, content);
        return text;
    }

    private static Button Button(string key) => new() { Name = key, Content = Localizer.Get(key) };
}
