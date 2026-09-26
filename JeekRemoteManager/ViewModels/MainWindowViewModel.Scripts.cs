using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jeek.Avalonia.Localization;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;
using JeekTools;

namespace JeekRemoteManager.ViewModels;

/// <summary>Remote scripts: suite choices, single and batch runs, parameter bindings and output.</summary>
public partial class MainWindowViewModel
{
    // --- SSH script actions ---

    [RelayCommand(CanExecute = nameof(CanRunSelectedScriptBinding))]
    private void RunSelectedScriptBinding()
    {
        var choices = PrepareScriptSuiteChoicesForSelectedConnection();
        if (choices.Count == 1)
            OpenScriptSuiteChoice(choices[0]);
        else if (choices.Count > 1)
            StatusMessage = L("StatusChooseScriptSuite");
    }

    public IReadOnlyList<ScriptSuiteChoiceViewModel> PrepareScriptSuiteChoicesForSelectedConnection()
    {
        if (SelectedNode is not { IsConnection: true, Connection: not null } node)
            return Array.Empty<ScriptSuiteChoiceViewModel>();

        FlushPendingAutoSave();

        if (node.Connection.Type is not (ConnectionType.Ssh or ConnectionType.Wsl))
        {
            StatusMessage = L("StatusScriptOnlySsh");
            return Array.Empty<ScriptSuiteChoiceViewModel>();
        }

        ReloadScripts();
        var removed = PruneMissingScriptBindingsForCurrentConnection(node.Connection);
        if (ScriptSuites.Count == 0)
        {
            StatusMessage = L("StatusNoScripts", $"{_scriptStore.BuiltInRootPath}; {_scriptStore.RootPath}");
            return Array.Empty<ScriptSuiteChoiceViewModel>();
        }

        if (removed > 0)
            StatusMessage = L("StatusMissingScriptBindingsRemoved", removed);
        _scriptContext = new ScriptExecutionContext(node, terminal: null);
        return BuildScriptSuiteChoices(node.Connection);
    }

    /// <summary>
    /// Selects the tree node behind an open terminal tab (matched by its on-disk
    /// path) and prepares its script-suite choices, so the existing selection-based
    /// script flow targets that connection. Returns an empty list when the node is
    /// gone or the connection has no usable scripts.
    /// </summary>
    public IReadOnlyList<ScriptSuiteChoiceViewModel> PrepareScriptSuiteChoicesForTerminal(TerminalScriptSession terminal)
    {
        var node = string.IsNullOrEmpty(terminal.SourcePath) ? null : FindNode(Nodes, terminal.SourcePath);
        if (node is null)
        {
            StatusMessage = L("StatusTerminalConnectionMissing");
            return Array.Empty<ScriptSuiteChoiceViewModel>();
        }

        ExpandAncestors(node);
        SelectedNode = node;
        var choices = PrepareScriptSuiteChoicesForSelectedConnection();
        if (choices.Count > 0)
            _scriptContext = new ScriptExecutionContext(node, terminal);
        return choices;
    }

    public Task CopyPublicKeyToServerAsync(Connection connection) =>
        CopyPublicKeyToServerAsync(
            connection,
            publicKeyText => PublicKeyInstaller.InstallAsync(
                connection,
                publicKeyText,
                ConfirmHostKeyReplacement,
                resolveConnection: _store.TryLoadByTreePath,
                promptUser: PromptUser));

