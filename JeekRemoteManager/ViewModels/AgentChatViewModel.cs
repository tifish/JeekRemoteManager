using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
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
/// One file transfer requested through the canonical file.upload or file.download tool.
/// Upload: <paramref name="Sources"/> are local Windows files, <paramref name="Destination"/>
/// is a remote directory (null = the shell's current directory). Download: sources are remote
/// files, destination is a local directory (null = the user's Downloads folder).
/// </summary>
public sealed record AgentFileTransfer(bool IsUpload, IReadOnlyList<string> Sources, string? Destination);

/// <summary>Terminal recovery operations the assistant can request explicitly.</summary>
public enum AgentTerminalAction
{
    ForceInterrupt,
    Reconnect,
}

/// <summary>One normalized JeekRemoteManager tool request parsed from an assistant response.</summary>
public sealed record AgentToolRequest(
    string Name,
    string? Command = null,
    bool Dangerous = false,
    AgentFileTransfer? Transfer = null,
    AgentTerminalAction? TerminalAction = null);

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
    Action<IReadOnlyList<AgentModelInfo>>? PersistCatalog = null,
    Func<string?, string?, string, IAgentChatSession?>? ResumeSessionFactory = null)
{
    public bool IsAvailable => SessionFactory is not null;
}

/// <summary>
/// Drives one AI chat conversation scoped to a single terminal tab. Two channels:
/// local agent tools (Claude/Codex/Grok CLI) run on the host; remote work uses an autonomous
/// command loop: each time the assistant emits a canonical jrm-tool request, the harness
/// executes it, feeds the result back, and continues.
/// Model and reasoning effort are chosen up front and become CLI flags when the session
/// process starts; changing them starts a fresh session.
/// </summary>
public sealed partial class AgentChatViewModel : ViewModelBase, IAsyncDisposable
{
    public const int InitialTranscriptMessageLimit = 200;
    public const int TranscriptMessagePageSize = 100;

    private const int StreamRenderIntervalMilliseconds = 50;

