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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSsh))]
    [NotifyPropertyChangedFor(nameof(IsRdp))]
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
    private string _privateKeyPath = "";

    [ObservableProperty]
    private string _privateKeyPassphrase = "";

    /// <summary>The passphrase ciphertext as stored on disk; preserved so a passphrase
    /// we could not decrypt (different master password) is never clobbered on save.</summary>
    private string _originalEncryptedPassphrase = "";

    [ObservableProperty]
    private bool _passphraseDecryptFailed;

    private bool _passphraseEdited;

    public ObservableCollection<ConnectionScriptBindingViewModel> ScriptBindings { get; } = new();

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

    /// <summary>Connection types offered in the editor's Type selector.</summary>
    public static string[] AvailableTypeDisplays { get; } = System.Enum.GetValues<ConnectionType>()
        .Select(type => type.ToDisplayName())
        .ToArray();

    public string TypeDisplay
    {
        get => Type.ToDisplayName();
        set => Type = ConnectionTypeDisplay.FromDisplayName(value);
    }

    public static ConnectionEditorViewModel FromConnection(Connection c)
    {
        var vm = new ConnectionEditorViewModel
        {
            Type = c.Type,
            Name = c.Name,
            Host = c.Host,
            Port = c.Port,
            Username = c.Username,
            PrivateKeyPath = c.PrivateKeyPath,
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

    /// <summary>Writes the edited values back into the given connection.</summary>
    public void ApplyTo(Connection c)
    {
        c.Type = Type;
        c.Name = string.IsNullOrWhiteSpace(Name) ? L("UnnamedConnection") : Name.Trim();
        c.Host = Host.Trim();
        c.Port = Port > 0 ? Port : Connection.DefaultPort(Type);
        c.Username = Username.Trim();
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
}
