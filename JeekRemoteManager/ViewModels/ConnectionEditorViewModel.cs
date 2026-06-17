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

    // SSH
    [ObservableProperty]
    private string _privateKeyPath = "";

    [ObservableProperty]
    private string _extraSshArguments = "";

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
    public static ConnectionType[] AvailableTypes { get; } = System.Enum.GetValues<ConnectionType>();

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
            ExtraSshArguments = c.ExtraSshArguments,
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

        // Decrypt the password, but remember the original ciphertext so we never
        // overwrite a password we could not read (see ApplyTo).
        vm._originalEncryptedPassword = c.EncryptedPassword;
        var decrypted = PasswordProtector.TryDecrypt(c.EncryptedPassword, out var clear);
        vm.PasswordDecryptFailed = !decrypted;
        vm.Password = decrypted ? clear : "";
        return vm;
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
        c.EncryptedPassword = PasswordDecryptFailed && Password.Length == 0
            ? _originalEncryptedPassword
            : PasswordProtector.Encrypt(Password);
        c.PrivateKeyPath = PrivateKeyPath.Trim();
        c.ExtraSshArguments = ExtraSshArguments.Trim();
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
