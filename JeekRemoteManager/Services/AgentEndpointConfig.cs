using System;
using System.Collections.Generic;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.Services;

/// <summary>
/// Points an agent at a custom API endpoint — an Anthropic-compatible gateway rather than
/// Anthropic's own API. The key is handed to the child process in its environment, so it is never
/// written to disk in clear: Claude reads <c>ANTHROPIC_BASE_URL</c> and <c>ANTHROPIC_AUTH_TOKEN</c>.
///
/// Claude is the only agent offered here. Codex dropped the Chat Completions protocol and now
/// speaks only the Responses API, which the Anthropic- and OpenAI-compatible gateways people
/// actually want to reach do not implement — so pointing Codex at one only produced 404s.
///
/// Only the terminal surfaces (CLI, Windows Terminal) can be redirected: desktop apps and
/// editors are launched by the shell or already running, so they use their own settings.
/// </summary>
public static class AgentEndpointConfig
{
    /// <summary>Agents that can be redirected at all.</summary>
    public static bool Supports(AgentCliKind kind) => kind is AgentCliKind.Claude;

    /// <summary>
    /// Environment overrides for launching <paramref name="kind"/>, or an empty map when the
    /// agent is on its official API, unsupported, or the endpoint has no base URL — which is
    /// treated as unusable rather than half-applied.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildEnvironment(
        AgentCliKind kind,
        AgentEndpointProfile? endpoint)
    {
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!IsUsable(kind, endpoint))
            return overrides;

        overrides["ANTHROPIC_BASE_URL"] = endpoint!.BaseUrl.Trim();

        // AUTH_TOKEN, not API_KEY: a gateway expects "Authorization: Bearer", while
        // ANTHROPIC_API_KEY sends the x-api-key header Anthropic's own API wants.
        if (DecryptKey(endpoint) is { Length: > 0 } key)
            overrides["ANTHROPIC_AUTH_TOKEN"] = key;

        if (endpoint.Model.Trim() is { Length: > 0 } model)
            overrides["ANTHROPIC_MODEL"] = model;

        return overrides;
    }

    /// <summary>
    /// True when an endpoint is selected and has the one field it cannot work without. A
    /// key-less gateway is allowed: some local relays do not authenticate.
    /// </summary>
    public static bool IsUsable(AgentCliKind kind, AgentEndpointProfile? endpoint) =>
        Supports(kind)
        && endpoint is not null
        && !string.IsNullOrWhiteSpace(endpoint.BaseUrl);

    private static string DecryptKey(AgentEndpointProfile endpoint)
    {
        if (string.IsNullOrEmpty(endpoint.EncryptedApiKey))
            return "";
        return PasswordProtector.TryDecrypt(endpoint.EncryptedApiKey, out var clear) ? clear : "";
    }
}
