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

    // SSH
    [ObservableProperty]
    private string _privateKeyPath = "";

    [ObservableProperty]
    private string _extraSshArguments = "";

    // RDP
    [ObservableProperty]
    private bool _rdpFullScreen = true;

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

    public static ConnectionEditorViewModel FromConnection(Connection c) => new()
    {
        Type = c.Type,
        Name = c.Name,
        Host = c.Host,
        Port = c.Port,
        Username = c.Username,
        Password = PasswordProtector.Decrypt(c.EncryptedPassword),
        PrivateKeyPath = c.PrivateKeyPath,
        ExtraSshArguments = c.ExtraSshArguments,
        RdpFullScreen = c.RdpFullScreen,
        RdpWidth = c.RdpWidth,
        RdpHeight = c.RdpHeight,
        RdpRedirectClipboard = c.RdpRedirectClipboard,
        RdpRedirectDrives = c.RdpRedirectDrives,
        RdpRedirectAudioPlayback = c.RdpRedirectAudioPlayback,
        RdpRedirectMicrophone = c.RdpRedirectMicrophone,
        Notes = c.Notes,
    };

    /// <summary>Writes the edited values back into the given connection.</summary>
    public void ApplyTo(Connection c)
    {
        c.Type = Type;
        c.Name = string.IsNullOrWhiteSpace(Name) ? "Unnamed" : Name.Trim();
        c.Host = Host.Trim();
        c.Port = Port > 0 ? Port : Connection.DefaultPort(Type);
        c.Username = Username.Trim();
        c.EncryptedPassword = PasswordProtector.Encrypt(Password);
        c.PrivateKeyPath = PrivateKeyPath.Trim();
        c.ExtraSshArguments = ExtraSshArguments.Trim();
        c.RdpFullScreen = RdpFullScreen;
        c.RdpWidth = RdpWidth;
        c.RdpHeight = RdpHeight;
        c.RdpRedirectClipboard = RdpRedirectClipboard;
        c.RdpRedirectDrives = RdpRedirectDrives;
        c.RdpRedirectAudioPlayback = RdpRedirectAudioPlayback;
        c.RdpRedirectMicrophone = RdpRedirectMicrophone;
        c.Notes = Notes;
    }
}