    /// <summary>
    /// Installs the local public key on the given connection's host
    /// (the ssh-copy-id equivalent), confirming first and reporting the outcome
    /// via the status bar.
    /// </summary>
    public async Task CopyPublicKeyToServerAsync(
        Connection connection,
        Func<string, Task<PublicKeyInstallResult>> installAsync)
    {
        if (connection.Type != ConnectionType.Ssh)
        {
            StatusMessage = L("StatusScriptOnlySsh");
            return;
        }

        var publicKeyPath = PublicKeyInstaller.FindLocalPublicKey(connection);
        if (publicKeyPath is null)
        {
            StatusMessage = L("StatusNoPublicKey");
            return;
        }

        string publicKeyText;
        try
        {
            publicKeyText = PublicKeyInstaller.ReadPublicKey(publicKeyPath);
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusPublicKeyFailed", ex.Message);
            return;
        }

        var target = string.IsNullOrWhiteSpace(connection.Name) ? connection.Host : connection.Name;
        if (ConfirmAsync is not null)
        {
            var ok = await ConfirmAsync(
                L("DialogCopyPublicKeyTitle"),
                L("DialogCopyPublicKeyMessage", publicKeyPath, target));
            if (!ok)
                return;
        }

        StatusMessage = L("StatusCopyingPublicKey", target);
        try
        {
            var result = await installAsync(publicKeyText);
            StatusMessage = result.AlreadyPresent
                ? L("StatusPublicKeyAlreadyPresent", target)
                : L("StatusPublicKeyInstalled", target);
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusPublicKeyFailed", ex.Message);
        }
    }

    public void OpenScriptSuiteChoice(ScriptSuiteChoiceViewModel? choice)
    {
        if (choice is null)
            return;
        var context = _scriptContext;
        var node = context?.Node;
        if (node is not { IsConnection: true, Connection: not null })
            return;

        var suite = choice.Suite;
        var binding = node.Connection.ScriptBindings.LastOrDefault(b =>
            string.Equals(
                RemoteScriptSuiteNames.NormalizeBindingName(b.Name),
                suite.RelativePath,
                StringComparison.OrdinalIgnoreCase));
        if (binding is not null)
            binding.Name = suite.RelativePath;
        binding = binding is null
            ? new ConnectionScriptBinding { Name = suite.RelativePath }
            : RemoteScriptLauncher.UnprotectSecretValues(suite, binding);

        ScriptPanel = new ScriptSuitePanelViewModel(suite, binding, () => _ = SaveScriptPanelBinding());
        RunScriptFileCommand.NotifyCanExecuteChanged();
        StatusMessage = L("StatusScriptSuiteOpened", suite.Name);
    }

    private bool CanRunSelectedScriptBinding() => IsShellConnectionContext;

    private IReadOnlyList<ScriptSuiteChoiceViewModel> BuildScriptSuiteChoices(Connection connection) =>
        SortScriptSuiteChoices(ScriptSuites, connection.ScriptBindings);

