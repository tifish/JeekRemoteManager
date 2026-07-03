using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JeekRemoteManager.Services;

/// <summary>
/// One multi-turn chat session against an OpenAI-compatible chat completions endpoint
/// (<c>{base}/chat/completions</c>, SSE streaming). The full message history is kept in
/// memory and resent each turn — the API is stateless, unlike the CLI sessions.
/// </summary>
public sealed class OpenAiChatSession : IAgentChatSession
{
    public const string DefaultBaseUrl = "https://api.openai.com/v1";

    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string? _effort;
    private readonly List<(string Role, string Text)> _history = new();
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _disposeCts = new();
    private int _numTurns;
    private volatile bool _disposed;

    public OpenAiChatSession(string? baseUrl, string apiKey, string model, string? systemPrompt, string? effort)
    {
        _endpoint = BuildEndpoint(baseUrl);
        _apiKey = apiKey;
        _model = model;
        _effort = effort;
        // Streaming turns can legitimately run for minutes; cancellation comes from the caller.
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            _history.Add(("system", systemPrompt));
        SessionId = Guid.NewGuid().ToString("N");
    }

    private static string BuildEndpoint(string? baseUrl)
    {
        var url = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim().TrimEnd('/');
        return url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? url
            : url + "/chat/completions";
    }

    public string? SessionId { get; }

    public event Action<string>? SessionInitialized;

    public event Action<string>? TextDelta;

    public event Action<AgentTurnResult>? TurnCompleted;

    // Failures surface as exceptions from SendAsync; the event exists only for the interface.
#pragma warning disable CS0067
    public event Action<string>? Errored;
#pragma warning restore CS0067

    public event Action? Exited;

    public void Start() => SessionInitialized?.Invoke(SessionId!);

    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new InvalidOperationException("The session has been disposed.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        _history.Add(("user", text));

        var answer = new StringBuilder();
        long outputTokens = 0;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            request.Content = new StringContent(BuildRequestBody(), Encoding.UTF8, "application/json");

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                throw new HttpRequestException(
                    $"{(int)response.StatusCode} {response.ReasonPhrase}: {ExtractApiError(body)}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            while (await reader.ReadLineAsync(cts.Token).ConfigureAwait(false) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    continue;
                var payload = line[5..].Trim();
                if (payload.Length == 0)
                    continue;
                if (payload == "[DONE]")
                    break;

                try
                {
                    using var doc = JsonDocument.Parse(payload);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("choices", out var choices)
                        && choices.ValueKind == JsonValueKind.Array
                        && choices.GetArrayLength() > 0
                        && choices[0].TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("content", out var content)
                        && content.ValueKind == JsonValueKind.String)
                    {
                        var chunk = content.GetString();
                        if (!string.IsNullOrEmpty(chunk))
                        {
                            answer.Append(chunk);
                            TextDelta?.Invoke(chunk);
                        }
                    }

                    if (root.TryGetProperty("usage", out var usage)
                        && usage.ValueKind == JsonValueKind.Object
                        && usage.TryGetProperty("completion_tokens", out var ct)
                        && ct.TryGetInt64(out var tokens))
                    {
                        outputTokens = tokens;
                    }
                }
                catch (JsonException)
                {
                    // A malformed SSE chunk must not kill the turn.
                }
            }
        }
        catch
        {
            // The failed user message stays out of the history so a retry starts clean.
            _history.RemoveAt(_history.Count - 1);
            throw;
        }

        _history.Add(("assistant", answer.ToString()));
        _numTurns++;
        TurnCompleted?.Invoke(new AgentTurnResult(answer.ToString(), 0, outputTokens, _numTurns, IsError: false));
    }

    private string BuildRequestBody()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", _model);
            writer.WriteBoolean("stream", true);
            writer.WriteStartObject("stream_options");
            writer.WriteBoolean("include_usage", true);
            writer.WriteEndObject();
            if (!string.IsNullOrWhiteSpace(_effort))
                writer.WriteString("reasoning_effort", _effort);
            writer.WriteStartArray("messages");
            foreach (var (role, content) in _history)
            {
                writer.WriteStartObject();
                writer.WriteString("role", role);
                writer.WriteString("content", content);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Pulls the human-readable message out of an API error body, falling back to
    /// the raw (truncated) body when it isn't the expected JSON shape.</summary>
    internal static string ExtractApiError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString() ?? body;
                }
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
        }

        return body.Length <= 500 ? body : body[..500];
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;

        _disposeCts.Cancel();
        _disposeCts.Dispose();
        _http.Dispose();
        Exited?.Invoke();
        return ValueTask.CompletedTask;
    }
}