    // Canonical tool protocol: one ```jrm-tool block whose first line is a namespaced
    // tool name and whose remaining lines are that tool's payload.
    private static readonly Regex UnifiedToolBlock = new(
        "```jrm-tool[ \\t]*\\n(?<body>.*?)```",
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
    private readonly Func<AgentTerminalAction, CancellationToken, Task<string>>? _runTerminalAction;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _thinkingTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Stopwatch _activityStopwatch = new();
    private readonly object _streamDeltaGate = new();
    private readonly StringBuilder _streamDeltaBuffer = new();
    private bool _streamFlushScheduled;
    private int _streamRenderUpdateCount;

    private readonly Dictionary<AgentProvider, (IReadOnlyList<AgentOption> Models, IReadOnlyList<AgentOption> Efforts)> _fetchedCatalogs = new();
    private readonly HashSet<AgentProvider> _catalogRefreshesStarted = new();
    private readonly HashSet<string> _persistedCatalogProviderLabels = new(StringComparer.Ordinal);
    private readonly HashSet<string> _liveCatalogProviderLabels = new(StringComparer.Ordinal);
    private readonly Action<AiPanelOptions>? _persistOptions;
    private readonly AiConversationStore _conversationStore;
    private readonly string _conversationScopeId;
    private readonly string _connectionLabel;

    private IAgentChatSession? _session;
    private ChatMessageViewModel? _pendingAssistant;
    private bool _sessionStarted;
    private bool _switchingProvider;
    private AiConversation? _currentConversation;
    private string? _pendingResumeSessionId;
    private int _conversationStartIndex;
    private bool _isModelTurnActive;
    private int _transcriptMessageLimit = InitialTranscriptMessageLimit;
    private long _terminalDisconnectNotificationCount;

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
        Action<AiPanelOptions>? persistOptions = null,
        Func<AgentTerminalAction, CancellationToken, Task<string>>? runTerminalAction = null,
        AiConversationStore? conversationStore = null,
        string? conversationScopeId = null,
        string? connectionLabel = null,
        IReadOnlyList<string>? legacyConversationScopeIds = null)
    {
        _takeSelection = takeSelection;
        _runCaptured = runCaptured;
        _transferFiles = transferFiles;
        _runTerminalAction = runTerminalAction;
        _persistOptions = persistOptions;
        _conversationStore = conversationStore ?? new AiConversationStore();
        _conversationScopeId = string.IsNullOrWhiteSpace(conversationScopeId) ? "default" : conversationScopeId;
        _connectionLabel = string.IsNullOrWhiteSpace(connectionLabel) ? _conversationScopeId : connectionLabel;
        MigratedConversationCount = _conversationStore.MigrateScopes(
            _conversationScopeId,
            legacyConversationScopeIds,
            _connectionLabel);
        _thinkingTimer.Tick += OnThinkingTimerTick;
        Messages.CollectionChanged += OnMessagesCollectionChanged;
        ConversationHistory.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasConversationHistory));
            ShowConversationHistoryCommand.NotifyCanExecuteChanged();
        };
        TrashedConversationHistory.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasConversationHistory));
            ShowConversationHistoryCommand.NotifyCanExecuteChanged();
        };
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
        RefreshConversationHistory();
    }

    /// <summary>Builds the Claude provider descriptor; pass a null factory when the CLI is missing.</summary>
    public static AgentProvider CreateClaudeProvider(
        Func<string?, string?, IAgentChatSession?>? sessionFactory,
        Func<string?, string?, string, IAgentChatSession?>? resumeSessionFactory = null) => new(
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
        sessionFactory,
        ResumeSessionFactory: resumeSessionFactory);

    /// <summary>Builds the Codex provider descriptor; pass null factories when the CLI is missing.
    /// The model list here is a snapshot fallback — <paramref name="catalogFetcher"/> refreshes it
    /// from the CLI at runtime.</summary>
    public static AgentProvider CreateCodexProvider(
        Func<string?, string?, IAgentChatSession?>? sessionFactory,
        Func<Task<IReadOnlyList<AgentModelInfo>?>>? catalogFetcher = null,
        IReadOnlyList<AgentModelInfo>? persistedCatalog = null,
        Action<IReadOnlyList<AgentModelInfo>>? persistCatalog = null,
        Func<string?, string?, string, IAgentChatSession?>? resumeSessionFactory = null) => new(
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
        persistCatalog,
        resumeSessionFactory);

    /// <summary>Builds the Grok Build provider descriptor; pass null factories when the CLI is
    /// missing. The model list is a snapshot fallback — <paramref name="catalogFetcher"/>
    /// refreshes it from the CLI / models cache at runtime.</summary>
    public static AgentProvider CreateGrokProvider(
        Func<string?, string?, IAgentChatSession?>? sessionFactory,
        Func<Task<IReadOnlyList<AgentModelInfo>?>>? catalogFetcher = null,
        IReadOnlyList<AgentModelInfo>? persistedCatalog = null,
        Action<IReadOnlyList<AgentModelInfo>>? persistCatalog = null,
        Func<string?, string?, string, IAgentChatSession?>? resumeSessionFactory = null) => new(
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
        persistCatalog,
        resumeSessionFactory);

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

    /// <summary>The bounded message window rendered by the transcript. Full history stays
    /// in <see cref="Messages"/> and in the conversation store; older rows are loaded in
    /// fixed-size pages so the non-virtualized visual tree remains predictable.</summary>
    public ObservableCollection<ChatMessageViewModel> TranscriptMessages { get; } = new();

    public int TranscriptMessageCount => TranscriptMessages.Count;

    public int HiddenEarlierMessageCount => Math.Max(0, Messages.Count - TranscriptMessages.Count);

    public bool HasEarlierMessages => HiddenEarlierMessageCount > 0;

    /// <summary>Number of UI text updates used for the current streamed answer. Public so
    /// Debug MCP can verify that token bursts are coalesced instead of rendered one by one.</summary>
    public int StreamRenderUpdateCount => _streamRenderUpdateCount;

    /// <summary>Characters waiting for the next coalesced UI update. Public for Debug MCP.</summary>
    public int PendingStreamCharacterCount
    {
        get
        {
            lock (_streamDeltaGate)
                return _streamDeltaBuffer.Length;
        }
    }

    public ObservableCollection<AiConversationSummary> ConversationHistory { get; } = new();

    public ObservableCollection<AiConversationSummary> TrashedConversationHistory { get; } = new();

    public bool HasConversationHistory => ConversationHistory.Count > 0 || TrashedConversationHistory.Count > 0;

    /// <summary>Recycle-bin entry count exposed for Debug MCP verification.</summary>
    public int TrashedConversationCount => TrashedConversationHistory.Count;

    /// <summary>Machine-local history directory, exposed for Debug MCP verification.</summary>
    public string ConversationStorePath => _conversationStore.RootPath;

    /// <summary>The stable connection scope used for persistence, exposed for Debug MCP.</summary>
    public string ConversationScopeId => _conversationScopeId;

    /// <summary>Number of legacy path-based records migrated when this panel opened.</summary>
    public int MigratedConversationCount { get; }

    /// <summary>The persisted JeekRemoteManager conversation id currently shown.</summary>
    public string? CurrentConversationId => _currentConversation?.Id;

    /// <summary>The Claude session id, Codex thread id, or Grok session id currently mapped
    /// to the visible transcript.</summary>
    public string? CurrentNativeSessionId => _currentConversation?.NativeSessionId;

    /// <summary>The native id that will be passed to the provider's resume factory on the
    /// next send; exposed for Debug MCP and smoke tests.</summary>
    public string? PendingResumeSessionId => _pendingResumeSessionId;

    /// <summary>Set by the hosting view to show and manage the history/recycle-bin dialog.</summary>
    public Func<Task>? ConversationHistoryInteraction { get; set; }

    public bool CanShowConversationHistory => !IsBusy && HasConversationHistory;

    /// <summary>Selects an available provider by label. Intended for Debug MCP runtime
    /// verification without relying on pointer coordinates in the provider ComboBox.</summary>
    public bool DebugSelectProvider(string label)
    {
        if (IsBusy)
            return false;
        var provider = Providers.FirstOrDefault(item =>
            item.IsAvailable && string.Equals(item.Label, label, StringComparison.Ordinal));
        if (provider is null)
            return false;
        SelectedProvider = provider;
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanShowConversationHistory))]
    private async Task ShowConversationHistoryAsync()
    {
        if (ConversationHistoryInteraction is null)
            return;

        await ConversationHistoryInteraction();
    }

    /// <summary>True while the transcript contains at least one message. The view uses this
    /// to swap the empty-conversation welcome state for the live transcript.</summary>
    public bool HasMessages => Messages.Count > 0;

    [RelayCommand(CanExecute = nameof(HasEarlierMessages))]
    private void LoadEarlierMessages()
    {
        _transcriptMessageLimit = Math.Min(Messages.Count,
            _transcriptMessageLimit + TranscriptMessagePageSize);
        RebuildTranscriptMessages();
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasMessages));

        if (e.Action == NotifyCollectionChangedAction.Add
            && e.NewItems is { Count: > 0 }
            && e.NewStartingIndex >= Messages.Count - e.NewItems.Count)
        {
            foreach (ChatMessageViewModel message in e.NewItems)
                TranscriptMessages.Add(message);
            while (TranscriptMessages.Count > _transcriptMessageLimit)
                TranscriptMessages.RemoveAt(0);
            NotifyTranscriptWindowChanged();
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
            _transcriptMessageLimit = InitialTranscriptMessageLimit;

        RebuildTranscriptMessages();
    }

    private void RebuildTranscriptMessages()
    {
        var start = Math.Max(0, Messages.Count - _transcriptMessageLimit);
        TranscriptMessages.Clear();
        for (var i = start; i < Messages.Count; i++)
            TranscriptMessages.Add(Messages[i]);
        NotifyTranscriptWindowChanged();
    }

    private void NotifyTranscriptWindowChanged()
    {
        OnPropertyChanged(nameof(TranscriptMessageCount));
        OnPropertyChanged(nameof(HiddenEarlierMessageCount));
        OnPropertyChanged(nameof(HasEarlierMessages));
        LoadEarlierMessagesCommand.NotifyCanExecuteChanged();
    }

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

    /// <summary>True only while the selected session has a live model turn that accepts
    /// same-turn input. Public so Debug MCP can verify the running state.</summary>
    public bool IsModelTurnActive => _isModelTurnActive;

    /// <summary>Whether the current busy state can accept a steer message. Command and
    /// transfer activity remains busy but is deliberately not steerable.</summary>
    public bool CanSteer => IsBusy && IsModelTurnActive && _session?.SupportsSteering == true;

    /// <summary>The latest terminal transport state reported by the hosting terminal.
    /// Exposed so Debug MCP can verify disconnect/reconnect feedback in the live panel.</summary>
    public string TerminalConnectionState { get; private set; } = "connected";

    /// <summary>Number of server disconnects surfaced in this panel.</summary>
    public long TerminalDisconnectNotificationCount =>
        Interlocked.Read(ref _terminalDisconnectNotificationCount);

    partial void OnIsBusyChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        SteerCommand.NotifyCanExecuteChanged();
        NewConversationCommand.NotifyCanExecuteChanged();
        ShowConversationHistoryCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanShowConversationHistory));
        OnPropertyChanged(nameof(CanSteer));
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

    /// <summary>Reloads this terminal connection's conversation list from disk. Public so
    /// Debug MCP can verify persistence after a restart.</summary>
    public void RefreshConversationHistory()
    {
        var summaries = _conversationStore.LoadSummaries(_conversationScopeId);
        ConversationHistory.Clear();
        foreach (var summary in summaries)
            ConversationHistory.Add(summary);

        var trashed = _conversationStore.LoadSummaries(_conversationScopeId, deleted: true);
        TrashedConversationHistory.Clear();
        foreach (var summary in trashed)
            TrashedConversationHistory.Add(summary);
        OnPropertyChanged(nameof(TrashedConversationCount));
    }

    /// <summary>Moves an active history entry into the 30-day recycle bin.</summary>
    public bool MoveConversationToTrash(string conversationId)
    {
        if (IsBusy)
            return false;

        var conversation = _conversationStore.Load(conversationId);
        if (conversation is null
            || conversation.DeletedAt is not null
            || !string.Equals(conversation.ScopeId, _conversationScopeId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var isCurrent = string.Equals(CurrentConversationId, conversationId, StringComparison.Ordinal);
        if (isCurrent)
            PersistCurrentConversation();
        if (!_conversationStore.MoveToTrash(conversationId))
            return false;

        if (isCurrent)
            ClearConversationState();
        RefreshConversationHistory();
        return true;
    }

    /// <summary>Returns a recycle-bin entry to normal conversation history.</summary>
    public bool RestoreConversationFromTrash(string conversationId)
    {
        if (IsBusy)
            return false;

        var conversation = _conversationStore.Load(conversationId);
        if (conversation?.DeletedAt is null
            || !string.Equals(conversation.ScopeId, _conversationScopeId, StringComparison.OrdinalIgnoreCase)
            || !_conversationStore.RestoreFromTrash(conversationId))
        {
            return false;
        }

        RefreshConversationHistory();
        return true;
    }

    /// <summary>Permanently removes an entry that is already in the recycle bin.</summary>
    public bool DeleteConversationPermanently(string conversationId)
    {
        if (IsBusy)
            return false;

        var conversation = _conversationStore.Load(conversationId);
        if (conversation?.DeletedAt is null
            || !string.Equals(conversation.ScopeId, _conversationScopeId, StringComparison.OrdinalIgnoreCase)
            || !_conversationStore.DeletePermanently(conversationId))
        {
            return false;
        }

        RefreshConversationHistory();
        return true;
    }

    /// <summary>Restores a persisted transcript and prepares the matching provider-native
    /// session for lazy resume on the next send. Returns false for another connection,
    /// unavailable providers, missing native ids, or corrupt records.</summary>
    public bool RestoreConversation(string conversationId)
    {
        if (IsBusy)
            return false;

        var conversation = _conversationStore.Load(conversationId);
        if (conversation is null
            || conversation.DeletedAt is not null
            || !string.Equals(conversation.ScopeId, _conversationScopeId, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(conversation.NativeSessionId))
        {
            return false;
        }

        var provider = Providers.FirstOrDefault(item =>
            string.Equals(item.Label, conversation.Provider, StringComparison.Ordinal)
            && item.IsAvailable
            && item.ResumeSessionFactory is not null);
        if (provider is null)
            return false;

        PersistCurrentConversation();
        DetachAndDisposeSession();
        ApplyRestoredProvider(provider, conversation.Model, conversation.Effort);

        Messages.Clear();
        foreach (var saved in conversation.Messages)
        {
            if (!Enum.TryParse<ChatRole>(saved.Role, ignoreCase: true, out var role))
                continue;
            Messages.Add(new ChatMessageViewModel(role, saved.Text)
            {
                IsAwaitingDecision = saved.IsAwaitingDecision,
                DecisionText = saved.DecisionText,
            });
        }

        _currentConversation = conversation;
        _pendingResumeSessionId = conversation.NativeSessionId;
        _conversationStartIndex = 0;
        StatusText = L("AiConversationRestored");
        OnPropertyChanged(nameof(CurrentConversationId));
        OnPropertyChanged(nameof(CurrentNativeSessionId));
        PersistOptions();
        RefreshConversationHistory();
        return true;
    }

    private void ApplyRestoredProvider(AgentProvider provider, string? model, string? effort)
    {
        var (models, efforts) = _fetchedCatalogs.TryGetValue(provider, out var fetched)
            ? fetched
            : (provider.ModelOptions, provider.EffortOptions);
        models = IncludeHistoricalOption(models, model);
        efforts = IncludeHistoricalOption(efforts, effort);

        _switchingProvider = true;
        try
        {
            SelectedProvider = provider;
            ModelOptions = models;
            EffortOptions = efforts;
            SelectedModel = models.First(item => item.Value == model);
            SelectedEffort = efforts.First(item => item.Value == effort);
            _desiredModelValue = model;
            _desiredEffortValue = effort;
            UnavailableText = provider.UnavailableMessage;
            IsAvailable = true;
            RememberProviderChoice();
        }
        finally
        {
            _switchingProvider = false;
        }
    }

    private static IReadOnlyList<AgentOption> IncludeHistoricalOption(
        IReadOnlyList<AgentOption> options,
        string? value)
    {
        if (options.Any(item => item.Value == value))
            return options;
        return options.Concat([new AgentOption(value!, value)]).ToArray();
    }

    private void BeginConversation(string firstUserMessage)
    {
        if (SelectedProvider.ResumeSessionFactory is null)
            return;

        var model = SelectedModel?.Value;
        var effort = SelectedEffort?.Value;
        if (_currentConversation is not null
            && string.Equals(_currentConversation.Provider, SelectedProvider.Label, StringComparison.Ordinal)
            && _currentConversation.Model == model
            && _currentConversation.Effort == effort)
        {
            return;
        }

        PersistCurrentConversation();
        _conversationStartIndex = Messages.Count;
        _currentConversation = new AiConversation
        {
            ScopeId = _conversationScopeId,
            ConnectionLabel = _connectionLabel,
            Provider = SelectedProvider.Label,
            Model = model,
            Effort = effort,
            Title = BuildConversationTitle(firstUserMessage),
        };
        _pendingResumeSessionId = null;
        OnPropertyChanged(nameof(CurrentConversationId));
        OnPropertyChanged(nameof(CurrentNativeSessionId));
    }

    private static string BuildConversationTitle(string text)
    {
        var title = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (title.Contains("  ", StringComparison.Ordinal))
            title = title.Replace("  ", " ", StringComparison.Ordinal);
        const int limit = 80;
        return title.Length <= limit ? title : title[..limit] + "…";
    }

    private void PersistCurrentConversation()
    {
        var conversation = _currentConversation;
        if (conversation is null)
            return;

        conversation.UpdatedAt = DateTimeOffset.Now;
        conversation.Messages = Messages
            .Skip(Math.Clamp(_conversationStartIndex, 0, Messages.Count))
            .Where(message => !message.IsAssistant || message.HasText)
            .Select(message => new AiConversationMessage
            {
                Role = message.Role.ToString(),
                Text = message.Text,
                IsAwaitingDecision = message.IsAwaitingDecision,
                DecisionText = message.DecisionText,
            })
            .ToList();
        try
        {
            _conversationStore.Save(conversation);
            RefreshConversationHistory();
        }
        catch
        {
            // History persistence is best-effort and must never block the live chat.
        }
    }

    private void OnSessionInitialized(string sessionId) => Dispatcher.UIThread.Post(() =>
    {
        if (_currentConversation is null)
            return;
        _currentConversation.NativeSessionId = sessionId;
        _pendingResumeSessionId = null;
        OnPropertyChanged(nameof(CurrentNativeSessionId));
        PersistCurrentConversation();
    });

    [RelayCommand(CanExecute = nameof(CanStartNewConversation))]
    private void NewConversation()
    {
        PersistCurrentConversation();
        ClearConversationState();
        RefreshConversationHistory();
    }

    private void ClearConversationState()
    {
        DetachAndDisposeSession();
        DiscardPendingTextDeltas();
        _currentConversation = null;
        _pendingResumeSessionId = null;
        _conversationStartIndex = 0;
        Messages.Clear();
        StatusText = IsAvailable ? "" : UnavailableText;
        OnPropertyChanged(nameof(CurrentConversationId));
        OnPropertyChanged(nameof(CurrentNativeSessionId));
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (!IsAvailable)
            return;

        var message = TakeUserMessage();
        if (message is null)
            return;

        BeginConversation(message.Value.DisplayText);
        Messages.Add(new ChatMessageViewModel(ChatRole.User, message.Value.DisplayText));
        _pendingAssistant = new ChatMessageViewModel(ChatRole.Assistant, "");
        Messages.Add(_pendingAssistant);
        DiscardPendingTextDeltas();
        _streamRenderUpdateCount = 0;
        OnPropertyChanged(nameof(StreamRenderUpdateCount));
        PersistCurrentConversation();

        InputText = "";
        IsBusy = true;
        StatusText = L("AiWaiting");
        StartThinking();

        _stopRequested = false;
        _turnCts?.Dispose();
        _turnCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

        await SendToSessionAsync(message.Value.Payload);
    }

    [RelayCommand(CanExecute = nameof(CanSteer))]
    private async Task SteerAsync()
    {
        if (!CanSteer || _session is not { } session)
            return;

        var draft = InputText;
        var message = TakeUserMessage();
        if (message is null)
            return;

        // Split the streaming answer at the insertion point so transcript order remains
        // assistant output -> steering message -> continued assistant output.
        var previousAssistant = _pendingAssistant;
        var previousIndex = previousAssistant is null ? -1 : Messages.IndexOf(previousAssistant);
        var previousWasThinking = previousAssistant?.IsThinking == true;
        StopThinking();
        if (previousAssistant is not null && string.IsNullOrEmpty(previousAssistant.Text))
            Messages.Remove(previousAssistant);

        var userMessage = new ChatMessageViewModel(ChatRole.User, message.Value.DisplayText);
        var continuedAssistant = new ChatMessageViewModel(ChatRole.Assistant, "");
        Messages.Add(userMessage);
        Messages.Add(continuedAssistant);
        _pendingAssistant = continuedAssistant;
        InputText = "";
        StatusText = L("AiWaiting");
        StartThinking();

        try
        {
            await session.SteerAsync(message.Value.Payload, _turnCts?.Token ?? _cts.Token);
        }
        catch (Exception ex)
        {
            // A failed write must not visually pretend that the active turn accepted the
            // steer. Restore the original stream bubble and keep the draft available.
            if (ReferenceEquals(_pendingAssistant, continuedAssistant))
            {
                StopThinking();
                if (continuedAssistant.Text.Length > 0 && previousAssistant is not null)
                    previousAssistant.Text += continuedAssistant.Text;
                Messages.Remove(continuedAssistant);
                Messages.Remove(userMessage);
                if (previousAssistant is not null && !Messages.Contains(previousAssistant))
                    Messages.Insert(Math.Clamp(previousIndex, 0, Messages.Count), previousAssistant);
                _pendingAssistant = previousAssistant;
                if (previousWasThinking)
                    StartThinking();
            }

            InputText = draft;
            Messages.Add(new ChatMessageViewModel(ChatRole.System, L("AiSteerFailed", ex.Message)));
        }
    }

    private (string DisplayText, string Payload)? TakeUserMessage()
    {
        var prompt = InputText.Trim();
        if (prompt.Length == 0)
            return null;

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

        return (displayText, payload);
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

        FlushPendingTextDeltas();
        SetModelTurnActive(false);
        StopThinking();
        if (_pendingAssistant is not null)
        {
            _pendingAssistant.IsStreaming = false;
            if (string.IsNullOrEmpty(_pendingAssistant.Text))
                Messages.Remove(_pendingAssistant);
        }
        _pendingAssistant = null;
        IsBusy = false;
        StatusText = L("AiStopped");
        PersistCurrentConversation();
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
                _session = !string.IsNullOrWhiteSpace(_pendingResumeSessionId)
                    ? SelectedProvider.ResumeSessionFactory?.Invoke(
                        SelectedModel?.Value,
                        SelectedEffort?.Value,
                        _pendingResumeSessionId)
                    : SelectedProvider.SessionFactory?.Invoke(SelectedModel?.Value, SelectedEffort?.Value);
                if (_session is null)
                {
                    IsAvailable = false;
                    IsBusy = false;
                    StatusText = SelectedProvider.UnavailableMessage;
                    StopThinking();
                    _pendingAssistant = null;
                    return;
                }

                _session.SessionInitialized += OnSessionInitialized;
                _session.TextDelta += OnTextDelta;
                _session.TextReplaced += OnTextReplaced;
                _session.TurnCompleted += OnTurnCompleted;
                _session.Errored += OnErrored;
                _session.Exited += OnExited;
                NotifySteerAvailabilityChanged();
            }

            if (!_sessionStarted)
            {
                _session.Start();
                _sessionStarted = true;
            }

            SetModelTurnActive(true);

            // The per-turn token lets Stop abort HTTP-backed sessions mid-stream; CLI-backed
            // sessions return quickly here and are interrupted via InterruptAsync instead.
            await _session.SendAsync(payload, _turnCts?.Token ?? _cts.Token);
        }
        catch (Exception ex)
        {
            SetModelTurnActive(false);
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
        SetModelTurnActive(false);
        StopThinking();
        DiscardPendingTextDeltas();
        if (_pendingAssistant is not null)
            _pendingAssistant.IsStreaming = false;
        _pendingAssistant = null;
        if (session is not null)
        {
            session.SessionInitialized -= OnSessionInitialized;
            session.TextDelta -= OnTextDelta;
            session.TextReplaced -= OnTextReplaced;
            session.TurnCompleted -= OnTurnCompleted;
            session.Errored -= OnErrored;
            session.Exited -= OnExited;
            _ = session.DisposeAsync();
        }
    }

    private void OnTextDelta(string delta)
    {
        if (string.IsNullOrEmpty(delta) || _cts.IsCancellationRequested)
            return;

        var scheduleFlush = false;
        lock (_streamDeltaGate)
        {
            _streamDeltaBuffer.Append(delta);
            if (!_streamFlushScheduled)
            {
                _streamFlushScheduled = true;
                scheduleFlush = true;
            }
        }

        if (scheduleFlush)
            _ = FlushPendingTextDeltasAfterDelayAsync();
    }

    private void OnTextReplaced(string text)
    {
        lock (_streamDeltaGate)
        {
            _streamDeltaBuffer.Clear();
            _streamFlushScheduled = false;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_pendingAssistant is null)
                return;

            StopThinking();
            _pendingAssistant.Text = text;
            _pendingAssistant.IsStreaming = true;
            _streamRenderUpdateCount++;
            OnPropertyChanged(nameof(StreamRenderUpdateCount));
            OnPropertyChanged(nameof(PendingStreamCharacterCount));
        });
    }

    private async Task FlushPendingTextDeltasAfterDelayAsync()
    {
        try
        {
            await Task.Delay(StreamRenderIntervalMilliseconds, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Dispatcher.UIThread.Post(FlushPendingTextDeltas, DispatcherPriority.Background);
    }

    private void FlushPendingTextDeltas()
    {
        string delta;
        lock (_streamDeltaGate)
        {
            delta = _streamDeltaBuffer.ToString();
            _streamDeltaBuffer.Clear();
            _streamFlushScheduled = false;
        }

        if (delta.Length == 0 || _pendingAssistant is null)
            return;

        _pendingAssistant.IsStreaming = true;
        StopThinking();
        _pendingAssistant.Text += delta;
        _streamRenderUpdateCount++;
        OnPropertyChanged(nameof(StreamRenderUpdateCount));
        OnPropertyChanged(nameof(PendingStreamCharacterCount));
    }

    private void DiscardPendingTextDeltas()
    {
        lock (_streamDeltaGate)
        {
            _streamDeltaBuffer.Clear();
            _streamFlushScheduled = false;
        }

        OnPropertyChanged(nameof(PendingStreamCharacterCount));
    }

    private void OnTurnCompleted(AgentTurnResult result) => Dispatcher.UIThread.Post(() =>
    {
        FlushPendingTextDeltas();

        // The completion of a turn the user stopped (interrupted backends still emit one):
        // the panel is already idle and shows "Stopped"; don't run its command or overwrite
        // the status.
        if (_stopRequested)
        {
            _stopRequested = false;
            DiscardPendingTextDeltas();
            return;
        }

        SetModelTurnActive(false);

        var answer = "";
        if (_pendingAssistant is not null)
        {
            StopThinking();
            _pendingAssistant.Text = SelectCompletedText(_pendingAssistant.Text, result.Text);
            _pendingAssistant.IsStreaming = false;
            answer = _pendingAssistant.Text;
            _pendingAssistant = null;
        }
        PersistCurrentConversation();

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

        var toolRequest = AutoRun && !result.IsError
            ? ExtractFirstToolRequest(answer)
            : null;
        if (toolRequest?.TerminalAction is { } terminalAction && _runTerminalAction is not null)
        {
            // Keep IsBusy true across the recovery and the model's follow-up turn.
            _ = RunTerminalActionAndContinueAsync(terminalAction);
            return;
        }

        if (toolRequest?.Transfer is { } transfer && _transferFiles is not null)
        {
            // Keep IsBusy true across the whole auto loop.
            _ = TransferAndContinueAsync(transfer);
            return;
        }

        if (toolRequest?.Command is { } command && _runCaptured is not null)
        {
            // Two independent danger signals: the model self-tagged the block, or the local
            // blacklist matched. Either one pauses the loop unless the user opted into
            // automatically approving potentially destructive commands.
            if (RequiresDangerConfirmation(command, toolRequest.Dangerous))
            {
                AskDangerConfirmation(command);
                return;
            }

            _ = RunAndContinueAsync(command);
            return;
        }

        IsBusy = false;
    });

    internal static string SelectCompletedText(string streamedText, string completedText) =>
        string.IsNullOrEmpty(completedText) ? streamedText : completedText;

    /// <summary>Returns the same authoritative-text selection used when a streamed turn
    /// completes. Public so Debug MCP can verify stale previews are replaced.</summary>
    public string DebugReconcileCompletedText(string streamedText, string completedText) =>
        SelectCompletedText(streamedText, completedText);

    /// <summary>Returns the final safety decision for an auto-run command. Public so the
    /// running UI can be verified through the generic Debug MCP object-path tools without
    /// executing the command.</summary>
    public bool RequiresDangerConfirmation(string command, bool dangerTagged) =>
        !AutoApproveDangerousCommands
        && (dangerTagged || DangerousCommandDetector.IsDangerous(command));

    /// <summary>Shows the same Running… activity bubble used while a remote command
    /// executes. Public for Debug MCP verification without needing a live shell wait.</summary>
    public void DebugShowRunningActivity() => BeginActivityPlaceholder(ActivityKind.Running);

    /// <summary>Shows the same Thinking… activity bubble used while waiting for an agent.
    /// Public for Debug MCP verification without starting a provider session.</summary>
    public void DebugShowThinkingActivity() => BeginActivityPlaceholder(ActivityKind.Thinking);

    /// <summary>Removes a temporary activity bubble created for Debug MCP checks.</summary>
    public void DebugClearActivity() => ClearActivityPlaceholder();

    /// <summary>Queues a temporary burst of streamed text through the production coalescer,
    /// finalizes its rendering, then removes it. Debug MCP uses the result to verify that a
    /// large token burst does not create one UI update per chunk.</summary>
    public async Task<string> RunDebugStreamingStressAsync(int chunkCount, int charactersPerChunk)
    {
        if (chunkCount is < 1 or > 20_000)
            throw new ArgumentOutOfRangeException(nameof(chunkCount));
        if (charactersPerChunk is < 1 or > 1_000)
            throw new ArgumentOutOfRangeException(nameof(charactersPerChunk));
        if (IsBusy || _pendingAssistant is not null)
            throw new InvalidOperationException("The AI panel must be idle for a streaming stress check.");

        var message = new ChatMessageViewModel(ChatRole.Assistant, "") { IsThinking = true };
        Messages.Add(message);
        _pendingAssistant = message;
        DiscardPendingTextDeltas();
        _streamRenderUpdateCount = 0;
        OnPropertyChanged(nameof(StreamRenderUpdateCount));

        var chunk = new string('x', charactersPerChunk);
        for (var i = 0; i < chunkCount; i++)
            OnTextDelta(chunk);
        var queuedCharacters = PendingStreamCharacterCount;

        await Task.Delay(StreamRenderIntervalMilliseconds * 3).ConfigureAwait(false);
        var state = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            FlushPendingTextDeltas();
            var streamedAsPlainText = message.ShowsPlainText
                                      && !message.ShowsAssistantMarkdown
                                      && message.RenderedMarkdown.Length == 0;
            message.IsStreaming = false;
            _pendingAssistant = null;
            return (streamedAsPlainText, message.ShowsAssistantMarkdown, message.ShowsPlainText,
                message.Text.Length, StreamRenderUpdateCount, PendingStreamCharacterCount);
        });

        // Give the final Markdown (when below the safety limit) one real layout pass.
        await Task.Delay(100).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => Messages.Remove(message));

        return $"chunks={chunkCount}; queuedChars={queuedCharacters}; textChars={state.Length}; "
               + $"uiUpdates={state.StreamRenderUpdateCount}; pending={state.PendingStreamCharacterCount}; "
               + $"streamPlain={state.streamedAsPlainText}; finalMarkdown={state.ShowsAssistantMarkdown}; "
               + $"finalPlain={state.ShowsPlainText}";
    }

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
        PersistCurrentConversation();
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
        DiscardPendingTextDeltas();
        _streamRenderUpdateCount = 0;
        OnPropertyChanged(nameof(StreamRenderUpdateCount));
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
            PersistCurrentConversation();
        }

        return command;
    }

    private async Task RunAndContinueAsync(string command)
    {
        Messages.Add(new ChatMessageViewModel(ChatRole.Tool, $"$ {command}"));
        PersistCurrentConversation();
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
        PersistCurrentConversation();

        BeginActivityPlaceholder(ActivityKind.Thinking);
        await SendToSessionAsync($"Output of `{command}`:\n```\n{output}\n```");
    }

    private async Task TransferAndContinueAsync(AgentFileTransfer transfer)
    {
        var label = transfer.IsUpload ? "upload" : "download";
        var description = $"⇅ {label}: {string.Join(", ", transfer.Sources)}"
            + (transfer.Destination is null ? "" : $" -> {transfer.Destination}");
        Messages.Add(new ChatMessageViewModel(ChatRole.Tool, description));
        PersistCurrentConversation();
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
        PersistCurrentConversation();

        BeginActivityPlaceholder(ActivityKind.Thinking);
        await SendToSessionAsync($"Result of the {label}:\n```\n{outcome}\n```");
    }

    private async Task RunTerminalActionAndContinueAsync(AgentTerminalAction action)
    {
        var actionName = action == AgentTerminalAction.ForceInterrupt ? "interrupt" : "reconnect";
        Messages.Add(new ChatMessageViewModel(ChatRole.Tool, $"[terminal action] {actionName}"));
        BeginActivityPlaceholder(ActivityKind.Running);

        string outcome;
        var turnToken = _turnCts?.Token ?? _cts.Token;
        try
        {
            outcome = await _runTerminalAction!(action, turnToken);
        }
        catch (OperationCanceledException)
        {
            outcome = "[terminal action cancelled]";
        }
        catch (Exception ex)
        {
            outcome = $"[terminal action failed: {ex.Message}]";
        }

        if (turnToken.IsCancellationRequested || _cts.IsCancellationRequested)
            return;

        ClearActivityPlaceholder();
        Messages.Add(new ChatMessageViewModel(ChatRole.Tool, outcome));
        BeginActivityPlaceholder(ActivityKind.Thinking);
        await SendToSessionAsync(
            $"Result of terminal action `{actionName}`:\n```\n{outcome}\n```");
    }

    /// <summary>Runs the same terminal recovery delegate used by jrm-tool requests. Public
    /// so Debug MCP can verify the live AI-to-terminal route without a model call.</summary>
    public Task<string> RunTerminalActionForDebugAsync(string action)
    {
        var parsed = ParseTerminalAction(action)
            ?? throw new ArgumentException("Expected terminal action 'interrupt' or 'reconnect'.", nameof(action));
        if (_runTerminalAction is null)
            throw new InvalidOperationException("Terminal recovery actions are unavailable.");
        return _runTerminalAction(parsed, CancellationToken.None);
    }

    private void OnErrored(string message) => Dispatcher.UIThread.Post(() =>
    {
        // stderr can be chatty; only surface it as a system bubble when a turn is in flight.
        if (IsBusy)
        {
            FlushPendingTextDeltas();
            Messages.Add(new ChatMessageViewModel(ChatRole.System, message));
            SetModelTurnActive(false);
            StopThinking();
            if (_pendingAssistant is not null)
            {
                _pendingAssistant.IsStreaming = false;
                if (string.IsNullOrEmpty(_pendingAssistant.Text))
                    Messages.Remove(_pendingAssistant);
            }
            _pendingAssistant = null;
            IsBusy = false;
            if (IsAuthenticationError(message))
                InvalidateSessionForAuthenticationError();
            else
                StatusText = L("AiTurnError");
            PersistCurrentConversation();
        }
    });

    private void SetModelTurnActive(bool value)
    {
        if (_isModelTurnActive == value)
            return;

        _isModelTurnActive = value;
        OnPropertyChanged(nameof(IsModelTurnActive));
        NotifySteerAvailabilityChanged();
    }

    private void NotifySteerAvailabilityChanged()
    {
        SteerCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSteer));
    }

    private void OnExited() => Dispatcher.UIThread.Post(HandleSessionExited);

    /// <summary>Surfaces an unexpected server disconnect while the AI is working. Public
    /// so the live notification path can be exercised through Debug MCP.</summary>
    public void NotifyTerminalDisconnected()
    {
        TerminalConnectionState = "reconnecting";
        OnPropertyChanged(nameof(TerminalConnectionState));
        Interlocked.Increment(ref _terminalDisconnectNotificationCount);
        OnPropertyChanged(nameof(TerminalDisconnectNotificationCount));
        AddTerminalConnectionMessage(L("AiTerminalDisconnected"));
    }

    /// <summary>Reports that the hosting terminal established a fresh server channel.</summary>
    public void NotifyTerminalReconnected()
    {
        TerminalConnectionState = "connected";
        OnPropertyChanged(nameof(TerminalConnectionState));
        AddTerminalConnectionMessage(L("AiTerminalReconnected"));
    }

    /// <summary>Reports that the automatic reconnect attempt could not restore the server.</summary>
    public void NotifyTerminalReconnectFailed(string error)
    {
        TerminalConnectionState = "failed";
        OnPropertyChanged(nameof(TerminalConnectionState));
        AddTerminalConnectionMessage(L("AiTerminalReconnectFailed", error));
    }

    private void AddTerminalConnectionMessage(string message)
    {
        Messages.Add(new ChatMessageViewModel(ChatRole.System, message));
        StatusText = message;
        PersistCurrentConversation();
    }

    private void HandleSessionExited()
    {
        FlushPendingTextDeltas();
        DetachAndDisposeSession();
        IsBusy = false;
        StatusText = L("AiSessionEnded");
        PersistCurrentConversation();
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
    public long ActivityElapsedSeconds { get; private set; }

    /// <summary>Adds an empty assistant bubble and starts its animated status text
    /// (Thinking… / Running…).</summary>
    private void BeginActivityPlaceholder(ActivityKind kind)
    {
        DiscardPendingTextDeltas();
        _streamRenderUpdateCount = 0;
        OnPropertyChanged(nameof(StreamRenderUpdateCount));
        _pendingAssistant = new ChatMessageViewModel(ChatRole.Assistant, "");
        Messages.Add(_pendingAssistant);
        StartActivity(kind);
    }

    /// <summary>Removes an empty activity placeholder bubble after a command/transfer
    /// finishes. Leaves bubbles that already received streamed text alone.</summary>
    private void ClearActivityPlaceholder()
    {
        FlushPendingTextDeltas();
        StopThinking();
        if (_pendingAssistant is not null)
        {
            _pendingAssistant.IsStreaming = false;
            if (string.IsNullOrEmpty(_pendingAssistant.Text))
                Messages.Remove(_pendingAssistant);
        }
        _pendingAssistant = null;
    }

    private void StartThinking() => StartActivity(ActivityKind.Thinking);

    private void StartActivity(ActivityKind kind)
    {
        _activityKind = kind;
        _thinkingStep = 2;
        ActivityElapsedSeconds = 0;
        _activityStopwatch.Restart();
        if (_pendingAssistant is not null)
        {
            StatusText = _pendingAssistant.ThinkingText = BuildActivityText(_thinkingStep);
            _pendingAssistant.IsThinking = true;
        }

        _thinkingTimer.Stop();
        _thinkingTimer.Start();
    }

    private void StopThinking()
    {
        _thinkingTimer.Stop();
        _activityStopwatch.Stop();
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

        var elapsedSeconds = (long)_activityStopwatch.Elapsed.TotalSeconds;
        if (elapsedSeconds == ActivityElapsedSeconds)
            return;

        ActivityElapsedSeconds = elapsedSeconds;
        _thinkingStep = (_thinkingStep + 1) % 3;
        StatusText = _pendingAssistant.ThinkingText = BuildActivityText(_thinkingStep);
    }

    private string BuildActivityText(int step)
    {
        var label = _activityKind == ActivityKind.Running ? L("AiRunning") : L("AiThinking");
        return label + new string('.', step + 1) + " · " + L("AiElapsedSeconds", ActivityElapsedSeconds);
    }

    /// <summary>Finds the first valid canonical jrm-tool request in an assistant response.</summary>
    public static AgentToolRequest? ExtractFirstToolRequest(string text)
    {
        foreach (Match match in UnifiedToolBlock.Matches(text))
        {
            if (ParseUnifiedToolRequest(match.Groups["body"].Value) is { } request)
                return request;
        }

        return null;
    }

    private static AgentToolRequest? ParseUnifiedToolRequest(string body)
    {
        var normalized = body.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var newline = normalized.IndexOf('\n');
        var name = (newline < 0 ? normalized : normalized[..newline]).Trim().ToLowerInvariant();
        var payload = newline < 0 ? "" : normalized[(newline + 1)..].Trim();

        return name switch
        {
            "terminal.run" when payload.Length > 0 =>
                new AgentToolRequest(name, Command: payload),
            "terminal.run-danger" when payload.Length > 0 =>
                new AgentToolRequest(name, Command: payload, Dangerous: true),
            "terminal.interrupt" when payload.Length == 0 =>
                new AgentToolRequest(name, TerminalAction: AgentTerminalAction.ForceInterrupt),
            "terminal.reconnect" when payload.Length == 0 =>
                new AgentToolRequest(name, TerminalAction: AgentTerminalAction.Reconnect),
            "file.upload" when ParseTransfer("upload", payload) is { } transfer =>
                new AgentToolRequest(name, Transfer: transfer),
            "file.download" when ParseTransfer("download", payload) is { } transfer =>
                new AgentToolRequest(name, Transfer: transfer),
            _ => null,
        };
    }

    private static AgentTerminalAction? ParseTerminalAction(string body) =>
        body.Trim().ToLowerInvariant() switch
        {
            "interrupt" or "force-interrupt" => AgentTerminalAction.ForceInterrupt,
            "reconnect" => AgentTerminalAction.Reconnect,
            _ => null,
        };

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
        PersistCurrentConversation();
        _cts.Cancel();
        DiscardPendingTextDeltas();
        if (_pendingAssistant is not null)
            _pendingAssistant.IsStreaming = false;
        _pendingAssistant = null;
        _turnCts?.Dispose();
        _turnCts = null;
        StopThinking();
        _thinkingTimer.Tick -= OnThinkingTimerTick;
        var session = _session;
        _session = null;
        if (session is not null)
        {
            session.SessionInitialized -= OnSessionInitialized;
            session.TextDelta -= OnTextDelta;
            session.TextReplaced -= OnTextReplaced;
            session.TurnCompleted -= OnTurnCompleted;
            session.Errored -= OnErrored;
            session.Exited -= OnExited;
            await session.DisposeAsync();
        }
        _cts.Dispose();
    }
}
