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

    public static ConnectionType FromDisplayName(string? displayName) =>
        displayName?.Trim().ToUpperInvariant() switch
        {
            "RDP" => ConnectionType.Rdp,
            "WSL" => ConnectionType.Wsl,
            _ => ConnectionType.Ssh,
        };
}
