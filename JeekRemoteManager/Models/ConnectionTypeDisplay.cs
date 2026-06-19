namespace JeekRemoteManager.Models;

public static class ConnectionTypeDisplay
{
    public static string ToDisplayName(this ConnectionType type) => type switch
    {
        ConnectionType.Ssh => "SSH",
        ConnectionType.Rdp => "RDP",
        _ => type.ToString().ToUpperInvariant(),
    };

    public static ConnectionType FromDisplayName(string? displayName) =>
        displayName?.Trim().ToUpperInvariant() switch
        {
            "RDP" => ConnectionType.Rdp,
            _ => ConnectionType.Ssh,
        };
}