    public static IReadOnlyList<ScriptSuiteChoiceViewModel> SortScriptSuiteChoices(
        IEnumerable<RemoteScriptSuite> suites,
        IEnumerable<ConnectionScriptBinding> bindings)
    {
        var boundSuites = new HashSet<string>(
            bindings
                .Where(b => !string.IsNullOrWhiteSpace(b.Name))
                .Select(b => RemoteScriptSuiteNames.NormalizeBindingName(b.Name)),
            StringComparer.OrdinalIgnoreCase);

        return suites
            .Select(suite => new ScriptSuiteChoiceViewModel(suite, boundSuites.Contains(suite.RelativePath)))
            .OrderByDescending(choice => choice.HasParameters)
            .ThenByDescending(choice => choice.Suite.Source == RemoteScriptSuiteSource.User)
            .ThenBy(choice => choice.Suite.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Concurrent executions are allowed so a running script in one terminal tab
    // does not disable the script buttons of every other tab; CanRunScriptFile
    // still greys out the buttons of the tab whose terminal is busy.
    [RelayCommand(CanExecute = nameof(CanRunScriptFile), AllowConcurrentExecutions = true)]
    private async Task RunScriptFile(RemoteScriptFile? scriptFile)
    {
        var context = _scriptContext;
        if (scriptFile is null
            || ScriptPanel is null
            || context?.Node is not { IsConnection: true, Connection: not null } node)
            return;

        if (ScriptPanel.IsRunning || context.Terminal is { IsScriptRunning: true })
        {
            StatusMessage = L("StatusScriptAlreadyRunning");
            return;
        }

        var binding = SaveScriptPanelBinding(flushImmediately: true);
        if (binding is null)
            return;

        var panel = ScriptPanel;
        var displayName = $"{panel.SuiteName}/{scriptFile.DisplayName}";
        panel.ClearExecutionResult();
        panel.StatusText = L("ScriptExecutionRunning");
        panel.IsRunning = true;
        RunScriptFileCommand.NotifyCanExecuteChanged();
        StatusMessage = L("StatusScriptRunning", displayName, node.Connection.Name);

        try
        {
            var terminal = context.Terminal;
            if (terminal is null)
            {
                if (EnsureSshTerminalAsync is null)
                    throw new InvalidOperationException(Localizer.Get("StatusScriptTerminalUnavailable"));

                panel.StatusText = L("ScriptExecutionWaitingTerminal");
                terminal = await EnsureSshTerminalAsync(node.Connection, node.FullPath);
                context.Terminal = terminal
                    ?? throw new InvalidOperationException(Localizer.Get("StatusScriptTerminalUnavailable"));
            }

            terminal.Activate();
            if (terminal.IsScriptRunning)
            {
                panel.StatusText = L("StatusScriptAlreadyRunning");
                StatusMessage = L("StatusScriptAlreadyRunning");
                return;
            }

            terminal.HideScriptPanel();
            await terminal.WaitUntilConnectedAsync();
            panel.StatusText = L("ScriptExecutionRunningInTerminal");

            var result = await terminal.RunScriptAsync(
                panel.Suite,
                scriptFile,
                binding);

            var duration = FormatScriptDuration(result.FinishedAt - result.StartedAt);
            panel.StatusText = result.ExitCode == 0
                ? L("ScriptExecutionSucceeded", duration)
                : L("ScriptExecutionFailed", result.ExitCode, duration);
            panel.SetExecutionResult(result.ExitCode == 0);
            StatusMessage = result.ExitCode == 0
                ? L("StatusScriptSucceeded", displayName)
                : L("StatusScriptFailed", displayName, result.ExitCode);
        }
        catch (Exception ex)
        {
            panel.StatusText = L("ScriptExecutionStartFailed", ex.Message);
            panel.SetExecutionResult(false);
            StatusMessage = L("StatusScriptLaunchFailed", ex.Message);
        }
        finally
        {
            panel.IsRunning = false;
            RunScriptFileCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanRunScriptFile(RemoteScriptFile? scriptFile) =>
        ScriptPanel is not { IsRunning: true }
        && _scriptContext?.Terminal is not { IsScriptRunning: true };

    // --- Batch script run over the tree multi-selection ---

    // Caps how many servers connect and run at once; the rest queue up.
    private const int BatchScriptMaxConcurrency = 8;

    // Targets captured when the script-suite chooser opens, consumed by
    // OpenBatchScriptSuiteChoice. Deduplicated by connection file path.
    private List<TreeNodeViewModel> _batchScriptNodes = new();

    /// <summary>
    /// Collects the script-capable connections of the current multi-selection and
    /// returns the suite choices for them (a suite counts as "bound" when any
    /// target has saved parameters for it). Empty when fewer than two distinct
    /// connections qualify.
    /// </summary>
    public IReadOnlyList<ScriptSuiteChoiceViewModel> PrepareBatchScriptSuiteChoices()
    {
        FlushPendingAutoSave();

        var targets = new List<TreeNodeViewModel>();
        foreach (var node in EffectiveSelection())
        {
            if (node is not
                {
                    IsConnection: true,
                    IsNameEditing: false,
                    Connection.Type: ConnectionType.Ssh or ConnectionType.Wsl,
                })
            {
                continue;
            }

            // A Recent shadow and its real node share the connection file.
            if (targets.Any(t => PathEquals(t.FullPath, node.FullPath)))
                continue;

            targets.Add(node);
        }

        if (targets.Count < 2)
            return Array.Empty<ScriptSuiteChoiceViewModel>();

        ReloadScripts();
        if (ScriptSuites.Count == 0)
        {
            StatusMessage = L("StatusNoScripts", $"{_scriptStore.BuiltInRootPath}; {_scriptStore.RootPath}");
            return Array.Empty<ScriptSuiteChoiceViewModel>();
        }

        _batchScriptNodes = targets;
        return SortScriptSuiteChoices(
            ScriptSuites,
            targets.SelectMany(t => t.Connection!.ScriptBindings));
    }

    /// <summary>Opens the batch panel for the chosen suite on the targets captured
    /// by <see cref="PrepareBatchScriptSuiteChoices"/>.</summary>
    public void OpenBatchScriptSuiteChoice(ScriptSuiteChoiceViewModel? choice)
    {
        if (choice is null || _batchScriptNodes.Count == 0)
            return;

        var targets = new ObservableCollection<BatchScriptTargetViewModel>(
            _batchScriptNodes.Select(n => new BatchScriptTargetViewModel(n.Connection!, n.FullPath)));

        BatchScriptPanelViewModel? panel = null;
        panel = new BatchScriptPanelViewModel(
            choice.Suite,
            targets,
            scriptFile => RunBatchScriptFileAsync(panel!, scriptFile),
            () =>
            {
                if (ReferenceEquals(BatchPanel, panel))
                    BatchPanel = null;
            });
        BatchPanel = panel;
        StatusMessage = L("StatusScriptSuiteOpened", choice.Suite.Name);
    }

    private async Task RunBatchScriptFileAsync(BatchScriptPanelViewModel panel, RemoteScriptFile scriptFile)
    {
        if (panel.IsRunning)
            return;

        var ensureTerminal = EnsureSshTerminalQuietlyAsync ?? EnsureSshTerminalAsync;
        if (ensureTerminal is null)
        {
            panel.StatusText = Localizer.Get("StatusScriptTerminalUnavailable");
            return;
        }

        var displayName = $"{panel.SuiteName}/{scriptFile.DisplayName}";
        using var cts = new CancellationTokenSource();
        panel.Cts = cts;
        panel.IsRunning = true;
        panel.BeginRun();
        StatusMessage = L("StatusBatchScriptRunning", displayName, panel.Targets.Count);

        try
        {
            var unifiedParams = panel.GetUnifiedParameterValues();
            using var slots = new SemaphoreSlim(BatchScriptMaxConcurrency);
            await Task.WhenAll(panel.Targets.Select(target =>
                RunBatchTargetAsync(panel, target, scriptFile, unifiedParams, ensureTerminal, slots, cts.Token)));

            var succeeded = panel.Targets.Count(t => t.State == BatchScriptTargetState.Succeeded);
            var failed = panel.Targets.Count - succeeded;
            panel.StatusText = L("BatchStatusSummary", succeeded, failed);
            StatusMessage = failed == 0
                ? L("StatusBatchScriptSucceeded", displayName, succeeded)
                : L("StatusBatchScriptFailed", displayName, failed);
        }
        finally
        {
            panel.IsRunning = false;
            panel.Cts = null;
        }
    }

    private async Task RunBatchTargetAsync(
        BatchScriptPanelViewModel panel,
        BatchScriptTargetViewModel target,
        RemoteScriptFile scriptFile,
        IReadOnlyList<KeyValuePair<string, string>> unifiedParams,
        Func<Connection, string?, Task<TerminalScriptSession?>> ensureTerminal,
        SemaphoreSlim slots,
        CancellationToken cancellationToken)
    {
        try
        {
            // Each server runs with its own saved parameters, overridden by the
            // "unified" values from the panel; a target whose merged binding does
            // not validate is skipped instead of failing the batch.
            var binding = ResolveBatchScriptBinding(panel.Suite, target.Connection);
            if (unifiedParams.Count > 0 && ApplyUnifiedScriptParams(binding, unifiedParams))
                PersistBatchScriptBinding(target, panel.Suite, binding);
            var errors = RemoteScriptLauncher.ValidateBinding(panel.Suite, binding);
            if (errors.Count > 0)
            {
                target.SetState(BatchScriptTargetState.Skipped, L("BatchTargetSkipped", errors[0]));
                return;
            }

            await slots.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                target.SetState(BatchScriptTargetState.Connecting, L("BatchTargetConnecting"));

                var terminal = await ensureTerminal(target.Connection, target.SourcePath)
                    ?? throw new InvalidOperationException(Localizer.Get("StatusScriptTerminalUnavailable"));
                target.Terminal = terminal;

                if (terminal.IsScriptRunning)
                {
                    target.SetState(BatchScriptTargetState.Skipped, Localizer.Get("StatusScriptAlreadyRunning"));
                    return;
                }

                await terminal.WaitUntilConnectedAsync(cancellationToken);
                target.SetState(BatchScriptTargetState.Running, L("BatchTargetRunning"));

                var result = await terminal.RunScriptAsync(panel.Suite, scriptFile, binding, cancellationToken);
                var duration = FormatScriptDuration(result.FinishedAt - result.StartedAt);
                if (result.ExitCode == 0)
                    target.SetState(BatchScriptTargetState.Succeeded, L("BatchTargetSucceeded", duration));
                else
                    target.SetState(BatchScriptTargetState.Failed, L("BatchTargetFailed", result.ExitCode, duration));
            }
            finally
            {
                slots.Release();
            }
        }
        catch (OperationCanceledException)
        {
            target.SetState(BatchScriptTargetState.Canceled, L("BatchTargetCanceled"));
        }
        catch (Exception ex)
        {
            target.SetState(BatchScriptTargetState.Failed, ex.Message);
        }
        finally
        {
            panel.ReportTargetFinished();
        }
    }

    /// <summary>The connection's saved parameters for the suite (cloned, secrets
    /// left protected — payload building decrypts them), or an empty binding that
    /// falls back to the suite's defaults.</summary>
    private static ConnectionScriptBinding ResolveBatchScriptBinding(RemoteScriptSuite suite, Connection connection) =>
        BatchScriptPanelViewModel.FindSavedBinding(connection, suite)
        ?? new ConnectionScriptBinding { Name = suite.RelativePath };

    /// <summary>Overrides the binding's parameters with the panel's unified values.
    /// Returns true when any value actually differed from what was stored (stored
    /// secrets are compared decrypted), so unchanged runs skip the file save.</summary>
    private static bool ApplyUnifiedScriptParams(
        ConnectionScriptBinding binding,
        IReadOnlyList<KeyValuePair<string, string>> unifiedParams)
    {
        var changed = false;
        foreach (var (name, value) in unifiedParams)
        {
            var existing = binding.Params.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

            var current = existing?.Value;
            if (current is not null
                && MasterKeyService.IsPasswordBlob(current)
                && PasswordProtector.TryDecrypt(current, out var clear))
            {
                current = clear;
            }

            if (!string.Equals(current, value, StringComparison.Ordinal))
                changed = true;

            if (existing is null)
                binding.Params.Add(new ConnectionScriptParameterValue { Name = name, Value = value });
            else
                existing.Value = value;
        }

        return changed;
    }

    /// <summary>
    /// Writes the merged binding back to the target's connection file so unified
    /// parameter values persist for later single-server and batch runs.
    /// </summary>
    private void PersistBatchScriptBinding(
        BatchScriptTargetViewModel target,
        RemoteScriptSuite suite,
        ConnectionScriptBinding binding)
    {
        var node = FindNode(Nodes, target.SourcePath);
        var connection = node?.Connection ?? target.Connection;

        var protectedBinding = RemoteScriptLauncher.ProtectSecretValues(suite, binding);
        UpsertScriptBinding(connection.ScriptBindings, protectedBinding);

        if (node is not null)
        {
            // The connection may be open in the editor; keep its bindings view in
            // sync and let the editor's auto-save own the file write.
            if (ReferenceEquals(_editingNode, node) && Editor is not null)
            {
                UpsertScriptBinding(Editor.ScriptBindings, protectedBinding);
                _editorHasPendingChanges = true;
                FlushPendingAutoSave();
            }
            else
            {
                SaveScriptContextConnection(node);
            }

            return;
        }

        try
        {
            ProtectConnectionScriptBindings(connection);
            var folder = Path.GetDirectoryName(target.SourcePath) ?? _store.RootPath;
            _store.Save(connection, folder, target.SourcePath);
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusAutoSaveFailed", ex.Message);
        }
    }

    private ConnectionScriptBinding? SaveScriptPanelBinding(bool flushImmediately = false)
    {
        if (ScriptPanel is null
            || _scriptContext?.Node is not { IsConnection: true, Connection: not null } node)
            return null;

        var currentBinding = ScriptPanel.ToBinding();
        var existingBinding = node.Connection.ScriptBindings.LastOrDefault(b =>
            string.Equals(b.Name, currentBinding.Name, StringComparison.OrdinalIgnoreCase));

        if (existingBinding is not null
            && ScriptBindingsEquivalent(ScriptPanel.Suite, currentBinding, existingBinding))
        {
            return existingBinding;
        }

        if (existingBinding is null && !HasMeaningfulScriptParams(ScriptPanel.Suite, currentBinding))
            return currentBinding;

        var protectedBinding = RemoteScriptLauncher.ProtectSecretValues(ScriptPanel.Suite, currentBinding);
        if (ReferenceEquals(_editingNode, node) && Editor is not null)
            UpsertScriptBinding(Editor.ScriptBindings, protectedBinding);
        UpsertScriptBinding(node.Connection.ScriptBindings, protectedBinding);

        if (!ReferenceEquals(_editingNode, node))
        {
            SaveScriptContextConnection(node);
        }
        else
        {
            _editorHasPendingChanges = true;
            if (flushImmediately)
                FlushPendingAutoSave();
            else
                ScheduleAutoSave();
        }

        RunSelectedScriptBindingCommand.NotifyCanExecuteChanged();
        return protectedBinding;
    }

    private void SaveScriptContextConnection(TreeNodeViewModel node)
    {
        if (node.Connection is null)
            return;

        try
        {
            ProtectConnectionScriptBindings(node.Connection);
            var folder = Path.GetDirectoryName(node.FullPath) ?? _store.RootPath;
            var newPath = _store.Save(node.Connection, folder, node.FullPath);
            if (!PathEquals(newPath, node.FullPath))
            {
                node.FullPath = newPath;
                node.Name = node.Connection.Name;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusAutoSaveFailed", ex.Message);
        }
    }

    private static bool ScriptBindingsEquivalent(
        RemoteScriptSuite suite,
        ConnectionScriptBinding currentBinding,
        ConnectionScriptBinding storedBinding)
    {
        var current = ComparableScriptParams(suite, currentBinding);
        var stored = ComparableScriptParams(
            suite,
            RemoteScriptLauncher.UnprotectSecretValues(suite, storedBinding));

        if (current.Count != stored.Count)
            return false;

        foreach (var (name, value) in current)
        {
            if (!stored.TryGetValue(name, out var storedValue)
                || !string.Equals(value, storedValue, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static Dictionary<string, string> ComparableScriptParams(
        RemoteScriptSuite suite,
        ConnectionScriptBinding binding)
    {
        var values = binding.Params.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in suite.Parameters)
        {
            var value = values.TryGetValue(parameter.Name, out var storedValue)
                ? storedValue
                : GetDefaultScriptParamValue(parameter);
            result[parameter.Name] = NormalizeComparableScriptParam(parameter, value);
        }

        return result;
    }

    private static bool HasMeaningfulScriptParams(RemoteScriptSuite suite, ConnectionScriptBinding binding) =>
        ComparableScriptParams(suite, binding).Any(item => !IsDefaultScriptParamValue(suite, item.Key, item.Value));

    private static bool IsDefaultScriptParamValue(RemoteScriptSuite suite, string name, string value)
    {
        var parameter = suite.Parameters.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        return parameter is not null
            && string.Equals(value, NormalizeComparableScriptParam(parameter, GetDefaultScriptParamValue(parameter)),
                StringComparison.Ordinal);
    }

    private static string NormalizeComparableScriptParam(RemoteScriptParameter parameter, string value)
    {
        if (parameter.Type == RemoteScriptParameterType.Bool)
        {
            return value.Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "yes" or "y" => "true",
                _ => "false",
            };
        }

        if (parameter.Type == RemoteScriptParameterType.Enum)
        {
            return parameter.EnumOptions.FirstOrDefault(o =>
                string.Equals(o, value, StringComparison.OrdinalIgnoreCase)) ?? value;
        }

        return value;
    }

    private static string GetDefaultScriptParamValue(RemoteScriptParameter parameter) =>
        parameter.Type == RemoteScriptParameterType.Bool && string.IsNullOrEmpty(parameter.DefaultValue)
            ? "false"
            : parameter.DefaultValue;

    private static void UpsertScriptBinding(
        ObservableCollection<ConnectionScriptBindingViewModel> bindings,
        ConnectionScriptBinding binding)
    {
        for (var i = bindings.Count - 1; i >= 0; i--)
        {
            if (string.Equals(bindings[i].Name, binding.Name, StringComparison.OrdinalIgnoreCase))
                bindings.RemoveAt(i);
        }

        bindings.Add(ConnectionScriptBindingViewModel.FromModel(binding));
    }

    private static void UpsertScriptBinding(
        IList<ConnectionScriptBinding> bindings,
        ConnectionScriptBinding binding)
    {
        for (var i = bindings.Count - 1; i >= 0; i--)
        {
            if (string.Equals(bindings[i].Name, binding.Name, StringComparison.OrdinalIgnoreCase))
                bindings.RemoveAt(i);
        }

        bindings.Add(RemoteScriptLauncher.CloneBinding(binding));
    }

    private static void RemoveScriptBinding(
        ObservableCollection<ConnectionScriptBindingViewModel> bindings,
        string name)
    {
        for (var i = bindings.Count - 1; i >= 0; i--)
        {
            if (string.Equals(bindings[i].Name, name, StringComparison.OrdinalIgnoreCase))
                bindings.RemoveAt(i);
        }
    }

    private static void RemoveScriptBinding(
        IList<ConnectionScriptBinding> bindings,
        string name)
    {
        for (var i = bindings.Count - 1; i >= 0; i--)
        {
            if (string.Equals(bindings[i].Name, name, StringComparison.OrdinalIgnoreCase))
                bindings.RemoveAt(i);
        }
    }

    [RelayCommand]
    private async Task CopyScriptOutput()
    {
        if (Clipboard is null || ScriptPanel is null)
            return;

        await Clipboard.SetTextAsync(ScriptPanel.Output);
        StatusMessage = L("StatusScriptOutputCopied");
    }

    [RelayCommand]
    private void ClearScriptParameters()
    {
        if (ScriptPanel is null
            || _scriptContext?.Node is not { IsConnection: true, Connection: not null } node)
            return;

        if (ScriptPanel.IsRunning)
        {
            StatusMessage = L("StatusScriptStillRunning");
            return;
        }

        var suitePath = ScriptPanel.Suite.RelativePath;
        ScriptPanel.ClearParameters();
        if (ReferenceEquals(_editingNode, node) && Editor is not null)
            RemoveScriptBinding(Editor.ScriptBindings, suitePath);
        RemoveScriptBinding(node.Connection.ScriptBindings, suitePath);
        if (ReferenceEquals(_editingNode, node))
        {
            ScheduleAutoSave();
        }
        else
        {
            SaveScriptContextConnection(node);
        }
        RunSelectedScriptBindingCommand.NotifyCanExecuteChanged();
        StatusMessage = L("StatusScriptParametersCleared", ScriptPanel.SuiteName);
    }

    [RelayCommand]
    private void ClearScriptOutput()
    {
        if (ScriptPanel is null)
            return;

        ScriptPanel.Output = "";
    }

    [RelayCommand]
    private void CloseScriptExecution()
    {
        if (ScriptPanel is { IsRunning: true })
        {
            StatusMessage = L("StatusScriptStillRunning");
            return;
        }

        ScriptPanel = null;
        _scriptContext = null;
    }

    private static string FormatScriptDuration(TimeSpan value)
    {
        if (value.TotalHours >= 1)
            return value.ToString(@"h\:mm\:ss");
        if (value.TotalMinutes >= 1)
            return value.ToString(@"m\:ss");
        return value.TotalSeconds < 1 ? "<1s" : $"{value.TotalSeconds:0.#}s";
    }
}
