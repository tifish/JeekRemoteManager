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
/// One selectable AI backend (Claude / Codex / Grok): its display label, the model/effort
/// choices it supports, and a factory that creates a session for a (model, effort) pair. A
/// null <paramref name="SessionFactory"/> means the CLI is not installed; the provider still
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
    Func<Task<IReadOnlyList<AgentModelInfo>?>>? CatalogFetcher = null,
    IReadOnlyList<AgentModelInfo>? PersistedCatalog = null,
    Action<IReadOnlyList<AgentModelInfo>>? PersistCatalog = null)
{
    public bool IsAvailable => SessionFactory is not null;
}

/// <summary>
/// Drives one AI chat conversation scoped to a single terminal tab. Two channels:
/// local agent tools (Claude/Codex/Grok CLI) run on the host; remote work uses an autonomous
/// command loop — each time the assistant emits a fenced shell block, the harness executes
/// it on the connected server (via <c>runCaptured</c>), feeds the output back, and continues.
/// Model and reasoning effort are chosen up front and become CLI flags when the session
/// process starts; changing them starts a fresh session.
/// </summary>
public sealed partial class AgentChatViewModel : ViewModelBase, IAsyncDisposable
{
    // Only blocks explicitly tagged as shell are executable; plain ``` blocks (quotes,
    // translations, sample output) must never reach the server. A "-danger" suffix is the
    // model self-reporting a destructive command; it forces the confirmation flow.
    private static readonly Regex FencedBlock = new(
        "```(?:bash|sh|shell)(?<danger>-danger)?[ \\t]*\\n(?<body>.*?)```",
        RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ```upload / ```download blocks request a ZMODEM file transfer through the terminal;
    // each body line is "SOURCE -> DESTINATION" (destination optional).
    private static readonly Regex TransferBlock = new(
        "```(upload|download)[ \\t]*\\n(.*?)```",
        RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Agent CLIs do not expose one shared structured authentication error. Keep this list
    // deliberately narrow: these phrases describe credentials, while ordinary transport,
    // quota, model, and tool errors leave the current session available for retry.
    private static readonly string[] AuthenticationErrorMarkers =
    [
        "401 unauthorized",
        "not logged in",
        "not authenticated",
        "login required",
        "authentication required",
        "please log in",
        "please login",
        "run /login",
        "token expired",
        "token has expired",
        "expired token",
        "invalid api key",
        "invalid_api_key",
        "authentication token has expired",
        "refresh token has expired",
        "refresh token is expired",
        "failed to refresh token",
        "could not refresh token",
    ];

    // Returns the terminal selection and clears it, so the same selection isn't
    // silently re-attached to the next message.
    private readonly Func<string?> _takeSelection;
    private readonly Func<string, CancellationToken, Task<string>>? _runCaptured;
    private readonly Func<AgentFileTransfer, CancellationToken, Task<string>>? _transferFiles;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _thinkingTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };

    private readonly Dictionary<AgentProvider, (IReadOnlyList<AgentOption> Models, IReadOnlyList<AgentOption> Efforts)> _fetchedCatalogs = new();
    private readonly HashSet<AgentProvider> _catalogRefreshesStarted = new();
    private readonly HashSet<string> _persistedCatalogProviderLabels = new(StringComparer.Ordinal);
    private readonly HashSet<string> _liveCatalogProviderLabels = new(StringComparer.Ordinal);
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
        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMessages));
        Providers = new ObservableCollection<AgentProvider>(providers);
        InitializePersistedCatalogs(providers);

        var provider = providers.FirstOrDefault(p => p.Label == initialOptions?.Provider && p.IsAvailable)
            ?? providers.FirstOrDefault(p => p.IsAvailable)
            ?? providers[0];
        _selectedProvider = provider;
        _isAvailable = provider.IsAvailable;
        _statusText = provider.IsAvailable ? "" : provider.UnavailableMessage;
        _unavailableText = provider.UnavailableMessage;
        var initialCatalog = _fetchedCatalogs.TryGetValue(provider, out var persisted)
            ? persisted
            : (provider.ModelOptions, provider.EffortOptions);
        _modelOptions = initialCatalog.Item1;
        _effortOptions = initialCatalog.Item2;

        if (initialOptions is not null)
        {
            foreach (var (label, choice) in initialOptions.ProviderChoices)
                _providerChoices[label] = new AiProviderChoice { Model = choice.Model, Effort = choice.Effort };
            _autoRun = initialOptions.AutoRun;
            _autoApproveDangerousCommands = initialOptions.AutoApproveDangerousCommands;
            _showCommandOutput = initialOptions.ShowCommandOutput;
            _agentMode = initialOptions.AgentMode;
        }

        if (_providerChoices.TryGetValue(provider.Label, out var savedChoice))
        {
            _desiredModelValue = savedChoice.Model;
            _desiredEffortValue = savedChoice.Effort;
        }
        _selectedModel = _modelOptions.FirstOrDefault(o => o.Value == _desiredModelValue) ?? _modelOptions[0];
        _selectedEffort = _effortOptions.FirstOrDefault(o => o.Value == _desiredEffortValue) ?? _effortOptions[0];

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
        Func<Task<IReadOnlyList<AgentModelInfo>?>>? catalogFetcher = null,
        IReadOnlyList<AgentModelInfo>? persistedCatalog = null,
        Action<IReadOnlyList<AgentModelInfo>>? persistCatalog = null) => new(
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
        catalogFetcher,
        persistedCatalog,
        persistCatalog);

    /// <summary>Builds the Grok Build provider descriptor; pass null factories when the CLI is
    /// missing. The model list is a snapshot fallback — <paramref name="catalogFetcher"/>
    /// refreshes it from the CLI / models cache at runtime.</summary>
    public static AgentProvider CreateGrokProvider(
        Func<string?, string?, IAgentChatSession?>? sessionFactory,
        Func<Task<IReadOnlyList<AgentModelInfo>?>>? catalogFetcher = null,
        IReadOnlyList<AgentModelInfo>? persistedCatalog = null,
        Action<IReadOnlyList<AgentModelInfo>>? persistCatalog = null) => new(
        "Grok",
        L("AiNotAvailableGrok"),
        [
            new AgentOption(L("AiOptionDefault"), null),
            new AgentOption("Grok 4.5", "grok-4.5"),
            new AgentOption("Composer 2.5", "grok-composer-2.5-fast"),
        ],
        [
            new AgentOption(L("AiOptionDefault"), null),
            new AgentOption("Low", "low"),
            new AgentOption("Medium", "medium"),
            new AgentOption("High", "high"),
        ],
        sessionFactory,
        catalogFetcher,
        persistedCatalog,
        persistCatalog);

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

    /// <summary>True while the transcript contains at least one message. The view uses this
    /// to swap the empty-conversation welcome state for the live transcript.</summary>
    public bool HasMessages => Messages.Count > 0;

    public ObservableCollection<AgentProvider> Providers { get; }

    /// <summary>Provider labels whose persisted catalog was loaded when this panel was built.
    /// Exposed so Debug MCP can verify restart behavior independently of a fast live refresh.</summary>
    public IReadOnlyList<string> PersistedCatalogProviderLabels =>
        _persistedCatalogProviderLabels.OrderBy(label => label, StringComparer.Ordinal).ToArray();

    /// <summary>Provider labels whose catalog has been refreshed live in this process.</summary>
    public IReadOnlyList<string> LiveCatalogProviderLabels =>
        _liveCatalogProviderLabels.OrderBy(label => label, StringComparer.Ordinal).ToArray();

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
            _catalogRefreshesStarted.Clear();
            _persistedCatalogProviderLabels.Clear();
            _liveCatalogProviderLabels.Clear();
            Providers.Clear();
            foreach (var provider in providers)
                Providers.Add(provider);
            InitializePersistedCatalogs(providers);
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
    private bool _autoApproveDangerousCommands;

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

    partial void OnAutoApproveDangerousCommandsChanged(bool value) => PersistOptions();

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
        AutoApproveDangerousCommands,
        ShowCommandOutput,
        AgentMode));

    /// <summary>
    /// Asks the provider's CLI for its live model catalog and swaps the option lists in
    /// place, keeping the user's current selection when it still exists. The static lists
    /// stay as fallback on failure, so this is silent best-effort.
    /// </summary>
    private async Task RefreshProviderCatalogAsync(AgentProvider provider)
    {
        if (provider.CatalogFetcher is null || !provider.IsAvailable || !_catalogRefreshesStarted.Add(provider))
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

        try
        {
            provider.PersistCatalog?.Invoke(models);
        }
        catch
        {
            // Persistence is best-effort; the freshly fetched catalog is still usable.
        }

        var catalogOptions = CreateCatalogOptions(models);
        if (catalogOptions is null)
            return;

        var (modelOptions, effortOptions) = catalogOptions.Value;

        Dispatcher.UIThread.Post(() =>
        {
            _fetchedCatalogs[provider] = (modelOptions, effortOptions);
            _liveCatalogProviderLabels.Add(provider.Label);
            OnPropertyChanged(nameof(LiveCatalogProviderLabels));
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

    private void InitializePersistedCatalogs(IEnumerable<AgentProvider> providers)
    {
        foreach (var provider in providers)
        {
            var options = CreateCatalogOptions(provider.PersistedCatalog);
            if (options is null)
                continue;

            _fetchedCatalogs[provider] = options.Value;
            _persistedCatalogProviderLabels.Add(provider.Label);
        }

        OnPropertyChanged(nameof(PersistedCatalogProviderLabels));
    }

    private static (IReadOnlyList<AgentOption> Models, IReadOnlyList<AgentOption> Efforts)?
        CreateCatalogOptions(IReadOnlyList<AgentModelInfo>? models)
    {
        if (models is null || models.Count == 0)
            return null;

        var modelOptions = new List<AgentOption> { new(L("AiOptionDefault"), null) };
        var seenModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models)
        {
            if (!string.IsNullOrWhiteSpace(model.Id) && seenModels.Add(model.Id))
                modelOptions.Add(new AgentOption(model.DisplayName, model.Id));
        }

        if (modelOptions.Count == 1)
            return null;

        // Efforts are per-model in the catalog but identical across models in practice;
        // offer the union in first-seen order.
        var effortOptions = new List<AgentOption> { new(L("AiOptionDefault"), null) };
        var seenEfforts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models)
        foreach (var effort in model.ReasoningEfforts)
        {
            if (!string.IsNullOrWhiteSpace(effort) && seenEfforts.Add(effort))
                effortOptions.Add(new AgentOption(EffortLabel(effort), effort));
        }

        return (modelOptions, effortOptions);
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

        TakePendingConfirmation(L("AiDangerSkipped"));
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
        TakePendingConfirmation(L("AiDangerSkipped"));
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

        if (result.IsError && IsAuthenticationError(answer))
        {
            InvalidateSessionForAuthenticationError();
            return;
        }

        // Codex reports token usage but no dollar cost; omit the $0.0000.
        StatusText = result.IsError
            ? L("AiTurnError")
            : result.CostUsd > 0
                ? L("AiTurnCost", result.OutputTokens, result.CostUsd)
                : L("AiTurnTokens", result.OutputTokens);

        var (command, dangerTagged, transfer) = AutoRun && !result.IsError
            ? ExtractFirstAction(answer)
            : (null, false, (AgentFileTransfer?)null);
        if (transfer is not null && _transferFiles is not null)
        {
            // Keep IsBusy true across the whole auto loop.
            _ = TransferAndContinueAsync(transfer);
            return;
        }

        if (command is not null && _runCaptured is not null)
        {
            // Two independent danger signals: the model self-tagged the block, or the local
            // blacklist matched. Either one pauses the loop unless the user opted into
            // automatically approving potentially destructive commands.
            if (RequiresDangerConfirmation(command, dangerTagged))
            {
                AskDangerConfirmation(command);
                return;
            }

            _ = RunAndContinueAsync(command);
            return;
        }

        IsBusy = false;
    });

    /// <summary>Returns the final safety decision for an auto-run command. Public so the
    /// running UI can be verified through the generic Debug MCP object-path tools without
    /// executing the command.</summary>
    public bool RequiresDangerConfirmation(string command, bool dangerTagged) =>
        !AutoApproveDangerousCommands
        && (dangerTagged || DangerousCommandDetector.IsDangerous(command));

    /// <summary>Shows the same Running… activity bubble used while a remote command
    /// executes. Public for Debug MCP verification without needing a live shell wait.</summary>
    public void DebugShowRunningActivity() => BeginActivityPlaceholder(ActivityKind.Running);

    /// <summary>Removes a temporary activity bubble created for Debug MCP checks.</summary>
    public void DebugClearActivity() => ClearActivityPlaceholder();

    // The dangerous command (and its bubble) waiting for the user's run/skip decision.
    // IsBusy stays true while waiting, so the input box is blocked but Stop still works.
    private string? _pendingDangerCommand;
    private ChatMessageViewModel? _pendingDangerMessage;

    private void AskDangerConfirmation(string command)
    {
        _pendingDangerCommand = command;
        _pendingDangerMessage = new ChatMessageViewModel(ChatRole.Confirmation, command)
        {
            IsAwaitingDecision = true,
        };
        Messages.Add(_pendingDangerMessage);
        StatusText = L("AiDangerWaiting");
    }

    [RelayCommand]
    private void RunPendingCommand()
    {
        var command = TakePendingConfirmation(L("AiDangerRan"));
        if (command is null)
            return;

        IsBusy = true;
        _ = RunAndContinueAsync(command);
    }

    [RelayCommand]
    private async Task SkipPendingCommandAsync()
    {
        var command = TakePendingConfirmation(L("AiDangerSkipped"));
        if (command is null)
            return;

        IsBusy = true;
        _pendingAssistant = new ChatMessageViewModel(ChatRole.Assistant, "");
        Messages.Add(_pendingAssistant);
        StatusText = L("AiWaiting");
        StartThinking();

        await SendToSessionAsync(
            $"The user declined to run this command:\n```\n{command}\n```\n" +
            "Do not run it again. Explain the risk, propose a safer alternative, or ask the user how to proceed.");
    }

    /// <summary>Resolves the pending confirmation bubble with the given outcome line and
    /// returns the command, or null when nothing is pending.</summary>
    private string? TakePendingConfirmation(string decisionText)
    {
        var command = _pendingDangerCommand;
        var message = _pendingDangerMessage;
        _pendingDangerCommand = null;
        _pendingDangerMessage = null;
        if (message is not null)
        {
            message.IsAwaitingDecision = false;
            message.DecisionText = decisionText;
        }

        return command;
    }

    private async Task RunAndContinueAsync(string command)
    {
        Messages.Add(new ChatMessageViewModel(ChatRole.Tool, $"$ {command}"));
        BeginActivityPlaceholder(ActivityKind.Running);

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

        ClearActivityPlaceholder();
        Messages.Add(new ChatMessageViewModel(ChatRole.Tool, TruncateForDisplay(output)));

        BeginActivityPlaceholder(ActivityKind.Thinking);
        await SendToSessionAsync($"Output of `{command}`:\n```\n{output}\n```");
    }

    private async Task TransferAndContinueAsync(AgentFileTransfer transfer)
    {
        var label = transfer.IsUpload ? "upload" : "download";
        var description = $"⇅ {label}: {string.Join(", ", transfer.Sources)}"
            + (transfer.Destination is null ? "" : $" -> {transfer.Destination}");
        Messages.Add(new ChatMessageViewModel(ChatRole.Tool, description));
        BeginActivityPlaceholder(ActivityKind.Running);

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

        ClearActivityPlaceholder();
        Messages.Add(new ChatMessageViewModel(ChatRole.Tool, TruncateForDisplay(outcome)));

        BeginActivityPlaceholder(ActivityKind.Thinking);
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
            if (IsAuthenticationError(message))
                InvalidateSessionForAuthenticationError();
            else
                StatusText = L("AiTurnError");
        }
    });

    private void OnExited() => Dispatcher.UIThread.Post(HandleSessionExited);

    private void HandleSessionExited()
    {
        DetachAndDisposeSession();
        IsBusy = false;
        StatusText = L("AiSessionEnded");
    }

    private void InvalidateSessionForAuthenticationError()
    {
        DetachAndDisposeSession();
        IsBusy = false;
        StatusText = L("AiLoginRequired", SelectedProvider.Label);
    }

    private static bool IsAuthenticationError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        foreach (var marker in AuthenticationErrorMarkers)
        {
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private enum ActivityKind
    {
        Thinking,
        Running,
    }

    private int _thinkingStep;
    private ActivityKind _activityKind = ActivityKind.Thinking;

    /// <summary>Adds an empty assistant bubble and starts its animated status text
    /// (Thinking… / Running…).</summary>
    private void BeginActivityPlaceholder(ActivityKind kind)
    {
        _pendingAssistant = new ChatMessageViewModel(ChatRole.Assistant, "");
        Messages.Add(_pendingAssistant);
        StatusText = kind == ActivityKind.Running ? L("AiWaitingCommand") : L("AiWaiting");
        StartActivity(kind);
    }

    /// <summary>Removes an empty activity placeholder bubble after a command/transfer
    /// finishes. Leaves bubbles that already received streamed text alone.</summary>
    private void ClearActivityPlaceholder()
    {
        StopThinking();
        if (_pendingAssistant is not null && string.IsNullOrEmpty(_pendingAssistant.Text))
            Messages.Remove(_pendingAssistant);
        _pendingAssistant = null;
    }

    private void StartThinking() => StartActivity(ActivityKind.Thinking);

    private void StartActivity(ActivityKind kind)
    {
        _activityKind = kind;
        _thinkingStep = 2;
        if (_pendingAssistant is not null)
        {
            _pendingAssistant.ThinkingText = BuildActivityText(_thinkingStep);
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
        _pendingAssistant.ThinkingText = BuildActivityText(_thinkingStep);
    }

    private string BuildActivityText(int step)
    {
        var label = _activityKind == ActivityKind.Running ? L("AiRunning") : L("AiThinking");
        return label + new string('.', step + 1);
    }

    /// <summary>Finds the first actionable fenced block in the answer — a shell command or
    /// a file-transfer request, whichever the assistant emitted first. <c>Dangerous</c> is
    /// true when the model tagged the block <c>```bash-danger</c>.</summary>
    internal static (string? Command, bool Dangerous, AgentFileTransfer? Transfer) ExtractFirstAction(string text)
    {
        var commandMatch = FencedBlock.Match(text);
        var transferMatch = TransferBlock.Match(text);

        if (transferMatch.Success && (!commandMatch.Success || transferMatch.Index < commandMatch.Index))
        {
            var transfer = ParseTransfer(transferMatch.Groups[1].Value, transferMatch.Groups[2].Value);
            if (transfer is not null)
                return (null, false, transfer);
        }

        if (commandMatch.Success)
        {
            var body = commandMatch.Groups["body"].Value.Trim();
            if (body.Length > 0)
                return (body, commandMatch.Groups["danger"].Success, null);
        }

        return (null, false, null);
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
