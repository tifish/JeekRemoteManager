using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JeekRemoteManager.Models;
using Renci.SshNet;

namespace JeekRemoteManager.Services;

/// <summary>
/// Outcome of installing a local public key on a remote host's
/// <c>~/.ssh/authorized_keys</c> (the ssh-copy-id equivalent).
/// </summary>
public sealed record PublicKeyInstallResult(bool AlreadyPresent, string Output);

/// <summary>
/// Installs a local SSH public key into the remote account's
/// <c>~/.ssh/authorized_keys</c> over a fresh SSH connection, reusing the same
/// programmatic auth and known-hosts path as the terminal and script runner.
/// Idempotent: an already-present key is detected rather than duplicated.
/// </summary>
public static class PublicKeyInstaller
{
    // Default public key names tried under ~/.ssh, in preference order, when the
    // connection has no explicit private key with a matching ".pub" file.
    private static readonly string[] DefaultPublicKeyNames =
        { "id_ed25519.pub", "id_ecdsa.pub", "id_rsa.pub" };

    /// <summary>
    /// Picks the best local public key to install: the connection's own private
    /// key's <c>.pub</c> sibling if present, otherwise the first default key found
    /// under <c>~/.ssh</c>. Returns null when none can be located.
    /// </summary>
    public static string? FindLocalPublicKey(Connection connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.PrivateKeyPath))
        {
            var sibling = connection.PrivateKeyPath.Trim() + ".pub";
            if (File.Exists(sibling))
                return sibling;
        }

        try
        {
            var sshDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
            foreach (var name in DefaultPublicKeyNames)
            {
                var path = Path.Combine(sshDir, name);
                if (File.Exists(path))
                    return path;
            }
        }
        catch
        {
            // Best-effort; fall through to "not found".
        }

        return null;
    }

    /// <summary>Reads and validates the public key text from <paramref name="publicKeyPath"/>.</summary>
    /// <exception cref="InvalidOperationException">When the file is empty or doesn't look like a public key.</exception>
    public static string ReadPublicKey(string publicKeyPath)
    {
        var key = (File.Exists(publicKeyPath) ? File.ReadAllText(publicKeyPath) : "")
            .Replace("\r", "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var line = key.Length > 0 ? key[0] : "";
        if (string.IsNullOrWhiteSpace(line) || !line.Contains(' '))
            throw new InvalidOperationException($"'{Path.GetFileName(publicKeyPath)}' is not a valid public key file.");

        return line;
    }

    public static async Task<PublicKeyInstallResult> InstallAsync(
        Connection connection,
        string publicKeyText,
        Func<string, int, string, string, bool>? confirmHostKey = null,
        CancellationToken cancellationToken = default)
    {
        if (connection.Type != ConnectionType.Ssh)
            throw new InvalidOperationException("Public keys can only be installed on SSH connections.");

        var host = connection.Host.Trim();
        var port = connection.Port > 0 ? connection.Port : 22;
        var output = new StringBuilder();

        // Build (which may query ssh-agent / Pageant over IPC) and Connect both run
        // on a background thread; those calls can block and must not run on the UI
        // thread. Same auth + known_hosts path as the terminal and script runner.
        using var client = await Task.Run(() =>
        {
            var sshClient = new SshClient(SshConnectionFactory.Build(connection));
            SshHostKey.Attach(sshClient, host, port,
                onUnknown: (keyType, fingerprint) => confirmHostKey?.Invoke(host, port, keyType, fingerprint) ?? false,
                onRejected: message => output.Append(message).Append('\n'));
            sshClient.Connect();
            return sshClient;
        }, cancellationToken).ConfigureAwait(false);

        using var command = client.CreateCommand("sh -s");
        var async = command.BeginExecute();

        try
        {
            using (var input = command.CreateInputStream())
            {
                var payload = Encoding.UTF8.GetBytes(BuildPayload(publicKeyText));
                await input.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await input.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            try { command.EndExecute(async); } catch { /* superseded by the original */ }
            throw;
        }

        var result = command.EndExecute(async) ?? "";
        var errors = command.Error ?? "";
        if (errors.Length > 0)
            output.Append(errors);

        if ((command.ExitStatus ?? -1) != 0)
            throw new InvalidOperationException(
                output.Length > 0 ? output.ToString().Trim() : $"Remote command exited with code {command.ExitStatus}.");

        return new PublicKeyInstallResult(
            AlreadyPresent: result.Contains("__JRM_KEY_PRESENT__", StringComparison.Ordinal),
            Output: output.ToString());
    }

    private static string BuildPayload(string publicKeyText) =>
        "set -e\n" +
        "umask 077\n" +
        "mkdir -p \"$HOME/.ssh\"\n" +
        "touch \"$HOME/.ssh/authorized_keys\"\n" +
        "KEY=" + ShellQuote(publicKeyText) + "\n" +
        "if grep -qxF \"$KEY\" \"$HOME/.ssh/authorized_keys\" 2>/dev/null; then\n" +
        "  printf '__JRM_KEY_PRESENT__\\n'\n" +
        "else\n" +
        "  printf '%s\\n' \"$KEY\" >> \"$HOME/.ssh/authorized_keys\"\n" +
        "  printf '__JRM_KEY_ADDED__\\n'\n" +
        "fi\n" +
        "chmod 700 \"$HOME/.ssh\" 2>/dev/null || true\n" +
        "chmod 600 \"$HOME/.ssh/authorized_keys\" 2>/dev/null || true\n";

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'") + "'";
}
