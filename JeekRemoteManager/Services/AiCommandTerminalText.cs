namespace JeekRemoteManager.Services;

public static class AiCommandTerminalText
{
    public static string NormalizeForTerminalEcho(string command)
    {
        var normalized = command.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        return normalized.Replace("\n", "\r\n");
    }
}
