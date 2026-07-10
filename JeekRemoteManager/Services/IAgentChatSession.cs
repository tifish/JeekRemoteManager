using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace JeekRemoteManager.Services;

/// <summary>Result of one completed assistant turn.</summary>
public readonly record struct AgentTurnResult(string Text, double CostUsd, long OutputTokens, int NumTurns, bool IsError);

/// <summary>One model advertised by a provider's CLI, with the reasoning efforts it supports.</summary>
public sealed record AgentModelInfo(string Id, string DisplayName, bool IsDefault, IReadOnlyList<string> ReasoningEfforts);

/// <summary>
/// One multi-turn AI chat session backed by an agent CLI subprocess. Implementations wrap
/// provider-specific wire protocols (Claude stream-json, Codex app-server JSON-RPC, Grok
/// Build ACP) behind the same turn-based surface: send a user message, receive streaming
/// text, get a completion event with usage.
///
/// Events are raised on a background read-loop thread — consumers must marshal to the UI
/// thread themselves (e.g. via <c>Dispatcher.UIThread.Post</c>).
/// </summary>
public interface IAgentChatSession : IAsyncDisposable
{
    /// <summary>The provider's conversation id (Claude session id / Codex thread id), once known.</summary>
    string? SessionId { get; }

    /// <summary>Raised once, when the provider reports its conversation id.</summary>
    event Action<string>? SessionInitialized;

    /// <summary>Incremental assistant answer text. Thinking/reasoning is not included.</summary>
    event Action<string>? TextDelta;

    /// <summary>A turn finished; carries the final text plus cost/usage.</summary>
    event Action<AgentTurnResult>? TurnCompleted;

    /// <summary>A protocol or process error (stderr line, parse failure, or crash).</summary>
    event Action<string>? Errored;

    /// <summary>The subprocess exited.</summary>
    event Action? Exited;

    /// <summary>Launches the subprocess. Call once before the first <see cref="SendAsync"/>.</summary>
    void Start();

    /// <summary>Sends one user message; the reply arrives via <see cref="TextDelta"/> and
    /// <see cref="TurnCompleted"/>.</summary>
    Task SendAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Best-effort abort of the in-flight turn. Sessions without a wire-level
    /// interrupt (HTTP APIs) rely on the caller cancelling <see cref="SendAsync"/>'s token
    /// instead, so the default is a no-op.</summary>
    Task InterruptAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
