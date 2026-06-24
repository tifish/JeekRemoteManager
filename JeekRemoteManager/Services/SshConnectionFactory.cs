using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using JeekRemoteManager.Models;
using Renci.SshNet;
using SshNet.Agent;

namespace JeekRemoteManager.Services;

/// <summary>
/// Builds an SSH.NET <see cref="ConnectionInfo"/> from a <see cref="Connection"/>,
/// authenticating programmatically with the master-password-decrypted credentials
/// so the user never has to type a password. Shared by the interactive terminal and
/// (later) the non-interactive script runner so both use one auth path.
/// </summary>
public static class SshConnectionFactory
{
    // Default key file names tried under ~/.ssh, in preference order, when a
    // connection has neither a password nor an explicit key path — mirrors what
    // ssh.exe does out of the box.
    private static readonly string[] DefaultKeyNames = { "id_ed25519", "id_ecdsa", "id_rsa" };

    /// <summary>
    /// Produces a <see cref="ConnectionInfo"/>. Tries, in order: an explicit private
    /// key, then a stored password (+ keyboard-interactive); failing a password, it
    /// falls back to ssh-agent / Pageant identities and the default <c>~/.ssh</c> keys.
    /// </summary>
    /// <exception cref="InvalidOperationException">When no username is set, or no usable credential is available.</exception>
    public static ConnectionInfo Build(Connection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.Host))
            throw new InvalidOperationException("Host is empty.");
        if (string.IsNullOrWhiteSpace(connection.Username))
            throw new InvalidOperationException("Username is required for SSH.");

        var host = connection.Host.Trim();
        var port = connection.Port > 0 ? connection.Port : 22;
        var user = connection.Username.Trim();
        var password = PasswordProtector.Decrypt(connection.EncryptedPassword);
        var passphrase = PasswordProtector.Decrypt(connection.EncryptedPrivateKeyPassphrase);

        var methods = new List<AuthenticationMethod>();
        var hasExplicitKey = !string.IsNullOrWhiteSpace(connection.PrivateKeyPath);

        // 1. Explicit private key (with optional passphrase).
        if (hasExplicitKey && File.Exists(connection.PrivateKeyPath))
        {
            var keyFile = TryLoadKey(connection.PrivateKeyPath, passphrase);
            if (keyFile is not null)
                methods.Add(new PrivateKeyAuthenticationMethod(user, keyFile));
        }

        if (!string.IsNullOrEmpty(password))
        {
            // 2. Stored password -> password + keyboard-interactive (many sshd setups
            //    expose password auth only via PAM/keyboard-interactive).
            methods.Add(new PasswordAuthenticationMethod(user, password));

            var keyboard = new KeyboardInteractiveAuthenticationMethod(user);
            keyboard.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    if (prompt.Request.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0)
                        prompt.Response = password;
                }
            };
            methods.Add(keyboard);
        }
        else
        {
            // 3. No password: fall back to key-based auth like ssh.exe — ssh-agent /
            //    Pageant identities, then the default ~/.ssh keys. Gated on "no
            //    password" so a connection that does use a password isn't sprayed
            //    with extra key attempts (which can trip the server's MaxAuthTries).
            var agentKeys = TryGetAgentKeys();
            if (agentKeys.Count > 0)
                methods.Add(new PrivateKeyAuthenticationMethod(user, agentKeys.ToArray()));

            if (!hasExplicitKey)
            {
                foreach (var keyFile in LoadDefaultKeys(passphrase))
                    methods.Add(new PrivateKeyAuthenticationMethod(user, keyFile));
            }
        }

        if (methods.Count == 0)
            throw new InvalidOperationException(
                "No usable credential: set a password or private key, or load a key into ssh-agent / Pageant.");

        return new ConnectionInfo(host, port, user, methods.ToArray());
    }

    private static PrivateKeyFile? TryLoadKey(string path, string? passphrase)
    {
        try
        {
            return string.IsNullOrEmpty(passphrase)
                ? new PrivateKeyFile(path)
                : new PrivateKeyFile(path, passphrase);
        }
        catch
        {
            // Encrypted key without the right passphrase, or an unsupported format.
            return null;
        }
    }

    private static IEnumerable<PrivateKeyFile> LoadDefaultKeys(string? passphrase)
    {
        string sshDir;
        try
        {
            sshDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        }
        catch
        {
            yield break;
        }

        foreach (var name in DefaultKeyNames)
        {
            var path = Path.Combine(sshDir, name);
            if (!File.Exists(path))
                continue;

            var keyFile = TryLoadKey(path, passphrase);
            if (keyFile is not null)
                yield return keyFile;
        }
    }

    private static List<IPrivateKeySource> TryGetAgentKeys()
    {
        var keys = new List<IPrivateKeySource>();
        AddAgentKeys(keys, () => new SshAgent().RequestIdentities());   // OpenSSH agent
        AddAgentKeys(keys, () => new Pageant().RequestIdentities());    // PuTTY Pageant
        return keys;
    }

    private static void AddAgentKeys(List<IPrivateKeySource> keys, Func<IEnumerable<IPrivateKeySource>> fetch)
    {
        try
        {
            // Run the agent IPC on a worker and bound it: a missing or unresponsive
            // agent must not stall the connection. Materialize inside the task so the
            // actual IPC runs under the timeout, not later during AddRange.
            var task = Task.Run(() => new List<IPrivateKeySource>(fetch()));
            if (task.Wait(TimeSpan.FromSeconds(2)))
                keys.AddRange(task.Result);
            else
                _ = task.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);
        }
        catch
        {
            // Agent not running, not available on this platform, or timed out — ignore
            // and let the other methods carry the connection.
        }
    }
}
