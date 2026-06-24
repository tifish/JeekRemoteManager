using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JeekRemoteManager.Models;
using Renci.SshNet;

namespace JeekRemoteManager.Services;

public sealed record RemoteScriptExecutionResult(
    int ExitCode,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt);

public class RemoteScriptLauncher
{
    public async Task<RemoteScriptExecutionResult> RunAsync(
        Connection connection,
        RemoteScriptSuite suite,
        RemoteScriptFile scriptFile,
        ConnectionScriptBinding binding,
        Action<string> appendOutput,
        Func<string, int, string, string, bool>? confirmHostKey = null,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidateBinding(suite, binding);
        if (connection.Type != ConnectionType.Ssh)
            errors.Add("Script actions are only available for SSH connections.");
        if (!File.Exists(scriptFile.FullPath))
            errors.Add($"Script file does not exist: {scriptFile.Name}");
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

        var payload = BuildPayload(suite, scriptFile, binding);
        var host = connection.Host.Trim();
        var port = connection.Port > 0 ? connection.Port : 22;

        var startedAt = DateTimeOffset.Now;
        // Build (which may query ssh-agent / Pageant over IPC) and Connect both run on
        // a background thread; those calls can block and must not run on the UI thread.
        // Scripts share the same auth + known_hosts path as the terminal: prompt for a
        // first-seen host, and reject (not silently trust) when no prompt is available.
        using var client = await Task.Run(() =>
        {
            var sshClient = new SshClient(SshConnectionFactory.Build(connection));
            SshHostKey.Attach(sshClient, host, port,
                onUnknown: (keyType, fingerprint) => confirmHostKey?.Invoke(host, port, keyType, fingerprint) ?? false,
                onRejected: message => appendOutput(message + Environment.NewLine),
                onTrusted: fingerprint => appendOutput($"trusted new host key SHA256:{fingerprint}{Environment.NewLine}"));
            sshClient.Connect();
            return sshClient;
        }, cancellationToken).ConfigureAwait(false);

        using var command = client.CreateCommand("sh -s");
        var async = command.BeginExecute();

        // Drain stdout/stderr concurrently with writing stdin: a script that emits
        // output while it is still being read off stdin could otherwise fill the
        // channel window, block the remote, and deadlock the stdin write. Both
        // streams EOF when `sh -s` exits.
        var outputPump = PumpAsync(command.OutputStream, appendOutput, cancellationToken);
        var errorPump = PumpAsync(command.ExtendedOutputStream, appendOutput, cancellationToken);

        try
        {
            // CreateInputStream must be called after execution begins; disposing it
            // signals EOF so `sh -s` runs the piped script and exits.
            using (var input = command.CreateInputStream())
            {
                var payloadBytes = Encoding.UTF8.GetBytes(payload);
                await input.WriteAsync(payloadBytes, cancellationToken).ConfigureAwait(false);
                await input.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await Task.WhenAll(outputPump, errorPump).ConfigureAwait(false);
        }
        catch
        {
            // Observe the execution result so its exception isn't left unobserved,
            // but don't let it mask the original failure (cancellation, write error…).
            try { command.EndExecute(async); } catch { /* superseded by the original */ }
            throw;
        }

        command.EndExecute(async);
        var finishedAt = DateTimeOffset.Now;
        return new RemoteScriptExecutionResult(command.ExitStatus ?? -1, startedAt, finishedAt);
    }

    private static async Task PumpAsync(Stream stream, Action<string> appendOutput, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];

        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            var charCount = decoder.GetChars(buffer, 0, read, chars, 0);
            if (charCount > 0)
                appendOutput(new string(chars, 0, charCount));
        }
    }

    public static string BuildPayload(
        RemoteScriptSuite suite,
        RemoteScriptFile scriptFile,
        ConnectionScriptBinding binding)
    {
        var sb = new StringBuilder();
        sb.Append("set -u\n");

        foreach (var parameter in suite.Parameters)
        {
            var value = ResolveValue(parameter, binding, out _);
            sb.Append("export ");
            sb.Append(parameter.Name);
            sb.Append('=');
            sb.Append(ShellQuote(value));
            sb.Append('\n');
        }

        sb.Append('\n');
        var script = File.ReadAllText(scriptFile.FullPath)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');
        sb.Append(script);
        if (!script.EndsWith('\n'))
            sb.Append('\n');

        return sb.ToString();
    }

    public static List<string> ValidateBinding(RemoteScriptSuite suite, ConnectionScriptBinding binding)
    {
        var errors = suite.Errors.ToList();
        if (!Directory.Exists(suite.FullPath))
            errors.Add($"Script suite does not exist: {suite.Name}");

        if (!string.Equals(binding.Name, suite.RelativePath, StringComparison.OrdinalIgnoreCase))
            errors.Add($"Script binding does not match suite: {suite.Name}");

        foreach (var parameter in suite.Parameters)
        {
            var value = ResolveValue(parameter, binding, out var decryptFailed);
            if (decryptFailed)
            {
                errors.Add($"Secret parameter '{parameter.Name}' cannot be decrypted with the current master password.");
                continue;
            }

            switch (parameter.Type)
            {
                case RemoteScriptParameterType.Number:
                    if (string.IsNullOrWhiteSpace(value)
                        || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                        errors.Add($"Parameter '{parameter.Name}' must be a number.");
                    break;
                case RemoteScriptParameterType.Bool:
                    if (!TryNormalizeBool(value, out _))
                        errors.Add($"Parameter '{parameter.Name}' must be true or false.");
                    break;
                case RemoteScriptParameterType.Enum:
                    if (!parameter.EnumOptions.Contains(value, StringComparer.OrdinalIgnoreCase))
                        errors.Add($"Parameter '{parameter.Name}' must be one of: {string.Join(", ", parameter.EnumOptions)}.");
                    break;
                case RemoteScriptParameterType.Secret:
                    if (string.IsNullOrEmpty(value))
                        errors.Add($"Parameter '{parameter.Name}' is required.");
                    break;
            }
        }

        return errors;
    }

    public static ConnectionScriptBinding ProtectSecretValues(
        RemoteScriptSuite suite,
        ConnectionScriptBinding binding)
    {
        var result = CloneBinding(binding);
        foreach (var parameter in suite.Parameters.Where(p => p.Type == RemoteScriptParameterType.Secret))
        {
            var value = result.Params.FirstOrDefault(v =>
                string.Equals(v.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
            if (value is null)
                continue;

            value.Value = string.IsNullOrEmpty(value.Value) || MasterKeyService.IsPasswordBlob(value.Value)
                ? value.Value
                : PasswordProtector.Encrypt(value.Value);
        }

        return result;
    }

    public static ConnectionScriptBinding UnprotectSecretValues(
        RemoteScriptSuite suite,
        ConnectionScriptBinding binding)
    {
        var result = CloneBinding(binding);
        foreach (var parameter in suite.Parameters.Where(p => p.Type == RemoteScriptParameterType.Secret))
        {
            var value = result.Params.FirstOrDefault(v =>
                string.Equals(v.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
            if (value is null || string.IsNullOrEmpty(value.Value))
                continue;
            value.Value = PasswordProtector.TryDecrypt(value.Value, out var clear) ? clear : "";
        }

        return result;
    }

    public static ConnectionScriptBinding CloneBinding(ConnectionScriptBinding binding) => new()
    {
        Name = binding.Name,
        Params = binding.Params
            .Select(v => new ConnectionScriptParameterValue { Name = v.Name, Value = v.Value })
            .ToList(),
    };

    private static string ResolveValue(
        RemoteScriptParameter parameter,
        ConnectionScriptBinding binding,
        out bool decryptFailed)
    {
        decryptFailed = false;
        var parameterValue = binding.Params.FirstOrDefault(v =>
            string.Equals(v.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
        var value = parameterValue is null
            ? GetDefaultValue(parameter)
            : parameterValue.Value;

        if (parameter.Type == RemoteScriptParameterType.Secret && !string.IsNullOrEmpty(value)
            && MasterKeyService.IsPasswordBlob(value))
        {
            if (!PasswordProtector.TryDecrypt(value, out var clear))
            {
                decryptFailed = true;
                return "";
            }

            value = clear;
        }
        else if (parameter.Type == RemoteScriptParameterType.Bool
                 && TryNormalizeBool(value, out var normalized))
        {
            value = normalized;
        }

        return value;
    }

    private static string GetDefaultValue(RemoteScriptParameter parameter) =>
        parameter.Type == RemoteScriptParameterType.Bool && string.IsNullOrEmpty(parameter.DefaultValue)
            ? "false"
            : parameter.DefaultValue;

    private static bool TryNormalizeBool(string value, out string normalized)
    {
        normalized = "";
        if (bool.TryParse(value, out var b))
        {
            normalized = b ? "true" : "false";
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "1":
            case "yes":
            case "y":
                normalized = "true";
                return true;
            case "0":
            case "no":
            case "n":
                normalized = "false";
                return true;
            default:
                return false;
        }
    }

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'") + "'";
}
