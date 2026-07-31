using System;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using Microsoft.Win32;

namespace JeekRemoteManager.Services;

/// <summary>
/// One application instance that the fixed per-user MCP adapter can route to.
/// Release uses the stable <c>release</c> key; every Debug worktree uses its path-derived id.
/// </summary>
public sealed record McpRegisteredInstance(
    string InstanceId,
    string AppPath,
    string ProductPipeName,
    string DebugPipeName,
    bool IsDebugBuild,
    string WorkspaceRoot);

/// <summary>
/// Fixed adapter location and per-instance registry shared by the GUI and the stdio adapter.
/// The adapter executable is stable, while these entries point it at the current Release install
/// or a specific Debug worktree.
/// </summary>
public static class McpAdapterRegistry
{
    private const string RegistryBasePath = @"Software\JeekRemoteManager\Mcp\Instances";
    private const string InstallMutexName = "JeekRemoteManager.McpAdapter.Install";

    public static string AdapterDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JeekRemoteManager",
        "Mcp");

    public static string AdapterPath { get; } =
        Path.Combine(AdapterDirectory, "JeekRemoteManagerMcp.exe");

    /// <summary>
    /// Installs the side-by-side adapter at its stable per-user path. Release owns adapter updates;
    /// Debug builds may update it only while no Release installation is registered.
    /// </summary>
    public static bool EnsureAdapterInstalled(string sourcePath, bool allowUpdate)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The JeekRemoteManager MCP adapter was not published.", sourcePath);

        Directory.CreateDirectory(AdapterDirectory);
        using var mutex = new Mutex(false, InstallMutexName);
        var lockTaken = false;
        try
        {
            try
            {
                lockTaken = mutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                lockTaken = true;
            }
            if (!lockTaken)
                return File.Exists(AdapterPath);

            if (File.Exists(AdapterPath)
                && (!allowUpdate || FilesEqual(sourcePath, AdapterPath)))
            {
                return true;
            }

            var temporary = AdapterPath + "." + Environment.ProcessId + ".new";
            try
            {
                File.Copy(sourcePath, temporary, overwrite: true);
                File.Move(temporary, AdapterPath, overwrite: true);
                return true;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException
                && File.Exists(AdapterPath))
            {
                // An agent may currently be running the fixed executable. It is a protocol-
                // agnostic pipe forwarder, so retaining the installed copy is safe.
                return true;
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best-effort cleanup after a locked replacement.
                }
            }
        }
        finally
        {
            if (lockTaken)
                mutex.ReleaseMutex();
        }
    }

    public static void WriteInstance(McpRegisteredInstance instance)
    {
        ValidateInstanceId(instance.InstanceId);
        using var key = Registry.CurrentUser.CreateSubKey(
            $@"{RegistryBasePath}\{instance.InstanceId}",
            writable: true)
            ?? throw new InvalidOperationException("Could not open the MCP instance registry key.");

        key.SetValue("AppPath", Path.GetFullPath(instance.AppPath), RegistryValueKind.String);
        key.SetValue("ProductPipeName", instance.ProductPipeName, RegistryValueKind.String);
        key.SetValue("DebugPipeName", instance.DebugPipeName, RegistryValueKind.String);
        key.SetValue("Build", instance.IsDebugBuild ? "Debug" : "Release", RegistryValueKind.String);
        key.SetValue("WorkspaceRoot", Path.GetFullPath(instance.WorkspaceRoot), RegistryValueKind.String);
        key.SetValue("UpdatedUtc", DateTime.UtcNow.ToString("O"), RegistryValueKind.String);
    }

    public static bool TryReadInstance(string? instanceId, out McpRegisteredInstance instance)
    {
        instanceId = string.IsNullOrWhiteSpace(instanceId) ? "release" : instanceId.Trim();
        instance = null!;
        if (!IsValidInstanceId(instanceId))
            return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                $@"{RegistryBasePath}\{instanceId}",
                writable: false);
            if (key is null)
                return false;

            var appPath = key.GetValue("AppPath") as string ?? "";
            var productPipe = key.GetValue("ProductPipeName") as string ?? "";
            var debugPipe = key.GetValue("DebugPipeName") as string ?? "";
            var build = key.GetValue("Build") as string ?? "";
            var workspace = key.GetValue("WorkspaceRoot") as string ?? "";
            if (!Path.IsPathFullyQualified(appPath)
                || !File.Exists(appPath)
                || productPipe.Length == 0)
            {
                return false;
            }

            var isDebug = build.Equals("Debug", StringComparison.OrdinalIgnoreCase);
            if (isDebug
                && !string.Equals(
                    McpPipeNames.InstanceId(Path.GetDirectoryName(appPath)!),
                    instanceId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            instance = new McpRegisteredInstance(
                instanceId,
                Path.GetFullPath(appPath),
                productPipe,
                debugPipe,
                isDebug,
                workspace.Length == 0
                    ? Path.GetDirectoryName(appPath)!
                    : Path.GetFullPath(workspace));
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            return false;
        }
    }

    private static bool FilesEqual(string first, string second)
    {
        var a = new FileInfo(first);
        var b = new FileInfo(second);
        if (a.Length != b.Length)
            return false;

        using var left = File.OpenRead(first);
        using var right = File.OpenRead(second);
        var leftBuffer = new byte[64 * 1024];
        var rightBuffer = new byte[leftBuffer.Length];
        while (true)
        {
            var leftRead = left.Read(leftBuffer);
            var rightRead = right.Read(rightBuffer);
            if (leftRead != rightRead)
                return false;
            if (leftRead == 0)
                return true;
            if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
                return false;
        }
    }

    private static void ValidateInstanceId(string instanceId)
    {
        if (!IsValidInstanceId(instanceId))
            throw new ArgumentException($"Invalid MCP instance id '{instanceId}'.", nameof(instanceId));
    }

    private static bool IsValidInstanceId(string instanceId) =>
        instanceId.Equals("release", StringComparison.OrdinalIgnoreCase)
        || instanceId.Length == 12 && instanceId.All(char.IsAsciiHexDigit);
}
