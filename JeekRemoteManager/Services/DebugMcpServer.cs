#if DEBUG
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekRemoteManager.Services;

/// <summary>
/// Debug-only MCP (Model Context Protocol) server over HTTP so an AI agent can
/// inspect and drive the running app: read/write properties by object path,
/// execute commands and methods on the UI thread, dump the visual tree, take
/// screenshots and tail logs. Bound to the loopback interface only and compiled
/// out of Release builds entirely. Registered for agents in the repo-root
/// .mcp.json (http://127.0.0.1:8737/mcp, port overridable via JRM_MCP_PORT).
/// </summary>
internal static class DebugMcpServer
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(DebugMcpServer));

    private const int DefaultPort = 8737;
    private const string SupportedProtocolVersion = "2025-06-18";
    private static readonly string[] KnownProtocolVersions = ["2024-11-05", "2025-03-26", "2025-06-18"];

    private static readonly JsonSerializerOptions ConvertOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
        // MCP clients often send every scalar as a string; accept "42" for int etc.
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };

    private static HttpListener? _listener;
    private static string _url = "";

    public static void Start()
    {
        if (_listener != null)
            return;

        var port = DefaultPort;
        if (int.TryParse(Environment.GetEnvironmentVariable("JRM_MCP_PORT"), out var envPort) && envPort > 0)
            port = envPort;

        // http.sys may require a URL ACL for the explicit 127.0.0.1 prefix;
        // "localhost" is exempt and still binds loopback only.
        foreach (var host in new[] { "127.0.0.1", "localhost" })
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://{host}:{port}/");
            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                Log.ZLogWarning(ex, $"Debug MCP server failed to bind http://{host}:{port}/");
                continue;
            }

            _listener = listener;
            _url = $"http://{host}:{port}/mcp";
            _ = Task.Run(() => ListenLoopAsync(listener));
            Log.ZLogInformation($"Debug MCP server listening on {_url}");
            return;
        }

        Log.ZLogError($"Debug MCP server could not start on port {port}");
    }

    public static void Stop()
    {
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch
        {
            // Shutting down; nothing useful to do.
        }

        _listener = null;
    }

    private static async Task ListenLoopAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                if (!listener.IsListening)
                    break;
                continue;
            }

            _ = Task.Run(() => HandleContextAsync(context));
        }
    }

    private static async Task HandleContextAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var request = context.Request;

            // DNS-rebinding protection: browsers send Origin; only loopback is legit.
            var origin = request.Headers["Origin"];
            if (origin != null && !IsLoopbackOrigin(origin))
            {
                response.StatusCode = 403;
                return;
            }

            if (request.Url?.AbsolutePath is not ("/mcp" or "/"))
            {
                response.StatusCode = 404;
                return;
            }

            if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                // No SSE stream support; MCP clients treat 405 on GET as "POST only".
                response.StatusCode = 405;
                response.AddHeader("Allow", "POST");
                return;
            }

            string body;
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                body = await reader.ReadToEndAsync();

            JsonNode? responseNode;
            try
            {
                var message = JsonNode.Parse(body);
                responseNode = message switch
                {
                    JsonObject single => await HandleMessageAsync(single),
                    JsonArray batch => await HandleBatchAsync(batch),
                    _ => RpcError(null, -32600, "Invalid request"),
                };
            }
            catch (JsonException ex)
            {
                responseNode = RpcError(null, -32700, $"Parse error: {ex.Message}");
            }

            if (responseNode == null)
            {
                // Notifications get 202 Accepted with no body.
                response.StatusCode = 202;
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(responseNode.ToJsonString());
            response.StatusCode = 200;
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            Log.ZLogError(ex, $"Debug MCP request failed");
            try
            {
                response.StatusCode = 500;
            }
            catch
            {
                // Response already started; nothing to do.
            }
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch
            {
                // Client may have disconnected.
            }
        }
    }

    private static bool IsLoopbackOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return false;
        return uri.IsLoopback;
    }

    private static async Task<JsonNode?> HandleBatchAsync(JsonArray batch)
    {
        var results = new JsonArray();
        foreach (var item in batch)
        {
            if (item is not JsonObject message)
                continue;
            if (await HandleMessageAsync(message) is { } result)
                results.Add(result);
        }

        return results.Count > 0 ? results : null;
    }

    private static async Task<JsonNode?> HandleMessageAsync(JsonObject message)
    {
        var id = message["id"]?.DeepClone();
        var isRequest = id != null;
        var method = message["method"]?.GetValue<string>();
        if (method == null)
            return null; // A response or malformed message; nothing to answer.

        try
        {
            switch (method)
            {
                case "initialize":
                    return RpcResult(id, HandleInitialize(message["params"] as JsonObject));
                case "ping":
                    return RpcResult(id, new JsonObject());
                case "tools/list":
                    return RpcResult(id, new JsonObject { ["tools"] = BuildToolList() });
                case "tools/call":
                    return RpcResult(id, await HandleToolCallAsync(message["params"] as JsonObject));
                default:
                    if (method.StartsWith("notifications/", StringComparison.Ordinal))
                        return null;
                    return isRequest ? RpcError(id, -32601, $"Method not found: {method}") : null;
            }
        }
        catch (Exception ex)
        {
            Log.ZLogError(ex, $"Debug MCP method {method} failed");
            return isRequest ? RpcError(id, -32603, ex.Message) : null;
        }
    }

    private static JsonObject HandleInitialize(JsonObject? parameters)
    {
        var requested = parameters?["protocolVersion"]?.GetValue<string>();
        var version = KnownProtocolVersions.Contains(requested) ? requested! : SupportedProtocolVersion;
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "jeek-remote-manager-debug",
                ["title"] = "JeekRemoteManager Debug Server",
                ["version"] = $"{AutoUpdateService.GetLocalCommitCount()}",
            },
        };
    }

    private static JsonObject RpcResult(JsonNode? id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject RpcError(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    #region Tool list

    private const string PathHelp =
        "Paths start from a root: App (the Application), Desktop (the desktop lifetime), " +
        "MainWindow, or MainVm (MainWindow.DataContext). Segments: '.Member' reads a property or field " +
        "(non-public included), '[0]' indexes a list, '[\"key\"]' indexes a dictionary, and " +
        "'#Name' finds a named control in the visual tree below the current object. " +
        "Examples: MainVm.Nodes[0].Name, MainWindow.#ConnectionTree.SelectedItem";

    private static JsonArray BuildToolList() => new(
        Tool("describe",
            "Overview of the running app: windows, roots for object paths, path syntax, log file. Start here.",
            new JsonObject()),
        Tool("get_value",
            "Read a value from the app's object graph. " + PathHelp,
            new JsonObject
            {
                ["path"] = Prop("string", "Object path to read."),
                ["depth"] = Prop("integer", "Levels of nested objects to expand, 0-5 (default 1)."),
            },
            ["path"]),
        Tool("set_value",
            "Write a property, field, or list element in the app's object graph (runs on the UI thread). " + PathHelp,
            new JsonObject
            {
                ["path"] = Prop("string", "Object path to write; must end with a property, field, or list index."),
                ["value"] = new JsonObject { ["description"] = "New value as JSON; deserialized to the member's type (enums accept their string names)." },
            },
            ["path", "value"]),
        Tool("invoke",
            "Execute an ICommand property or call a method on the UI thread; returned Tasks are awaited. " + PathHelp,
            new JsonObject
            {
                ["path"] = Prop("string", "Object path ending with the command property or method name, e.g. MainVm.OpenSettingsCommand."),
                ["args"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Arguments as JSON values. For a command this is the single command parameter; for a method the argument list.",
                },
                ["depth"] = Prop("integer", "Levels of the return value to expand, 0-5 (default 1)."),
            },
            ["path"]),
        Tool("list_members",
            "List properties (with current values), fields, and methods of the object at a path. " + PathHelp,
            new JsonObject { ["path"] = Prop("string", "Object path to inspect.") },
            ["path"]),
        Tool("visual_tree",
            "Dump the visual tree (control types, #names, classes, bounds, text, data contexts) below a visual.",
            new JsonObject
            {
                ["path"] = Prop("string", "Path to a Visual to start from (default MainWindow)."),
                ["max_depth"] = Prop("integer", "Maximum tree depth (default 12)."),
            }),
        Tool("screenshot",
            "Render the main window to a PNG image and return it.",
            new JsonObject()),
        Tool("read_logs",
            "Read the tail of the app's current log file.",
            new JsonObject
            {
                ["lines"] = Prop("integer", "Number of lines to return, 1-2000 (default 200)."),
                ["filter"] = Prop("string", "Only return lines containing this text (case-insensitive)."),
            }));

    private static JsonObject Tool(string name, string description, JsonObject properties, string[]? required = null)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required is { Length: > 0 })
            schema["required"] = new JsonArray(required.Select(JsonNode (r) => r).ToArray());

        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schema,
        };
    }

    private static JsonObject Prop(string type, string description) =>
        new() { ["type"] = type, ["description"] = description };

    #endregion

    #region Tool dispatch

    private static async Task<JsonObject> HandleToolCallAsync(JsonObject? parameters)
    {
        var name = parameters?["name"]?.GetValue<string>()
                   ?? throw new InvalidOperationException("tools/call requires params.name");
        var args = parameters["arguments"] as JsonObject ?? [];

        try
        {
            return name switch
            {
                "describe" => await DescribeAsync(),
                "get_value" => await GetValueAsync(args),
                "set_value" => await SetValueAsync(args),
                "invoke" => await InvokeAsync(args),
                "list_members" => await ListMembersAsync(args),
                "visual_tree" => await VisualTreeAsync(args),
                "screenshot" => await ScreenshotAsync(),
                "read_logs" => ReadLogs(args),
                _ => throw new InvalidOperationException($"Unknown tool: {name}"),
            };
        }
        catch (Exception ex)
        {
            var error = ex is TimeoutException
                ? "Timed out waiting for the UI thread; the app may be blocked or showing a nested dialog."
                : ex.ToString();
            return ToolText(Truncate(error, 4000), isError: true);
        }
    }

    private static JsonObject ToolText(string text, bool isError = false)
    {
        var result = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        };
        if (isError)
            result["isError"] = true;
        return result;
    }

    private static Task<T> OnUiAsync<T>(Func<T> func) =>
        Dispatcher.UIThread.InvokeAsync(func).GetTask().WaitAsync(TimeSpan.FromSeconds(15));

    #endregion

    #region Tools

    private static async Task<JsonObject> DescribeAsync()
    {
        var text = await OnUiAsync(() =>
        {
            var sb = new StringBuilder();
            sb.AppendLine($"JeekRemoteManager debug MCP server at {_url} (build {AutoUpdateService.GetLocalCommitCount()}).");
            sb.AppendLine($"Process uptime: {DateTime.Now - Process.GetCurrentProcess().StartTime:hh\\:mm\\:ss}.");
            sb.AppendLine($"Log file: {LogManager.CurrentRollingLogFile}");
            sb.AppendLine();
            sb.AppendLine("Roots for object paths:");
            sb.AppendLine("- App: the Avalonia Application instance");
            sb.AppendLine("- Desktop: the IClassicDesktopStyleApplicationLifetime (Windows list, Shutdown, ...)");
            sb.AppendLine("- MainWindow: the main window (null until the master password is unlocked)");
            sb.AppendLine("- MainVm: MainWindow.DataContext (MainWindowViewModel)");
            sb.AppendLine();
            sb.AppendLine(PathHelp);
            sb.AppendLine();

            if (Desktop is not { } desktop)
            {
                sb.AppendLine("No desktop lifetime yet.");
            }
            else
            {
                sb.AppendLine($"Windows ({desktop.Windows.Count}):");
                foreach (var window in desktop.Windows)
                {
                    sb.AppendLine(
                        $"- {window.GetType().Name} \"{window.Title}\" Visible={window.IsVisible} " +
                        $"State={window.WindowState} ClientSize={window.ClientSize} " +
                        $"DataContext={window.DataContext?.GetType().Name ?? "null"}");
                }
            }

            return sb.ToString();
        });

        return ToolText(text);
    }

    private static async Task<JsonObject> GetValueAsync(JsonObject args)
    {
        var path = RequiredString(args, "path");
        var depth = Math.Clamp(args["depth"]?.GetValue<int>() ?? 1, 0, 5);

        var node = await OnUiAsync(() => FormatValue(ResolvePath(path), depth));
        return ToolText(node?.ToJsonString(PrettyOptions) ?? "null");
    }

    private static async Task<JsonObject> SetValueAsync(JsonObject args)
    {
        var path = RequiredString(args, "path");
        var valueNode = args["value"];

        await OnUiAsync<object?>(() =>
        {
            var segments = ParsePath(path);
            if (segments.Count < 2)
                throw new InvalidOperationException("set_value needs a path with at least one member after the root.");

            var parent = ResolveSegments(segments, segments.Count - 1)
                         ?? throw new InvalidOperationException("The object owning the target member is null.");

            switch (segments[^1])
            {
                case MemberSegment member:
                {
                    var type = parent.GetType();
                    var property = FindProperty(type, member.Name);
                    if (property != null)
                    {
                        if (!property.CanWrite)
                            throw new InvalidOperationException($"Property '{member.Name}' on {type.Name} is read-only.");
                        property.SetValue(parent, ConvertJson(valueNode, property.PropertyType));
                        return null;
                    }

                    var field = FindField(type, member.Name)
                                ?? throw new InvalidOperationException(
                                    $"No property or field '{member.Name}' on {type.Name}. Use list_members to inspect.");
                    field.SetValue(parent, ConvertJson(valueNode, field.FieldType));
                    return null;
                }
                case IndexSegment index when parent is IList list:
                    list[index.Index] = ConvertJson(valueNode, ListElementType(parent.GetType()));
                    return null;
                default:
                    throw new InvalidOperationException("set_value requires the path to end with a property, field, or list index.");
            }
        });

        return ToolText($"Set {path}.");
    }

    private static async Task<JsonObject> InvokeAsync(JsonObject args)
    {
        var path = RequiredString(args, "path");
        var callArgs = args["args"] as JsonArray ?? [];
        var depth = Math.Clamp(args["depth"]?.GetValue<int>() ?? 1, 0, 5);

        var result = await OnUiAsync<object?>(() =>
        {
            var segments = ParsePath(path);
            if (segments.Count < 2 || segments[^1] is not MemberSegment member)
                throw new InvalidOperationException("invoke requires a path ending with a command property or method name.");

            var target = ResolveSegments(segments, segments.Count - 1)
                         ?? throw new InvalidOperationException("The object owning the member is null.");
            var type = target.GetType();

            var property = FindProperty(type, member.Name);
            if (property != null && typeof(ICommand).IsAssignableFrom(property.PropertyType))
            {
                var command = (ICommand?)property.GetValue(target)
                              ?? throw new InvalidOperationException($"Command '{member.Name}' is null.");
                var parameter = callArgs.Count > 0 ? ConvertJson(callArgs[0], typeof(object)) : null;
                if (!command.CanExecute(parameter))
                    return "CanExecute returned false; command not executed.";
                command.Execute(parameter);
                return "Command executed.";
            }

            var candidates = new List<MethodInfo>();
            for (var t = type; t != null; t = t.BaseType)
            {
                candidates.AddRange(t
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name == member.Name && m.GetParameters().Length == callArgs.Count));
            }

            if (candidates.Count == 0)
                throw new InvalidOperationException(
                    $"No command or method '{member.Name}' taking {callArgs.Count} argument(s) on {type.Name}. Use list_members to inspect.");

            Exception? conversionError = null;
            foreach (var method in candidates)
            {
                var parameters = method.GetParameters();
                var converted = new object?[parameters.Length];
                try
                {
                    for (var i = 0; i < parameters.Length; i++)
                        converted[i] = ConvertJson(callArgs[i], parameters[i].ParameterType);
                }
                catch (Exception ex)
                {
                    conversionError = ex;
                    continue;
                }

                return method.Invoke(target, converted);
            }

            throw new InvalidOperationException($"Could not convert arguments for '{member.Name}'.", conversionError);
        });

        if (result is Task task)
        {
            await task.WaitAsync(TimeSpan.FromSeconds(60));
            var taskType = task.GetType();
            result = taskType.IsGenericType && taskType.GetGenericArguments()[0].Name != "VoidTaskResult"
                ? taskType.GetProperty("Result")?.GetValue(task)
                : "Task completed.";
        }

        var node = await OnUiAsync(() => FormatValue(result, depth));
        return ToolText(node?.ToJsonString(PrettyOptions) ?? "null");
    }

    private static async Task<JsonObject> ListMembersAsync(JsonObject args)
    {
        var path = RequiredString(args, "path");

        var text = await OnUiAsync(() =>
        {
            var target = ResolvePath(path) ?? throw new InvalidOperationException($"'{path}' is null.");
            var type = target.GetType();
            var sb = new StringBuilder();
            sb.AppendLine(type.FullName);

            sb.AppendLine();
            sb.AppendLine("Properties:");
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).OrderBy(p => p.Name))
            {
                if (property.GetIndexParameters().Length > 0)
                    continue;

                string value;
                try
                {
                    value = Summary(property.GetValue(target));
                }
                catch (Exception ex)
                {
                    value = $"<threw {ex.InnerException?.GetType().Name ?? ex.GetType().Name}>";
                }

                var access = property.CanWrite ? "get/set" : "get";
                sb.AppendLine($"- {property.Name}: {TypeName(property.PropertyType)} ({access}) = {value}");
            }

            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public)
                .OrderBy(f => f.Name)
                .ToList();
            if (fields.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Fields:");
                foreach (var field in fields)
                    sb.AppendLine($"- {field.Name}: {TypeName(field.FieldType)} = {Summary(field.GetValue(target))}");
            }

            sb.AppendLine();
            sb.AppendLine("Methods:");
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object))
                .OrderBy(m => m.Name)
                .Take(300);
            foreach (var method in methods)
            {
                var parameters = string.Join(", ", method.GetParameters().Select(p => $"{TypeName(p.ParameterType)} {p.Name}"));
                sb.AppendLine($"- {method.Name}({parameters}): {TypeName(method.ReturnType)}");
            }

            return sb.ToString();
        });

        return ToolText(text);
    }

    private const int MaxVisualNodes = 2000;

    private static async Task<JsonObject> VisualTreeAsync(JsonObject args)
    {
        var path = args["path"]?.GetValue<string>() ?? "MainWindow";
        var maxDepth = Math.Max(1, args["max_depth"]?.GetValue<int>() ?? 12);

        var text = await OnUiAsync(() =>
        {
            if (ResolvePath(path) is not Visual root)
                throw new InvalidOperationException($"'{path}' is not a Visual.");

            var sb = new StringBuilder();
            var count = 0;
            AppendVisual(sb, root, 0, maxDepth, null, ref count);
            if (count >= MaxVisualNodes)
                sb.AppendLine($"… truncated at {MaxVisualNodes} nodes.");
            return sb.ToString();
        });

        return ToolText(text);
    }

    private static void AppendVisual(
        StringBuilder sb, Visual visual, int depth, int maxDepth, object? parentDataContext, ref int count)
    {
        if (count >= MaxVisualNodes)
            return;
        count++;

        sb.Append(' ', depth * 2).Append(visual.GetType().Name);

        var dataContext = parentDataContext;
        if (visual is StyledElement styled)
        {
            if (!string.IsNullOrEmpty(styled.Name))
                sb.Append(" #").Append(styled.Name);
            var classes = string.Join(' ', styled.Classes);
            if (classes.Length > 0)
                sb.Append(" (").Append(classes).Append(')');
            dataContext = styled.DataContext;
            if (dataContext != null && !ReferenceEquals(dataContext, parentDataContext))
                sb.Append(" DataContext=").Append(dataContext.GetType().Name);
        }

        var bounds = visual.Bounds;
        sb.Append($" [{bounds.X:0},{bounds.Y:0} {bounds.Width:0}x{bounds.Height:0}]");
        if (!visual.IsVisible)
            sb.Append(" HIDDEN");

        switch (visual)
        {
            case TextBlock { Text.Length: > 0 } textBlock:
                sb.Append($" Text=\"{Truncate(textBlock.Text, 80)}\"");
                break;
            case TextBox { Text.Length: > 0 } textBox:
                sb.Append($" Text=\"{Truncate(textBox.Text, 80)}\"");
                break;
        }

        sb.AppendLine();

        if (depth >= maxDepth)
        {
            if (visual.GetVisualChildren().Any())
                sb.Append(' ', (depth + 1) * 2).AppendLine("…");
            return;
        }

        foreach (var child in visual.GetVisualChildren())
            AppendVisual(sb, child, depth + 1, maxDepth, dataContext, ref count);
    }

    private static async Task<JsonObject> ScreenshotAsync()
    {
        var (bytes, pixelSize) = await OnUiAsync(() =>
        {
            var window = Desktop?.MainWindow
                         ?? throw new InvalidOperationException("MainWindow is not created yet.");
            var scaling = window.RenderScaling;
            var size = new PixelSize(
                Math.Max(1, (int)Math.Ceiling(window.ClientSize.Width * scaling)),
                Math.Max(1, (int)Math.Ceiling(window.ClientSize.Height * scaling)));

            using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
            bitmap.Render(window);
            using var stream = new MemoryStream();
            bitmap.Save(stream);
            return (stream.ToArray(), size);
        });

        return new JsonObject
        {
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = $"Main window screenshot, {pixelSize.Width}x{pixelSize.Height}px." },
                new JsonObject
                {
                    ["type"] = "image",
                    ["data"] = Convert.ToBase64String(bytes),
                    ["mimeType"] = "image/png",
                }),
        };
    }

    private static JsonObject ReadLogs(JsonObject args)
    {
        var lineCount = Math.Clamp(args["lines"]?.GetValue<int>() ?? 200, 1, 2000);
        var filter = args["filter"]?.GetValue<string>();

        var path = LogManager.CurrentRollingLogFile;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            path = LogManager.CurrentLogFile;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            throw new InvalidOperationException("No log file found.");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        const int tailBytes = 1024 * 1024;
        var truncated = stream.Length > tailBytes;
        if (truncated)
            stream.Seek(-tailBytes, SeekOrigin.End);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        if (truncated && lines.Count > 0)
            lines.RemoveAt(0); // Likely a partial line after seeking.

        IEnumerable<string> selected = lines;
        if (!string.IsNullOrEmpty(filter))
            selected = selected.Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase));

        var result = selected.TakeLast(lineCount).ToList();
        return ToolText(result.Count == 0 ? "(no matching log lines)" : string.Join('\n', result));
    }

    #endregion

    #region Object paths

    private abstract record Segment;

    private sealed record MemberSegment(string Name) : Segment;

    private sealed record FindControlSegment(string Name) : Segment;

    private sealed record IndexSegment(int Index) : Segment;

    private sealed record KeySegment(string Key) : Segment;

    private static IClassicDesktopStyleApplicationLifetime? Desktop =>
        Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

    private static object ResolveRoot(string name) => name switch
    {
        "App" => Application.Current
                 ?? throw new InvalidOperationException("Application.Current is null."),
        "Desktop" => Desktop
                     ?? throw new InvalidOperationException("No desktop lifetime."),
        "MainWindow" => Desktop?.MainWindow
                        ?? throw new InvalidOperationException("MainWindow is not created yet (master password not unlocked?)."),
        "MainVm" => Desktop?.MainWindow?.DataContext
                    ?? throw new InvalidOperationException("MainWindow.DataContext is not set yet."),
        _ => throw new InvalidOperationException($"Unknown root '{name}'. Available roots: App, Desktop, MainWindow, MainVm."),
    };

    private static object? ResolvePath(string path)
    {
        var segments = ParsePath(path);
        return ResolveSegments(segments, segments.Count);
    }

    private static object? ResolveSegments(List<Segment> segments, int count)
    {
        object? current = null;
        for (var i = 0; i < count; i++)
        {
            var segment = segments[i];
            if (i == 0)
            {
                current = ResolveRoot(((MemberSegment)segment).Name);
                continue;
            }

            if (current == null)
                throw new InvalidOperationException($"Path is null before '{DescribeSegment(segment)}'.");
            current = Step(current, segment);
        }

        return current;
    }

    private static object? Step(object target, Segment segment)
    {
        switch (segment)
        {
            case MemberSegment member:
            {
                var type = target.GetType();
                var property = FindProperty(type, member.Name);
                if (property != null)
                    return property.GetValue(target);
                var field = FindField(type, member.Name);
                if (field != null)
                    return field.GetValue(target);
                throw new InvalidOperationException(
                    $"No property or field '{member.Name}' on {type.Name}. Use list_members to inspect.");
            }
            case IndexSegment index:
                return target switch
                {
                    IList list when index.Index >= 0 && index.Index < list.Count => list[index.Index],
                    IList list => throw new InvalidOperationException($"Index {index.Index} out of range (Count={list.Count})."),
                    IEnumerable enumerable => enumerable.Cast<object?>().ElementAt(index.Index),
                    _ => throw new InvalidOperationException($"{target.GetType().Name} is not indexable."),
                };
            case KeySegment key:
                return target switch
                {
                    IDictionary dictionary when dictionary.Contains(key.Key) => dictionary[key.Key],
                    IDictionary => throw new InvalidOperationException($"Key '{key.Key}' not found."),
                    _ => throw new InvalidOperationException($"{target.GetType().Name} is not a dictionary."),
                };
            case FindControlSegment find:
            {
                if (target is not Visual visual)
                    throw new InvalidOperationException(
                        $"'#{find.Name}' requires a Visual; {target.GetType().Name} is not one.");
                return FindDescendantByName(visual, find.Name)
                       ?? throw new InvalidOperationException(
                           $"No visual named '{find.Name}' under {target.GetType().Name}. Use visual_tree to inspect.");
            }
            default:
                throw new InvalidOperationException("Unknown path segment.");
        }
    }

    private static Visual? FindDescendantByName(Visual root, string name)
    {
        var queue = new Queue<Visual>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var visual = queue.Dequeue();
            if (visual is StyledElement styled && styled.Name == name)
                return visual;
            foreach (var child in visual.GetVisualChildren())
                queue.Enqueue(child);
        }

        return null;
    }

    private static List<Segment> ParsePath(string path)
    {
        var segments = new List<Segment>();
        var i = 0;
        while (i < path.Length)
        {
            var c = path[i];
            if (c == '.')
            {
                i++;
            }
            else if (c == '#')
            {
                i++;
                var start = i;
                while (i < path.Length && (char.IsLetterOrDigit(path[i]) || path[i] == '_'))
                    i++;
                if (i == start)
                    throw new FormatException($"Expected a control name after '#' at position {start} in '{path}'.");
                segments.Add(new FindControlSegment(path[start..i]));
            }
            else if (c == '[')
            {
                i++;
                if (i < path.Length && path[i] is '"' or '\'')
                {
                    var quote = path[i++];
                    var start = i;
                    while (i < path.Length && path[i] != quote)
                        i++;
                    if (i >= path.Length)
                        throw new FormatException($"Unterminated string indexer in '{path}'.");
                    segments.Add(new KeySegment(path[start..i]));
                    i++;
                }
                else
                {
                    var start = i;
                    while (i < path.Length && path[i] != ']')
                        i++;
                    if (!int.TryParse(path[start..i].Trim(), out var index))
                        throw new FormatException($"Invalid index '{path[start..i]}' in '{path}'.");
                    segments.Add(new IndexSegment(index));
                }

                if (i >= path.Length || path[i] != ']')
                    throw new FormatException($"Expected ']' in '{path}'.");
                i++;
            }
            else if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < path.Length && (char.IsLetterOrDigit(path[i]) || path[i] == '_'))
                    i++;
                segments.Add(new MemberSegment(path[start..i]));
            }
            else
            {
                throw new FormatException($"Unexpected character '{c}' at position {i} in '{path}'.");
            }
        }

        if (segments.Count == 0 || segments[0] is not MemberSegment)
            throw new FormatException("Path must start with a root name (App, Desktop, MainWindow, MainVm).");

        return segments;
    }

    private static string DescribeSegment(Segment segment) => segment switch
    {
        MemberSegment member => "." + member.Name,
        FindControlSegment find => "#" + find.Name,
        IndexSegment index => $"[{index.Index}]",
        KeySegment key => $"[\"{key.Key}\"]",
        _ => "?",
    };

    private static PropertyInfo? FindProperty(Type type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var property = t.GetProperty(
                name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property != null)
                return property;
        }

        return null;
    }

    private static FieldInfo? FindField(Type type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var field = t.GetField(
                name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null)
                return field;
        }

        return null;
    }

    private static Type ListElementType(Type type)
    {
        var listInterface = type.GetInterfaces()
            .Prepend(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>));
        return listInterface?.GetGenericArguments()[0] ?? typeof(object);
    }

    private static object? ConvertJson(JsonNode? node, Type targetType)
    {
        if (node == null)
        {
            if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                throw new InvalidOperationException($"Cannot assign null to {TypeName(targetType)}.");
            return null;
        }

        if (targetType == typeof(object))
        {
            return node switch
            {
                JsonValue value when value.TryGetValue<bool>(out var b) => b,
                JsonValue value when value.TryGetValue<long>(out var l) => l,
                JsonValue value when value.TryGetValue<double>(out var d) => d,
                JsonValue value when value.TryGetValue<string>(out var s) => s,
                _ => node.Deserialize<object>(ConvertOptions),
            };
        }

        // Same string-leniency for bools ("true"), which NumberHandling doesn't cover.
        var boolTarget = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (boolTarget == typeof(bool) && node is JsonValue v && v.TryGetValue<string>(out var text)
            && bool.TryParse(text, out var parsedBool))
            return parsedBool;

        return node.Deserialize(targetType, ConvertOptions);
    }

    #endregion

    #region Value formatting

    private static JsonNode? FormatValue(object? value, int depth)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                return s;
            case bool b:
                return b;
            case sbyte or byte or short or ushort or int or uint or long:
                return JsonValue.Create(Convert.ToInt64(value));
            case ulong ul:
                return JsonValue.Create(ul);
            case float f:
                return float.IsFinite(f) ? JsonValue.Create(f) : f.ToString();
            case double d:
                return double.IsFinite(d) ? JsonValue.Create(d) : d.ToString();
            case decimal m:
                return JsonValue.Create(m);
            case char c:
                return c.ToString();
        }

        var type = value.GetType();
        if (type.IsEnum || value is DateTime or DateTimeOffset or TimeSpan or Guid or Uri or Type or Delegate)
            return value.ToString();
        if (type.IsValueType)
            return value.ToString(); // Rect, Size, Thickness, Color, ... read well as strings.

        if (depth <= 0)
            return Summary(value);

        switch (value)
        {
            case IDictionary dictionary:
            {
                var result = new JsonObject();
                var i = 0;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (i++ >= 50)
                    {
                        result["…"] = $"truncated; {dictionary.Count} entries total";
                        break;
                    }

                    result[entry.Key?.ToString() ?? "null"] = FormatValue(entry.Value, depth - 1);
                }

                return result;
            }
            case IEnumerable enumerable:
            {
                var result = new JsonArray();
                var i = 0;
                foreach (var item in enumerable)
                {
                    if (i++ >= 50)
                    {
                        result.Add("… truncated at 50 items");
                        break;
                    }

                    result.Add(FormatValue(item, depth - 1));
                }

                return result;
            }
        }

        var obj = new JsonObject { ["$type"] = TypeName(type) };
        var count = 0;
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;
            if (count++ >= 80)
            {
                obj["…"] = "more properties omitted";
                break;
            }

            try
            {
                obj[property.Name] = FormatValue(property.GetValue(value), depth - 1);
            }
            catch (Exception ex)
            {
                obj[property.Name] = $"<threw {ex.InnerException?.GetType().Name ?? ex.GetType().Name}>";
            }
        }

        return obj;
    }

    private static string Summary(object? value)
    {
        if (value == null)
            return "null";

        var type = value.GetType();
        if (value is string s)
            return $"\"{Truncate(s, 120)}\"";
        if (type.IsPrimitive || type.IsEnum || type.IsValueType || value is decimal)
            return value.ToString() ?? "";
        if (value is ICollection collection)
            return $"{TypeName(type)} (Count={collection.Count})";

        var text = value.ToString();
        return text == null || text == type.ToString()
            ? TypeName(type)
            : $"{TypeName(type)} \"{Truncate(text, 120)}\"";
    }

    private static string TypeName(Type type)
    {
        if (type == typeof(void))
            return "void";
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return TypeName(underlying) + "?";
        if (!type.IsGenericType)
            return type.Name;

        var name = type.Name;
        var tick = name.IndexOf('`');
        if (tick >= 0)
            name = name[..tick];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>";
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";

    private static string RequiredString(JsonObject args, string name) =>
        args[name]?.GetValue<string>()
        ?? throw new InvalidOperationException($"Missing required argument '{name}'.");

    #endregion
}
#endif
