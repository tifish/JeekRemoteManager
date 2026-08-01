using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;

namespace JeekRemoteManager.ViewModels;

/// <summary>
/// Editable view of a single connection. Loaded from a <see cref="Connection"/>
/// and applied back to it on save. The password is held in clear text only in
/// memory while editing.
/// </summary>
public partial class ConnectionEditorViewModel : ViewModelBase
{
    private BastionLoginProfileStore? _bastionProfiles;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSsh))]
    [NotifyPropertyChangedFor(nameof(IsRdp))]
    [NotifyPropertyChangedFor(nameof(IsWsl))]
    [NotifyPropertyChangedFor(nameof(HasHostPort))]
    [NotifyPropertyChangedFor(nameof(HasPassword))]
    [NotifyPropertyChangedFor(nameof(SupportsScripts))]
    [NotifyPropertyChangedFor(nameof(ShowNoWslDistrosHint))]
    [NotifyPropertyChangedFor(nameof(TypeDisplay))]
    private ConnectionType _type = ConnectionType.Ssh;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _host = "";

    [ObservableProperty]
    private int _port = 22;

    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private string _password = "";

    /// <summary>
    /// The password ciphertext exactly as stored on disk. Kept so that a password we
    /// could not decrypt (e.g. it belongs to a different master password) is never
    /// silently destroyed on auto-save.
    /// </summary>
    private string _originalEncryptedPassword = "";

    /// <summary>
    /// True when the stored password is non-empty but could not be decrypted with the
    /// current master password. While true the cleartext box is empty; the original
    /// ciphertext is preserved unless the user types a new password.
    /// </summary>
    [ObservableProperty]
    private bool _passwordDecryptFailed;

    private bool _passwordEdited;

    // SSH
    [ObservableProperty]
    private string _terminalType = Connection.DefaultTerminalType;

    [ObservableProperty]
    private string _privateKeyPath = "";

    [ObservableProperty]
    private string _privateKeyPassphrase = "";

    /// <summary>The passphrase ciphertext as stored on disk; preserved so a passphrase
    /// we could not decrypt (different master password) is never clobbered on save.</summary>
    private string _originalEncryptedPassphrase = "";

    [ObservableProperty]
    private bool _passphraseDecryptFailed;

    private bool _passphraseEdited;

    [ObservableProperty]
    private string _loginCommands = "";

    [ObservableProperty]
    private bool _hasBastionProfile;

    [ObservableProperty]
    private string _bastionTemplateId = "";

    [ObservableProperty]
    private string _bastionProfileEndpoint = "";

    [ObservableProperty]
    private string _bastionTemplateSegment1 = "";

    [ObservableProperty]
    private string _bastionTemplateSegment2 = "";

    [ObservableProperty]
    private string _bastionTemplateSegment3 = "";

    [ObservableProperty]
    private string _bastionTemplateSegment4 = "";

    private BastionLoginProfile? CurrentBastionTemplate =>
        !HasBastionProfile
            ? null
            : new BastionLoginProfile
            {
                Id = BastionTemplateId,
                EndpointLabel = BastionProfileEndpoint,
                Segments =
                [
                    BastionTemplateSegment1,
                    BastionTemplateSegment2,
                    BastionTemplateSegment3,
                    BastionTemplateSegment4,
                ],
            };

    private string? _cachedLoginCommandsValidationMessage;

    public string LoginCommandsValidationMessage =>
        _cachedLoginCommandsValidationMessage ??= string.Join(
            System.Environment.NewLine,
            LoginCommandSequence.Validate(
                LoginCommands,
                CurrentBastionTemplate,
                CreateLoginCommandVariableConnection()));

    public bool HasLoginCommandsValidationMessage =>
        LoginCommandsValidationMessage.Length > 0;

    [ObservableProperty]
    private bool _autoOpenMonitorPanel;

    [ObservableProperty]
    private bool _autoOpenFileBrowserPanel;

    public ObservableCollection<ConnectionScriptBindingViewModel> ScriptBindings { get; } = new();

    // WSL
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableWslDistros))]
    private string _wslDistro = "";

    [ObservableProperty]
    private string _wslStartDirectory = "";

    // RDP
    [ObservableProperty]
    private bool _rdpFullScreen = true;

    [ObservableProperty]
    private bool _rdpUseAllMonitors;

    [ObservableProperty]
    private int _rdpWidth = 1280;

    [ObservableProperty]
    private int _rdpHeight = 720;

    [ObservableProperty]
    private bool _rdpRedirectClipboard = true;

    [ObservableProperty]
    private bool _rdpRedirectDrives;

    [ObservableProperty]
    private bool _rdpRedirectAudioPlayback = true;

    [ObservableProperty]
    private bool _rdpRedirectMicrophone;

    [ObservableProperty]
    private string _notes = "";

    public bool IsSsh => Type == ConnectionType.Ssh;

    public bool IsRdp => Type == ConnectionType.Rdp;

    public bool IsWsl => Type == ConnectionType.Wsl;

    /// <summary>WSL connections have no host/port — the editor swaps that row for
    /// the distribution selector.</summary>
    public bool HasHostPort => !IsWsl;

    /// <summary>WSL needs no password (wsl.exe runs as the local user).</summary>
    public bool HasPassword => !IsWsl;

    /// <summary>SSH scripts run through the interactive terminal, which WSL shares.</summary>
    public bool SupportsScripts => Type is ConnectionType.Ssh or ConnectionType.Wsl;

    /// <summary>Installed WSL distributions for the selector, prepending the current
    /// value when it is not (or no longer) installed.</summary>
    public string[] AvailableWslDistros
    {
        get
        {
            var current = WslDistro ?? "";
            var names = Services.WslDistroService.ListDistros().Select(d => d.Name).ToArray();
            return current.Length == 0 || names.Contains(current)
                ? names
                : names.Prepend(current).ToArray();
        }
    }

    public bool ShowNoWslDistrosHint => IsWsl && AvailableWslDistros.Length == 0;

    partial void OnWslDistroChanged(string? oldValue, string newValue)
    {
        // The distro ComboBox writes null through its two-way binding while its
        // items detach/reattach (editor rebinds, the type switches). Keep the
        // previous value so the selection survives, and push it back to the
        // ComboBox once the binding storm settles.
        if (newValue is not null)
            return;
        _wslDistro = oldValue ?? "";
        Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(WslDistro)));
    }

    partial void OnTypeChanged(ConnectionType value)
    {
        NotifyLoginCommandPresentationChanged();

        // Switching an existing connection to WSL: preselect the default distro so
        // the selector is not blank.
        if (value == ConnectionType.Wsl && string.IsNullOrEmpty(WslDistro))
            WslDistro = Services.WslDistroService.ListDistros().FirstOrDefault(d => d.IsDefault)?.Name ?? "";
    }

    /// <summary>Connection types offered in the editor's Type selector.</summary>
    public static string[] AvailableTypeDisplays { get; } = System.Enum.GetValues<ConnectionType>()
        .Select(type => type.ToDisplayName())
        .ToArray();

    /// <summary>Common TERM values offered in the editor's terminal-type selector.</summary>
    private static readonly string[] CommonTerminalTypes =
    {
        "xterm-256color", "xterm", "screen-256color", "screen", "tmux-256color", "vt100", "linux", "ansi",
    };

    /// <summary>Terminal types for the selector, prepending the current value if it is not a known one.</summary>
    public string[] AvailableTerminalTypes =>
        CommonTerminalTypes.Contains(TerminalType)
            ? CommonTerminalTypes
            : CommonTerminalTypes.Prepend(TerminalType).ToArray();

    public string TypeDisplay
    {
        get => Type.ToDisplayName();
        set => Type = ConnectionTypeDisplay.FromDisplayName(value);
    }

    public static ConnectionEditorViewModel FromConnection(
        Connection c,
        BastionLoginProfileStore? bastionProfiles = null)
    {
        bastionProfiles?.Resolve(c);
        var profile = c.ResolvedBastionProfile;
        var vm = new ConnectionEditorViewModel
        {
            _bastionProfiles = bastionProfiles,
            Type = c.Type,
            Name = c.Name,
            Host = c.Host,
            Port = c.Port,
            Username = c.Username,
            TerminalType = string.IsNullOrWhiteSpace(c.TerminalType) ? Connection.DefaultTerminalType : c.TerminalType,
            PrivateKeyPath = c.PrivateKeyPath,
            LoginCommands = c.LoginCommands,
            HasBastionProfile = profile is not null,
            BastionTemplateId = profile?.Id ?? "",
            BastionProfileEndpoint = profile?.EndpointLabel ?? "",
            BastionTemplateSegment1 = profile?.GetSegment(1) ?? "",
            BastionTemplateSegment2 = profile?.GetSegment(2) ?? "",
            BastionTemplateSegment3 = profile?.GetSegment(3) ?? "",
            BastionTemplateSegment4 = profile?.GetSegment(4) ?? "",
            AutoOpenMonitorPanel = c.AutoOpenMonitorPanel,
            AutoOpenFileBrowserPanel = c.AutoOpenFileBrowserPanel,
            WslDistro = c.WslDistro,
            WslStartDirectory = c.WslStartDirectory,
            RdpFullScreen = c.RdpFullScreen,
            RdpUseAllMonitors = c.RdpUseAllMonitors,
            RdpWidth = c.RdpWidth,
            RdpHeight = c.RdpHeight,
            RdpRedirectClipboard = c.RdpRedirectClipboard,
            RdpRedirectDrives = c.RdpRedirectDrives,
            RdpRedirectAudioPlayback = c.RdpRedirectAudioPlayback,
            RdpRedirectMicrophone = c.RdpRedirectMicrophone,
            Notes = c.Notes,
        };

        foreach (var binding in c.ScriptBindings)
            vm.ScriptBindings.Add(ConnectionScriptBindingViewModel.FromModel(binding));

        // Decrypt the password, but remember the original ciphertext so we never
        // overwrite a password we could not read (see ApplyTo).
        vm._originalEncryptedPassword = c.EncryptedPassword;
        var decrypted = PasswordProtector.TryDecrypt(c.EncryptedPassword, out var clear);
        vm.PasswordDecryptFailed = !decrypted;
        vm.Password = decrypted ? clear : "";
        vm._passwordEdited = false;

        vm._originalEncryptedPassphrase = c.EncryptedPrivateKeyPassphrase;
        var passphraseDecrypted = PasswordProtector.TryDecrypt(c.EncryptedPrivateKeyPassphrase, out var clearPassphrase);
        vm.PassphraseDecryptFailed = !passphraseDecrypted;
        vm.PrivateKeyPassphrase = passphraseDecrypted ? clearPassphrase : "";
        vm._passphraseEdited = false;
        return vm;
    }

    partial void OnPasswordChanged(string value)
    {
        _passwordEdited = true;
    }

    partial void OnPrivateKeyPassphraseChanged(string value)
    {
        _passphraseEdited = true;
    }

    partial void OnNameChanged(string value) =>
        NotifyLoginCommandPresentationChanged();

    partial void OnHostChanged(string value) =>
        NotifyLoginCommandPresentationChanged();

    partial void OnPortChanged(int value) =>
        NotifyLoginCommandPresentationChanged();

    partial void OnUsernameChanged(string value) =>
        NotifyLoginCommandPresentationChanged();

    partial void OnLoginCommandsChanged(string value)
    {
        NotifyLoginCommandPresentationChanged();
    }

    partial void OnHasBastionProfileChanged(bool value) =>
        NotifyLoginCommandPresentationChanged();

    partial void OnBastionTemplateSegment1Changed(string value) =>
        NotifyLoginCommandPresentationChanged();

    partial void OnBastionTemplateSegment2Changed(string value) =>
        NotifyLoginCommandPresentationChanged();

    partial void OnBastionTemplateSegment3Changed(string value) =>
        NotifyLoginCommandPresentationChanged();

    partial void OnBastionTemplateSegment4Changed(string value) =>
        NotifyLoginCommandPresentationChanged();

    private void NotifyLoginCommandPresentationChanged()
    {
        _cachedLoginCommandsValidationMessage = null;
        OnPropertyChanged(nameof(LoginCommandsValidationMessage));
        OnPropertyChanged(nameof(HasLoginCommandsValidationMessage));
    }

    public IReadOnlyList<string> ValidateBastionTemplateFragment(string fragment) =>
        LoginCommandSequence.ValidateVariables(
            fragment,
            CreateLoginCommandVariableConnection());

    private Connection CreateLoginCommandVariableConnection() =>
        new()
        {
            Type = Type,
            Name = Name,
            Host = Host,
            Port = Port,
            Username = Username,
        };

    /// <summary>Writes the edited values back into the given connection.</summary>
    public void ApplyTo(Connection c)
    {
        c.Type = Type;
        c.Name = string.IsNullOrWhiteSpace(Name) ? L("UnnamedConnection") : Name.Trim();
        c.Host = Host.Trim();
        c.Port = Port > 0 ? Port : Connection.DefaultPort(Type);
        c.Username = Username.Trim();
        c.TerminalType = string.IsNullOrWhiteSpace(TerminalType) ? Connection.DefaultTerminalType : TerminalType.Trim();
        // If we could not decrypt the stored password and the user has not typed a
        // replacement, keep the original ciphertext intact instead of clobbering it
        // with an encryption of the (empty) box.
        var preserveExistingPassword = !_passwordEdited || (PasswordDecryptFailed && Password.Length == 0);
        c.EncryptedPassword = preserveExistingPassword
            ? _originalEncryptedPassword
            : PasswordProtector.Encrypt(Password);
        c.PrivateKeyPath = PrivateKeyPath.Trim();
        // Same preserve-on-undecryptable rule as the password above.
        var preserveExistingPassphrase = !_passphraseEdited || (PassphraseDecryptFailed && PrivateKeyPassphrase.Length == 0);
        c.EncryptedPrivateKeyPassphrase = preserveExistingPassphrase
            ? _originalEncryptedPassphrase
            : PasswordProtector.Encrypt(PrivateKeyPassphrase);
        ApplyBastionCommands(c);
        c.AutoOpenMonitorPanel = AutoOpenMonitorPanel;
        c.AutoOpenFileBrowserPanel = AutoOpenFileBrowserPanel;
        c.WslDistro = WslDistro.Trim();
        c.WslStartDirectory = WslStartDirectory.Trim();
        c.ScriptBindings = ScriptBindings
            .Select(b => b.ToModel())
            .Where(b => !string.IsNullOrWhiteSpace(b.Name))
            .GroupBy(b => b.Name, System.StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();
        c.RdpFullScreen = RdpFullScreen;
        c.RdpUseAllMonitors = RdpUseAllMonitors;
        c.RdpWidth = RdpWidth;
        c.RdpHeight = RdpHeight;
        c.RdpRedirectClipboard = RdpRedirectClipboard;
        c.RdpRedirectDrives = RdpRedirectDrives;
        c.RdpRedirectAudioPlayback = RdpRedirectAudioPlayback;
        c.RdpRedirectMicrophone = RdpRedirectMicrophone;
        c.Notes = Notes;
    }

    private void ApplyBastionCommands(Connection connection)
    {
        var normalizedLoginCommands =
            LoginCommandSequence.TrimSurroundingBlankLines(LoginCommands);
        connection.LoginCommands = normalizedLoginCommands;
        if (_bastionProfiles is null)
            return;

        if (!connection.IsSsh)
        {
            connection.ResolvedBastionProfile = null;
            return;
        }

        var automaticId = _bastionProfiles.AutomaticProfileId(connection);
        if (!string.Equals(BastionTemplateId, automaticId, StringComparison.Ordinal))
        {
            _bastionProfiles.Resolve(connection);
            var profile = connection.ResolvedBastionProfile!;
            LoadProfileIntoEditor(profile);
            HasBastionProfile = true;
            return;
        }

        var edited = CurrentBastionTemplate!;
        var existing = _bastionProfiles.Get(automaticId);
        if (existing is not null || edited.Segments.Any(segment => !string.IsNullOrWhiteSpace(segment)))
            _bastionProfiles.Save(edited);
        _bastionProfiles.Resolve(connection);
    }

    private void LoadProfileIntoEditor(BastionLoginProfile profile)
    {
        BastionTemplateId = profile.Id;
        BastionProfileEndpoint = profile.EndpointLabel;
        BastionTemplateSegment1 = profile.GetSegment(1);
        BastionTemplateSegment2 = profile.GetSegment(2);
        BastionTemplateSegment3 = profile.GetSegment(3);
        BastionTemplateSegment4 = profile.GetSegment(4);
    }

}
