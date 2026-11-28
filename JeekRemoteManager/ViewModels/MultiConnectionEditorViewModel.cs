using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;

namespace JeekRemoteManager.ViewModels;

/// <summary>
/// Selectable option in a multi-edit ComboBox. A sentinel entry with
/// <see cref="IsKeepUnchanged"/> true represents mixed values across targets.
/// </summary>
public sealed partial class MultiEditStringOption : ObservableObject
{
    public MultiEditStringOption(string value, string label, bool isKeepUnchanged = false)
    {
        Value = value;
        _label = label;
        IsKeepUnchanged = isKeepUnchanged;
    }

    public string Value { get; }

    public bool IsKeepUnchanged { get; }

    [ObservableProperty]
    private string _label;
}

/// <summary>
/// Selectable SSH MFA profile entry in the multi-edit ComboBox, distinguishing
/// "keep each connection's existing profile" from "None (manual codes)".
/// </summary>
public sealed partial class MultiEditSshMfaOption : ObservableObject
{
    public MultiEditSshMfaOption(Guid? profileId, string name, bool isKeepUnchanged = false)
    {
        ProfileId = profileId;
        _name = name;
        IsKeepUnchanged = isKeepUnchanged;
    }

    public Guid? ProfileId { get; }

    public bool IsKeepUnchanged { get; }

    [ObservableProperty]
    private string _name;
}

/// <summary>
/// Batch editor for two or more selected connections. Shows shared values as-is,
/// marks differing values as mixed, tracks only fields explicitly modified (or
/// explicitly marked to clear), and applies those deltas when the user confirms.
/// Saved passwords and passphrases are never decrypted on load so selecting many
/// connections stays instantaneous and keeps secrets untouched unless replaced
/// or explicitly cleared.
/// </summary>
public partial class MultiConnectionEditorViewModel : ViewModelBase, IJumpHostDropTarget
{
    private readonly IReadOnlyList<Connection> _targets;
    private readonly BastionLoginProfileStore? _bastionProfiles;

    // Initial baselines for change tracking and single-field / full reset.
    private bool _shortNameInitialMixed;
    private string _shortNameInitial = "";

    private bool _hostInitialMixed;
    private string _hostInitial = "";

    private bool _portInitialMixed;
    private string _portInitialText = "";

    private bool _usernameInitialMixed;
    private string _usernameInitial = "";

    private bool _hasAnyPassword;
    private bool _hasAnyPassphrase;

    private bool _terminalTypeInitialMixed;
    private string _terminalTypeInitial = Connection.DefaultTerminalType;

    private bool _terminalEncodingInitialMixed;
    private string _terminalEncodingInitial = Services.TerminalEncoding.DefaultName;

    private bool _privateKeyPathInitialMixed;
    private string _privateKeyPathInitial = "";

    private bool _sshMfaInitialMixed;
    private Guid? _sshMfaInitialId;
    private bool _sshMfaSelectedKeepUnchanged;
    private Guid? _sshMfaProfileId;

    private bool _loginCommandsInitialMixed;
    private string _loginCommandsInitial = "";

    private bool? _autoOpenMonitorInitial;
    private bool? _autoOpenFileBrowserInitial;
    private bool? _autoLogSessionInitial;

    private bool _jumpHostInitialMixed;
    private string _jumpHostInitialId = "";
    private bool _jumpHostIsMixed;
    private string _jumpHostConnectionId = "";
    private Dictionary<string, Connection>? _jumpConnections;
    private string _jumpHostDropError = "";
    private string? _cachedJumpHostValidationMessage;

    private bool _portForwardsInitialMixed;
    private string _portForwardsInitial = "";

    private bool _wslDistroInitialMixed;
    private string _wslDistroInitial = "";
    private string _wslDistroValue = "";
    private bool _wslDistroSelectedKeepUnchanged;

    private bool _wslStartDirectoryInitialMixed;
    private string _wslStartDirectoryInitial = "";

    private bool? _rdpFullScreenInitial;
    private bool? _rdpUseAllMonitorsInitial;
    private bool _rdpWidthInitialMixed;
    private string _rdpWidthInitialText = "";
    private bool _rdpHeightInitialMixed;
    private string _rdpHeightInitialText = "";
    private bool? _rdpRedirectClipboardInitial;
    private bool? _rdpRedirectDrivesInitial;
    private bool? _rdpRedirectAudioPlaybackInitial;
    private bool? _rdpRedirectMicrophoneInitial;

    private bool? _vncFullScreenInitial;
    private bool? _vncViewOnlyInitial;
    private bool? _vncSharedInitial;

    private bool _notesInitialMixed;
    private string _notesInitial = "";

    private string? _cachedLoginCommandsValidationMessage;

    private MultiConnectionEditorViewModel(
        IReadOnlyList<Connection> targets,
        BastionLoginProfileStore? bastionProfiles)
    {
        _targets = targets;
        _bastionProfiles = bastionProfiles;

        IsAllSsh = targets.All(c => c.IsSsh);
        IsAllWsl = targets.All(c => c.IsWsl);
        IsAllRdp = targets.All(c => c.IsRdp);
        IsAllVnc = targets.All(c => c.IsVnc);
        HasHostPort = targets.All(c => !c.IsWsl);
        HasPassword = targets.All(c => !c.IsWsl);
        SupportsScripts = targets.All(c => c.Type is ConnectionType.Ssh or ConnectionType.Wsl);

        LoadBaselinesFromTargets();
    }

    public static MultiConnectionEditorViewModel FromConnections(
        IReadOnlyList<Connection> connections,
        BastionLoginProfileStore? bastionProfiles = null)
    {
        if (connections.Count < 2)
            throw new ArgumentException("Multi-connection editor requires at least two connections.", nameof(connections));

        return new MultiConnectionEditorViewModel(connections.ToArray(), bastionProfiles);
    }

    public IReadOnlyList<Connection> Targets => _targets;

    public int TargetCount => _targets.Count;

    public bool IsAllSsh { get; }

    public bool IsAllWsl { get; }

    public bool IsAllRdp { get; }

    public bool IsAllVnc { get; }

    public bool HasHostPort { get; }

    public bool HasPassword { get; }

    public bool SupportsScripts { get; }

    public string Title => L("MultiEditTitle", _targets.Count);

    public string TypeSummary
    {
        get
        {
            var groups = _targets
                .GroupBy(c => c.Type)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .ToList();
            return groups.Count == 1
                ? groups[0].Key.ToDisplayName()
                : string.Join(", ", groups.Select(g => $"{g.Count()} {g.Key.ToDisplayName()}"));
        }
    }

    public string Subtitle => $"{TypeSummary} · {L("MultiEditHint")}";

    public string ApplyButtonText => L("MultiEditApply", _targets.Count);

    // --- ShortName ---

    [ObservableProperty]
    private string _shortName = "";

    [ObservableProperty]
    private bool _shortNameWillClear;

    public bool IsShortNameModified =>
        _shortNameInitialMixed
            ? ShortNameWillClear || ShortName.Length > 0
            : !string.Equals(ShortName, _shortNameInitial, StringComparison.Ordinal);

    public bool CanClearShortName => _shortNameInitialMixed && !IsShortNameModified;

    public string ShortNamePlaceholder =>
        ShortNameWillClear ? L("MultiEditWillClear")
        : _shortNameInitialMixed ? L("MultiEditMultipleValues")
        : L("ShortNameWatermark");

    partial void OnShortNameChanged(string value)
    {
        if (value.Length > 0 && ShortNameWillClear)
            ShortNameWillClear = false;
        NotifyShortNameState();
    }

    partial void OnShortNameWillClearChanged(bool value) => NotifyShortNameState();

    public void ClearShortName()
    {
        ShortName = "";
        ShortNameWillClear = _shortNameInitialMixed;
        NotifyShortNameState();
    }

    public void RevertShortName()
    {
        ShortNameWillClear = false;
        ShortName = _shortNameInitial;
        NotifyShortNameState();
    }

    private void NotifyShortNameState()
    {
        OnPropertyChanged(nameof(IsShortNameModified));
        OnPropertyChanged(nameof(CanClearShortName));
        OnPropertyChanged(nameof(ShortNamePlaceholder));
        NotifySummaryChanged();
    }

    // --- Host ---

    [ObservableProperty]
    private string _host = "";

    [ObservableProperty]
    private bool _hostWillClear;

    public bool IsHostModified =>
        HasHostPort && (_hostInitialMixed
            ? HostWillClear || Host.Length > 0
            : !string.Equals(Host, _hostInitial, StringComparison.Ordinal));

    public bool CanClearHost => HasHostPort && _hostInitialMixed && !IsHostModified;

    public string HostPlaceholder =>
        HostWillClear ? L("MultiEditWillClear")
        : _hostInitialMixed ? L("MultiEditMultipleValues")
        : L("HostWatermark");

    partial void OnHostChanged(string value)
    {
        if (value.Length > 0 && HostWillClear)
            HostWillClear = false;
        NotifyHostState();
    }

    partial void OnHostWillClearChanged(bool value) => NotifyHostState();

    public void ClearHost()
    {
        Host = "";
        HostWillClear = _hostInitialMixed;
        NotifyHostState();
    }

    public void RevertHost()
    {
        HostWillClear = false;
        Host = _hostInitial;
        NotifyHostState();
    }

    private void NotifyHostState()
    {
        OnPropertyChanged(nameof(IsHostModified));
        OnPropertyChanged(nameof(CanClearHost));
        OnPropertyChanged(nameof(HostPlaceholder));
        NotifyLoginCommandValidationChanged();
        NotifySummaryChanged();
    }

    // --- Port ---

    [ObservableProperty]
    private string _portText = "";

