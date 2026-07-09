using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;

namespace JeekRemoteManager.ViewModels;

/// <summary>A selectable model or effort choice: the label shown in the UI plus the value
/// passed to the CLI (<c>null</c> = don't pass the flag, use the CLI's own default).</summary>
public sealed record AgentOption(string Label, string? Value);

/// <summary>
/// One file transfer requested by the assistant via a ```upload / ```download fenced block.
/// Upload: <paramref name="Sources"/> are local Windows files, <paramref name="Destination"/>
/// is a remote directory (null = the shell's current directory). Download: sources are remote
/// files, destination is a local directory (null = the user's Downloads folder).
/// </summary>
public sealed record AgentFileTransfer(bool IsUpload, IReadOnlyList<string> Sources, string? Destination);

/// <summary>
/// One selectable AI backend (Claude / Codex): its display label, the model/effort choices
/// it supports, and a factory that creates a session for a (model, effort) pair. A null
/// <paramref name="SessionFactory"/> means the CLI is not installed; the provider still
/// appears in the picker so the user learns it exists. <paramref name="CatalogFetcher"/>
/// optionally queries the CLI for its live model catalog; the static option lists remain
/// the fallback when it is null or fails.
/// </summary>
public sealed record AgentProvider(
    string Label,
    string UnavailableMessage,
    IReadOnlyList<AgentOption> ModelOptions,
    IReadOnlyList<AgentOption> EffortOptions,
    Func<string?, string?, IAgentChatSession?>? SessionFactory,
    Func<Task<IReadOnlyList<AgentModelInfo>?>>? CatalogFetcher = null)
{
    public bool IsAvailable => SessionFactory is not null;
}

