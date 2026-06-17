using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.Services;

/// <summary>
/// Launches connections using the operating system's own clients:
/// <c>ssh.exe</c> for SSH and <c>mstsc.exe</c> for RDP.
/// </summary>
public class ConnectionLauncher
{
    /// <summary>Launches the given connection. Throws on failure to start the client.</summary>
    public void Launch(Connection connection)
    {
        switch (connection.Type)
        {
            case ConnectionType.Ssh:
                LaunchSsh(connection);
                break;
            case ConnectionType.Rdp:
                LaunchRdp(connection);
                break;
            default:
                throw new NotSupportedException($"Unknown connection type: {connection.Type}");
        }
    }

    private static void LaunchSsh(Connection connection)
    {
        var psi = new ProcessStartInfo("ssh.exe")
        {
            // A console application started with UseShellExecute gets its own window.
            UseShellExecute = true,
        };

        foreach (var arg in BuildSshArguments(connection))
            psi.ArgumentList.Add(arg);

        Process.Start(psi);
    }

    public static IReadOnlyList<string> BuildSshArguments(Connection connection)
    {
        var args = new List<string>();

        if (!string.IsNullOrWhiteSpace(connection.PrivateKeyPath))
        {
            args.Add("-i");
            args.Add(connection.PrivateKeyPath);
        }

        var port = connection.Port > 0 ? connection.Port : 22;
        if (port != 22)
        {
            args.Add("-p");
            args.Add(port.ToString());
        }

        foreach (var extra in SplitArguments(connection.ExtraSshArguments))
            args.Add(extra);

        var target = string.IsNullOrWhiteSpace(connection.Username)
            ? connection.Host
            : $"{connection.Username}@{connection.Host}";
        args.Add(target);

        return args;
    }

    private static void LaunchRdp(Connection connection)
    {
        var rdpPath = WriteRdpFile(connection);

        var psi = new ProcessStartInfo("mstsc.exe")
        {
            UseShellExecute = true,
        };
        psi.ArgumentList.Add(rdpPath);

        Process.Start(psi);

        // mstsc reads the file at startup; remove it shortly after so the
        // (DPAPI-encrypted) password does not linger on disk.
        ScheduleDelete(rdpPath, TimeSpan.FromSeconds(15));
    }

    private static string WriteRdpFile(Connection connection)
    {
        var port = connection.Port > 0 ? connection.Port : 3389;
        var address = $"{connection.Host}:{port}";

        var sb = new StringBuilder();
        sb.AppendLine($"full address:s:{address}");

        if (!string.IsNullOrWhiteSpace(connection.Username))
            sb.AppendLine($"username:s:{connection.Username}");

        var clearPassword = PasswordProtector.Decrypt(connection.EncryptedPassword);
        if (!string.IsNullOrEmpty(clearPassword))
        {
            sb.AppendLine($"password 51:b:{PasswordProtector.EncryptForRdpFile(clearPassword)}");
            sb.AppendLine("prompt for credentials:i:0");
        }

        if (connection.RdpFullScreen)
        {
            sb.AppendLine("screen mode id:i:2"); // full screen
        }
        else
        {
            sb.AppendLine("screen mode id:i:1"); // windowed
            sb.AppendLine($"desktopwidth:i:{Math.Max(640, connection.RdpWidth)}");
            sb.AppendLine($"desktopheight:i:{Math.Max(480, connection.RdpHeight)}");
        }

        // multimon only applies in full screen; mstsc ignores it otherwise.
        var useMultimon = connection.RdpFullScreen && connection.RdpUseAllMonitors;
        sb.AppendLine($"use multimon:i:{(useMultimon ? 1 : 0)}");
        sb.AppendLine("authentication level:i:2");
        sb.AppendLine($"redirectclipboard:i:{(connection.RdpRedirectClipboard ? 1 : 0)}");
        sb.AppendLine($"redirectdrives:i:{(connection.RdpRedirectDrives ? 1 : 0)}");
        // audiomode: 0 = play on this computer, 2 = do not play
        sb.AppendLine($"audiomode:i:{(connection.RdpRedirectAudioPlayback ? 0 : 2)}");
        sb.AppendLine($"audiocapturemode:i:{(connection.RdpRedirectMicrophone ? 1 : 0)}");

        // mstsc uses the .rdp file name as the window title, so name the file
        // after the connection and isolate it in a unique subdirectory to
        // avoid collisions across simultaneous launches.
        var dir = Path.Combine(Path.GetTempPath(), $"jrm_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{ConnectionStore.SanitizeName(connection.Name)}.rdp");

        // .rdp files are conventionally UTF-16 LE; mstsc accepts UTF-8 too, but
        // UTF-16 LE matches what Windows itself writes.
        File.WriteAllText(path, sb.ToString(), Encoding.Unicode);
        return path;
    }

    private static void ScheduleDelete(string path, TimeSpan delay)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
                if (File.Exists(path))
                    File.Delete(path);
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; ignore failures.
            }
        });
    }

    /// <summary>Splits a raw argument string on whitespace, honoring double quotes.</summary>
    private static System.Collections.Generic.IEnumerable<string> SplitArguments(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            yield break;

        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in raw)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            yield return current.ToString();
    }
}
