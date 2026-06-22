using Aevatar.GAgents.Scheduled;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Mainnet.Host.Api.Hosting;

/// <summary>
/// Adapts the platform <see cref="IScheduledAgentApiKeyIssuer"/> (the proven
/// SkillRunner scheduled-agent pattern) into the Studio
/// <see cref="IStudioRunCredentialIssuer"/> contract: mints a <b>durable</b> NyxID
/// agent key under the caller's account so a C1-provisioned scheduled workflow run
/// can authenticate its LLM call without a re-mintable subject binding.
///
/// Lives in the host because the host is the only layer that legitimately depends
/// on both Studio.Application and the scheduled-agent issuer (mirrors
/// <see cref="StudioUserConfigOwnerLlmConfigSource"/>); the Studio packages stay
/// free of any agents reference.
///
/// The issuer demands a primary outbound service slug; a generic workflow run has
/// no outbound delivery channel, so the slug is host configuration
/// (<see cref="StudioRunCredentialIssuerOptions.OutboundServiceSlug"/>) — C1 itself
/// stays free of any host/business slug. When no slug is configured (the default),
/// this adapter returns <c>null</c> and the provisioning service falls back to the
/// caller's forwarded token (valid for a soon-firing one-shot run). The issuer also
/// auto-grants the owner's LLM route, which is what the workflow run's LLM call
/// actually needs.
/// </summary>
internal sealed class ScheduledAgentKeyStudioRunCredentialIssuer : IStudioRunCredentialIssuer
{
    private readonly IScheduledAgentApiKeyIssuer _apiKeyIssuer;
    private readonly StudioRunCredentialIssuerOptions _options;
    private readonly ILogger<ScheduledAgentKeyStudioRunCredentialIssuer>? _logger;

    public ScheduledAgentKeyStudioRunCredentialIssuer(
        IScheduledAgentApiKeyIssuer apiKeyIssuer,
        StudioRunCredentialIssuerOptions options,
        ILogger<ScheduledAgentKeyStudioRunCredentialIssuer>? logger = null)
    {
        _apiKeyIssuer = apiKeyIssuer ?? throw new ArgumentNullException(nameof(apiKeyIssuer));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public async Task<string?> IssueDurableRunCredentialAsync(
        string callerBearerToken,
        string agentId,
        string scopeId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(callerBearerToken) || string.IsNullOrWhiteSpace(agentId))
            return null;

        var outboundSlug = _options.OutboundServiceSlug?.Trim();
        if (string.IsNullOrEmpty(outboundSlug))
        {
            // No host-configured slug → cannot mint a least-privilege key without
            // guessing a service. Fall back to the forwarded token (the caller).
            return null;
        }

        // A workflow run consumes the LLM proxy, not Ornn skills — skip the Ornn
        // grant. The issuer auto-adds the owner's LLM route grant from scope config.
        var result = await _apiKeyIssuer.IssueAsync(
            callerBearerToken,
            new ScheduledAgentServiceSlugs(
                PrimaryOutboundSlug: outboundSlug,
                FailureNotificationSlug: null,
                RequiredServiceSlugs: [],
                RequiresOrnnService: false),
            agentId,
            skillName: string.Empty,
            scopeId: scopeId,
            ct);

        if (result.Success && !string.IsNullOrWhiteSpace(result.FullKey))
            return result.FullKey;

        // Minting failed (e.g. the caller does not own the configured service).
        // Log and fall back to the forwarded token so provisioning still succeeds.
        _logger?.LogWarning(
            "Durable run-credential mint failed for agent {AgentId} in scope {ScopeId}: {Error}. Falling back to the forwarded caller token.",
            agentId,
            scopeId,
            result.Error ?? "unknown_error");
        return null;
    }
}

/// <summary>
/// Host configuration for <see cref="ScheduledAgentKeyStudioRunCredentialIssuer"/>.
/// <see cref="OutboundServiceSlug"/> is the NyxID service slug a minted durable run
/// key is authorized for. Unset (the default) disables minting — provisioning then
/// threads the caller's forwarded token as the run credential. C1 carries no
/// hardcoded slug; only the host opts into durable-key minting by setting this.
/// </summary>
public sealed class StudioRunCredentialIssuerOptions
{
    public const string ConfigurationSection = "Aevatar:Studio:RunCredentialIssuer";

    public string? OutboundServiceSlug { get; set; }
}
