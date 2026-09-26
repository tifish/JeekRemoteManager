namespace JeekRemoteManager.Models;

public static class ConnectionTypeDisplay
{
    public static string ToDisplayName(this ConnectionType type) => type switch
    {
        ConnectionType.Ssh => "SSH",
        ConnectionType.Rdp => "RDP",
        ConnectionType.Wsl => "WSL",
        ConnectionType.Vnc => "VNC",
        _ => type.ToString().ToUpperInvariant(),
    };

    public static string ToGlyph(this ConnectionType type) => type switch
    {
        ConnectionType.Rdp => "\U0001F5A5",
        ConnectionType.Wsl => "\U0001F427",
        ConnectionType.Vnc => "\U0001F4FA",
        _ => ">_",
    };

    public static ConnectionType FromDisplayName(string? displayName) =>
        displayName?.Trim().ToUpperInvariant() switch
        {
            "RDP" => ConnectionType.Rdp,
            "WSL" => ConnectionType.Wsl,
            "VNC" => ConnectionType.Vnc,
            _ => ConnectionType.Ssh,
        };
}
