using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using JeekRemoteManager.Models;
using JeekTools;

namespace JeekRemoteManager.Services;

public class RemoteScriptStore
{
    public const string ParameterFileName = "params.conf";
    public const string ScriptExtension = ".sh";

    private static readonly Regex ParameterNamePattern =
        new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    public RemoteScriptStore(string? rootPath = null, string? builtInRootPath = null)
    {
        RootPath = rootPath ?? SettingsService.ResolveScriptsRoot(StorageLocation.UserDirectory);
        BuiltInRootPath = builtInRootPath ?? SettingsService.ResolveBuiltInScriptsRoot();
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; private set; }

    public string BuiltInRootPath { get; private set; }

    public void SetRoot(string newRoot)
    {
        using var lease = SharedDataFile.Acquire(newRoot);
        RootPath = newRoot;
        Directory.CreateDirectory(RootPath);
    }

    public void SetBuiltInRoot(string newRoot)
    {
        BuiltInRootPath = newRoot;
    }

    public IReadOnlyList<RemoteScriptSuite> LoadAll()
    {
        var suites = new Dictionary<string, RemoteScriptSuite>(StringComparer.OrdinalIgnoreCase);

        foreach (var suite in LoadFromRoot(BuiltInRootPath, RemoteScriptSuiteSource.BuiltIn))
            suites[suite.RelativePath] = suite;

        foreach (var suite in LoadFromRoot(RootPath, RemoteScriptSuiteSource.User))
            suites[suite.RelativePath] = suite;

        return suites.Values
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<RemoteScriptSuite> LoadFromRoot(
        string rootPath,
        RemoteScriptSuiteSource source)
    {
        if (!Directory.Exists(rootPath))
            return Array.Empty<RemoteScriptSuite>();

        return Directory.GetDirectories(rootPath)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(path => LoadSuite(path, source))
            .ToList();
    }

    public RemoteScriptSuite LoadSuite(string suiteDirectory) =>
        LoadSuite(suiteDirectory, RemoteScriptSuiteSource.User);

    public static RemoteScriptSuite LoadSuite(
        string suiteDirectory,
        RemoteScriptSuiteSource source)
    {
        var fullPath = Path.GetFullPath(suiteDirectory);
        var suite = new RemoteScriptSuite
        {
            Name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar)),
            RelativePath = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar)),
            FullPath = fullPath,
            Source = source,
        };

        var parameterFile = Path.Combine(fullPath, ParameterFileName);
        if (File.Exists(parameterFile))
        {
            try
            {
                suite.Parameters = ParseParameterFile(File.ReadAllLines(parameterFile), suite.Errors);
            }
            catch (Exception ex)
            {
                suite.Errors.Add($"Could not read {ParameterFileName}: {ex.Message}");
            }
        }
        else
        {
            suite.Errors.Add($"{ParameterFileName} not found.");
        }

        try
        {
            suite.Scripts = Directory.GetFiles(fullPath, "*" + ScriptExtension, SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
                .Select(path => new RemoteScriptFile
                {
                    Name = Path.GetFileName(path),
                    DisplayName = BuildScriptDisplayName(Path.GetFileNameWithoutExtension(path)),
                    FullPath = path,
                })
                .ToList();
        }
        catch (Exception ex)
        {
            suite.Errors.Add($"Could not list scripts: {ex.Message}");
        }

        return suite;
    }

    public static string BuildScriptDisplayName(string fileName)
    {
        var name = fileName.Replace('-', ' ');
        if (name.Length > 0 && char.IsLower(name[0]))
            name = char.ToUpper(name[0]) + name.Substring(1);
        return name;
    }

    public static List<RemoteScriptParameter> ParseParameterFile(
        IEnumerable<string> lines,
        ICollection<string>? errors = null)
    {
        var parameters = new List<RemoteScriptParameter>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lineNo = 0;

        foreach (var rawLine in lines)
        {
            lineNo++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                continue;

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                errors?.Add($"Line {lineNo}: expected NAME=TYPE[=DEFAULT].");
                continue;
            }

            var name = line[..separator].Trim();
            var rawDefinition = line[(separator + 1)..];
            var defaultSeparator = rawDefinition.IndexOf('=');
            var typeText = defaultSeparator < 0
                ? rawDefinition.Trim()
                : rawDefinition[..defaultSeparator].Trim();
            var defaultValue = defaultSeparator < 0
                ? ""
                : rawDefinition[(defaultSeparator + 1)..].Trim();
            if (!IsValidParameterName(name))
            {
                errors?.Add($"Line {lineNo}: invalid parameter name '{name}'.");
                continue;
            }

            if (!names.Add(name))
            {
                errors?.Add($"Line {lineNo}: duplicate parameter '{name}'.");
                continue;
            }

            if (!TryParseParameterType(typeText, out var type, out var enumOptions, out var error))
            {
                errors?.Add($"Line {lineNo}: {error}");
                continue;
            }

            if (!TryNormalizeDefaultValue(type, enumOptions, defaultValue, out var normalizedDefault, out error))
            {
                errors?.Add($"Line {lineNo}: {error}");
                continue;
            }

            parameters.Add(new RemoteScriptParameter
            {
                Name = name,
                Type = type,
                DefaultValue = normalizedDefault,
                EnumOptions = enumOptions,
            });
        }

        return parameters;
    }

    public static bool IsValidParameterName(string name) =>
        !string.IsNullOrWhiteSpace(name) && ParameterNamePattern.IsMatch(name);

    public static bool TryParseParameterType(
        string raw,
        out RemoteScriptParameterType type,
        out List<string> enumOptions,
        out string error)
    {
        enumOptions = new List<string>();
        error = "";
        type = RemoteScriptParameterType.String;

        var value = raw.Trim();
        if (value.Equals("string", StringComparison.OrdinalIgnoreCase))
            return true;
        if (value.Equals("number", StringComparison.OrdinalIgnoreCase))
        {
            type = RemoteScriptParameterType.Number;
            return true;
        }
        if (value.Equals("bool", StringComparison.OrdinalIgnoreCase))
        {
            type = RemoteScriptParameterType.Bool;
            return true;
        }
        if (value.Equals("secret", StringComparison.OrdinalIgnoreCase))
        {
            type = RemoteScriptParameterType.Secret;
            return true;
        }

        const string enumPrefix = "enum:";
        if (value.StartsWith(enumPrefix, StringComparison.OrdinalIgnoreCase))
        {
            type = RemoteScriptParameterType.Enum;
            enumOptions = value[enumPrefix.Length..]
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (enumOptions.Count == 0)
            {
                error = "enum must define at least one option.";
                return false;
            }

            return true;
        }

        error = $"unknown type '{raw}'.";
        return false;
    }

    private static bool TryNormalizeDefaultValue(
        RemoteScriptParameterType type,
        IReadOnlyList<string> enumOptions,
        string defaultValue,
        out string normalizedDefault,
        out string error)
    {
        normalizedDefault = defaultValue;
        error = "";

        if (string.IsNullOrEmpty(defaultValue))
            return true;

        switch (type)
        {
            case RemoteScriptParameterType.Number:
                if (!double.TryParse(defaultValue, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out _))
                {
                    error = $"default value '{defaultValue}' must be a number.";
                    return false;
                }
                return true;
            case RemoteScriptParameterType.Bool:
                if (!TryNormalizeBool(defaultValue, out normalizedDefault))
                {
                    error = $"default value '{defaultValue}' must be true or false.";
                    return false;
                }
                return true;
            case RemoteScriptParameterType.Enum:
                var option = enumOptions.FirstOrDefault(o =>
                    string.Equals(o, defaultValue, StringComparison.OrdinalIgnoreCase));
                if (option is null)
                {
                    error = $"default value '{defaultValue}' must be one of: {string.Join(", ", enumOptions)}.";
                    return false;
                }

                normalizedDefault = option;
                return true;
            default:
                return true;
        }
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

    public void CopyTreeContents(string sourceRoot, string destRoot)
    {
        using var lease = SharedDataFile.AcquireMany(RootPath, sourceRoot, destRoot);
        if (!Directory.Exists(sourceRoot) || ConnectionStore.IsSameOrInside(sourceRoot, destRoot))
            return;

        Directory.CreateDirectory(destRoot);

        foreach (var dir in Directory.GetDirectories(sourceRoot))
        {
            var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
            var target = UniqueDirectoryPath(destRoot, name);
            CopyDirectory(dir, target);
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
            SharedDataFile.CopyAtomic(file, Path.Combine(destDir, Path.GetFileName(file)));

        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }

    private static string UniqueDirectoryPath(string parentPath, string baseName)
    {
        var candidate = Path.Combine(parentPath, baseName);
        var i = 2;
        while (Directory.Exists(candidate))
            candidate = Path.Combine(parentPath, $"{baseName} ({i++})");
        return candidate;
    }
}
