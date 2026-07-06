using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace JeekRemoteManager.Models;

/// <summary>
/// A single remote connection (SSH, RDP, or a local WSL distribution). Each
/// instance is persisted to its own file on disk by <see cref="Services.ConnectionStore"/>.
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
    /// Master-password-encrypted jrm1 blob. Never stored in plain text. Use
    /// <see cref="Services.PasswordProtector"/> to read/write the clear value.
    /// </summary>
    public string EncryptedPassword { get; set; } = "";

    // --- SSH specific ---

    /// <summary>Terminal type advertised to servers that lack an xterm-256color terminfo entry.</summary>
    public const string DefaultTerminalType = "xterm-256color";

    /// <summary>
    /// TERM value sent during the SSH PTY request. Defaults to "xterm-256color";
    /// set to "xterm" for hosts that lack a 256-color terminfo entry and warn at login.
    /// </summary>
    public string TerminalType { get; set; } = DefaultTerminalType;

    /// <summary>Optional path to a private key file (ssh -i).</summary>
    public string PrivateKeyPath { get; set; } = "";

    /// <summary>
    /// Master-password-encrypted passphrase for the private key (jrm1 blob), or
    /// empty when the key is unencrypted. Read/write via <see cref="Services.PasswordProtector"/>.
    /// </summary>
    public string EncryptedPrivateKeyPassphrase { get; set; } = "";

    /// <summary>
    /// Commands typed into the shell automatically after login, one per line.
    /// Each line is sent only after the remote output has gone quiet, so bastion
    /// menus, sudo prompts, etc. are on screen before their answer is typed.
    /// </summary>
    public string LoginCommands { get; set; } = "";

    /// <summary>Per-connection parameter bindings for reusable SSH scripts.</summary>
    public List<ConnectionScriptBinding> ScriptBindings { get; set; } = new();

    // --- WSL specific ---

    /// <summary>WSL distribution name (wsl -d). Empty means the default distribution.</summary>
    public string WslDistro { get; set; } = "";

    /// <summary>Initial directory inside the distribution (wsl --cd). Empty = the user's home.</summary>
    public string WslStartDirectory { get; set; } = "";

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

    [JsonIgnore]
    public bool IsWsl => Type == ConnectionType.Wsl;

    /// <summary>What this connection points at, for status/log messages:
    /// the host for SSH/RDP, the distribution (or "WSL") for WSL.</summary>
    [JsonIgnore]
    public string TargetLabel => IsWsl
        ? (string.IsNullOrWhiteSpace(WslDistro) ? "WSL" : WslDistro.Trim())
        : Host;
}
