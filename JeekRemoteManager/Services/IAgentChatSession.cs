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

    /// <summary>Replaces all assistant text streamed so far for the active turn. Providers
    /// raise this when a retry retracts an abandoned attempt or a protocol supplies an
    /// authoritative completed-message snapshot.</summary>
    event Action<string>? TextReplaced;

    /// <summary>A turn finished; carries the final text plus cost/usage.</summary>
    event Action<AgentTurnResult>? TurnCompleted;

    /// <summary>A protocol or process error (stderr line, parse failure, or crash).</summary>
    event Action<string>? Errored;

    /// <summary>Optional in-turn progress (e.g. provider API retries). Not a hard failure.</summary>
    event Action<string>? StatusHint
    {
        add { }
        remove { }
    }

    /// <summary>The subprocess exited.</summary>
    event Action? Exited;

    /// <summary>Launches the subprocess. Call once before the first <see cref="SendAsync"/>.</summary>
    void Start();

    /// <summary>Whether the provider can append another user message to the currently
    /// active turn without interrupting it or creating a new turn.</summary>
    bool SupportsSteering => false;

    /// <summary>Sends one user message; the reply arrives via <see cref="TextDelta"/> and
    /// <see cref="TurnCompleted"/>.</summary>
    Task SendAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Appends user input to the currently active turn. Providers that do not
    /// expose same-turn steering keep the default unsupported implementation.</summary>
    Task SteerAsync(string text, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("This AI provider does not support steering an active turn."));

    /// <summary>Best-effort abort of the in-flight turn. Sessions without a wire-level
    /// interrupt (HTTP APIs) rely on the caller cancelling <see cref="SendAsync"/>'s token
    /// instead, so the default is a no-op.</summary>
    Task InterruptAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
