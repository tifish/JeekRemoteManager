using System.Text.Json.Serialization;

namespace JeekRemoteManager.Models;

/// <summary>
/// A single remote connection (SSH or RDP). Each instance is persisted to its
/// own file on disk by <see cref="Services.ConnectionStore"/>.
/// </summary>
public class Connection
{
    public ConnectionType Type { get; set; } = ConnectionType.Ssh;

    /// <summary>Display name. Also used as the file name on disk.</summary>
    public string Name { get; set; } = "New Connection";

    public string Host { get; set; } = "";

    /// <summary>TCP port. Defaults to 22 (SSH) or 3389 (RDP).</summary>
    public int Port { get; set; } = 22;

    public string Username { get; set; } = "";

    /// <summary>
    /// DPAPI-encrypted password, Base64 encoded. Never stored in plain text.
    /// Use <see cref="Services.PasswordProtector"/> to read/write the clear value.
    /// </summary>
    public string EncryptedPassword { get; set; } = "";

    // --- SSH specific ---

    /// <summary>Optional path to a private key file (ssh -i).</summary>
    public string PrivateKeyPath { get; set; } = "";

    /// <summary>Extra raw arguments appended to the ssh command line.</summary>
    public string ExtraSshArguments { get; set; } = "";

    // --- RDP specific ---

    /// <summary>When true, mstsc launches full screen; otherwise windowed.</summary>
    public bool RdpFullScreen { get; set; } = true;

    /// <summary>When true (and full screen), spans the session across all monitors.</summary>
    public bool RdpUseAllMonitors { get; set; } = false;

    public int RdpWidth { get; set; } = 1280;

    public int RdpHeight { get; set; } = 720;

    /// <summary>Share the local clipboard with the remote session.</summary>
    public bool RdpRedirectClipboard { get; set; } = true;

    /// <summary>Share local drives with the remote session.</summary>
    public bool RdpRedirectDrives { get; set; } = false;

    /// <summary>Play remote audio on this (local) machine.</summary>
    public bool RdpRedirectAudioPlayback { get; set; } = true;

    /// <summary>Capture the local microphone and send it to the remote session.</summary>
    public bool RdpRedirectMicrophone { get; set; } = false;

    /// <summary>Free-form note shown in the editor.</summary>
    public string Notes { get; set; } = "";

    /// <summary>Default port for the given connection type.</summary>
    public static int DefaultPort(ConnectionType type) =>
        type == ConnectionType.Rdp ? 3389 : 22;

    [JsonIgnore]
    public bool IsSsh => Type == ConnectionType.Ssh;

    [JsonIgnore]
    public bool IsRdp => Type == ConnectionType.Rdp;
}