/// <summary>
/// Drives one AI chat conversation scoped to a single terminal tab, and runs an autonomous
/// command loop: each time the assistant emits a fenced shell block, the harness executes it
/// on the connected server (via <c>runCaptured</c>), feeds the captured output back to the
/// assistant, and lets it continue. Model and reasoning effort are chosen up front and become
/// CLI flags when the session process starts; changing them starts a fresh session.
/// </summary>
public sealed partial class AgentChatViewModel : ViewModelBase, IAsyncDisposable
{
    // Only blocks explicitly tagged as shell are executable; plain ``` blocks (quotes,
    // translations, sample output) must never reach the server.
    private static readonly Regex FencedBlock = new(
        "```(?:bash|sh|shell)[ \\t]*\\n(.*?)```",
        RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ```upload / ```download blocks request a ZMODEM file transfer through the terminal;
    // each body line is "SOURCE -> DESTINATION" (destination optional).
    private static readonly Regex TransferBlock = new(
        "```(upload|download)[ \\t]*\\n(.*?)```",
        RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Returns the terminal selection and clears it, so the same selection isn't
    // silently re-attached to the next message.
    private readonly Func<string?> _takeSelection;
    private readonly Func<string, CancellationToken, Task<string>>? _runCaptured;
    private readonly Func<AgentFileTransfer, CancellationToken, Task<string>>? _transferFiles;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _thinkingTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };

    private readonly Dictionary<AgentProvider, (IReadOnlyList<AgentOption> Models, IReadOnlyList<AgentOption> Efforts)> _fetchedCatalogs = new();
    private readonly Action<AiPanelOptions>? _persistOptions;

    private IAgentChatSession? _session;
    private ChatMessageViewModel? _pendingAssistant;
    private bool _sessionStarted;
    private bool _switchingProvider;

    // One CTS per user-initiated turn (covering the whole auto-run loop); Stop cancels it.
    private CancellationTokenSource? _turnCts;
    private bool _stopRequested;

    // Each provider's last-chosen model/effort, restored when switching back to it.
    private readonly Dictionary<string, AiProviderChoice> _providerChoices = new();

    // The saved model/effort values the user last chose for the current provider. They may
    // refer to options that only exist in the dynamically fetched catalog, so selection is
    // re-resolved against these when the catalog arrives.
    private string? _desiredModelValue;
    private string? _desiredEffortValue;

    public AgentChatViewModel(
        IReadOnlyList<AgentProvider> providers,
        Func<string?> takeSelection,
        Func<string, CancellationToken, Task<string>>? runCaptured,
        Func<AgentFileTransfer, CancellationToken, Task<string>>? transferFiles = null,
        AiPanelOptions? initialOptions = null,
        Action<AiPanelOptions>? persistOptions = null)
    {
        _takeSelection = takeSelection;
        _runCaptured = runCaptured;
        _transferFiles = transferFiles;
        _persistOptions = persistOptions;
        _thinkingTimer.Tick += OnThinkingTimerTick;
        Providers = new ObservableCollection<AgentProvider>(providers);

        var provider = providers.FirstOrDefault(p => p.Label == initialOptions?.Provider && p.IsAvailable)
            ?? providers.FirstOrDefault(p => p.IsAvailable)
            ?? providers[0];
        _selectedProvider = provider;
        _isAvailable = provider.IsAvailable;
        _statusText = provider.IsAvailable ? "" : provider.UnavailableMessage;
        _unavailableText = provider.UnavailableMessage;
        _modelOptions = provider.ModelOptions;
        _effortOptions = provider.EffortOptions;

        if (initialOptions is not null)
        {
            foreach (var (label, choice) in initialOptions.ProviderChoices)
                _providerChoices[label] = new AiProviderChoice { Model = choice.Model, Effort = choice.Effort };
            _autoRun = initialOptions.AutoRun;
            _showCommandOutput = initialOptions.ShowCommandOutput;
            _agentMode = initialOptions.AgentMode;
        }

        if (_providerChoices.TryGetValue(provider.Label, out var savedChoice))
        {
            _desiredModelValue = savedChoice.Model;
            _desiredEffortValue = savedChoice.Effort;
        }
        _selectedModel = provider.ModelOptions.FirstOrDefault(o => o.Value == _desiredModelValue) ?? provider.ModelOptions[0];
        _selectedEffort = provider.EffortOptions.FirstOrDefault(o => o.Value == _desiredEffortValue) ?? provider.EffortOptions[0];

        _ = RefreshProviderCatalogAsync(provider);
    }

    /// <summary>Builds the Claude provider descriptor; pass a null factory when the CLI is missing.</summary>
    public static AgentProvider CreateClaudeProvider(Func<string?, string?, IAgentChatSession?>? sessionFactory) => new(
        "Claude",
        L("AiNotAvailable"),
        [
            new AgentOption(L("AiOptionDefault"), null),
            new AgentOption("Fable", "fable"),
            new AgentOption("Opus", "opus"),
            new AgentOption("Sonnet", "sonnet"),
            new AgentOption("Haiku", "haiku"),
        ],
        [
            new AgentOption(L("AiOptionDefault"), null),
            new AgentOption("Low", "low"),
            new AgentOption("Medium", "medium"),
            new AgentOption("High", "high"),
            new AgentOption("xHigh", "xhigh"),
            new AgentOption("Max", "max"),
        ],
        sessionFactory);

    /// <summary>Builds the Codex provider descriptor; pass null factories when the CLI is missing.
    /// The model list here is a snapshot fallback — <paramref name="catalogFetcher"/> refreshes it
    /// from the CLI at runtime.</summary>
    public static AgentProvider CreateCodexProvider(
        Func<string?, string?, IAgentChatSession?>? sessionFactory,
        Func<Task<IReadOnlyList<AgentModelInfo>?>>? catalogFetcher = null) => new(
        "Codex",
        L("AiNotAvailableCodex"),
        [
            new AgentOption(L("AiOptionDefault"), null),
            new AgentOption("GPT-5.5", "gpt-5.5"),
            new AgentOption("GPT-5.4", "gpt-5.4"),
            new AgentOption("GPT-5.4 Mini", "gpt-5.4-mini"),
            new AgentOption("GPT-5.3 Codex Spark", "gpt-5.3-codex-spark"),
        ],
        [
            new AgentOption(L("AiOptionDefault"), null),
            new AgentOption("Low", "low"),
            new AgentOption("Medium", "medium"),
            new AgentOption("High", "high"),
            new AgentOption("xHigh", "xhigh"),
        ],
        sessionFactory,
        catalogFetcher);

    /// <summary>Builds the descriptor for a user-defined API provider. Its models come from
    /// the user's configuration; efforts only apply to the OpenAI API (reasoning_effort).
    /// Pass a null factory when the provider is missing its API key or model list.</summary>
    public static AgentProvider CreateCustomProvider(
        CustomAiProvider config,
        Func<string?, string?, IAgentChatSession?>? sessionFactory)
    {
        var models = new List<AgentOption>();
        foreach (var model in config.Models)
            models.Add(new AgentOption(model, model));
        if (models.Count == 0)
            models.Add(new AgentOption(L("AiOptionDefault"), null));

        var efforts = config.ApiType == CustomAiApiType.OpenAI
            ? new List<AgentOption>
            {
                new(L("AiOptionDefault"), null),
                new("Minimal", "minimal"),
                new("Low", "low"),
                new("Medium", "medium"),
                new("High", "high"),
            }
            : new List<AgentOption> { new(L("AiOptionDefault"), null) };

        return new AgentProvider(config.Name, L("AiCustomNotConfigured"), models, efforts, sessionFactory);
    }

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();

    public ObservableCollection<AgentProvider> Providers { get; }

    /// <summary>Set by the hosting view: opens the custom-providers dialog and, when it is
    /// saved, rebuilds the provider list (via <see cref="ReplaceProviders"/>).</summary>
    public Func<Task>? ManageProvidersInteraction { get; set; }

    [RelayCommand]
    private async Task ManageProvidersAsync()
    {
        if (ManageProvidersInteraction is not null)
            await ManageProvidersInteraction();
    }

    /// <summary>Swaps the provider list in place (after the custom providers changed),
    /// keeping the current selection by label when it still exists.</summary>
    public void ReplaceProviders(IReadOnlyList<AgentProvider> providers)
    {
        if (providers.Count == 0)
            return;

        var currentLabel = SelectedProvider?.Label;
        _switchingProvider = true;
        try
        {
            _fetchedCatalogs.Clear();
            Providers.Clear();
            foreach (var provider in providers)
                Providers.Add(provider);
        }
        finally
        {
            _switchingProvider = false;
        }

        SelectedProvider = providers.FirstOrDefault(p => p.Label == currentLabel && p.IsAvailable)
            ?? providers.FirstOrDefault(p => p.IsAvailable)
            ?? providers[0];
    }

    [ObservableProperty]
    private IReadOnlyList<AgentOption> _modelOptions;

    [ObservableProperty]
    private IReadOnlyList<AgentOption> _effortOptions;

    [ObservableProperty]
    private AgentProvider _selectedProvider;

    [ObservableProperty]
    private string _unavailableText;

    [ObservableProperty]
    private string _inputText = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isAvailable;

    /// <summary>Whether the terminal currently has a selection; kept up to date by the
    /// hosting view. Only drives the "selection will be attached" hint — the selection
    /// text itself is read fresh at send time.</summary>
    [ObservableProperty]
    private bool _hasTerminalSelection;

    [ObservableProperty]
    private bool _autoRun = true;

    [ObservableProperty]
    private bool _showCommandOutput;

    /// <summary>Agent mode: the AI panel takes over the whole tab and the terminal is hidden,
    /// so executed commands always show in the chat. The view reacts to this to relayout.</summary>
    [ObservableProperty]
    private bool _agentMode;

    [ObservableProperty]
    private AgentOption _selectedModel;

    [ObservableProperty]
    private AgentOption _selectedEffort;

    [ObservableProperty]
    private string _statusText;

    public bool CanSend => IsAvailable && !IsBusy;

    public bool CanStartNewConversation => !IsBusy;

    public bool CanStop => IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        NewConversationCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanStop));
    }

    partial void OnIsAvailableChanged(bool value) => SendCommand.NotifyCanExecuteChanged();

    // Model/effort are process-launch flags, so changing them requires a fresh session.
    partial void OnSelectedModelChanged(AgentOption value)
    {
        if (_switchingProvider)
            return;
        _desiredModelValue = value?.Value;
        RememberProviderChoice();
        ResetSessionForSettingsChange();
        PersistOptions();
    }

    partial void OnSelectedEffortChanged(AgentOption value)
    {
        if (_switchingProvider)
            return;
        _desiredEffortValue = value?.Value;
        RememberProviderChoice();
        ResetSessionForSettingsChange();
        PersistOptions();
    }

    private void RememberProviderChoice()
    {
        if (_desiredModelValue is null && _desiredEffortValue is null)
            _providerChoices.Remove(SelectedProvider.Label);
        else
            _providerChoices[SelectedProvider.Label] =
                new AiProviderChoice { Model = _desiredModelValue, Effort = _desiredEffortValue };
    }

    partial void OnAutoRunChanged(bool value) => PersistOptions();

    partial void OnShowCommandOutputChanged(bool value) => PersistOptions();

    partial void OnAgentModeChanged(bool value) => PersistOptions();

    partial void OnSelectedProviderChanged(AgentProvider value)
    {
        // Null arrives transiently while ReplaceProviders swaps the collection out
        // under the ComboBox; the swap ends by assigning a real selection.
        if (value is null || _switchingProvider)
            return;

        var (models, efforts) = _fetchedCatalogs.TryGetValue(value, out var fetched)
            ? fetched
            : (value.ModelOptions, value.EffortOptions);

        // Restore this provider's own remembered model/effort (it may only resolve once
        // the dynamic catalog arrives; RefreshProviderCatalogAsync re-applies it then).
        if (_providerChoices.TryGetValue(value.Label, out var savedChoice))
        {
            _desiredModelValue = savedChoice.Model;
            _desiredEffortValue = savedChoice.Effort;
        }
        else
        {
            _desiredModelValue = null;
            _desiredEffortValue = null;
        }

        _switchingProvider = true;
        try
        {
            ModelOptions = models;
            EffortOptions = efforts;
            SelectedModel = models.FirstOrDefault(o => o.Value == _desiredModelValue) ?? models[0];
            SelectedEffort = efforts.FirstOrDefault(o => o.Value == _desiredEffortValue) ?? efforts[0];
            UnavailableText = value.UnavailableMessage;
            IsAvailable = value.IsAvailable;
        }
        finally
        {
            _switchingProvider = false;
        }

        PersistOptions();

        if (!value.IsAvailable)
        {
            StatusText = value.UnavailableMessage;
            DetachAndDisposeSession();
            return;
        }

        StatusText = "";
        ResetSessionForSettingsChange();
        _ = RefreshProviderCatalogAsync(value);
    }

    private void PersistOptions() => _persistOptions?.Invoke(new AiPanelOptions(
        SelectedProvider.Label,
        new Dictionary<string, AiProviderChoice>(_providerChoices),
        AutoRun,
        ShowCommandOutput,
        AgentMode));

    /// <summary>
    /// Asks the provider's CLI for its live model catalog and swaps the option lists in
    /// place, keeping the user's current selection when it still exists. The static lists
    /// stay as fallback on failure, so this is silent best-effort.
    /// </summary>
    private async Task RefreshProviderCatalogAsync(AgentProvider provider)
    {
        if (provider.CatalogFetcher is null || !provider.IsAvailable || _fetchedCatalogs.ContainsKey(provider))
            return;

        IReadOnlyList<AgentModelInfo>? models;
        try
        {
            models = await provider.CatalogFetcher().WaitAsync(_cts.Token);
        }
        catch
        {
            return;
        }

        if (models is null || models.Count == 0 || _cts.IsCancellationRequested)
            return;

        var modelOptions = new List<AgentOption> { new(L("AiOptionDefault"), null) };
        foreach (var model in models)
            modelOptions.Add(new AgentOption(model.DisplayName, model.Id));

        // Efforts are per-model in the catalog but identical across models in practice;
        // offer the union in first-seen order.
        var effortOptions = new List<AgentOption> { new(L("AiOptionDefault"), null) };
        var seenEfforts = new HashSet<string>();
        foreach (var model in models)
        foreach (var effort in model.ReasoningEfforts)
        {
            if (seenEfforts.Add(effort))
                effortOptions.Add(new AgentOption(EffortLabel(effort), effort));
        }

        Dispatcher.UIThread.Post(() =>
        {
            _fetchedCatalogs[provider] = (modelOptions, effortOptions);
            if (SelectedProvider != provider)
                return;

            var previousModel = SelectedModel?.Value;
            var previousEffort = SelectedEffort?.Value;

            _switchingProvider = true;
            try
            {
                ModelOptions = modelOptions;
                EffortOptions = effortOptions;
                // Prefer the remembered choice — it may only exist in the fetched catalog.
                SelectedModel = modelOptions.FirstOrDefault(o => o.Value == _desiredModelValue)
                    ?? modelOptions.FirstOrDefault(o => o.Value == previousModel)
                    ?? modelOptions[0];
                SelectedEffort = effortOptions.FirstOrDefault(o => o.Value == _desiredEffortValue)
                    ?? effortOptions.FirstOrDefault(o => o.Value == previousEffort)
                    ?? effortOptions[0];
            }
            finally
            {
                _switchingProvider = false;
            }

            // Only restart the session if the refresh actually invalidated a selection.
            if (SelectedModel?.Value != previousModel || SelectedEffort?.Value != previousEffort)
                ResetSessionForSettingsChange();
        });
    }

    private static string EffortLabel(string effort) => effort switch
    {
        "minimal" => "Minimal",
        "low" => "Low",
        "medium" => "Medium",
        "high" => "High",
        "xhigh" => "xHigh",
        "max" => "Max",
        _ => effort,
    };

    [RelayCommand(CanExecute = nameof(CanStartNewConversation))]
    private void NewConversation()
    {
        DetachAndDisposeSession();
        Messages.Clear();
        StatusText = IsAvailable ? "" : UnavailableText;
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (!IsAvailable)
            return;

        var prompt = InputText.Trim();
        if (prompt.Length == 0)
            return;

        // A terminal selection always rides along when present; the panel shows a hint
        // (bound to HasTerminalSelection) so the user knows it will be attached.
        var displayText = prompt;
        var payload = prompt;
        var selection = _takeSelection()?.Trim();
        if (!string.IsNullOrEmpty(selection))
        {
            displayText = $"{prompt}\n\n{L("AiSelectionAttached")}";
            payload = $"{prompt}\n\nHere is the relevant terminal output:\n```\n{selection}\n```";
        }

        Messages.Add(new ChatMessageViewModel(ChatRole.User, displayText));
        _pendingAssistant = new ChatMessageViewModel(ChatRole.Assistant, "");
        Messages.Add(_pendingAssistant);

        InputText = "";
        IsBusy = true;
        StatusText = L("AiWaiting");
        StartThinking();

        _stopRequested = false;
        _turnCts?.Dispose();
        _turnCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

        await SendToSessionAsync(payload);
    }

    /// <summary>Aborts the current turn: cancels the running command (if any), asks the
    /// backend to interrupt the in-flight model turn, and returns the panel to idle. The
    /// interrupted turn's late completion event is swallowed.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        if (!IsBusy)
            return;

        _stopRequested = true;
        try
        {
            _turnCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (_session is { } session)
            _ = SafeInterruptAsync(session);

        StopThinking();
        if (_pendingAssistant is not null && string.IsNullOrEmpty(_pendingAssistant.Text))
            Messages.Remove(_pendingAssistant);
        _pendingAssistant = null;
        IsBusy = false;
        StatusText = L("AiStopped");
    }

    private static async Task SafeInterruptAsync(IAgentChatSession session)
    {
        try
        {
            await session.InterruptAsync();
        }
        catch
        {
            // Best-effort: the process may be exiting; local cancellation already applied.
        }
    }

    private async Task SendToSessionAsync(string payload)
    {
        try
        {
            if (_session is null)
            {
                _session = SelectedProvider.SessionFactory?.Invoke(SelectedModel?.Value, SelectedEffort?.Value);
                if (_session is null)
                {
                    IsAvailable = false;
                    IsBusy = false;
                    StatusText = SelectedProvider.UnavailableMessage;
                    StopThinking();
                    _pendingAssistant = null;
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

            // The per-turn token lets Stop abort HTTP-backed sessions mid-stream; CLI-backed
            // sessions return quickly here and are interrupted via InterruptAsync instead.
            await _session.SendAsync(payload, _turnCts?.Token ?? _cts.Token);
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
        StopThinking();
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
        {
            if (delta.Length > 0)
                StopThinking();

            _pendingAssistant.Text += delta;
        }
    });

    private void OnTurnCompleted(AgentTurnResult result) => Dispatcher.UIThread.Post(() =>
    {
        // The completion of a turn the user stopped (interrupted backends still emit one):
        // the panel is already idle and shows "Stopped"; don't run its command or overwrite
        // the status.
        if (_stopRequested)
        {
            _stopRequested = false;
            return;
        }

        var answer = "";
        if (_pendingAssistant is not null)
        {
            StopThinking();
            if (string.IsNullOrEmpty(_pendingAssistant.Text))
                _pendingAssistant.Text = result.Text;
            answer = _pendingAssistant.Text;
            _pendingAssistant = null;
        }

        // Codex reports token usage but no dollar cost; omit the $0.0000.
        StatusText = result.IsError
            ? L("AiTurnError")
            : result.CostUsd > 0
                ? L("AiTurnCost", result.OutputTokens, result.CostUsd)
                : L("AiTurnTokens", result.OutputTokens);

        var (command, transfer) = AutoRun && !result.IsError
            ? ExtractFirstAction(answer)
            : (null, (AgentFileTransfer?)null);
        if (transfer is not null && _transferFiles is not null)
        {
            // Keep IsBusy true across the whole auto loop.
            _ = TransferAndContinueAsync(transfer);
            return;
        }

        if (command is not null && _runCaptured is not null)
        {
            _ = RunAndContinueAsync(command);
            return;
        }

        IsBusy = false;
    });

    private async Task RunAndContinueAsync(string command)
    {
        Messages.Add(new ChatMessageViewModel(ChatRole.Tool, $"$ {command}"));

        string output;
        var turnToken = _turnCts?.Token ?? _cts.Token;
        try
        {
            output = await _runCaptured!(command, turnToken);
        }
        catch (OperationCanceledException)
        {
            output = "[command cancelled]";
        }
        catch (Exception ex)
        {
            output = $"[command failed: {ex.Message}]";
        }

        // Stopped or disposed while the command ran: don't feed anything back.
        if (turnToken.IsCancellationRequested || _cts.IsCancellationRequested)
            return;

        Messages.Add(new ChatMessageViewModel(ChatRole.Tool, TruncateForDisplay(output)));

        _pendingAssistant = new ChatMessageViewModel(ChatRole.Assistant, "");
        Messages.Add(_pendingAssistant);
        StatusText = L("AiWaiting");
        StartThinking();

        await SendToSessionAsync($"Output of `{command}`:\n```\n{output}\n```");
    }

    private async Task TransferAndContinueAsync(AgentFileTransfer transfer)
    {
        var label = transfer.IsUpload ? "upload" : "download";
        var description = $"⇅ {label}: {string.Join(", ", transfer.Sources)}"
            + (transfer.Destination is null ? "" : $" -> {transfer.Destination}");
        Messages.Add(new ChatMessageViewModel(ChatRole.Tool, description));

        string outcome;
        var turnToken = _turnCts?.Token ?? _cts.Token;
        try
        {
            outcome = await _transferFiles!(transfer, turnToken);
        }
        catch (OperationCanceledException)
        {
            outcome = "[transfer cancelled]";
        }
        catch (Exception ex)
        {
            outcome = $"[transfer failed: {ex.Message}]";
        }

        // Stopped or disposed while the transfer ran: don't feed anything back.
        if (turnToken.IsCancellationRequested || _cts.IsCancellationRequested)
            return;

        Messages.Add(new ChatMessageViewModel(ChatRole.Tool, TruncateForDisplay(outcome)));

        _pendingAssistant = new ChatMessageViewModel(ChatRole.Assistant, "");
        Messages.Add(_pendingAssistant);
        StatusText = L("AiWaiting");
        StartThinking();

        await SendToSessionAsync($"Result of the {label}:\n```\n{outcome}\n```");
    }

    private void OnErrored(string message) => Dispatcher.UIThread.Post(() =>
    {
        // stderr can be chatty; only surface it as a system bubble when a turn is in flight.
        if (IsBusy)
        {
            Messages.Add(new ChatMessageViewModel(ChatRole.System, message));
            StopThinking();
            _pendingAssistant = null;
            IsBusy = false;
            StatusText = L("AiTurnError");
        }
    });

    private void OnExited() => Dispatcher.UIThread.Post(() =>
    {
        StopThinking();
        _pendingAssistant = null;
        IsBusy = false;
        _sessionStarted = false;
        StatusText = L("AiSessionEnded");
    });

    private int _thinkingStep;

    private void StartThinking()
    {
        _thinkingStep = 2;
        if (_pendingAssistant is not null)
        {
            _pendingAssistant.ThinkingText = BuildThinkingText(_thinkingStep);
            _pendingAssistant.IsThinking = true;
        }

        _thinkingTimer.Stop();
        _thinkingTimer.Start();
    }

    private void StopThinking()
    {
        _thinkingTimer.Stop();
        if (_pendingAssistant is not null)
            _pendingAssistant.IsThinking = false;
    }

    private void OnThinkingTimerTick(object? sender, EventArgs e)
    {
        if (_pendingAssistant is null || !_pendingAssistant.ShowsThinking)
        {
            _thinkingTimer.Stop();
            return;
        }

        _thinkingStep = (_thinkingStep + 1) % 3;
        _pendingAssistant.ThinkingText = BuildThinkingText(_thinkingStep);
    }

    private static string BuildThinkingText(int step) => L("AiThinking") + new string('.', step + 1);

    /// <summary>Finds the first actionable fenced block in the answer — a shell command or
    /// a file-transfer request, whichever the assistant emitted first.</summary>
    internal static (string? Command, AgentFileTransfer? Transfer) ExtractFirstAction(string text)
    {
        var commandMatch = FencedBlock.Match(text);
        var transferMatch = TransferBlock.Match(text);

        if (transferMatch.Success && (!commandMatch.Success || transferMatch.Index < commandMatch.Index))
        {
            var transfer = ParseTransfer(transferMatch.Groups[1].Value, transferMatch.Groups[2].Value);
            if (transfer is not null)
                return (null, transfer);
        }

        if (commandMatch.Success)
        {
            var body = commandMatch.Groups[1].Value.Trim();
            if (body.Length > 0)
                return (body, null);
        }

        return (null, null);
    }

    private static AgentFileTransfer? ParseTransfer(string kind, string body)
    {
        var isUpload = kind.Equals("upload", StringComparison.OrdinalIgnoreCase);
        var sources = new List<string>();
        string? destination = null;

        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            var arrow = line.IndexOf("->", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                var source = line[..arrow].Trim();
                var target = line[(arrow + 2)..].Trim();
                if (source.Length > 0)
                    sources.Add(source);
                // All lines share one transfer session, so the first destination wins.
                if (target.Length > 0)
                    destination ??= target;
            }
            else
            {
                sources.Add(line);
            }
        }

        return sources.Count == 0 ? null : new AgentFileTransfer(isUpload, sources, destination);
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
        _turnCts?.Dispose();
        _turnCts = null;
        StopThinking();
        _thinkingTimer.Tick -= OnThinkingTimerTick;
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
