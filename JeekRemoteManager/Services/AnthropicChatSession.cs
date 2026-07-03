using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JeekRemoteManager.Services;

/// <summary>
/// One multi-turn chat session against an Anthropic-compatible messages endpoint
/// (<c>{base}/v1/messages</c>, SSE streaming). The full message history is kept in
/// memory and resent each turn — the API is stateless, unlike the CLI sessions.
/// </summary>
public sealed class AnthropicChatSession : IAgentChatSession
{
    public const string DefaultBaseUrl = "https://api.anthropic.com";

    private const int MaxOutputTokens = 8192;

    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string? _systemPrompt;
    private readonly List<(string Role, string Text)> _history = new();
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _disposeCts = new();
    private int _numTurns;
    private volatile bool _disposed;

    public AnthropicChatSession(string? baseUrl, string apiKey, string model, string? systemPrompt)
    {
        _endpoint = BuildEndpoint(baseUrl);
        _apiKey = apiKey;
        _model = model;
        _systemPrompt = systemPrompt;
        // Streaming turns can legitimately run for minutes; cancellation comes from the caller.
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        SessionId = Guid.NewGuid().ToString("N");
    }

    private static string BuildEndpoint(string? baseUrl)
    {
        var url = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim().TrimEnd('/');
        if (url.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
            return url;
        if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return url + "/messages";
        return url + "/v1/messages";
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
            request.Headers.Add("x-api-key", _apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Content = new StringContent(BuildRequestBody(), Encoding.UTF8, "application/json");

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                throw new HttpRequestException(
                    $"{(int)response.StatusCode} {response.ReasonPhrase}: {OpenAiChatSession.ExtractApiError(body)}");
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

                try
                {
                    using var doc = JsonDocument.Parse(payload);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var typeProp))
                        continue;

                    switch (typeProp.GetString())
                    {
                        case "content_block_delta":
                            if (root.TryGetProperty("delta", out var delta)
                                && delta.TryGetProperty("type", out var deltaType)
                                && deltaType.GetString() == "text_delta"
                                && delta.TryGetProperty("text", out var textProp))
                            {
                                var chunk = textProp.GetString();
                                if (!string.IsNullOrEmpty(chunk))
                                {
                                    answer.Append(chunk);
                                    TextDelta?.Invoke(chunk);
                                }
                            }
                            break;

                        case "message_delta":
                            if (root.TryGetProperty("usage", out var usage)
                                && usage.TryGetProperty("output_tokens", out var ot)
                                && ot.TryGetInt64(out var tokens))
                            {
                                outputTokens = tokens;
                            }
                            break;

                        case "error":
                            var message = root.TryGetProperty("error", out var error)
                                && error.TryGetProperty("message", out var errorMessage)
                                ? errorMessage.GetString()
                                : null;
                            throw new HttpRequestException(message ?? "The API reported an error.");
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
            writer.WriteNumber("max_tokens", MaxOutputTokens);
            writer.WriteBoolean("stream", true);
            if (!string.IsNullOrWhiteSpace(_systemPrompt))
                writer.WriteString("system", _systemPrompt);
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
