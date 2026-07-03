using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JeekRemoteManager.Services;

namespace JeekRemoteManager.ViewModels;

/// <summary>A selectable model or effort choice: the label shown in the UI plus the value
/// passed to the CLI (<c>null</c> = don't pass the flag, use the CLI's own default).</summary>
public sealed record AgentOption(string Label, string? Value);

/// <summary>
/// Drives one AI chat conversation scoped to a single terminal tab, and runs an autonomous
/// command loop: each time the assistant emits a fenced shell block, the harness executes it
/// on the connected server (via <c>runCaptured</c>), feeds the captured output back to the
/// assistant, and lets it continue. Model and reasoning effort are chosen up front and become
/// CLI flags when the session process starts; changing them starts a fresh session.
/// </summary>
public sealed partial class AgentChatViewModel : ViewModelBase, IAsyncDisposable
{
    private const int MaxAutoSteps = 8;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);

    private static readonly Regex FencedBlock = new(
        "```[^\\n`]*\\n(.*?)```",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly Func<string?, string?, AgentChatSession?> _sessionFactory;
    private readonly Func<string?> _readSelection;
    private readonly Func<string, CancellationToken, Task<string>>? _runCaptured;
    private readonly CancellationTokenSource _cts = new();

    private AgentChatSession? _session;
    private ChatMessageViewModel? _pendingAssistant;
    private bool _sessionStarted;
    private int _autoStepsRemaining;

    /// <param name="available">Whether the Claude CLI was found.</param>
    /// <param name="sessionFactory">Creates a session for a (model, effort) pair; returns null if unavailable.</param>
    public AgentChatViewModel(
        bool available,
        Func<string?, string?, AgentChatSession?> sessionFactory,
        Func<string?> readSelection,
        Func<string, CancellationToken, Task<string>>? runCaptured)
    {
        _sessionFactory = sessionFactory;
        _readSelection = readSelection;
        _runCaptured = runCaptured;
        _isAvailable = available;
        _statusText = available ? "" : L("AiNotAvailable");
        _selectedModel = ModelOptions[0];
        _selectedEffort = EffortOptions[0];
    }

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();

    public IReadOnlyList<AgentOption> ModelOptions { get; } =
    [
        new AgentOption(L("AiOptionDefault"), null),
        new AgentOption("Fable", "fable"),
        new AgentOption("Opus", "opus"),
        new AgentOption("Sonnet", "sonnet"),
        new AgentOption("Haiku", "haiku"),
    ];

    public IReadOnlyList<AgentOption> EffortOptions { get; } =
    [
        new AgentOption(L("AiOptionDefault"), null),
        new AgentOption("Low", "low"),
        new AgentOption("Medium", "medium"),
        new AgentOption("High", "high"),
        new AgentOption("xHigh", "xhigh"),
        new AgentOption("Max", "max"),
    ];

    [ObservableProperty]
    private string _inputText = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isAvailable;

    [ObservableProperty]
    private bool _includeTerminalSelection;

    [ObservableProperty]
    private bool _autoRun = true;

    [ObservableProperty]
    private bool _showCommandOutput;

    [ObservableProperty]
    private AgentOption _selectedModel;

    [ObservableProperty]
    private AgentOption _selectedEffort;

    [ObservableProperty]
    private string _statusText;

    public bool CanSend => IsAvailable && !IsBusy;

    partial void OnIsBusyChanged(bool value) => SendCommand.NotifyCanExecuteChanged();

    partial void OnIsAvailableChanged(bool value) => SendCommand.NotifyCanExecuteChanged();

    // Model/effort are process-launch flags, so changing them requires a fresh session.
    partial void OnSelectedModelChanged(AgentOption value) => ResetSessionForSettingsChange();

    partial void OnSelectedEffortChanged(AgentOption value) => ResetSessionForSettingsChange();

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (!IsAvailable)
            return;

        var prompt = InputText.Trim();
        if (prompt.Length == 0)
            return;

        var displayText = prompt;
        var payload = prompt;
        if (IncludeTerminalSelection)
        {
            var selection = _readSelection()?.Trim();
            if (!string.IsNullOrEmpty(selection))
            {
                displayText = $"{prompt}\n\n[+ 终端选中内容]";
                payload = $"{prompt}\n\nHere is the relevant terminal output:\n```\n{selection}\n```";
            }
        }

        Messages.Add(new ChatMessageViewModel(ChatRole.User, displayText));
        _pendingAssistant = new ChatMessageViewModel(ChatRole.Assistant, "");
        Messages.Add(_pendingAssistant);

        InputText = "";
        IsBusy = true;
        _autoStepsRemaining = MaxAutoSteps;
        StatusText = L("AiWaiting");

        await SendToSessionAsync(payload);
    }

    private async Task SendToSessionAsync(string payload)
    {
        try
        {
            if (_session is null)
            {
                _session = _sessionFactory(SelectedModel?.Value, SelectedEffort?.Value);
                if (_session is null)
                {
                    IsAvailable = false;
                    IsBusy = false;
                    StatusText = L("AiNotAvailable");
                    return;
                }

                _session.TextDelta += OnTextDelta;
                _session.TurnCompleted += OnTurnCompleted;
                _session.Errored += OnErrored;
                _session.Exited += OnExited;
            }

            if (!_sessionStarted)
            {
                _session.Start();
                _sessionStarted = true;
            }

            await _session.SendAsync(payload, _cts.Token);
        }
        catch (Exception ex)
        {
            OnErrored(ex.Message);
        }
    }

    // Tears down the current session so the next message starts a fresh one with the new
    // model/effort. Only meaningful once a session exists and while idle.
    private void ResetSessionForSettingsChange()
    {
        if (_session is null || IsBusy)
            return;

        DetachAndDisposeSession();
        if (Messages.Count > 0)
            StatusText = L("AiSettingsChanged");
    }

    private void DetachAndDisposeSession()
    {
        var session = _session;
        _session = null;
        _sessionStarted = false;
        _pendingAssistant = null;
        if (session is not null)
        {
            session.TextDelta -= OnTextDelta;
            session.TurnCompleted -= OnTurnCompleted;
            session.Errored -= OnErrored;
            session.Exited -= OnExited;
            _ = session.DisposeAsync();
        }
    }

    private void OnTextDelta(string delta) => Dispatcher.UIThread.Post(() =>
    {
        if (_pendingAssistant is not null)
            _pendingAssistant.Text += delta;
    });

    private void OnTurnCompleted(AgentTurnResult result) => Dispatcher.UIThread.Post(() =>
    {
        var answer = "";
        if (_pendingAssistant is not null)
        {
            if (string.IsNullOrEmpty(_pendingAssistant.Text))
                _pendingAssistant.Text = result.Text;
            answer = _pendingAssistant.Text;
            _pendingAssistant = null;
        }

        StatusText = result.IsError
            ? L("AiTurnError")
            : L("AiTurnCost", result.OutputTokens, result.CostUsd);

        var command = AutoRun && !result.IsError ? ExtractFirstCommand(answer) : null;
        if (command is not null && _runCaptured is not null)
        {
            if (_autoStepsRemaining > 0)
            {
                // Keep IsBusy true across the whole auto loop.
                _ = RunAndContinueAsync(command);
                return;
            }

            Messages.Add(new ChatMessageViewModel(ChatRole.System, L("AiAutoLimit", MaxAutoSteps)));
        }

        IsBusy = false;
    });

    private async Task RunAndContinueAsync(string command)
    {
        _autoStepsRemaining--;
        Messages.Add(new ChatMessageViewModel(ChatRole.Tool, $"$ {command}"));

        string output;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        timeoutCts.CancelAfter(CommandTimeout);
        try
        {
            output = await _runCaptured!(command, timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            output = "[command cancelled or timed out]";
        }
        catch (Exception ex)
        {
            output = $"[command failed: {ex.Message}]";
        }

        if (_cts.IsCancellationRequested)
            return;

        Messages.Add(new ChatMessageViewModel(ChatRole.Tool, TruncateForDisplay(output)));

        _pendingAssistant = new ChatMessageViewModel(ChatRole.Assistant, "");
        Messages.Add(_pendingAssistant);
        StatusText = L("AiWaiting");

        await SendToSessionAsync($"Output of `{command}`:\n```\n{output}\n```");
    }

    private void OnErrored(string message) => Dispatcher.UIThread.Post(() =>
    {
        // stderr can be chatty; only surface it as a system bubble when a turn is in flight.
        if (IsBusy)
        {
            Messages.Add(new ChatMessageViewModel(ChatRole.System, message));
            _pendingAssistant = null;
            IsBusy = false;
            StatusText = L("AiTurnError");
        }
    });

    private void OnExited() => Dispatcher.UIThread.Post(() =>
    {
        IsBusy = false;
        _sessionStarted = false;
        StatusText = L("AiSessionEnded");
    });

    private static string? ExtractFirstCommand(string text)
    {
        var match = FencedBlock.Match(text);
        if (!match.Success)
            return null;

        var body = match.Groups[1].Value.Trim();
        return body.Length == 0 ? null : body;
    }

    private static string TruncateForDisplay(string output)
    {
        const int limit = 4000;
        return output.Length <= limit
            ? output
            : output[..limit] + "\n… [truncated]";
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        var session = _session;
        _session = null;
        if (session is not null)
        {
            session.TextDelta -= OnTextDelta;
            session.TurnCompleted -= OnTurnCompleted;
            session.Errored -= OnErrored;
            session.Exited -= OnExited;
            await session.DisposeAsync();
        }
        _cts.Dispose();
    }
}
