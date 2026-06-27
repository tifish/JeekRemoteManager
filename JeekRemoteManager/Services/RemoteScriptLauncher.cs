using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.Services;

public sealed record RemoteScriptExecutionResult(
    int ExitCode,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt);

public class RemoteScriptLauncher
{
    public const string TerminalScriptExitMarkerPrefix = "\u001b]777;JRM_SCRIPT_EXIT:";

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

    public static string BuildTerminalInvocation(string payload, string token)
    {
        if (string.IsNullOrWhiteSpace(token)
            || token.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
            throw new ArgumentException("Terminal script token must contain only letters, digits, or underscores.", nameof(token));

        var normalizedPayload = payload
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');
        var delimiter = "JRM_SCRIPT_" + token;
        var sb = new StringBuilder();

        sb.Append('\n');
        sb.Append("__jrm_stty=$(stty -g 2>/dev/null || true)\n");
        sb.Append("stty -echo 2>/dev/null || true\n");
        sb.Append("__jrm_payload=$(cat <<'");
        sb.Append(delimiter);
        sb.Append("'\n");
        sb.Append(normalizedPayload);
        if (!normalizedPayload.EndsWith('\n'))
            sb.Append('\n');
        sb.Append(delimiter);
        sb.Append("\n)\n");
        sb.Append("if [ -n \"$__jrm_stty\" ]; then stty \"$__jrm_stty\" 2>/dev/null || stty echo 2>/dev/null || true; else stty echo 2>/dev/null || true; fi\n");
        sb.Append("printf '%s\\n' \"$__jrm_payload\" | sh\n");
        sb.Append("__jrm_status=$?\n");
        sb.Append("unset __jrm_payload\n");
        sb.Append("printf '\\033]777;JRM_SCRIPT_EXIT:");
        sb.Append(token);
        sb.Append(":%s\\007\\n' \"$__jrm_status\"\n");
        sb.Append("unset __jrm_stty __jrm_status\n");

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
