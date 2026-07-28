using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace JeekRemoteManager.Services;

/// <summary>
/// Named-pipe naming convention shared by the app and the <c>JrmMcp</c> stdio adapter.
/// Compiled into both (the adapter links this file), so the two sides can never drift.
///
/// A pipe replaces the loopback HTTP endpoint for MCP: nothing to allocate, so the name is
/// stable forever and can be hard-coded in a user's project config. Release builds use the
/// bare names; Debug builds append an instance id derived from the executable directory so
/// parallel worktree instances never answer for each other.
/// </summary>
public static class McpPipeNames
{
    /// <summary>Product surface: connections, sessions, terminal tools. Shipped to users.</summary>
    public const string ProductBase = "JeekRemoteManager.Mcp";

    /// <summary>Debug surface: object graph, visual tree, probes. Debug builds only.</summary>
    public const string DebugBase = "JeekRemoteManager.Mcp.Debug";

    /// <summary>
    /// Stable 12-hex identity of an installation, hashed from its executable directory.
    /// The adapter lives in the same folder as the app, so it derives the same value
    /// without being told which instance to talk to.
    /// </summary>
    public static string InstanceId(string executableDirectory) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Normalize(executableDirectory))))[..12].ToLowerInvariant();

    public static string Product(string? instanceId) => Compose(ProductBase, instanceId);

    public static string Debug(string? instanceId) => Compose(DebugBase, instanceId);

    /// <summary>Resolves "product"/"debug" plus an optional instance id to a pipe name.</summary>
    public static string Resolve(string surface, string? instanceId) =>
        surface.Equals("debug", StringComparison.OrdinalIgnoreCase)
            ? Debug(instanceId)
            : Product(instanceId);

    private static string Compose(string baseName, string? instanceId) =>
        string.IsNullOrWhiteSpace(instanceId) || instanceId == "release"
            ? baseName
            : $"{baseName}.{instanceId.Trim()}";

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
}
