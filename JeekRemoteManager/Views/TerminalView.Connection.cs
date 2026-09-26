using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Diagnostics;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;
using Renci.SshNet;
using SvcSystems.UI.Terminal;
using JeekTools;

namespace JeekRemoteManager.Views;

/// <summary>Connection lifecycle: dialing (SSH, jump host, WSL), shell channels, reconnect, login commands and bastion reuse.</summary>
public partial class TerminalView
{
    private void Reconnect()
    {
        if (_connection is null || _disposed || _connectInProgress || IsConnected)
            return;

        _activePayloadMonitor?.Fail(new InvalidOperationException("SSH terminal is reconnecting."));
        _activePayloadMonitor = null;
        _suppressUserInput = false;
        Interlocked.Increment(ref _connectionGeneration);
        // When only the shell channel died (e.g. the user typed `exit`) but the
        // transport is still up, reopen a channel on it instead of redialing;
        // ConnectAsync falls back to a fresh connection if that fails.
        var useManagedPool = BastionSessionPool is not null
                             && _connection is not null
                             && LoginCommandSequence.HasStructuredReuseWorkflow(
                                 _connection.EffectiveLoginCommands);
        _pendingSharedClient = !useManagedPool
                               && _client is { IsConnected: true } live
                               && live.TryAddRef()
            ? live
            : null;
        DisposeTransport();

        FeedLine("\r\n\u001b[36m[reconnect]\u001b[0m");
        BeginConnectionAttempt();
    }

    /// <summary>Manually aborts the active terminal payload and restores an interactive
    /// prompt. Public so the terminal-level recovery can be verified through Debug MCP.</summary>
    public void ForceInterruptTerminalCommand()
    {
        Interlocked.Increment(ref _terminalRecoveryCount);
        var monitor = Volatile.Read(ref _activePayloadMonitor);
        if (monitor is not null)
        {
            Volatile.Write(ref _manualRecoveryMonitor, monitor);
            monitor.Fail(new ShellRecoveryRequestedException(
                "Terminal command was forcefully interrupted by the user."));
        }

        // Restore local keyboard routing immediately; do not wait for the background
        // command task to unwind before the user can type in the terminal again.
        _suppressUserInput = false;

        // Never make releasing the command lock depend on a possibly wedged shell write.
        // The monitor failure above unwinds RunCapturedAsync first; recovery bytes are
        // best-effort on a worker and target only the channel that was current at the click.
        RecoverShellInputInBackground(_channel);
        FocusTerminal();
    }

    /// <summary>Manually replaces the current terminal channel with a fresh connection.
    /// Unlike Enter-to-reconnect, this is available even while the old channel appears live.</summary>
    public void ReconnectTerminal()
    {
        if (_connection is null || _disposed || _connectInProgress)
            return;

        var monitor = Volatile.Read(ref _activePayloadMonitor);
        if (monitor is not null)
        {
            Volatile.Write(ref _manualRecoveryMonitor, monitor);
            monitor.Fail(new ShellRecoveryRequestedException("Terminal is reconnecting."));
        }
        _activePayloadMonitor = null;
        _suppressUserInput = false;
        Interlocked.Increment(ref _connectionGeneration);
        DisposeTransport();

        FeedLine("\r\n\u001b[36m[reconnect]\u001b[0m");
        BeginConnectionAttempt();
    }

    private void BeginConnectionAttempt()
    {
        var pendingLoginInput = Interlocked.Exchange(ref _loginManualInputTcs, null);
        var hadPendingLoginInput = pendingLoginInput is not null;
        pendingLoginInput?.TrySetCanceled();
        var generation = Interlocked.Increment(ref _connectionGeneration);
        if (hadPendingLoginInput)
            RefreshLoginManualInputLayout(generation);
        _connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _shellClosed = false;
        Volatile.Write(ref _loginSequenceState, "connecting");
        Volatile.Write(ref _bastionSessionState, "none");
        _outputFrameFlushTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _sessionOutputBuffer.Clear();
        // WSL runs under ConPTY, which always speaks UTF-8.
        ApplyTerminalEncoding(_connection is { IsWsl: false } connection
            ? TerminalEncoding.Resolve(connection.TerminalEncoding)
            : TerminalEncoding.Utf8);
        _ = ConnectAsync(generation);
    }

