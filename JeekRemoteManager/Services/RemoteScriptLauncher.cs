using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JeekRemoteManager.Models;

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
        var args = ConnectionLauncher.BuildSshArguments(connection).ToList();
        args.Add("sh");
        args.Add("-s");

        var psi = new ProcessStartInfo("ssh.exe")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var startedAt = DateTimeOffset.Now;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                appendOutput(e.Data + Environment.NewLine);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                appendOutput(e.Data + Environment.NewLine);
        };

        if (!process.Start())
            throw new InvalidOperationException("Could not start ssh.exe.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.StandardInput.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var finishedAt = DateTimeOffset.Now;
        return new RemoteScriptExecutionResult(process.ExitCode, startedAt, finishedAt);
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

        var values = binding.Params.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);

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
                    if (!values.ContainsKey(parameter.Name))
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
        var value = binding.Params.FirstOrDefault(v =>
            string.Equals(v.Name, parameter.Name, StringComparison.OrdinalIgnoreCase))?.Value ?? "";

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
