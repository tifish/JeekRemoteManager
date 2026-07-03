using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.Services;

/// <summary>
/// Launches RDP connections via the operating system's <c>mstsc.exe</c>.
/// SSH is not handled here: it always runs in the in-app terminal backed by
/// SSH.NET (see <see cref="SshConnectionFactory"/>).
/// </summary>
public class ConnectionLauncher
{
    /// <summary>Launches the given connection. Throws on failure to start the client.</summary>
    public void Launch(Connection connection)
    {
        switch (connection.Type)
        {
            case ConnectionType.Rdp:
                LaunchRdp(connection);
                break;
            case ConnectionType.Ssh:
                throw new NotSupportedException(
                    "SSH connections are handled by the in-app terminal, not the OS client.");
            default:
                throw new NotSupportedException($"Unknown connection type: {connection.Type}");
        }
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

}