    private async Task ConnectAsync(int generation)
    {
        var connection = _connection!;
        if (connection.IsWsl)
        {
            await ConnectWslAsync(connection, generation);
            return;
        }

        var host = connection.Host.Trim();
        var port = connection.Port > 0 ? connection.Port : 22;
        _connectInProgress = true;
        if (!connection.TryResolveLoginCommands(out var effectiveLoginCommands, out var loginCommandsError))
        {
            var failure = new InvalidOperationException(loginCommandsError);
            _connected?.TrySetException(failure);
            _connectInProgress = false;
            Volatile.Write(ref _loginSequenceState, "template-error");
            FeedLine($"\u001b[31m[login template error] {loginCommandsError}\u001b[0m");
            FeedReconnectHint();
            return;
        }

        // Legacy duplicated tabs can be handed a direct counted reference. "Open new
        // TCP connection" explicitly forbids using it: that action promises another TCP
        // transport, not merely another shell channel.
        var shared = Interlocked.Exchange(ref _pendingSharedClient, null);
        if (_forceNewTcpConnection)
        {
            shared?.Release();
        }
        else if (shared is not null)
        {
            FeedLine($"Opening a new session on the existing connection to {host}:{port} ...");
            Volatile.Write(ref _bastionSessionState, "direct-duplicate");
            if (await TryOpenShellAsync(
                    shared,
                    generation,
                    reportFailure: false,
                    [LoginCommandSequence.Select(effectiveLoginCommands, LoginCommandSection.Duplicate)]))
                return;

            shared.Release();
            if (_disposed || generation != _connectionGeneration)
                return;
        }

        BastionSessionPool.BastionSessionLease? pooledLease = null;
        try
        {
            // Normal connects may automatically borrow a matching structured-bastion
            // transport. A new TCP connection must bypass the pool as well as the
            // direct duplicate path above.
            if (!_forceNewTcpConnection && BastionSessionPool is not null)
            {
                var hasKnownSession = BastionSessionPool.HasKnownSession(connection);
                var hasReusableSession = BastionSessionPool.HasReusableSession(connection);
                if (hasReusableSession)
                {
                    Volatile.Write(ref _bastionSessionState, "pooled-waiting");
                    FeedLine(BastionPoolWaitingMessage);
                }
                else if (hasKnownSession)
                {
                    Volatile.Write(ref _bastionSessionState, "pooled-full");
                    FeedLine(BastionPoolFullMessage);
                }

                // Everyone borrowing this transport switches routes one at a time, so
                // wait out the queue that is ahead instead of giving up mid-line and
                // paying for a whole fresh login.
                var waitSeconds = Math.Min(
                    BastionPoolWaitCapSeconds,
                    BastionPoolWaitTimeoutSeconds
                    * (1 + BastionSessionPool.PendingBorrowCount(connection)));
                using var waitTimeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(waitSeconds));
                try
                {
                    pooledLease = await BastionSessionPool.TryAcquireAsync(
                        connection,
                        waitTimeout.Token);
                }
                catch (OperationCanceledException) when (waitTimeout.IsCancellationRequested)
                {
                    Volatile.Write(ref _bastionSessionState, "pooled-wait-timeout");
                    FeedLine(BastionPoolWaitTimeoutMessage(waitSeconds));
                }

                // Nothing to borrow yet, but another connection to this same bastion is
                // authenticating right now. Waiting for it is the whole point of the
                // pool: dialing our own transport would ask the user for a second code.
                if (pooledLease is null)
                    pooledLease = await BorrowAfterPendingLoginAsync(connection);
            }
        }
        catch (Exception ex)
        {
            FeedLine($"\u001b[33m[bastion pool unavailable] {ex.Message}\u001b[0m");
        }

        if (pooledLease is not null)
        {
            var reuseStart = pooledLease.ReuseStart;
            var phases = BastionLanding.SelectReusePhases(
                reuseStart,
                pooledLease.SourceRoute.LoginCommands,
                effectiveLoginCommands);
            var action = reuseStart == BastionReuseStart.Switch
                ? $"Switching the existing bastion session from {pooledLease.SourceRoute.Name} to {connection.Name}"
                : $"Opening {connection.Name} on the existing bastion session";
            FeedLine($"{action} ...");
            Volatile.Write(
                ref _bastionSessionState,
                reuseStart switch
                {
                    BastionReuseStart.Switch => "pooled-switching",
                    BastionReuseStart.Enter => "pooled-entering",
                    _ => "pooled-reused",
                });
            if (await TryOpenShellAsync(
                    pooledLease.Client,
                    generation,
                    reportFailure: false,
                    phases,
                    pooledLease))
            {
                return;
            }

            pooledLease.Dispose();
            if (_disposed || generation != _connectionGeneration)
                return;
            FeedLine(
                $"\u001b[33m{BastionReuseFallbackMessage}\u001b[0m");
        }

        // Claim this bastion identity's fresh login, so connections started while this
        // one authenticates wait for it instead of dialing their own transport and
        // prompting for a second two-factor code. A null claim only means someone else
        // got there first, which costs this one connection its own login.
        _freshLoginReservation = BastionSessionPool?.TryReserveFreshLogin(connection);

        var jumpPath = connection.JumpHost.Trim();
        FeedLine(jumpPath.Length == 0
            ? $"Connecting to {host}:{port} ..."
            : $"Connecting to {host}:{port} via {jumpPath} ...");
        Volatile.Write(
            ref _bastionSessionState,
            _forceNewTcpConnection ? "new-tcp-forced" : "fresh");

        // A malformed forward is reported and skipped; it must not cost the session.
        IReadOnlyList<PortForwardSpec> forwards = [];
        try
        {
            forwards = SshPortForwarding.Parse(connection.PortForwards);
        }
        catch (FormatException ex)
        {
            FeedLine($"\u001b[33m[port forwarding] {ex.Message}\u001b[0m");
        }

