using System;
using System.Collections.Generic;
using System.Text;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.Services;

/// <summary>
/// Points an agent at a custom API endpoint — an Anthropic- or OpenAI-compatible gateway rather
/// than the vendor's own API. Both supported agents take the key from the environment, so the
/// secret is passed to the child process and never written to disk in clear:
///
/// <list type="bullet">
/// <item>Claude reads <c>ANTHROPIC_BASE_URL</c> and <c>ANTHROPIC_AUTH_TOKEN</c> directly.</item>
/// <item>Codex needs a provider table in its <c>config.toml</c>, but that table only names an
/// environment variable (<c>env_key</c>) — the key itself still travels in the environment.</item>
/// </list>
///
/// Only the terminal surfaces (CLI, Windows Terminal) can be redirected: desktop apps and
/// editors are launched by the shell or already running, so they use their own settings.
/// </summary>
public static class AgentEndpointConfig
{
    /// <summary>Provider name written into Codex's config; also the table key.</summary>
    public const string CodexProviderName = "jrm_custom";

    /// <summary>
    /// Environment variable Codex's <c>env_key</c> points at. Named for this app so it cannot
    /// collide with a key the user already exports for their own Codex setup.
    /// </summary>
    public const string CodexApiKeyVariable = "JRM_CODEX_API_KEY";

    /// <summary>Agents that can be redirected at all.</summary>
    public static bool Supports(AgentCliKind kind) =>
        kind is AgentCliKind.Claude or AgentCliKind.Codex;

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

        var baseUrl = endpoint!.BaseUrl.Trim();
        var key = DecryptKey(endpoint);

        switch (kind)
        {
            case AgentCliKind.Claude:
                overrides["ANTHROPIC_BASE_URL"] = baseUrl;
                // AUTH_TOKEN, not API_KEY: a gateway expects "Authorization: Bearer", while
                // ANTHROPIC_API_KEY sends the x-api-key header Anthropic's own API wants.
                if (key.Length > 0)
                    overrides["ANTHROPIC_AUTH_TOKEN"] = key;
                if (endpoint.Model.Trim() is { Length: > 0 } claudeModel)
                    overrides["ANTHROPIC_MODEL"] = claudeModel;
                break;

            case AgentCliKind.Codex:
                // The base URL and model live in config.toml; only the secret comes through here.
                if (key.Length > 0)
                    overrides[CodexApiKeyVariable] = key;
                break;
        }

        return overrides;
    }

    /// <summary>
    /// The <c>model_provider</c> block appended to the workspace's <c>.codex/config.toml</c>, or
    /// an empty string when Codex is not redirected. Returns config only — the key stays out.
    /// </summary>
    public static string BuildCodexProviderToml(AgentEndpointProfile? endpoint)
    {
        if (!IsUsable(AgentCliKind.Codex, endpoint))
            return "";

        var sb = new StringBuilder();
        sb.Append("model_provider = \"").Append(CodexProviderName).Append("\"\n");
        if (endpoint!.Model.Trim() is { Length: > 0 } model)
            sb.Append("model = \"").Append(AgentMcpConfigCatalog.EscapeToml(model)).Append("\"\n");
        sb.Append("[model_providers.").Append(CodexProviderName).Append("]\n");
        sb.Append("name = \"JeekRemoteManager custom endpoint\"\n");
        sb.Append("base_url = \"")
          .Append(AgentMcpConfigCatalog.EscapeToml(endpoint.BaseUrl.Trim()))
          .Append("\"\n");
        // env_key names the variable, never the value — the key is injected at launch.
        sb.Append("env_key = \"").Append(CodexApiKeyVariable).Append("\"\n");
        return sb.ToString();
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