    public bool IsPortModified =>
        HasHostPort && (_portInitialMixed
            ? PortText.Trim().Length > 0
            : !string.Equals(PortText.Trim(), _portInitialText, StringComparison.Ordinal));

    public string PortPlaceholder => _portInitialMixed ? L("MultiEditMultipleShort") : "";

    public string PortValidationMessage =>
        IsPortModified && !TryParsePort(PortText, out _)
            ? L("MultiEditInvalidPort")
            : "";

    public bool HasPortValidationMessage => PortValidationMessage.Length > 0;

    partial void OnPortTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsPortModified));
        OnPropertyChanged(nameof(PortValidationMessage));
        OnPropertyChanged(nameof(HasPortValidationMessage));
        NotifyLoginCommandValidationChanged();
        NotifySummaryChanged();
    }

    public void RevertPort() => PortText = _portInitialText;

    // --- Username ---

    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private bool _usernameWillClear;

    public bool IsUsernameModified =>
        _usernameInitialMixed
            ? UsernameWillClear || Username.Length > 0
            : !string.Equals(Username, _usernameInitial, StringComparison.Ordinal);

    public bool CanClearUsername => _usernameInitialMixed && !IsUsernameModified;

    public string UsernamePlaceholder =>
        UsernameWillClear ? L("MultiEditWillClear")
        : _usernameInitialMixed ? L("MultiEditMultipleValues")
        : "";

    partial void OnUsernameChanged(string value)
    {
        if (value.Length > 0 && UsernameWillClear)
            UsernameWillClear = false;
        NotifyUsernameState();
    }

    partial void OnUsernameWillClearChanged(bool value) => NotifyUsernameState();

    public void ClearUsername()
    {
        Username = "";
        UsernameWillClear = _usernameInitialMixed;
        NotifyUsernameState();
    }

    public void RevertUsername()
    {
        UsernameWillClear = false;
        Username = _usernameInitial;
        NotifyUsernameState();
    }

    private void NotifyUsernameState()
    {
        OnPropertyChanged(nameof(IsUsernameModified));
        OnPropertyChanged(nameof(CanClearUsername));
        OnPropertyChanged(nameof(UsernamePlaceholder));
        NotifyLoginCommandValidationChanged();
        NotifySummaryChanged();
    }

    // --- Password (write-only on multi-edit: never pre-decrypted) ---

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private bool _passwordWillClear;

    public bool IsPasswordModified => HasPassword && (PasswordWillClear || Password.Length > 0);

    public bool CanClearPassword => HasPassword && _hasAnyPassword && !IsPasswordModified;

    public string PasswordPlaceholder =>
        PasswordWillClear ? L("MultiEditWillClear")
        : _hasAnyPassword ? L("MultiEditKeepPasswords")
        : "";

    partial void OnPasswordChanged(string value)
    {
        if (value.Length > 0 && PasswordWillClear)
            PasswordWillClear = false;
        NotifyPasswordState();
    }

    partial void OnPasswordWillClearChanged(bool value) => NotifyPasswordState();

    public void ClearPassword()
    {
        Password = "";
        PasswordWillClear = _hasAnyPassword;
        NotifyPasswordState();
    }

    public void RevertPassword()
    {
        PasswordWillClear = false;
        Password = "";
        NotifyPasswordState();
    }

    private void NotifyPasswordState()
    {
        OnPropertyChanged(nameof(IsPasswordModified));
        OnPropertyChanged(nameof(CanClearPassword));
        OnPropertyChanged(nameof(PasswordPlaceholder));
        NotifySummaryChanged();
    }

    // --- SSH MFA Profile ---

    public ObservableCollection<MultiEditSshMfaOption> SshMfaProfileChoices { get; } = [];

    public MultiEditSshMfaOption? SelectedSshMfaProfile
    {
        get => _sshMfaSelectedKeepUnchanged
            ? SshMfaProfileChoices.FirstOrDefault(item => item.IsKeepUnchanged)
            : SshMfaProfileChoices.FirstOrDefault(item => !item.IsKeepUnchanged && item.ProfileId == _sshMfaProfileId);
        set
        {
            if (value is null)
                return;
            if (_sshMfaSelectedKeepUnchanged == value.IsKeepUnchanged
                && _sshMfaProfileId == value.ProfileId)
            {
                return;
            }
            _sshMfaSelectedKeepUnchanged = value.IsKeepUnchanged;
            _sshMfaProfileId = value.ProfileId;
            OnPropertyChanged(nameof(SelectedSshMfaProfile));
            OnPropertyChanged(nameof(IsSshMfaModified));
            NotifySummaryChanged();
        }
    }

    public bool IsSshMfaModified =>
        IsAllSsh && (_sshMfaInitialMixed
            ? !_sshMfaSelectedKeepUnchanged
            : _sshMfaProfileId != _sshMfaInitialId);

    public void RevertSshMfaProfile()
    {
        _sshMfaSelectedKeepUnchanged = _sshMfaInitialMixed;
        _sshMfaProfileId = _sshMfaInitialId;
        OnPropertyChanged(nameof(SelectedSshMfaProfile));
        OnPropertyChanged(nameof(IsSshMfaModified));
        NotifySummaryChanged();
    }

    internal void SetSshMfaProfileChoices(IReadOnlyList<SshMfaProfileChoice> profiles)
    {
        List<MultiEditSshMfaOption> choices = [];
        if (_sshMfaInitialMixed)
            choices.Add(new(null, L("MultiEditMultipleChoice"), isKeepUnchanged: true));
        choices.Add(new(null, L("SshMfaNone")));
        foreach (var profile in profiles)
            choices.Add(new(profile.Id, profile.Name));

        if (!_sshMfaSelectedKeepUnchanged
            && _sshMfaProfileId is { } id
            && choices.All(item => item.IsKeepUnchanged || item.ProfileId != id))
        {
            choices.Add(new(id, L("SshMfaMissing")));
        }

        // Update existing entries in place so an open ComboBox does not drop selection.
        for (var index = 0; index < choices.Count; index++)
        {
            var desired = choices[index];
            var existing = SshMfaProfileChoices
                .Skip(index)
                .FirstOrDefault(item => item.IsKeepUnchanged == desired.IsKeepUnchanged && item.ProfileId == desired.ProfileId);
            if (existing is null)
            {
                SshMfaProfileChoices.Insert(index, desired);
                continue;
            }

            existing.Name = desired.Name;
            var existingIndex = SshMfaProfileChoices.IndexOf(existing);
            if (existingIndex != index)
                SshMfaProfileChoices.Move(existingIndex, index);
        }

        while (SshMfaProfileChoices.Count > choices.Count)
            SshMfaProfileChoices.RemoveAt(SshMfaProfileChoices.Count - 1);
        OnPropertyChanged(nameof(SelectedSshMfaProfile));
    }

    // --- TerminalType ---

    private static readonly string[] CommonTerminalTypes =
    [
        "xterm-256color", "xterm", "screen-256color", "screen", "tmux-256color", "vt100", "linux", "ansi",
    ];

    public ObservableCollection<MultiEditStringOption> TerminalTypeChoices { get; } = [];

    private bool _terminalTypeSelectedKeepUnchanged;
    private string _terminalTypeValue = Connection.DefaultTerminalType;

    public MultiEditStringOption? SelectedTerminalType
    {
        get => _terminalTypeSelectedKeepUnchanged
            ? TerminalTypeChoices.FirstOrDefault(item => item.IsKeepUnchanged)
            : TerminalTypeChoices.FirstOrDefault(item => !item.IsKeepUnchanged && string.Equals(item.Value, _terminalTypeValue, StringComparison.Ordinal));
        set
        {
            if (value is null)
                return;
            if (_terminalTypeSelectedKeepUnchanged == value.IsKeepUnchanged
                && string.Equals(_terminalTypeValue, value.Value, StringComparison.Ordinal))
            {
                return;
            }
            _terminalTypeSelectedKeepUnchanged = value.IsKeepUnchanged;
            _terminalTypeValue = value.Value;
            OnPropertyChanged(nameof(SelectedTerminalType));
            OnPropertyChanged(nameof(IsTerminalTypeModified));
            NotifySummaryChanged();
        }
    }

    public bool IsTerminalTypeModified =>
        IsAllSsh && (_terminalTypeInitialMixed
            ? !_terminalTypeSelectedKeepUnchanged
            : !string.Equals(_terminalTypeValue, _terminalTypeInitial, StringComparison.Ordinal));

    public void RevertTerminalType()
    {
        _terminalTypeSelectedKeepUnchanged = _terminalTypeInitialMixed;
        _terminalTypeValue = _terminalTypeInitial;
        OnPropertyChanged(nameof(SelectedTerminalType));
        OnPropertyChanged(nameof(IsTerminalTypeModified));
        NotifySummaryChanged();
    }

    // --- TerminalEncoding ---

    public ObservableCollection<MultiEditStringOption> TerminalEncodingChoices { get; } = [];

    private bool _terminalEncodingSelectedKeepUnchanged;
    private string _terminalEncodingValue = Services.TerminalEncoding.DefaultName;

    public MultiEditStringOption? SelectedTerminalEncoding
    {
        get => _terminalEncodingSelectedKeepUnchanged
            ? TerminalEncodingChoices.FirstOrDefault(item => item.IsKeepUnchanged)
            : TerminalEncodingChoices.FirstOrDefault(item => !item.IsKeepUnchanged && string.Equals(item.Value, _terminalEncodingValue, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null)
                return;
            if (_terminalEncodingSelectedKeepUnchanged == value.IsKeepUnchanged
                && string.Equals(_terminalEncodingValue, value.Value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _terminalEncodingSelectedKeepUnchanged = value.IsKeepUnchanged;
            _terminalEncodingValue = value.Value;
            OnPropertyChanged(nameof(SelectedTerminalEncoding));
            OnPropertyChanged(nameof(IsTerminalEncodingModified));
            NotifySummaryChanged();
        }
    }

    public bool IsTerminalEncodingModified =>
        IsAllSsh && (_terminalEncodingInitialMixed
            ? !_terminalEncodingSelectedKeepUnchanged
            : !string.Equals(_terminalEncodingValue, _terminalEncodingInitial, StringComparison.OrdinalIgnoreCase));

    public void RevertTerminalEncoding()
    {
        _terminalEncodingSelectedKeepUnchanged = _terminalEncodingInitialMixed;
        _terminalEncodingValue = _terminalEncodingInitial;
        OnPropertyChanged(nameof(SelectedTerminalEncoding));
        OnPropertyChanged(nameof(IsTerminalEncodingModified));
        NotifySummaryChanged();
    }

    // --- PrivateKeyPath ---

    [ObservableProperty]
    private string _privateKeyPath = "";

    [ObservableProperty]
    private bool _privateKeyPathWillClear;

    public bool IsPrivateKeyPathModified =>
        IsAllSsh && (_privateKeyPathInitialMixed
            ? PrivateKeyPathWillClear || PrivateKeyPath.Length > 0
            : !string.Equals(PrivateKeyPath, _privateKeyPathInitial, StringComparison.Ordinal));

    public bool CanClearPrivateKeyPath => IsAllSsh && _privateKeyPathInitialMixed && !IsPrivateKeyPathModified;

    public string PrivateKeyPathPlaceholder =>
        PrivateKeyPathWillClear ? L("MultiEditWillClear")
        : _privateKeyPathInitialMixed ? L("MultiEditMultipleValues")
        : L("PrivateKeyWatermark");

    partial void OnPrivateKeyPathChanged(string value)
    {
        if (value.Length > 0 && PrivateKeyPathWillClear)
            PrivateKeyPathWillClear = false;
        NotifyPrivateKeyPathState();
    }

    partial void OnPrivateKeyPathWillClearChanged(bool value) => NotifyPrivateKeyPathState();

    public void ClearPrivateKeyPath()
    {
        PrivateKeyPath = "";
        PrivateKeyPathWillClear = _privateKeyPathInitialMixed;
        NotifyPrivateKeyPathState();
    }

    public void RevertPrivateKeyPath()
    {
        PrivateKeyPathWillClear = false;
        PrivateKeyPath = _privateKeyPathInitial;
        NotifyPrivateKeyPathState();
    }

    private void NotifyPrivateKeyPathState()
    {
        OnPropertyChanged(nameof(IsPrivateKeyPathModified));
        OnPropertyChanged(nameof(CanClearPrivateKeyPath));
        OnPropertyChanged(nameof(PrivateKeyPathPlaceholder));
        NotifySummaryChanged();
    }

    // --- PrivateKeyPassphrase (write-only on multi-edit) ---

    [ObservableProperty]
    private string _privateKeyPassphrase = "";

    [ObservableProperty]
    private bool _passphraseWillClear;

    public bool IsPassphraseModified => IsAllSsh && (PassphraseWillClear || PrivateKeyPassphrase.Length > 0);

    public bool CanClearPassphrase => IsAllSsh && _hasAnyPassphrase && !IsPassphraseModified;

    public string PassphrasePlaceholder =>
        PassphraseWillClear ? L("MultiEditWillClear")
        : _hasAnyPassphrase ? L("MultiEditKeepPassphrases")
        : L("PassphraseWatermark");

    partial void OnPrivateKeyPassphraseChanged(string value)
    {
        if (value.Length > 0 && PassphraseWillClear)
            PassphraseWillClear = false;
        NotifyPassphraseState();
    }

    partial void OnPassphraseWillClearChanged(bool value) => NotifyPassphraseState();

    public void ClearPassphrase()
    {
        PrivateKeyPassphrase = "";
        PassphraseWillClear = _hasAnyPassphrase;
        NotifyPassphraseState();
    }

    public void RevertPassphrase()
    {
        PassphraseWillClear = false;
        PrivateKeyPassphrase = "";
        NotifyPassphraseState();
    }

    private void NotifyPassphraseState()
    {
        OnPropertyChanged(nameof(IsPassphraseModified));
        OnPropertyChanged(nameof(CanClearPassphrase));
        OnPropertyChanged(nameof(PassphrasePlaceholder));
        NotifySummaryChanged();
    }

    // --- LoginCommands (SSH & WSL) ---

    [ObservableProperty]
    private string _loginCommands = "";

    [ObservableProperty]
    private bool _loginCommandsWillClear;

    public bool IsLoginCommandsModified =>
        (IsAllSsh || IsAllWsl) && (_loginCommandsInitialMixed
            ? LoginCommandsWillClear || LoginCommands.Length > 0
            : !string.Equals(LoginCommands, _loginCommandsInitial, StringComparison.Ordinal));

    public bool CanClearLoginCommands => (IsAllSsh || IsAllWsl) && _loginCommandsInitialMixed && !IsLoginCommandsModified;

    public string LoginCommandsPlaceholder =>
        LoginCommandsWillClear ? L("MultiEditWillClear")
        : _loginCommandsInitialMixed ? L("MultiEditMultipleValues")
        : L("LoginCommandsWatermark");

    public string LoginCommandsValidationMessage =>
        _cachedLoginCommandsValidationMessage ??= ComputeLoginCommandsValidationMessage();

    public bool HasLoginCommandsValidationMessage => LoginCommandsValidationMessage.Length > 0;

    partial void OnLoginCommandsChanged(string value)
    {
        if (value.Length > 0 && LoginCommandsWillClear)
            LoginCommandsWillClear = false;
        NotifyLoginCommandsState();
    }

    partial void OnLoginCommandsWillClearChanged(bool value) => NotifyLoginCommandsState();

    public void ClearLoginCommands()
    {
        LoginCommands = "";
        LoginCommandsWillClear = _loginCommandsInitialMixed;
        NotifyLoginCommandsState();
    }

    public void RevertLoginCommands()
    {
        LoginCommandsWillClear = false;
        LoginCommands = _loginCommandsInitial;
        NotifyLoginCommandsState();
    }

    private void NotifyLoginCommandsState()
    {
        OnPropertyChanged(nameof(IsLoginCommandsModified));
        OnPropertyChanged(nameof(CanClearLoginCommands));
        OnPropertyChanged(nameof(LoginCommandsPlaceholder));
        NotifyLoginCommandValidationChanged();
        NotifySummaryChanged();
    }

    private void NotifyLoginCommandValidationChanged()
    {
        _cachedLoginCommandsValidationMessage = null;
        OnPropertyChanged(nameof(LoginCommandsValidationMessage));
        OnPropertyChanged(nameof(HasLoginCommandsValidationMessage));
    }

    private string ComputeLoginCommandsValidationMessage()
    {
        if (!IsLoginCommandsModified || LoginCommandsWillClear || string.IsNullOrWhiteSpace(LoginCommands))
            return "";

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var messages = new List<string>();
        foreach (var target in _targets)
        {
            var probe = new Connection
            {
                Type = target.Type,
                Name = target.Name,
                Host = IsHostModified ? (HostWillClear ? "" : Host.Trim()) : target.Host,
                Port = IsPortModified && TryParsePort(PortText, out var parsedPort) ? parsedPort : target.Port,
                Username = IsUsernameModified ? (UsernameWillClear ? "" : Username.Trim()) : target.Username,
            };
            _bastionProfiles?.Resolve(probe);
            foreach (var error in LoginCommandSequence.Validate(LoginCommands, probe.ResolvedBastionProfile, probe))
            {
                if (seen.Add(error))
                    messages.Add(error);
            }
        }

        return string.Join(Environment.NewLine, messages);
    }

    // --- SSH auto-open & logging checkboxes ---

    [ObservableProperty]
    private bool? _autoOpenMonitorPanel;

    public bool AutoOpenMonitorPanelThreeState => _autoOpenMonitorInitial is null;

    public bool IsAutoOpenMonitorPanelModified => IsAllSsh && AutoOpenMonitorPanel != _autoOpenMonitorInitial;

    partial void OnAutoOpenMonitorPanelChanged(bool? value)
    {
        OnPropertyChanged(nameof(IsAutoOpenMonitorPanelModified));
        NotifySummaryChanged();
    }

    [ObservableProperty]
    private bool? _autoOpenFileBrowserPanel;

    public bool AutoOpenFileBrowserPanelThreeState => _autoOpenFileBrowserInitial is null;

    public bool IsAutoOpenFileBrowserPanelModified => IsAllSsh && AutoOpenFileBrowserPanel != _autoOpenFileBrowserInitial;

    partial void OnAutoOpenFileBrowserPanelChanged(bool? value)
    {
        OnPropertyChanged(nameof(IsAutoOpenFileBrowserPanelModified));
        NotifySummaryChanged();
    }

    [ObservableProperty]
    private bool? _autoLogSession;

    public bool AutoLogSessionThreeState => _autoLogSessionInitial is null;

    public bool IsAutoLogSessionModified => IsAllSsh && AutoLogSession != _autoLogSessionInitial;

    partial void OnAutoLogSessionChanged(bool? value)
    {
        OnPropertyChanged(nameof(IsAutoLogSessionModified));
        NotifySummaryChanged();
    }

    // --- Jump host (SSH & VNC) ---

    public string JumpHostConnectionId => _jumpHostConnectionId;

    public bool IsJumpHostMixed => _jumpHostIsMixed;

    public bool HasJumpHost => !_jumpHostIsMixed && !string.IsNullOrWhiteSpace(_jumpHostConnectionId);

    public bool ShowJumpHostSummary => _jumpHostIsMixed || HasJumpHost;

    public bool CanClearJumpHost => _jumpHostIsMixed || HasJumpHost;

    public bool IsJumpHostModified =>
        (IsAllSsh || IsAllVnc) && (_jumpHostIsMixed != _jumpHostInitialMixed
            || !string.Equals(_jumpHostConnectionId, _jumpHostInitialId, StringComparison.OrdinalIgnoreCase));

    private KeyValuePair<string, Connection>? CurrentJump =>
        _jumpConnections?.FirstOrDefault(pair =>
            string.Equals(pair.Value.ConnectionId, _jumpHostConnectionId, StringComparison.OrdinalIgnoreCase)) is { Value: not null } match
            ? match
            : null;

    public string JumpHostDisplay =>
        _jumpHostIsMixed
            ? L("MultiEditMultipleValues")
            : CurrentJump is { } saved
                ? $"{saved.Key}  ({saved.Value.Username}@{saved.Value.Host}:{(saved.Value.Port > 0 ? saved.Value.Port : 22)})"
                : _jumpHostConnectionId;

    public string JumpHostValidationMessage =>
        _cachedJumpHostValidationMessage ??= ComputeJumpHostValidationMessage();

    public bool HasJumpHostValidationMessage => JumpHostValidationMessage.Length > 0;

    public void SetJumpHostConnections(IEnumerable<KeyValuePair<string, Connection>> connections)
    {
        _jumpConnections = connections.ToDictionary(
            pair => NormalizeJumpPath(pair.Key),
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        _cachedJumpHostValidationMessage = null;
        NotifyJumpHostState();
    }

    public string ValidateJumpHostDrop(string path)
    {
        if (_jumpConnections is null)
            return L("JumpHostDropSshOnly");
        try
        {
            var jump = _jumpConnections.GetValueOrDefault(NormalizeJumpPath(path));
            if (jump is not { IsSsh: true } || jump.ConnectionId.Length == 0)
                return L("JumpHostDropSshOnly");
            foreach (var target in _targets)
            {
                SshDialer.ResolveJumpRoute(
                    new Connection { ConnectionId = target.ConnectionId, JumpHostConnectionId = jump.ConnectionId },
                    ResolveJumpReference);
            }
            return "";
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }

    public bool TrySetJumpHostFromTree(string path)
    {
        var error = ValidateJumpHostDrop(path);
        ReportJumpHostDropError(error);
        if (error.Length > 0)
            return false;

        _jumpHostIsMixed = false;
        _jumpHostConnectionId = _jumpConnections![NormalizeJumpPath(path)].ConnectionId;
        _cachedJumpHostValidationMessage = null;
        NotifyJumpHostState();
        NotifySummaryChanged();
        return true;
    }

    public void ClearJumpHost()
    {
        _jumpHostIsMixed = false;
        _jumpHostConnectionId = "";
        _jumpHostDropError = "";
        _cachedJumpHostValidationMessage = null;
        NotifyJumpHostState();
        NotifySummaryChanged();
    }

    public void RevertJumpHost()
    {
        _jumpHostIsMixed = _jumpHostInitialMixed;
        _jumpHostConnectionId = _jumpHostInitialId;
        _jumpHostDropError = "";
        _cachedJumpHostValidationMessage = null;
        NotifyJumpHostState();
        NotifySummaryChanged();
    }

    public void ReportJumpHostDropError(string error)
    {
        _jumpHostDropError = error;
        _cachedJumpHostValidationMessage = null;
        OnPropertyChanged(nameof(JumpHostValidationMessage));
        OnPropertyChanged(nameof(HasJumpHostValidationMessage));
    }

    private Connection? ResolveJumpReference(string id)
    {
        if (_jumpConnections is null)
            return null;
        var matches = _jumpConnections.Values
            .Where(connection => string.Equals(connection.ConnectionId, id, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException($"Jump host identity '{id}' is ambiguous.");
        return matches.SingleOrDefault();
    }

    private string ComputeJumpHostValidationMessage()
    {
        if (_jumpHostDropError.Length > 0)
            return _jumpHostDropError;
        if (_jumpConnections is null || !HasJumpHost)
            return "";
        try
        {
            foreach (var target in _targets)
            {
                SshDialer.ResolveJumpRoute(
                    new Connection { ConnectionId = target.ConnectionId, JumpHostConnectionId = _jumpHostConnectionId },
                    ResolveJumpReference);
            }
            return "";
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }

    private void NotifyJumpHostState()
    {
        OnPropertyChanged(nameof(JumpHostConnectionId));
        OnPropertyChanged(nameof(IsJumpHostMixed));
        OnPropertyChanged(nameof(HasJumpHost));
        OnPropertyChanged(nameof(ShowJumpHostSummary));
        OnPropertyChanged(nameof(CanClearJumpHost));
        OnPropertyChanged(nameof(IsJumpHostModified));
        OnPropertyChanged(nameof(JumpHostDisplay));
        OnPropertyChanged(nameof(JumpHostValidationMessage));
        OnPropertyChanged(nameof(HasJumpHostValidationMessage));
    }

    private static string NormalizeJumpPath(string path)
    {
        var normalized = path.Trim().Replace('\\', '/').Trim('/');
        return normalized.EndsWith(ConnectionStore.FileExtension, StringComparison.OrdinalIgnoreCase)
            ? normalized[..^ConnectionStore.FileExtension.Length]
            : normalized;
    }

    // --- PortForwards ---

    [ObservableProperty]
    private string _portForwards = "";

    [ObservableProperty]
    private bool _portForwardsWillClear;

    public bool IsPortForwardsModified =>
        IsAllSsh && (_portForwardsInitialMixed
            ? PortForwardsWillClear || PortForwards.Length > 0
            : !string.Equals(PortForwards, _portForwardsInitial, StringComparison.Ordinal));

    public bool CanClearPortForwards => IsAllSsh && _portForwardsInitialMixed && !IsPortForwardsModified;

    public string PortForwardsPlaceholder =>
        PortForwardsWillClear ? L("MultiEditWillClear")
        : _portForwardsInitialMixed ? L("MultiEditMultipleValues")
        : L("PortForwardsWatermark");

    public string PortForwardsValidationMessage =>
        IsPortForwardsModified && !PortForwardsWillClear
            ? SshPortForwarding.Validate(PortForwards) ?? ""
            : "";

    public bool HasPortForwardsValidationMessage => PortForwardsValidationMessage.Length > 0;

    partial void OnPortForwardsChanged(string value)
    {
        if (value.Length > 0 && PortForwardsWillClear)
            PortForwardsWillClear = false;
        NotifyPortForwardsState();
    }

    partial void OnPortForwardsWillClearChanged(bool value) => NotifyPortForwardsState();

    public void ClearPortForwards()
    {
        PortForwards = "";
        PortForwardsWillClear = _portForwardsInitialMixed;
        NotifyPortForwardsState();
    }

    public void RevertPortForwards()
    {
        PortForwardsWillClear = false;
        PortForwards = _portForwardsInitial;
        NotifyPortForwardsState();
    }

    private void NotifyPortForwardsState()
    {
        OnPropertyChanged(nameof(IsPortForwardsModified));
        OnPropertyChanged(nameof(CanClearPortForwards));
        OnPropertyChanged(nameof(PortForwardsPlaceholder));
        OnPropertyChanged(nameof(PortForwardsValidationMessage));
        OnPropertyChanged(nameof(HasPortForwardsValidationMessage));
        NotifySummaryChanged();
    }

    // --- WSL ---

    public ObservableCollection<MultiEditStringOption> WslDistroChoices { get; } = [];

    public MultiEditStringOption? SelectedWslDistro
    {
        get => _wslDistroSelectedKeepUnchanged
            ? WslDistroChoices.FirstOrDefault(item => item.IsKeepUnchanged)
            : WslDistroChoices.FirstOrDefault(item => !item.IsKeepUnchanged && string.Equals(item.Value, _wslDistroValue, StringComparison.Ordinal));
        set
        {
            if (value is null)
                return;
            if (_wslDistroSelectedKeepUnchanged == value.IsKeepUnchanged
                && string.Equals(_wslDistroValue, value.Value, StringComparison.Ordinal))
            {
                return;
            }
            _wslDistroSelectedKeepUnchanged = value.IsKeepUnchanged;
            _wslDistroValue = value.Value;
            OnPropertyChanged(nameof(SelectedWslDistro));
            OnPropertyChanged(nameof(IsWslDistroModified));
            NotifySummaryChanged();
        }
    }

    public bool IsWslDistroModified =>
        IsAllWsl && (_wslDistroInitialMixed
            ? !_wslDistroSelectedKeepUnchanged
            : !string.Equals(_wslDistroValue, _wslDistroInitial, StringComparison.Ordinal));

    public bool ShowNoWslDistrosHint =>
        IsAllWsl && WslDistroChoices.All(choice => choice.IsKeepUnchanged);

    public void RevertWslDistro()
    {
        _wslDistroSelectedKeepUnchanged = _wslDistroInitialMixed;
        _wslDistroValue = _wslDistroInitial;
        OnPropertyChanged(nameof(SelectedWslDistro));
        OnPropertyChanged(nameof(IsWslDistroModified));
        NotifySummaryChanged();
    }

    [ObservableProperty]
    private string _wslStartDirectory = "";

    [ObservableProperty]
    private bool _wslStartDirectoryWillClear;

    public bool IsWslStartDirectoryModified =>
        IsAllWsl && (_wslStartDirectoryInitialMixed
            ? WslStartDirectoryWillClear || WslStartDirectory.Length > 0
            : !string.Equals(WslStartDirectory, _wslStartDirectoryInitial, StringComparison.Ordinal));

    public bool CanClearWslStartDirectory => IsAllWsl && _wslStartDirectoryInitialMixed && !IsWslStartDirectoryModified;

    public string WslStartDirectoryPlaceholder =>
        WslStartDirectoryWillClear ? L("MultiEditWillClear")
        : _wslStartDirectoryInitialMixed ? L("MultiEditMultipleValues")
        : L("WslStartDirectoryWatermark");

    partial void OnWslStartDirectoryChanged(string value)
    {
        if (value.Length > 0 && WslStartDirectoryWillClear)
            WslStartDirectoryWillClear = false;
        NotifyWslStartDirectoryState();
    }

    partial void OnWslStartDirectoryWillClearChanged(bool value) => NotifyWslStartDirectoryState();

    public void ClearWslStartDirectory()
    {
        WslStartDirectory = "";
        WslStartDirectoryWillClear = _wslStartDirectoryInitialMixed;
        NotifyWslStartDirectoryState();
    }

    public void RevertWslStartDirectory()
    {
        WslStartDirectoryWillClear = false;
        WslStartDirectory = _wslStartDirectoryInitial;
        NotifyWslStartDirectoryState();
    }

    private void NotifyWslStartDirectoryState()
    {
        OnPropertyChanged(nameof(IsWslStartDirectoryModified));
        OnPropertyChanged(nameof(CanClearWslStartDirectory));
        OnPropertyChanged(nameof(WslStartDirectoryPlaceholder));
        NotifySummaryChanged();
    }

    // --- RDP ---

    [ObservableProperty]
    private bool? _rdpFullScreen;

    public bool RdpFullScreenThreeState => _rdpFullScreenInitial is null;

    public bool IsRdpFullScreenModified => IsAllRdp && RdpFullScreen != _rdpFullScreenInitial;

    public bool ShowRdpUseAllMonitors => RdpFullScreen != false;

    public bool ShowRdpDimensions => RdpFullScreen != true;

    partial void OnRdpFullScreenChanged(bool? value)
    {
        OnPropertyChanged(nameof(IsRdpFullScreenModified));
        OnPropertyChanged(nameof(ShowRdpUseAllMonitors));
        OnPropertyChanged(nameof(ShowRdpDimensions));
        NotifySummaryChanged();
    }

    [ObservableProperty]
    private bool? _rdpUseAllMonitors;

    public bool RdpUseAllMonitorsThreeState => _rdpUseAllMonitorsInitial is null;

    public bool IsRdpUseAllMonitorsModified => IsAllRdp && RdpUseAllMonitors != _rdpUseAllMonitorsInitial;

    partial void OnRdpUseAllMonitorsChanged(bool? value)
    {
        OnPropertyChanged(nameof(IsRdpUseAllMonitorsModified));
        NotifySummaryChanged();
    }

    [ObservableProperty]
    private string _rdpWidthText = "";

    public bool IsRdpWidthModified =>
        IsAllRdp && (_rdpWidthInitialMixed
            ? RdpWidthText.Trim().Length > 0
            : !string.Equals(RdpWidthText.Trim(), _rdpWidthInitialText, StringComparison.Ordinal));

    public string RdpWidthPlaceholder => _rdpWidthInitialMixed ? L("MultiEditMultipleShort") : "";

    partial void OnRdpWidthTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsRdpWidthModified));
        OnPropertyChanged(nameof(RdpDimensionsValidationMessage));
        OnPropertyChanged(nameof(HasRdpDimensionsValidationMessage));
        NotifySummaryChanged();
    }

    public void RevertRdpWidth() => RdpWidthText = _rdpWidthInitialText;

    [ObservableProperty]
    private string _rdpHeightText = "";

    public bool IsRdpHeightModified =>
        IsAllRdp && (_rdpHeightInitialMixed
            ? RdpHeightText.Trim().Length > 0
            : !string.Equals(RdpHeightText.Trim(), _rdpHeightInitialText, StringComparison.Ordinal));

    public string RdpHeightPlaceholder => _rdpHeightInitialMixed ? L("MultiEditMultipleShort") : "";

    partial void OnRdpHeightTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsRdpHeightModified));
        OnPropertyChanged(nameof(RdpDimensionsValidationMessage));
        OnPropertyChanged(nameof(HasRdpDimensionsValidationMessage));
        NotifySummaryChanged();
    }

    public void RevertRdpHeight() => RdpHeightText = _rdpHeightInitialText;

    public string RdpDimensionsValidationMessage =>
        (IsRdpWidthModified && !TryParsePositiveInt(RdpWidthText, out _))
        || (IsRdpHeightModified && !TryParsePositiveInt(RdpHeightText, out _))
            ? L("MultiEditInvalidDimension")
            : "";

    public bool HasRdpDimensionsValidationMessage => RdpDimensionsValidationMessage.Length > 0;

    [ObservableProperty]
    private bool? _rdpRedirectClipboard;

    public bool RdpRedirectClipboardThreeState => _rdpRedirectClipboardInitial is null;

    public bool IsRdpRedirectClipboardModified => IsAllRdp && RdpRedirectClipboard != _rdpRedirectClipboardInitial;

    partial void OnRdpRedirectClipboardChanged(bool? value)
    {
        OnPropertyChanged(nameof(IsRdpRedirectClipboardModified));
        NotifySummaryChanged();
    }

    [ObservableProperty]
    private bool? _rdpRedirectDrives;

    public bool RdpRedirectDrivesThreeState => _rdpRedirectDrivesInitial is null;

    public bool IsRdpRedirectDrivesModified => IsAllRdp && RdpRedirectDrives != _rdpRedirectDrivesInitial;

    partial void OnRdpRedirectDrivesChanged(bool? value)
    {
        OnPropertyChanged(nameof(IsRdpRedirectDrivesModified));
        NotifySummaryChanged();
    }

    [ObservableProperty]
    private bool? _rdpRedirectAudioPlayback;

    public bool RdpRedirectAudioPlaybackThreeState => _rdpRedirectAudioPlaybackInitial is null;

    public bool IsRdpRedirectAudioPlaybackModified => IsAllRdp && RdpRedirectAudioPlayback != _rdpRedirectAudioPlaybackInitial;

    partial void OnRdpRedirectAudioPlaybackChanged(bool? value)
    {
        OnPropertyChanged(nameof(IsRdpRedirectAudioPlaybackModified));
        NotifySummaryChanged();
    }

    [ObservableProperty]
    private bool? _rdpRedirectMicrophone;

    public bool RdpRedirectMicrophoneThreeState => _rdpRedirectMicrophoneInitial is null;

    public bool IsRdpRedirectMicrophoneModified => IsAllRdp && RdpRedirectMicrophone != _rdpRedirectMicrophoneInitial;

    partial void OnRdpRedirectMicrophoneChanged(bool? value)
    {
        OnPropertyChanged(nameof(IsRdpRedirectMicrophoneModified));
        NotifySummaryChanged();
    }

    // --- VNC ---

    [ObservableProperty]
    private bool? _vncFullScreen;

    public bool VncFullScreenThreeState => _vncFullScreenInitial is null;

    public bool IsVncFullScreenModified => IsAllVnc && VncFullScreen != _vncFullScreenInitial;

    partial void OnVncFullScreenChanged(bool? value)
    {
        OnPropertyChanged(nameof(IsVncFullScreenModified));
        NotifySummaryChanged();
    }

    [ObservableProperty]
    private bool? _vncViewOnly;

    public bool VncViewOnlyThreeState => _vncViewOnlyInitial is null;

    public bool IsVncViewOnlyModified => IsAllVnc && VncViewOnly != _vncViewOnlyInitial;

    partial void OnVncViewOnlyChanged(bool? value)
    {
        OnPropertyChanged(nameof(IsVncViewOnlyModified));
        NotifySummaryChanged();
    }

    [ObservableProperty]
    private bool? _vncShared;

    public bool VncSharedThreeState => _vncSharedInitial is null;

    public bool IsVncSharedModified => IsAllVnc && VncShared != _vncSharedInitial;

    partial void OnVncSharedChanged(bool? value)
    {
        OnPropertyChanged(nameof(IsVncSharedModified));
        NotifySummaryChanged();
    }

    // --- Notes ---

    [ObservableProperty]
    private string _notes = "";

    [ObservableProperty]
    private bool _notesWillClear;

    public bool IsNotesModified =>
        _notesInitialMixed
            ? NotesWillClear || Notes.Length > 0
            : !string.Equals(Notes, _notesInitial, StringComparison.Ordinal);

    public bool CanClearNotes => _notesInitialMixed && !IsNotesModified;

    public string NotesPlaceholder =>
        NotesWillClear ? L("MultiEditWillClear")
        : _notesInitialMixed ? L("MultiEditMultipleValues")
        : "";

    partial void OnNotesChanged(string value)
    {
        if (value.Length > 0 && NotesWillClear)
            NotesWillClear = false;
        NotifyNotesState();
    }

    partial void OnNotesWillClearChanged(bool value) => NotifyNotesState();

    public void ClearNotes()
    {
        Notes = "";
        NotesWillClear = _notesInitialMixed;
        NotifyNotesState();
    }

    public void RevertNotes()
    {
        NotesWillClear = false;
        Notes = _notesInitial;
        NotifyNotesState();
    }

    private void NotifyNotesState()
    {
        OnPropertyChanged(nameof(IsNotesModified));
        OnPropertyChanged(nameof(CanClearNotes));
        OnPropertyChanged(nameof(NotesPlaceholder));
        NotifySummaryChanged();
    }

    // --- Change summary & actions ---

    public int ModifiedFieldCount
    {
        get
        {
            var count = 0;
            if (IsShortNameModified) count++;
            if (IsHostModified) count++;
            if (IsPortModified) count++;
            if (IsUsernameModified) count++;
            if (IsPasswordModified) count++;
            if (IsSshMfaModified) count++;
            if (IsTerminalTypeModified) count++;
            if (IsTerminalEncodingModified) count++;
            if (IsPrivateKeyPathModified) count++;
            if (IsPassphraseModified) count++;
            if (IsLoginCommandsModified) count++;
            if (IsAutoOpenMonitorPanelModified) count++;
            if (IsAutoOpenFileBrowserPanelModified) count++;
            if (IsAutoLogSessionModified) count++;
            if (IsJumpHostModified) count++;
            if (IsPortForwardsModified) count++;
            if (IsWslDistroModified) count++;
            if (IsWslStartDirectoryModified) count++;
            if (IsRdpFullScreenModified) count++;
            if (IsRdpUseAllMonitorsModified) count++;
            if (IsRdpWidthModified) count++;
            if (IsRdpHeightModified) count++;
            if (IsRdpRedirectClipboardModified) count++;
            if (IsRdpRedirectDrivesModified) count++;
            if (IsRdpRedirectAudioPlaybackModified) count++;
            if (IsRdpRedirectMicrophoneModified) count++;
            if (IsVncFullScreenModified) count++;
            if (IsVncViewOnlyModified) count++;
            if (IsVncSharedModified) count++;
            if (IsNotesModified) count++;
            return count;
        }
    }

    public bool HasPendingChanges => ModifiedFieldCount > 0;

    public bool HasBlockingValidationErrors =>
        HasPortValidationMessage || HasRdpDimensionsValidationMessage;

    public bool CanApply => HasPendingChanges && !HasBlockingValidationErrors;

    public string ModifiedSummaryText =>
        ModifiedFieldCount == 0
            ? L("MultiEditNoChanges")
            : L("MultiEditModifiedCount", ModifiedFieldCount);

    private void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(ModifiedFieldCount));
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(HasBlockingValidationErrors));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(ModifiedSummaryText));
    }

    /// <summary>Applies only the modified fields to every target connection in place.</summary>
    public int ApplyToTargets()
    {
        if (!CanApply)
            return 0;

        var applyShortName = IsShortNameModified;
        var shortNameValue = ShortNameWillClear ? "" : ShortName.Trim();

        var applyHost = IsHostModified;
        var hostValue = HostWillClear ? "" : Host.Trim();

        var parsedPort = 0;
        var applyPort = IsPortModified && TryParsePort(PortText, out parsedPort);
        var portValue = applyPort ? parsedPort : 0;

        var applyUsername = IsUsernameModified;
        var usernameValue = UsernameWillClear ? "" : Username.Trim();

        var applyPassword = IsPasswordModified;
        var clearPassword = PasswordWillClear;
        var passwordClearText = Password;

        var applySshMfa = IsSshMfaModified;
        var sshMfaValue = _sshMfaProfileId;

        var applyTerminalType = IsTerminalTypeModified;
        var terminalTypeValue = string.IsNullOrWhiteSpace(_terminalTypeValue)
            ? Connection.DefaultTerminalType
            : _terminalTypeValue.Trim();

        var applyTerminalEncoding = IsTerminalEncodingModified;
        var terminalEncodingValue = Services.TerminalEncoding.Normalize(_terminalEncodingValue);

        var applyPrivateKeyPath = IsPrivateKeyPathModified;
        var privateKeyPathValue = PrivateKeyPathWillClear ? "" : PrivateKeyPath.Trim();

        var applyPassphrase = IsPassphraseModified;
        var clearPassphrase = PassphraseWillClear;
        var passphraseClearText = PrivateKeyPassphrase;

        var applyLoginCommands = IsLoginCommandsModified;
        var loginCommandsValue = LoginCommandsWillClear
            ? ""
            : LoginCommandSequence.TrimSurroundingBlankLines(LoginCommands);

        var applyAutoMonitor = IsAutoOpenMonitorPanelModified && AutoOpenMonitorPanel.HasValue;
        var applyAutoFileBrowser = IsAutoOpenFileBrowserPanelModified && AutoOpenFileBrowserPanel.HasValue;
        var applyAutoLog = IsAutoLogSessionModified && AutoLogSession.HasValue;

        var applyJumpHost = IsJumpHostModified && !_jumpHostIsMixed;
        var jumpHostValue = _jumpHostConnectionId;

        var applyPortForwards = IsPortForwardsModified;
        var portForwardsValue = PortForwardsWillClear ? "" : PortForwards;

        var applyWslDistro = IsWslDistroModified && !_wslDistroSelectedKeepUnchanged;
        var wslDistroValue = _wslDistroValue.Trim();

        var applyWslStartDir = IsWslStartDirectoryModified;
        var wslStartDirValue = WslStartDirectoryWillClear ? "" : WslStartDirectory.Trim();

        var applyRdpFullScreen = IsRdpFullScreenModified && RdpFullScreen.HasValue;
        var applyRdpAllMonitors = IsRdpUseAllMonitorsModified && RdpUseAllMonitors.HasValue;
        var parsedWidth = 0;
        var applyRdpWidth = IsRdpWidthModified && TryParsePositiveInt(RdpWidthText, out parsedWidth);
        var rdpWidthValue = applyRdpWidth ? parsedWidth : 0;
        var parsedHeight = 0;
        var applyRdpHeight = IsRdpHeightModified && TryParsePositiveInt(RdpHeightText, out parsedHeight);
        var rdpHeightValue = applyRdpHeight ? parsedHeight : 0;
        var applyRdpClipboard = IsRdpRedirectClipboardModified && RdpRedirectClipboard.HasValue;
        var applyRdpDrives = IsRdpRedirectDrivesModified && RdpRedirectDrives.HasValue;
        var applyRdpAudio = IsRdpRedirectAudioPlaybackModified && RdpRedirectAudioPlayback.HasValue;
        var applyRdpMic = IsRdpRedirectMicrophoneModified && RdpRedirectMicrophone.HasValue;

        var applyVncFullScreen = IsVncFullScreenModified && VncFullScreen.HasValue;
        var applyVncViewOnly = IsVncViewOnlyModified && VncViewOnly.HasValue;
        var applyVncShared = IsVncSharedModified && VncShared.HasValue;

        var applyNotes = IsNotesModified;
        var notesValue = NotesWillClear ? "" : Notes;

        foreach (var c in _targets)
        {
            if (applyShortName) c.ShortName = shortNameValue;
            if (applyHost) c.Host = hostValue;
            if (applyPort) c.Port = portValue;
            if (applyUsername) c.Username = usernameValue;
            if (applyPassword)
            {
                // Encrypt separately per connection so each file keeps its own random salt/nonce.
                c.EncryptedPassword = clearPassword ? "" : PasswordProtector.Encrypt(passwordClearText);
            }

            if (applySshMfa) c.SshMfaProfileId = sshMfaValue;
            if (applyTerminalType) c.TerminalType = terminalTypeValue;
            if (applyTerminalEncoding) c.TerminalEncoding = terminalEncodingValue;
            if (applyPrivateKeyPath) c.PrivateKeyPath = privateKeyPathValue;
            if (applyPassphrase)
            {
                c.EncryptedPrivateKeyPassphrase = clearPassphrase ? "" : PasswordProtector.Encrypt(passphraseClearText);
            }
            if (applyLoginCommands) c.LoginCommands = loginCommandsValue;
            if (applyAutoMonitor) c.AutoOpenMonitorPanel = AutoOpenMonitorPanel!.Value;
            if (applyAutoFileBrowser) c.AutoOpenFileBrowserPanel = AutoOpenFileBrowserPanel!.Value;
            if (applyAutoLog) c.AutoLogSession = AutoLogSession!.Value;
            if (applyJumpHost) c.JumpHostConnectionId = jumpHostValue;
            if (applyPortForwards) c.PortForwards = portForwardsValue;

            if (applyWslDistro) c.WslDistro = wslDistroValue;
            if (applyWslStartDir) c.WslStartDirectory = wslStartDirValue;

            if (applyRdpFullScreen) c.RdpFullScreen = RdpFullScreen!.Value;
            if (applyRdpAllMonitors) c.RdpUseAllMonitors = RdpUseAllMonitors!.Value;
            if (applyRdpWidth) c.RdpWidth = rdpWidthValue;
            if (applyRdpHeight) c.RdpHeight = rdpHeightValue;
            if (applyRdpClipboard) c.RdpRedirectClipboard = RdpRedirectClipboard!.Value;
            if (applyRdpDrives) c.RdpRedirectDrives = RdpRedirectDrives!.Value;
            if (applyRdpAudio) c.RdpRedirectAudioPlayback = RdpRedirectAudioPlayback!.Value;
            if (applyRdpMic) c.RdpRedirectMicrophone = RdpRedirectMicrophone!.Value;

            if (applyVncFullScreen) c.VncFullScreen = VncFullScreen!.Value;
            if (applyVncViewOnly) c.VncViewOnly = VncViewOnly!.Value;
            if (applyVncShared) c.VncShared = VncShared!.Value;

            if (applyNotes) c.Notes = notesValue;
        }

        return _targets.Count;
    }

    /// <summary>Reverts every uncommitted edit back to the targets' current saved state.</summary>
    public void Reset()
    {
        LoadBaselinesFromTargets();
        NotifyAllFieldProperties();
    }

    /// <summary>Refreshes localized labels and placeholders after an in-app language switch.</summary>
    public void RefreshLocalization()
    {
        foreach (var item in TerminalTypeChoices.Where(c => c.IsKeepUnchanged))
            item.Label = L("MultiEditMultipleChoice");
        foreach (var item in TerminalEncodingChoices.Where(c => c.IsKeepUnchanged))
            item.Label = L("MultiEditMultipleChoice");
        foreach (var item in WslDistroChoices.Where(c => c.IsKeepUnchanged))
            item.Label = L("MultiEditMultipleChoice");
        foreach (var item in SshMfaProfileChoices.Where(c => c.IsKeepUnchanged))
            item.Name = L("MultiEditMultipleChoice");

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(TypeSummary));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(ApplyButtonText));
        NotifyAllFieldProperties();
    }

