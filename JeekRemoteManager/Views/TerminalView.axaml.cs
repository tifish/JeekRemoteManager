using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;
using Renci.SshNet;
using SvcSystems.UI.Terminal;

namespace JeekRemoteManager.Views;

/// <summary>
/// Hosts a native Avalonia terminal control (SvcSystems.UI.Terminal) driven by an
/// SSH.NET interactive shell. Self-contained and reusable: a window can host one for
/// the Phase A spike, and the right-pane tab UI (Phase B) hosts one per tab.
/// Authentication is programmatic via <see cref="SshConnectionFactory"/> (the user
/// never types a password); live bytes, keyboard, title, and window size are wired
/// to the SSH channel.
/// </summary>
public partial class TerminalView : UserControl
{
    private readonly TerminalControlModel _model = new(new TerminalOptions
    {
        Cols = 120,
        Rows = 30,
        // Disable resize reflow so full-screen TUIs (top, vim, mc) stay stable.
        ReflowOnResize = false,
    });

    private Connection? _connection;
    private SshClient? _client;
    private ShellStream? _shell;
    private volatile bool _disposed;

    /// <summary>Raised on the UI thread when the remote shell sets its title (OSC).</summary>
    public event EventHandler<string>? TitleChanged;

    public TerminalView()
    {
        InitializeComponent();

        Term.Model = _model;

        _model.UserInput += (_, e) => SendToShell(e.Data);
        _model.SizeChanged += (_, _) => SyncWindowSize();
        _model.Terminal.TitleChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            var title = _model.Terminal.Title;
            if (!string.IsNullOrWhiteSpace(title))
                TitleChanged?.Invoke(this, title);
        });
    }

    /// <summary>Connects the given connection and starts streaming. Call once.</summary>
    public void Start(Connection connection)
    {
        _connection = connection;
        FocusTerminal();
        _ = ConnectAsync();
    }

    /// <summary>
    /// Moves keyboard focus into the inner terminal control. Posted on the UI
    /// thread so it runs after the view is (re)attached on a tab switch — focusing
    /// the UserControl itself would not forward focus to the terminal.
    /// </summary>
    public void FocusTerminal() =>
        Dispatcher.UIThread.Post(() => Term.Focus(), DispatcherPriority.Background);

    /// <summary>Sets the terminal font size in points.</summary>
    public void SetFontSize(double size)
    {
        Term.FontSize = size;
        // A font size change resizes the character cell, so the column/row geometry
        // changes too. Push the new size to the remote after the control re-measures
        // rather than relying on it raising SizeChanged for a font-only change.
        // SyncWindowSize no-ops while _shell is null (e.g. the pre-Start call).
        Dispatcher.UIThread.Post(SyncWindowSize, DispatcherPriority.Background);
    }

    private async Task ConnectAsync()
    {
        var connection = _connection!;
        var host = connection.Host.Trim();
        var port = connection.Port > 0 ? connection.Port : 22;
        FeedLine($"Connecting to {host}:{port} ...");

        SshClient client;
        try
        {
            // Build (which may query ssh-agent / Pageant over IPC) and Connect both
            // run on a background thread — those calls can block, and on the UI thread
            // would freeze the whole window.
            client = await Task.Run(() =>
            {
                var sshClient = new SshClient(SshConnectionFactory.Build(connection));
                SshHostKey.Attach(sshClient, host, port,
                onUnknown: (keyType, fingerprint) => HostKeyDialog.PromptTrust(host, port, keyType, fingerprint),
                onRejected: message => Dispatcher.UIThread.Post(() => FeedLine($"\r\n[31m[{message}][0m\r\n")));
                sshClient.Connect();
                return sshClient;
            });
        }
        catch (Exception ex)
        {
            FeedLine($"[31m[connect failed] {ex.Message}[0m\r\n");
            return;
        }

        if (_disposed)
        {
            try { client.Dispose(); } catch { /* ignore */ }
            return;
        }

        _client = client;

        var cols = (uint)Math.Max(20, _model.Terminal.Cols);
        var rows = (uint)Math.Max(5, _model.Terminal.Rows);
        var shell = client.CreateShellStream("xterm-256color", cols, rows, 0, 0, 4096);
        _shell = shell;

        shell.DataReceived += (_, e) => OnShellData(e.Data);
        shell.ErrorOccurred += (_, e) => Dispatcher.UIThread.Post(() =>
            FeedLine($"\r\n[31m[error] {e.Exception.Message}[0m"));
        shell.Closed += (_, _) => Dispatcher.UIThread.Post(() =>
            FeedLine("\r\n[33m[session closed][0m\r\n"));

        // Push the current (laid-out) size in case SizeChanged fired before connect.
        SyncWindowSize();
    }

    private void OnShellData(byte[] data)
    {
        if (_disposed || data.Length == 0)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
                _model.Feed(data, data.Length);
        });
    }

    private void SendToShell(ReadOnlyMemory<byte> data)
    {
        var shell = _shell;
        if (shell is null || _disposed || data.IsEmpty)
            return;
        try
        {
            shell.Write(data.Span);
            shell.Flush();
        }
        catch
        {
            // Best-effort; a closed/broken stream is reported via Closed/ErrorOccurred.
        }
    }

    private void SyncWindowSize()
    {
        var shell = _shell;
        if (shell is null || _disposed)
            return;
        try
        {
            var cols = (uint)Math.Max(20, _model.Terminal.Cols);
            var rows = (uint)Math.Max(5, _model.Terminal.Rows);
            shell.ChangeWindowSize(cols, rows, 0, 0);
        }
        catch
        {
            // Ignore resize failures on a closing stream.
        }
    }

    private void FeedLine(string text) => _model.Feed(text + "\r\n");

    /// <summary>Tears down the SSH session. Safe to call multiple times.</summary>
    public void Close()
    {
        _disposed = true;
        try { _shell?.Dispose(); } catch { /* ignore */ }
        try { _client?.Disconnect(); } catch { /* ignore */ }
        try { _client?.Dispose(); } catch { /* ignore */ }
        _shell = null;
        _client = null;
    }
}