        SharedSshClient client;
        var forwardReport = new List<string>();
        try
        {
            // Build (which may query ssh-agent / Pageant over IPC) and Connect both
            // run on a background thread — those calls can block, and on the UI thread
            // would freeze the whole window.
            client = await Task.Run(() =>
            {
                var (sshClient, tunnel) = SshDialer.Connect(
                    connection,
                    info => new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) },
                    new SshDialOptions(
                        OnMismatch: (keyType, saved, fingerprint) => HostKeyDialog.PromptReplace(host, port, keyType, saved, fingerprint),
                        OnRejected: message => Dispatcher.UIThread.Post(() => FeedLine($"\r\n\u001b[31m[{message}]\u001b[0m\r\n")),
                        PromptUser: KeyboardInteractiveDialog.Prompt),
                    ResolveConnection);
                var shared = new SharedSshClient(sshClient);
                if (tunnel is not null)
                    shared.AddOwnedResource(tunnel);

                // Forwards live with the transport this tab dialed: duplicated tabs share
                // them, and they stop when the last holder releases it.
                var started = SshPortForwarding.StartAll(sshClient, forwards);
                var ports = started.Where(result => result.Port is not null).Select(result => result.Port!).ToList();
                if (ports.Count > 0)
                    shared.AddOwnedResource(new PortForwardSet(ports));
                forwardReport.AddRange(started.Select(result => result.Error is null
                    ? $"\u001b[90m[port forwarding] {result.Spec}\u001b[0m"
                    : $"\u001b[33m[port forwarding] {result.Spec} failed: {result.Error}\u001b[0m"));
                return shared;
            });
        }
        catch (Exception ex)
        {
            _connected?.TrySetException(new InvalidOperationException($"Connection failed: {ex.Message}", ex));
            _connectInProgress = false;
            ReleaseFreshLoginReservation();
            FeedLine($"\u001b[31m[connect failed] {ex.Message}\u001b[0m");
            FeedReconnectHint();
            return;
        }

        if (_disposed || generation != _connectionGeneration)
        {
            _connected?.TrySetCanceled();
            _connectInProgress = false;
            ReleaseFreshLoginReservation();
            client.Release();
            return;
        }

        foreach (var line in forwardReport)
            FeedLine(line);

        // On success the login sequence owns the claim and releases it as soon as the
        // transport is pooled; a channel that never opened releases it here.
        if (!await TryOpenShellAsync(
                client,
                generation,
                reportFailure: true,
                [LoginCommandSequence.Select(effectiveLoginCommands, LoginCommandSection.Fresh)],
                registerFreshInPool: true))
        {
            ReleaseFreshLoginReservation();
            client.Release();
        }
    }

    /// <summary>
    /// Starts the connection's WSL distribution under a ConPTY and wires it to the
    /// terminal. Local counterpart of the SSH dial: same failure reporting, same
    /// reconnect hint. The first start of a stopped WSL VM can take several seconds.
    /// </summary>
    private async Task ConnectWslAsync(Connection connection, int generation)
    {
        _connectInProgress = true;
        var distro = connection.WslDistro.Trim();
        FeedLine($"Starting WSL ({(distro.Length == 0 ? "default distribution" : distro)}) ...");

        var cols = (uint)Math.Max(20, _model.Terminal.Cols);
        var rows = (uint)Math.Max(5, _model.Terminal.Rows);

        ConPtySession session;
        try
        {
            if (!WslDistroService.IsWslInstalled)
                throw new InvalidOperationException("WSL is not installed on this machine.");

            var args = WslDistroService.BuildLaunchArguments(connection);
            session = await Task.Run(() =>
                ConPtySession.Start(WslDistroService.WslExePath, args, (int)cols, (int)rows));
        }
        catch (Exception ex)
        {
            _connected?.TrySetException(new InvalidOperationException($"Connection failed: {ex.Message}", ex));
            _connectInProgress = false;
            FeedLine($"\u001b[31m[connect failed] {ex.Message}\u001b[0m");
            FeedReconnectHint();
            return;
        }

        if (_disposed || generation != _connectionGeneration)
        {
            _connected?.TrySetCanceled();
            _connectInProgress = false;
            session.Dispose();
            return;
        }

        AttachChannel(new WslTerminalChannel(session), generation, (cols, rows));
    }

    /// <summary>
    /// Debug-only: drives this tab from a local ConPTY shell instead of a remote host, so
    /// the login-command sequence (including the "#select" menu matcher) can be verified
    /// end-to-end without a bastion. Takes the same path as a WSL tab from AttachChannel on.
    /// </summary>
    public async Task DebugStartLocalShellAsync(
        Connection connection,
        string exePath,
        IReadOnlyList<string> arguments,
        IReadOnlyList<string[]>? loginPhases = null)
    {
        _connection = connection;
        _isDuplicatedSession = false;
        var generation = Interlocked.Increment(ref _connectionGeneration);
        _connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _shellClosed = false;
        _outputFrameFlushTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _sessionOutputBuffer.Clear();
        ApplyTerminalEncoding(TerminalEncoding.Utf8);
        _connectInProgress = true;

        var cols = (uint)Math.Max(20, _model.Terminal.Cols);
        var rows = (uint)Math.Max(5, _model.Terminal.Rows);
        var session = await Task.Run(() =>
            ConPtySession.Start(exePath, arguments, (int)cols, (int)rows));

        if (_disposed || generation != _connectionGeneration)
        {
            session.Dispose();
            return;
        }

        AttachChannel(
            new LocalConPtyTerminalChannel(session),
            generation,
            (cols, rows),
            loginPhases);
    }

    /// <summary>
    /// Opens the shell channel on <paramref name="client"/> and wires it to the
    /// terminal. Returns false without taking ownership when the channel cannot be
    /// opened or the attempt is stale — the caller still owns releasing the client.
    /// With <paramref name="reportFailure"/> false (the shared-connection fast path),
    /// a failure is only noted in the terminal so the caller can fall back to a
    /// fresh connection.
    /// </summary>
    private async Task<bool> TryOpenShellAsync(
        SharedSshClient client,
        int generation,
        bool reportFailure,
        IReadOnlyList<string[]> loginPhases,
        BastionSessionPool.BastionSessionLease? pooledLease = null,
        bool registerFreshInPool = false)
    {
        var connection = _connection!;
        var cols = (uint)Math.Max(20, _model.Terminal.Cols);
        var rows = (uint)Math.Max(5, _model.Terminal.Rows);
        var terminalType = string.IsNullOrWhiteSpace(connection.TerminalType)
            ? Connection.DefaultTerminalType
            : connection.TerminalType.Trim();

        SharedShellStreamLease shell;
        try
        {
            // The channel open is a network round-trip; keep it off the UI thread so
            // a stalled (half-dead) shared transport cannot freeze the window.
            shell = await client.CreateShellStreamAsync(
                terminalType,
                cols,
                rows,
                0,
                0,
                4096);
        }
        catch (Exception ex)
        {
            if (_disposed || generation != _connectionGeneration)
            {
                _connected?.TrySetCanceled();
                _connectInProgress = false;
                return false;
            }

            if (!reportFailure)
            {
                FeedLine($"\u001b[33m[shared connection unavailable] {ex.Message}\u001b[0m");
                return false;
            }

            _connected?.TrySetException(new InvalidOperationException($"Connection failed: {ex.Message}", ex));
            _connectInProgress = false;
            FeedLine($"\u001b[31m[connect failed] {ex.Message}\u001b[0m");
            FeedReconnectHint();
            return false;
        }

        if (_disposed || generation != _connectionGeneration)
        {
            _connected?.TrySetCanceled();
            _connectInProgress = false;
            try { shell.Dispose(); } catch { /* ignore */ }
            return false;
        }

        lock (_clientReferenceGate)
        {
            _client = client;
            _ownsClientReference = pooledLease is null;
        }
        AttachChannel(
            new SshTerminalChannel(shell),
            generation,
            (cols, rows),
            loginPhases,
            pooledLease,
            registerFreshInPool);
        return true;
    }

    /// <summary>
    /// Wires an opened channel (SSH shell or WSL ConPTY) to the terminal:
    /// data/error/close events, connection completion, window-size sync, and the
    /// connection's login commands.
    /// </summary>
    private void AttachChannel(
        ITerminalChannel channel,
        int generation,
        (uint Cols, uint Rows) initialSize,
        IReadOnlyList<string[]>? loginPhases = null,
        BastionSessionPool.BastionSessionLease? pooledLease = null,
        bool registerFreshInPool = false)
    {
        _channel = channel;
        _lastSentWindowSize = initialSize;
        Interlocked.Exchange(ref _lastShellDataTicks, 0);

        channel.DataReceived += data =>
        {
            if (generation == _connectionGeneration)
                OnShellData(data);
        };
        channel.ErrorMessage += message => Dispatcher.UIThread.Post(() =>
        {
            if (generation == _connectionGeneration && !_disposed)
                FeedLine($"\r\n\u001b[31m[error] {message}\u001b[0m");
        });
        channel.Closed += () =>
        {
            if (generation != _connectionGeneration || _disposed)
                return;

            _shellClosed = true;
            // While an agent CLI is open, keep the remote shell alive so MCP tools stay useful.
            var shouldAutoReconnect = _aiViewModel?.IsRunning == true;
            TaskCompletionSource<bool>? reconnectCompletion = null;
            if (shouldAutoReconnect)
            {
                reconnectCompletion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _aiAutoReconnectTask, reconnectCompletion.Task);
            }

            _activePayloadMonitor?.Fail(new TerminalConnectionLostException());
            Dispatcher.UIThread.Post(() =>
            {
                FeedLine("\r\n\u001b[33m[session closed]\u001b[0m");
                if (reconnectCompletion is null)
                {
                    FeedReconnectHint();
                    return;
                }

                _ = CompleteAiAutoReconnectAsync(reconnectCompletion);
            });
        };

        _connected?.TrySetResult(true);
        _connectInProgress = false;

        // Push the current (laid-out) size in case SizeChanged fired before connect.
        SyncWindowSize();
        StartLoginCommands(
            _connection!,
            generation,
            loginPhases
            ?? [LoginCommandSequence.Select(_connection!.EffectiveLoginCommands, _isDuplicatedSession)],
            pooledLease,
            registerFreshInPool);
    }

    private async Task CompleteAiAutoReconnectAsync(TaskCompletionSource<bool> completion)
    {
        string? error = null;
        try
        {
            if (_disposed || _connection is null)
                throw new InvalidOperationException("Terminal is closed.");

            _aiAutoReconnectState = "reconnecting";

            var retryDelays = new[]
            {
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(3),
            };
            foreach (var retryDelay in retryDelays)
            {
                if (retryDelay > TimeSpan.Zero)
                    await Task.Delay(retryDelay);
                if (_disposed)
                    throw new InvalidOperationException("Terminal is closed.");

                Interlocked.Increment(ref _aiAutoReconnectAttemptCount);
                var generation = ConnectionGeneration;
                ReconnectTerminal();
                if (ConnectionGeneration == generation || _connected is null)
                {
                    error = "Reconnect could not be started.";
                    continue;
                }

                try
                {
                    await _connected.Task;
                    if (!IsConnected)
                        throw new InvalidOperationException("The new terminal channel is not connected.");

                    _aiAutoReconnectState = "connected";
                    Interlocked.Increment(ref _aiAutoReconnectSuccessCount);
                    completion.TrySetResult(true);
                    return;
                }
                catch (Exception ex)
                {
                    error = ex.GetBaseException().Message;
                }
            }

            throw new InvalidOperationException(error ?? "Unknown connection error.");
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
        }

        _aiAutoReconnectState = "failed";
        if (!_disposed)
            FeedLine($"\u001b[31m[AI auto-reconnect failed] {error ?? "Unknown connection error."}\u001b[0m");
        completion.TrySetResult(false);
    }

    /// <summary>Runs the production AI disconnect notification and reconnect sequence on
    /// the current terminal. Intended for live Debug MCP verification.</summary>
    public async Task<string> RunAiAutoReconnectForDebugAsync()
    {
        if (_aiViewModel is null)
            return "[AI panel has not been opened]";
        if (_disposed || _connection is null)
            return "[terminal is closed]";

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _aiAutoReconnectTask, completion.Task);
        _ = CompleteAiAutoReconnectAsync(completion);
        var reconnected = await completion.Task;
        return $"state={AiAutoReconnectState}; connected={IsTerminalConnected}; success={reconnected}; "
               + $"attempts={AiAutoReconnectAttemptCount}; successes={AiAutoReconnectSuccessCount}";
    }

    /// <summary>
    /// Opens the panels selected on this SSH connection after authentication. This is
    /// public so the running behavior and resulting panel state can be exercised via
    /// the generic Debug MCP invoke/get_value tools.
    /// </summary>
    public void OpenConfiguredPanelsAfterLogin()
    {
        if (_disposed || _connection is not { IsSsh: true } connection || !IsConnected)
            return;

        if (connection.AutoOpenMonitorPanel && !IsMonitorPanelOpen)
            ToggleMonitorPanel();
        // The AI panel follows the global remembered state instead of a per-connection option.
        if ((DataContext as MainWindowViewModel)?.AiPanelOpen == true && !IsAiPanelOpen)
            ToggleAiPanel();
        if (connection.AutoOpenFileBrowserPanel && !IsFileBrowserPanelOpen)
            ToggleFileBrowserPanel();
    }

    /// <summary>
    /// Types the connection's login commands into the shell, one line at a time.
    /// Each line is sent only after the remote has produced output and then gone
    /// quiet, so bastion menus, sudo prompts, etc. are on screen before their
    /// answer is typed. A line consisting of "#input" pauses the sequence until
    /// the user types something manually (e.g. a 2FA code) and presses Enter.
    /// A "#select &lt;name&gt;" line types the number of the menu entry matching that
    /// name, so bastion menus keep working when their numbering shifts; a preceding
    /// "#pagekey &lt;key&gt;" line lets it page through a menu longer than one screen.
    /// In a duplicated tab, a "#duplicate" line skips all commands before it.
    /// Runs on every shell open, including reconnects.
    /// </summary>
    private void StartLoginCommands(
        Connection connection,
        int generation,
        IReadOnlyList<string[]> phases,
        BastionSessionPool.BastionSessionLease? pooledLease,
        bool registerFreshInPool)
    {
        _loginOutputCapture.Reset();
        _loginCaptureActive = true;
        Volatile.Write(ref _loginSequenceState, "running");
        var clientAtStart = _client;

        _ = Task.Run(async () =>
        {
            var succeeded = true;
            var reuseFailed = false;
            try
            {
                var selectedPhases = phases;
                if (pooledLease is not null)
                {
                    // The bastion may still be reconnecting this channel to its previous
                    // target when the channel opens. Typing into that would answer a
                    // screen that is about to be replaced, so wait for the landing to
                    // settle and let it correct the phases the route implied.
                    var landing = await DetectBastionLandingAsync(generation);
                    selectedPhases = BastionLanding.SelectReusePhases(
                        landing,
                        pooledLease.ReuseStart,
                        pooledLease.SourceRoute.LoginCommands,
                        connection.EffectiveLoginCommands);
                    Volatile.Write(
                        ref _bastionSessionState,
                        landing switch
                        {
                            BastionLandingKind.Menu => "pooled-menu",
                            BastionLandingKind.AuthPrompt => "pooled-auth-prompt",
                            BastionLandingKind.Shell => "pooled-shell",
                            _ => "pooled-landing-unknown",
                        });
                }

                for (var phaseIndex = 0; phaseIndex < selectedPhases.Count; phaseIndex++)
                {
                    if (!await RunLoginCommandsAsync(
                            selectedPhases[phaseIndex],
                            generation,
                            waitForTrailingOutput: phaseIndex < selectedPhases.Count - 1,
                            registerAfterInput: registerFreshInPool,
                            registerClient: clientAtStart,
                            registerConnection: connection))
                    {
                        succeeded = false;
                        break;
                    }
                }

                succeeded = succeeded
                            && !_disposed
                            && generation == _connectionGeneration
                            && !_shellClosed;
                if (succeeded)
                {
                    if (pooledLease is not null)
                    {
                        succeeded = TryTakePooledClientReference(
                            pooledLease,
                            generation,
                            completeRoute: true);
                        if (succeeded)
                        {
                            _bastionReuseRetries = 0;
                            Volatile.Write(ref _bastionSessionState, "pooled-ready");
                        }
                    }
                    else if (registerFreshInPool && clientAtStart is not null)
                    {
                        TryRegisterFresh(clientAtStart, connection);
                    }

                    if (succeeded)
                    {
                        Volatile.Write(ref _loginSequenceState, "ready");
                        Dispatcher.UIThread.Post(
                            OpenConfiguredPanelsAfterLogin,
                            DispatcherPriority.Background);
                    }
                }
                if (!succeeded)
                {
                    if (pooledLease is not null
                        && !_disposed
                        && generation == _connectionGeneration)
                    {
                        if (TryTakePooledClientReference(
                                pooledLease,
                                generation,
                                completeRoute: false))
                        {
                            Volatile.Write(ref _bastionSessionState, "pooled-route-unknown");
                        }

                        // A shell the user closed is not a failed reuse.
                        reuseFailed = !_shellClosed;
                    }
                    else if (registerFreshInPool && clientAtStart is not null)
                    {
                        // Stopped somewhere between the bastion menu and the target:
                        // keep the authenticated transport, but do not claim it arrived.
                        TryRegisterFresh(
                            clientAtStart,
                            connection,
                            BastionRoute.Unknown(connection.EffectiveLoginCommands),
                            "fresh-pooled-route-unknown");
                    }
                    Volatile.Write(ref _loginSequenceState, "failed");
                }
            }
            catch (Exception ex)
            {
                if (pooledLease is not null
                    && !pooledLease.Completed
                    && !_disposed
                    && generation == _connectionGeneration)
                {
                    if (TryTakePooledClientReference(
                            pooledLease,
                            generation,
                            completeRoute: false))
                    {
                        Volatile.Write(ref _bastionSessionState, "pooled-route-unknown");
                    }

                    reuseFailed = !_shellClosed;
                }
                else if (registerFreshInPool && clientAtStart is not null)
                {
                    TryRegisterFresh(
                        clientAtStart,
                        connection,
                        BastionRoute.Unknown(connection.EffectiveLoginCommands),
                        "fresh-pooled-route-unknown");
                }
                Volatile.Write(ref _loginSequenceState, "failed");
                Dispatcher.UIThread.Post(
                    () => ReportLoginMenuFailure($"login sequence failed: {ex.Message}"),
                    DispatcherPriority.Background);
            }
            finally
            {
                _loginCaptureActive = false;
                _loginOutputCapture.Reset();
                pooledLease?.Dispose();
                // However this ended, nobody should keep waiting on this login.
                ReleaseFreshLoginReservation();
                if (reuseFailed)
                {
                    Dispatcher.UIThread.Post(
                        () => RecoverFromFailedReuse(generation),
                        DispatcherPriority.Background);
                }
            }
        });
    }

    /// <summary>
    /// Waits for another connection's in-flight login to this same bastion and then
    /// borrows the transport it authenticated. Returns null when there was nothing to
    /// wait for, the wait ran out, or the transport is unusable — this connection then
    /// logs in on its own.
    /// </summary>
    private async Task<BastionSessionPool.BastionSessionLease?> BorrowAfterPendingLoginAsync(
        Connection connection)
    {
        var pool = BastionSessionPool;
        if (pool is null || !pool.HasPendingFreshLogin(connection))
            return null;

        Volatile.Write(ref _bastionSessionState, "pooled-waiting-login");
        FeedLine(BastionPendingLoginMessage);

        using var waitTimeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(BastionPendingLoginWaitSeconds));
        try
        {
            while (await pool.WaitForFreshLoginAsync(connection, waitTimeout.Token))
            {
                if (_disposed)
                    return null;

                if (await pool.TryAcquireAsync(connection, waitTimeout.Token) is { } lease)
                    return lease;

                // That login did not leave anything borrowable behind. If yet another
                // one started meanwhile, wait for that one too; otherwise give up and
                // dial our own transport.
            }
        }
        catch (OperationCanceledException) when (waitTimeout.IsCancellationRequested)
        {
            Volatile.Write(ref _bastionSessionState, "pooled-login-wait-timeout");
            FeedLine(BastionPendingLoginTimeoutMessage);
        }

        return null;
    }

    /// <summary>
    /// Waits for a reused channel's landing to stop moving, so the first command is not
    /// typed into a screen the bastion is about to replace. A credential prompt is
    /// returned immediately — it is waiting for the user, not for more output — and an
    /// unreadable landing simply ends the wait: the caller falls back to the route.
    /// </summary>
    private async Task<BastionLandingKind> DetectBastionLandingAsync(int generation)
    {
        const int quietMs = 500;
        const int timeoutMs = 10_000;
        var startedAt = Environment.TickCount64;
        var landing = BastionLandingKind.Unknown;

        while (true)
        {
            if (_disposed || _shellClosed || generation != _connectionGeneration)
                return BastionLandingKind.Unknown;

            landing = BastionLanding.Classify(_loginOutputCapture.Snapshot());
            var lastData = Interlocked.Read(ref _lastShellDataTicks);
            var now = Environment.TickCount64;
            if (landing == BastionLandingKind.AuthPrompt
                || (landing != BastionLandingKind.Unknown
                    && lastData != 0
                    && now - lastData >= quietMs))
            {
                return landing;
            }

            if (now - startedAt >= timeoutMs)
                return landing;
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// A reused channel failed to reach the target. The channel is left on screen for
    /// the user either way; the one thing worth trying automatically is a second borrow,
    /// because that gets a brand-new channel with a clean landing and costs no login.
    /// A fresh transport is deliberately not dialed: that means another two-factor code,
    /// and if the workflow itself is wrong it would fail there too.
    /// </summary>
    private void RecoverFromFailedReuse(int generation)
    {
        if (_disposed || generation != _connectionGeneration || _shellClosed)
            return;

        if (_bastionReuseRetries >= 1)
        {
            Volatile.Write(ref _bastionSessionState, "pooled-reuse-given-up");
            FeedLine($"\u001b[33m{BastionReuseGaveUpMessage}\u001b[0m");
            return;
        }

        _bastionReuseRetries++;
        FeedLine($"\u001b[33m{BastionReuseRetryMessage}\u001b[0m");
        DisposeTransport();
        BeginConnectionAttempt();
    }

    /// <summary>Releases this tab's claim on its bastion's fresh login, letting any
    /// connection waiting behind it proceed.</summary>
    private void ReleaseFreshLoginReservation() =>
        Interlocked.Exchange(ref _freshLoginReservation, null)?.Dispose();

    /// <summary>
    /// Puts a freshly authenticated transport in the pool. <paramref name="route"/> must
    /// describe where this shell actually is — recording the target before the login
    /// sequence has reached it makes the next borrower send that target's
    /// <c>#reuse-leave</c> into whatever is on screen.
    /// </summary>
    private void TryRegisterFresh(
        SharedSshClient client,
        Connection connection,
        BastionRoute? route = null,
        string? state = null)
    {
        var registered = BastionSessionPool?.Register(client, connection, route) == true;
        // The transport is borrowable from here on, so anyone waiting for this login
        // can stop waiting — they do not have to sit through the menu navigation too.
        if (registered)
            ReleaseFreshLoginReservation();
        Volatile.Write(
            ref _bastionSessionState,
            registered ? state ?? "fresh-pooled" : "fresh-pool-rejected");
    }

    private async Task<bool> RunLoginCommandsAsync(
        string[] lines,
        int generation,
        bool waitForTrailingOutput = false,
        bool registerAfterInput = false,
        SharedSshClient? registerClient = null,
        Connection? registerConnection = null)
    {
        if (lines.Length == 0)
            return true;

        // Output older than this tick doesn't count as the response to the
        // previous command; the PTY echoes every typed line, so waiting for
        // newer data never stalls on a command with no output of its own.
        long mustBeAfterTicks = 0;
        const int quietMs = 500;
        const int timeoutMs = 30000;
        // Set by "#pagekey"; long menus are paged with it until the wanted entry shows up.
        string? pageKey = null;

        // Waits for the remote to answer the previous key and then go quiet. False means
        // the session went away — the caller must stop typing into it.
        async Task<bool> WaitForOutputQuietAsync()
        {
            var startedAt = Environment.TickCount64;
            while (true)
            {
                if (_disposed || _shellClosed || generation != _connectionGeneration)
                    return false;

                var lastData = Interlocked.Read(ref _lastShellDataTicks);
                var now = Environment.TickCount64;
                // ">=" not ">": on a low-latency link the echo can land in the
                // same TickCount64 tick as the send that provoked it.
                if (lastData != 0 && lastData >= mustBeAfterTicks && now - lastData >= quietMs)
                    return true;
                // Don't stall forever on a server that prints nothing.
                if (now - startedAt >= timeoutMs)
                    return true;
                await Task.Delay(50);
            }
        }

        // A key press can echo an empty line before the bastion has finished returning
        // to its menu. Do not let that echo satisfy a following #select: wait until at
        // least one real numbered entry is present, then let the full menu go quiet.
        async Task<bool> WaitForNumberedMenuAsync()
        {
            var startedAt = Environment.TickCount64;
            while (LoginMenuSelection.ParseEntries(_loginOutputCapture.Snapshot()).Count == 0)
            {
                if (_disposed || _shellClosed || generation != _connectionGeneration)
                    return false;
                if (Environment.TickCount64 - startedAt >= timeoutMs)
                    return true; // Let the resolver produce the normal detailed failure.
                await Task.Delay(50);
            }

            return await WaitForOutputQuietAsync();
        }

        foreach (var line in lines)
        {
            if (LoginCommandSequence.IsManualInputDirective(line))
            {
                var manualInput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Interlocked.Exchange(ref _loginManualInputTcs, manualInput)?.TrySetCanceled();
                Volatile.Write(ref _loginSequenceState, "waiting-input");
                RefreshLoginManualInputLayout(generation);
                try
                {
                    // No timeout: fetching a 2FA code can take as long as it takes.
                    while (!manualInput.Task.IsCompleted)
                    {
                        if (_disposed || _shellClosed || generation != _connectionGeneration)
                            return false;
                        await Task.Delay(50);
                    }
                }
                finally
                {
                    Interlocked.CompareExchange(ref _loginManualInputTcs, null, manualInput);
                    if (!_disposed && generation == _connectionGeneration)
                        Volatile.Write(ref _loginSequenceState, "running");
                    RefreshLoginManualInputLayout(generation);
                }

                // 2FA is done; keep the authenticated transport even if a later
                // menu step fails, so the next target does not ask again. The shell
                // is still at the bastion itself, so record that and not the target:
                // the next borrower would otherwise try to leave a target this
                // transport has never entered.
                if (registerAfterInput && registerClient is not null && registerConnection is not null)
                {
                    TryRegisterFresh(
                        registerClient,
                        registerConnection,
                        BastionRoute.AtEntry(registerConnection.EffectiveLoginCommands),
                        "fresh-pooled-entry");
                }

                // The next command must wait for output produced after the
                // user's Enter, not for what was already on screen.
                mustBeAfterTicks = Environment.TickCount64;
                _loginOutputCapture.Reset();
                continue;
            }

            if (LoginCommandSequence.TryGetMenuPageKey(line) is { } keySpec)
            {
                if (!LoginKeySequence.TryParse(keySpec, out var parsedKey, out var keyError))
                {
                    ReportLoginMenuFailure($"{LoginCommandSequence.MenuPageKeyDirective} {keySpec}: {keyError}");
                    return false;
                }

                pageKey = parsedKey;
                continue;
            }

            if (LoginCommandSequence.TryGetKey(line) is { } immediateKeySpec)
            {
                if (!LoginKeySequence.TryParse(
                        immediateKeySpec,
                        out var immediateKey,
                        out var immediateKeyError))
                {
                    ReportLoginMenuFailure(
                        $"{LoginCommandSequence.KeyDirective} {immediateKeySpec}: {immediateKeyError}");
                    return false;
                }
                if (!await WaitForOutputQuietAsync())
                    return false;

                mustBeAfterTicks = Environment.TickCount64;
                _loginOutputCapture.Reset();
                try
                {
                    WriteToShell(immediateKey);
                }
                catch
                {
                    return false;
                }
                continue;
            }

            if (!await WaitForOutputQuietAsync())
                return false;

            var text = line;
            if (LoginCommandSequence.TryGetMenuSelectKeyword(line) is { } keyword)
            {
                if (!await WaitForNumberedMenuAsync())
                    return false;

                // The menu is on screen now that output went quiet; match it by name,
                // pressing the paging key for as long as new entries keep arriving.
                var selection = await LoginMenuPager.SelectAsync(
                    keyword,
                    pageKey,
                    _loginOutputCapture.Snapshot,
                    async () =>
                    {
                        mustBeAfterTicks = Environment.TickCount64;
                        _loginOutputCapture.Reset();
                        try
                        {
                            WriteToShell(pageKey!);
                        }
                        catch
                        {
                            return false;
                        }

                        return await WaitForOutputQuietAsync();
                    });
                if (!selection.Success)
                {
                    // Stop rather than type a number that could reach the wrong machine;
                    // the user takes over at the menu that is already on screen.
                    ReportLoginMenuFailure(selection.Failure ?? "menu selection failed");
                    return false;
                }

                text = selection.Choice!;
            }

            // Stamp before the write so an echo arriving in the same tick counts.
            mustBeAfterTicks = Environment.TickCount64;
            _loginOutputCapture.Reset();
            try
            {
                WriteToShell(text + "\r");
            }
            catch
            {
                // A closed/broken stream is reported via Closed/ErrorOccurred.
                return false;
            }
        }

        // A cross-target switch runs #reuse-leave and #reuse-enter as separate phases.
        // The last #reuse-leave action can return before the bastion has rendered its main menu
        // (notably "exit" followed by "#key Enter"). Preserve its write timestamp
        // and wait for newer output here, so the next phase cannot mistake old,
        // already-quiet target output for the menu it is about to search.
        return !waitForTrailingOutput
               || mustBeAfterTicks == 0
               || await WaitForOutputQuietAsync();
    }

    /// <summary>Writes a local notice into the terminal view; nothing is sent to the remote.</summary>
    private void ReportLoginMenuFailure(string message) =>
        FeedBytesDirect(_terminalEncoding.GetBytes($"\r\n[JeekRemoteManager] {message}\r\n"));

    private bool TryTakePooledClientReference(
        BastionSessionPool.BastionSessionLease lease,
        int generation,
        bool completeRoute)
    {
        lock (_clientReferenceGate)
        {
            if (_disposed
                || generation != _connectionGeneration
                || !ReferenceEquals(_client, lease.Client))
            {
                return false;
            }

            _ = completeRoute
                ? lease.CompleteAndTakeClient()
                : lease.KeepAndTakeClient();
            _ownsClientReference = true;
            return true;
        }
    }

    private void RefreshLoginManualInputLayout(int generation) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || generation != _connectionGeneration)
                return;

            ApplyAiPanelLayout();
            if (IsLoginManualInputPending)
                FocusTerminal();
            else if (IsSshTerminalHidden)
                Dispatcher.UIThread.Post(() => AiPanel.FocusCliTerminal(), DispatcherPriority.Background);
        }, DispatcherPriority.Background);
}