#pragma warning disable MVVMTK0034 // Bulk baseline initialization without intermediate change notifications.
    private void LoadBaselinesFromTargets()
    {
        (_shortNameInitialMixed, _shortNameInitial) = SummarizeString(_targets.Select(c => c.ShortName ?? ""));
        _shortNameWillClear = false;
        _shortName = _shortNameInitial;

        if (HasHostPort)
        {
            (_hostInitialMixed, _hostInitial) = SummarizeString(_targets.Select(c => c.Host ?? ""));
            _hostWillClear = false;
            _host = _hostInitial;

            (_portInitialMixed, _portInitialText) = SummarizeInt(
                _targets.Select(c => c.Port > 0 ? c.Port : Connection.DefaultPort(c.Type)));
            _portText = _portInitialText;
        }
        else
        {
            _hostInitialMixed = false;
            _hostInitial = "";
            _hostWillClear = false;
            _host = "";
            _portInitialMixed = false;
            _portInitialText = "";
            _portText = "";
        }

        (_usernameInitialMixed, _usernameInitial) = SummarizeString(_targets.Select(c => c.Username ?? ""));
        _usernameWillClear = false;
        _username = _usernameInitial;

        _hasAnyPassword = HasPassword && _targets.Any(c => !string.IsNullOrEmpty(c.EncryptedPassword));
        _password = "";
        _passwordWillClear = false;

        if (IsAllSsh)
        {
            var mfaDistinct = _targets.Select(c => c.SshMfaProfileId).Distinct().ToArray();
            _sshMfaInitialMixed = mfaDistinct.Length > 1;
            _sshMfaInitialId = _sshMfaInitialMixed ? null : mfaDistinct[0];
            _sshMfaSelectedKeepUnchanged = _sshMfaInitialMixed;
            _sshMfaProfileId = _sshMfaInitialId;

            (_terminalTypeInitialMixed, _terminalTypeInitial) = SummarizeString(
                _targets.Select(c => string.IsNullOrWhiteSpace(c.TerminalType) ? Connection.DefaultTerminalType : c.TerminalType));
            _terminalTypeSelectedKeepUnchanged = _terminalTypeInitialMixed;
            _terminalTypeValue = _terminalTypeInitialMixed ? Connection.DefaultTerminalType : _terminalTypeInitial;
            RebuildTerminalTypeChoices();

            (_terminalEncodingInitialMixed, _terminalEncodingInitial) = SummarizeString(
                _targets.Select(c => Services.TerminalEncoding.Normalize(c.TerminalEncoding)),
                StringComparer.OrdinalIgnoreCase);
            _terminalEncodingSelectedKeepUnchanged = _terminalEncodingInitialMixed;
            _terminalEncodingValue = _terminalEncodingInitialMixed ? Services.TerminalEncoding.DefaultName : _terminalEncodingInitial;
            RebuildTerminalEncodingChoices();

            (_privateKeyPathInitialMixed, _privateKeyPathInitial) = SummarizeString(_targets.Select(c => c.PrivateKeyPath ?? ""));
            _privateKeyPathWillClear = false;
            _privateKeyPath = _privateKeyPathInitial;

            _hasAnyPassphrase = _targets.Any(c => !string.IsNullOrEmpty(c.EncryptedPrivateKeyPassphrase));
            _privateKeyPassphrase = "";
            _passphraseWillClear = false;

            _autoOpenMonitorInitial = SummarizeBool(_targets.Select(c => c.AutoOpenMonitorPanel));
            _autoOpenMonitorPanel = _autoOpenMonitorInitial;

            _autoOpenFileBrowserInitial = SummarizeBool(_targets.Select(c => c.AutoOpenFileBrowserPanel));
            _autoOpenFileBrowserPanel = _autoOpenFileBrowserInitial;

            _autoLogSessionInitial = SummarizeBool(_targets.Select(c => c.AutoLogSession));
            _autoLogSession = _autoLogSessionInitial;

            (_portForwardsInitialMixed, _portForwardsInitial) = SummarizeString(_targets.Select(c => c.PortForwards ?? ""));
            _portForwardsWillClear = false;
            _portForwards = _portForwardsInitial;
        }
        else
        {
            _hasAnyPassphrase = false;
            _privateKeyPassphrase = "";
            _passphraseWillClear = false;
        }

        if (IsAllSsh || IsAllWsl)
        {
            (_loginCommandsInitialMixed, _loginCommandsInitial) = SummarizeString(_targets.Select(c => c.LoginCommands ?? ""));
            _loginCommandsWillClear = false;
            _loginCommands = _loginCommandsInitial;
        }
        else
        {
            _loginCommandsInitialMixed = false;
            _loginCommandsInitial = "";
            _loginCommandsWillClear = false;
            _loginCommands = "";
        }

        if (IsAllSsh || IsAllVnc)
        {
            (_jumpHostInitialMixed, _jumpHostInitialId) = SummarizeString(
                _targets.Select(c => c.JumpHostConnectionId ?? ""),
                StringComparer.OrdinalIgnoreCase);
            _jumpHostIsMixed = _jumpHostInitialMixed;
            _jumpHostConnectionId = _jumpHostInitialId;
            _jumpHostDropError = "";
            _cachedJumpHostValidationMessage = null;
        }
        else
        {
            _jumpHostInitialMixed = false;
            _jumpHostInitialId = "";
            _jumpHostIsMixed = false;
            _jumpHostConnectionId = "";
            _jumpHostDropError = "";
            _cachedJumpHostValidationMessage = null;
        }

        if (IsAllWsl)
        {
            (_wslDistroInitialMixed, _wslDistroInitial) = SummarizeString(_targets.Select(c => c.WslDistro ?? ""));
            _wslDistroSelectedKeepUnchanged = _wslDistroInitialMixed;
            _wslDistroValue = _wslDistroInitial;
            RebuildWslDistroChoices();

            (_wslStartDirectoryInitialMixed, _wslStartDirectoryInitial) = SummarizeString(_targets.Select(c => c.WslStartDirectory ?? ""));
            _wslStartDirectoryWillClear = false;
            _wslStartDirectory = _wslStartDirectoryInitial;
        }

        if (IsAllRdp)
        {
            _rdpFullScreenInitial = SummarizeBool(_targets.Select(c => c.RdpFullScreen));
            _rdpFullScreen = _rdpFullScreenInitial;

            _rdpUseAllMonitorsInitial = SummarizeBool(_targets.Select(c => c.RdpUseAllMonitors));
            _rdpUseAllMonitors = _rdpUseAllMonitorsInitial;

            (_rdpWidthInitialMixed, _rdpWidthInitialText) = SummarizeInt(_targets.Select(c => c.RdpWidth));
            _rdpWidthText = _rdpWidthInitialText;

            (_rdpHeightInitialMixed, _rdpHeightInitialText) = SummarizeInt(_targets.Select(c => c.RdpHeight));
            _rdpHeightText = _rdpHeightInitialText;

            _rdpRedirectClipboardInitial = SummarizeBool(_targets.Select(c => c.RdpRedirectClipboard));
            _rdpRedirectClipboard = _rdpRedirectClipboardInitial;

            _rdpRedirectDrivesInitial = SummarizeBool(_targets.Select(c => c.RdpRedirectDrives));
            _rdpRedirectDrives = _rdpRedirectDrivesInitial;

            _rdpRedirectAudioPlaybackInitial = SummarizeBool(_targets.Select(c => c.RdpRedirectAudioPlayback));
            _rdpRedirectAudioPlayback = _rdpRedirectAudioPlaybackInitial;

            _rdpRedirectMicrophoneInitial = SummarizeBool(_targets.Select(c => c.RdpRedirectMicrophone));
            _rdpRedirectMicrophone = _rdpRedirectMicrophoneInitial;
        }

        if (IsAllVnc)
        {
            _vncFullScreenInitial = SummarizeBool(_targets.Select(c => c.VncFullScreen));
            _vncFullScreen = _vncFullScreenInitial;

            _vncViewOnlyInitial = SummarizeBool(_targets.Select(c => c.VncViewOnly));
            _vncViewOnly = _vncViewOnlyInitial;

            _vncSharedInitial = SummarizeBool(_targets.Select(c => c.VncShared));
            _vncShared = _vncSharedInitial;
        }

        (_notesInitialMixed, _notesInitial) = SummarizeString(_targets.Select(c => c.Notes ?? ""));
        _notesWillClear = false;
        _notes = _notesInitial;

        _cachedLoginCommandsValidationMessage = null;
    }
