using System;
using System.IO;

namespace JeekRemoteManager.Services;

/// <summary>Registers this GUI build for the fixed per-user MCP adapter.</summary>
public static class McpAdapterRegistration
{
    public static string? ConfigInstanceId =>
        DebugInstanceContext.IsDebugBuild ? DebugInstanceContext.InstanceId : null;

    public static McpRegisteredInstance RegisterCurrentInstance()
    {
        var sourceAdapter = Path.Combine(AppContext.BaseDirectory, "JeekRemoteManagerMcp.exe");
        // Always refresh the fixed adapter when the side-by-side publish next to this app
        // differs. Agents launch that fixed path (not bin\), so Debug worktrees must be able
        // to push routing fixes even if a Release instance is also registered. A locked
        // destination keeps the previous file — safe for this protocol-agnostic forwarder.
        if (!McpAdapterRegistry.EnsureAdapterInstalled(sourceAdapter, allowUpdate: true))
        {
            throw new IOException("Could not install the fixed JeekRemoteManager MCP adapter.");
        }

        var appPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(appPath))
            throw new InvalidOperationException("The current JeekRemoteManager executable path is unavailable.");

        var registration = new McpRegisteredInstance(
            DebugInstanceContext.InstanceId,
            appPath,
            DebugInstanceContext.ProductMcpPipeName,
            DebugInstanceContext.IsDebugBuild ? DebugInstanceContext.DebugMcpPipeName : "",
            DebugInstanceContext.IsDebugBuild,
            DebugInstanceContext.WorkspaceRoot);
        McpAdapterRegistry.WriteInstance(registration);
        return registration;
    }

    public static bool IsCurrentInstanceRegistered()
    {
        if (!File.Exists(McpAdapterRegistry.AdapterPath)
            || !McpAdapterRegistry.TryReadInstance(DebugInstanceContext.InstanceId, out var registered))
        {
            return false;
        }

        return string.Equals(
                   Path.GetFullPath(registered.AppPath),
                   Path.GetFullPath(Environment.ProcessPath ?? ""),
                   StringComparison.OrdinalIgnoreCase)
               && registered.ProductPipeName == DebugInstanceContext.ProductMcpPipeName
               && (!DebugInstanceContext.IsDebugBuild
                   || registered.DebugPipeName == DebugInstanceContext.DebugMcpPipeName);
    }
}
