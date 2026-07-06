namespace JeekRemoteManager.Models;

public static class ConnectionTypeDisplay
{
    public static string ToDisplayName(this ConnectionType type) => type switch
    {
        ConnectionType.Ssh => "SSH",
        ConnectionType.Rdp => "RDP",
        ConnectionType.Wsl => "WSL",
        _ => type.ToString().ToUpperInvariant(),
    };

    public static string ToGlyph(this ConnectionType type) => type switch
    {
        ConnectionType.Rdp => "\U0001F5A5",
        ConnectionType.Wsl => "\U0001F427",
        _ => ">_",
    };

    public static ConnectionType FromDisplayName(string? displayName) =>
        displayName?.Trim().ToUpperInvariant() switch
        {
            "RDP" => ConnectionType.Rdp,
            "WSL" => ConnectionType.Wsl,
            _ => ConnectionType.Ssh,
        };
}