#pragma warning restore MVVMTK0034

    private void RebuildTerminalTypeChoices()
    {
        var values = new List<string>(CommonTerminalTypes);
        if (!_terminalTypeInitialMixed
            && !string.IsNullOrWhiteSpace(_terminalTypeInitial)
            && !values.Contains(_terminalTypeInitial, StringComparer.Ordinal))
        {
            values.Insert(0, _terminalTypeInitial);
        }

        TerminalTypeChoices.Clear();
        if (_terminalTypeInitialMixed)
            TerminalTypeChoices.Add(new("", L("MultiEditMultipleChoice"), isKeepUnchanged: true));
        foreach (var value in values)
            TerminalTypeChoices.Add(new(value, value));
    }

    private void RebuildTerminalEncodingChoices()
    {
        TerminalEncodingChoices.Clear();
        if (_terminalEncodingInitialMixed)
            TerminalEncodingChoices.Add(new("", L("MultiEditMultipleChoice"), isKeepUnchanged: true));
        foreach (var name in Services.TerminalEncoding.Names)
            TerminalEncodingChoices.Add(new(name, name));
    }

    private void RebuildWslDistroChoices()
    {
        var names = Services.WslDistroService.ListDistros().Select(d => d.Name).ToList();
        if (!_wslDistroInitialMixed
            && !string.IsNullOrEmpty(_wslDistroInitial)
            && !names.Contains(_wslDistroInitial, StringComparer.Ordinal))
        {
            names.Insert(0, _wslDistroInitial);
        }

        WslDistroChoices.Clear();
        if (_wslDistroInitialMixed)
            WslDistroChoices.Add(new("", L("MultiEditMultipleChoice"), isKeepUnchanged: true));
        foreach (var name in names)
            WslDistroChoices.Add(new(name, name));
    }

    private void NotifyAllFieldProperties()
    {
        OnPropertyChanged(nameof(ShortName));
        OnPropertyChanged(nameof(ShortNameWillClear));
        OnPropertyChanged(nameof(IsShortNameModified));
        OnPropertyChanged(nameof(CanClearShortName));
        OnPropertyChanged(nameof(ShortNamePlaceholder));

        OnPropertyChanged(nameof(Host));
        OnPropertyChanged(nameof(HostWillClear));
        OnPropertyChanged(nameof(IsHostModified));
        OnPropertyChanged(nameof(CanClearHost));
        OnPropertyChanged(nameof(HostPlaceholder));

        OnPropertyChanged(nameof(PortText));
        OnPropertyChanged(nameof(IsPortModified));
        OnPropertyChanged(nameof(PortPlaceholder));
        OnPropertyChanged(nameof(PortValidationMessage));
        OnPropertyChanged(nameof(HasPortValidationMessage));

        OnPropertyChanged(nameof(Username));
        OnPropertyChanged(nameof(UsernameWillClear));
        OnPropertyChanged(nameof(IsUsernameModified));
        OnPropertyChanged(nameof(CanClearUsername));
        OnPropertyChanged(nameof(UsernamePlaceholder));

        OnPropertyChanged(nameof(Password));
        OnPropertyChanged(nameof(PasswordWillClear));
        OnPropertyChanged(nameof(IsPasswordModified));
        OnPropertyChanged(nameof(CanClearPassword));
        OnPropertyChanged(nameof(PasswordPlaceholder));

        OnPropertyChanged(nameof(SelectedSshMfaProfile));
        OnPropertyChanged(nameof(IsSshMfaModified));

        OnPropertyChanged(nameof(SelectedTerminalType));
        OnPropertyChanged(nameof(IsTerminalTypeModified));

        OnPropertyChanged(nameof(SelectedTerminalEncoding));
        OnPropertyChanged(nameof(IsTerminalEncodingModified));

        OnPropertyChanged(nameof(PrivateKeyPath));
        OnPropertyChanged(nameof(PrivateKeyPathWillClear));
        OnPropertyChanged(nameof(IsPrivateKeyPathModified));
        OnPropertyChanged(nameof(CanClearPrivateKeyPath));
        OnPropertyChanged(nameof(PrivateKeyPathPlaceholder));

        OnPropertyChanged(nameof(PrivateKeyPassphrase));
        OnPropertyChanged(nameof(PassphraseWillClear));
        OnPropertyChanged(nameof(IsPassphraseModified));
        OnPropertyChanged(nameof(CanClearPassphrase));
        OnPropertyChanged(nameof(PassphrasePlaceholder));

        OnPropertyChanged(nameof(LoginCommands));
        OnPropertyChanged(nameof(LoginCommandsWillClear));
        OnPropertyChanged(nameof(IsLoginCommandsModified));
        OnPropertyChanged(nameof(CanClearLoginCommands));
        OnPropertyChanged(nameof(LoginCommandsPlaceholder));
        NotifyLoginCommandValidationChanged();

        OnPropertyChanged(nameof(AutoOpenMonitorPanel));
        OnPropertyChanged(nameof(AutoOpenMonitorPanelThreeState));
        OnPropertyChanged(nameof(IsAutoOpenMonitorPanelModified));
        OnPropertyChanged(nameof(AutoOpenFileBrowserPanel));
        OnPropertyChanged(nameof(AutoOpenFileBrowserPanelThreeState));
        OnPropertyChanged(nameof(IsAutoOpenFileBrowserPanelModified));
        OnPropertyChanged(nameof(AutoLogSession));
        OnPropertyChanged(nameof(AutoLogSessionThreeState));
        OnPropertyChanged(nameof(IsAutoLogSessionModified));

        NotifyJumpHostState();

        OnPropertyChanged(nameof(PortForwards));
        OnPropertyChanged(nameof(PortForwardsWillClear));
        OnPropertyChanged(nameof(IsPortForwardsModified));
        OnPropertyChanged(nameof(CanClearPortForwards));
        OnPropertyChanged(nameof(PortForwardsPlaceholder));
        OnPropertyChanged(nameof(PortForwardsValidationMessage));
        OnPropertyChanged(nameof(HasPortForwardsValidationMessage));

        OnPropertyChanged(nameof(SelectedWslDistro));
        OnPropertyChanged(nameof(IsWslDistroModified));
        OnPropertyChanged(nameof(ShowNoWslDistrosHint));
        OnPropertyChanged(nameof(WslStartDirectory));
        OnPropertyChanged(nameof(WslStartDirectoryWillClear));
        OnPropertyChanged(nameof(IsWslStartDirectoryModified));
        OnPropertyChanged(nameof(CanClearWslStartDirectory));
        OnPropertyChanged(nameof(WslStartDirectoryPlaceholder));

        OnPropertyChanged(nameof(RdpFullScreen));
        OnPropertyChanged(nameof(RdpFullScreenThreeState));
        OnPropertyChanged(nameof(IsRdpFullScreenModified));
        OnPropertyChanged(nameof(ShowRdpUseAllMonitors));
        OnPropertyChanged(nameof(ShowRdpDimensions));
        OnPropertyChanged(nameof(RdpUseAllMonitors));
        OnPropertyChanged(nameof(RdpUseAllMonitorsThreeState));
        OnPropertyChanged(nameof(IsRdpUseAllMonitorsModified));
        OnPropertyChanged(nameof(RdpWidthText));
        OnPropertyChanged(nameof(IsRdpWidthModified));
        OnPropertyChanged(nameof(RdpWidthPlaceholder));
        OnPropertyChanged(nameof(RdpHeightText));
        OnPropertyChanged(nameof(IsRdpHeightModified));
        OnPropertyChanged(nameof(RdpHeightPlaceholder));
        OnPropertyChanged(nameof(RdpDimensionsValidationMessage));
        OnPropertyChanged(nameof(HasRdpDimensionsValidationMessage));
        OnPropertyChanged(nameof(RdpRedirectClipboard));
        OnPropertyChanged(nameof(RdpRedirectClipboardThreeState));
        OnPropertyChanged(nameof(IsRdpRedirectClipboardModified));
        OnPropertyChanged(nameof(RdpRedirectDrives));
        OnPropertyChanged(nameof(RdpRedirectDrivesThreeState));
        OnPropertyChanged(nameof(IsRdpRedirectDrivesModified));
        OnPropertyChanged(nameof(RdpRedirectAudioPlayback));
        OnPropertyChanged(nameof(RdpRedirectAudioPlaybackThreeState));
        OnPropertyChanged(nameof(IsRdpRedirectAudioPlaybackModified));
        OnPropertyChanged(nameof(RdpRedirectMicrophone));
        OnPropertyChanged(nameof(RdpRedirectMicrophoneThreeState));
        OnPropertyChanged(nameof(IsRdpRedirectMicrophoneModified));

        OnPropertyChanged(nameof(VncFullScreen));
        OnPropertyChanged(nameof(VncFullScreenThreeState));
        OnPropertyChanged(nameof(IsVncFullScreenModified));
        OnPropertyChanged(nameof(VncViewOnly));
        OnPropertyChanged(nameof(VncViewOnlyThreeState));
        OnPropertyChanged(nameof(IsVncViewOnlyModified));
        OnPropertyChanged(nameof(VncShared));
        OnPropertyChanged(nameof(VncSharedThreeState));
        OnPropertyChanged(nameof(IsVncSharedModified));

        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(NotesWillClear));
        OnPropertyChanged(nameof(IsNotesModified));
        OnPropertyChanged(nameof(CanClearNotes));
        OnPropertyChanged(nameof(NotesPlaceholder));

        NotifySummaryChanged();
    }

    private static (bool IsMixed, string Value) SummarizeString(
        IEnumerable<string> values,
        StringComparer? comparer = null)
    {
        comparer ??= StringComparer.Ordinal;
        string? first = null;
        foreach (var value in values)
        {
            if (first is null)
            {
                first = value;
                continue;
            }
            if (!comparer.Equals(first, value))
                return (true, "");
        }

        return (false, first ?? "");
    }

    private static (bool IsMixed, string Text) SummarizeInt(IEnumerable<int> values)
    {
        int? first = null;
        foreach (var value in values)
        {
            if (first is null)
            {
                first = value;
                continue;
            }
            if (first.Value != value)
                return (true, "");
        }

        return (false, first?.ToString() ?? "");
    }

    private static bool? SummarizeBool(IEnumerable<bool> values)
    {
        bool? first = null;
        foreach (var value in values)
        {
            if (first is null)
            {
                first = value;
                continue;
            }
            if (first.Value != value)
                return null;
        }

        return first;
    }

    private static bool TryParsePort(string text, out int port) =>
        int.TryParse(text.Trim(), out port) && port is >= 1 and <= 65535;

    private static bool TryParsePositiveInt(string text, out int value) =>
        int.TryParse(text.Trim(), out value) && value >= 1;
}
