using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Core.Voice;

public sealed record VoiceAgentTurnToolCatalogMaterialization(
    AgentTurnToolCatalog Catalog,
    AgentToolExecutionContext ExecutionContext);

/// <summary>
/// The single voice adapter that converts route policy, caller authority, and request-local
/// discovery into the generic immutable turn catalog.
/// </summary>
public sealed class VoiceAgentTurnToolCatalogMaterializer
{
    public const string PolicyVersion = "voice-agent-turn-tool-catalog/v1";

    private readonly IReadOnlyList<IAgentToolSource> _toolSources;
    private readonly IReadOnlyList<ICredentialProvider> _credentialProviders;
    private readonly IAgentToolDiscoveryService _toolDiscoveryService;
    private readonly ILogger _logger;

    public VoiceAgentTurnToolCatalogMaterializer(
        IEnumerable<IAgentToolSource> toolSources,
        IEnumerable<ICredentialProvider>? credentialProviders = null,
        IAgentToolDiscoveryService? toolDiscoveryService = null,
        ILogger<VoiceAgentTurnToolCatalogMaterializer>? logger = null)
    {
        _toolSources = toolSources?.ToArray() ?? throw new ArgumentNullException(nameof(toolSources));
        _credentialProviders = credentialProviders?.ToArray() ?? [];
        _toolDiscoveryService = toolDiscoveryService ?? AgentToolDiscoveryService.Instance;
        _logger = logger ?? NullLogger<VoiceAgentTurnToolCatalogMaterializer>.Instance;
    }

    public async Task<AgentTurnToolCatalog> MaterializeAsync(
        VoiceToolExecutionContext? voiceContext,
        CancellationToken ct = default) =>
        (await MaterializeForExecutionAsync(voiceContext, ct).ConfigureAwait(false)).Catalog;

    public async Task<VoiceAgentTurnToolCatalogMaterialization> MaterializeForExecutionAsync(
        VoiceToolExecutionContext? voiceContext,
        CancellationToken ct = default)
    {
        var budget = AgentTurnToolCatalogBudget.Voice;
        if (voiceContext is null || voiceContext.AllowedToolNames.Count == 0)
        {
            var empty = AgentTurnToolCatalogFactory.RestrictedEmpty(budget);
            VoiceAgentTurnToolCatalogProofMapper.AssertMatchesIfPinned(voiceContext, empty.Proof);
            return new VoiceAgentTurnToolCatalogMaterialization(empty, RestrictedEmptyExecutionContext());
        }

        var agentToolContext = await ResolveToolContextAsync(voiceContext, ct).ConfigureAwait(false);
        if (agentToolContext is null)
        {
            var empty = AgentTurnToolCatalogFactory.RestrictedEmpty(budget);
            VoiceAgentTurnToolCatalogProofMapper.AssertMatchesIfPinned(voiceContext, empty.Proof);
            return new VoiceAgentTurnToolCatalogMaterialization(empty, RestrictedEmptyExecutionContext());
        }

        var discovery = await _toolDiscoveryService
            .DiscoverAsync(_toolSources, agentToolContext, ct)
            .ConfigureAwait(false);
        if (!discovery.IsSuccess)
        {
            _logger.LogWarning(
                "Voice tool catalog discovery failed closed. code={FailureCode} tool={ToolName} source={SourceType} conflictingSource={ConflictingSourceType}",
                discovery.Failure!.Code,
                discovery.Failure.ToolName,
                discovery.Failure.SourceType,
                discovery.Failure.ConflictingSourceType);
            throw new AgentToolDiscoveryException(discovery.Failure);
        }

        var exactTools = discovery.Tools
            .Where(tool => agentToolContext.ToolVisibility.Allows(tool.Name))
            .ToArray();
        var catalog = new AgentTurnToolCatalog(
            exactTools.Select(static tool => tool.Name),
            profilePromptLayer: null,
            selectedSkillPromptLayer: null,
            selectedIntentId: null,
            candidateIntentId: null,
            diagnostics: null,
            exactToolSelections: exactTools.Select(static tool =>
                new AgentTurnToolSelection(tool, AgentTurnToolOrigin.Voice)),
            hasUnresolvedConnectedServiceSelectors: false,
            requiredToolInvocation: null,
            budget: budget);
        VoiceAgentTurnToolCatalogProofMapper.AssertMatchesIfPinned(voiceContext, catalog.Proof);
        _logger.LogInformation(
            "Voice turn tool catalog frozen. toolCount={ToolCount} schemaBytes={SchemaBytes} digest={CatalogDigest}",
            catalog.Proof.ToolCount,
            catalog.Proof.SchemaBytes,
            catalog.Proof.CatalogDigest);
        return new VoiceAgentTurnToolCatalogMaterialization(catalog, agentToolContext);
    }

    private static AgentToolExecutionContext RestrictedEmptyExecutionContext() =>
        AgentToolExecutionContext.Empty with
        {
            ToolVisibility = AgentToolVisibilityScope.FromAllowedToolNames([]),
        };

    private async Task<AgentToolExecutionContext?> ResolveToolContextAsync(
        VoiceToolExecutionContext voiceContext,
        CancellationToken ct)
    {
        if (!VoiceToolExecutionContextMapper.IsUsableCredentialRef(voiceContext, DateTimeOffset.UtcNow) ||
            _credentialProviders.Count == 0)
        {
            return null;
        }

        var credentialRef = VoiceToolExecutionContextMapper.Normalize(voiceContext.CredentialRef);
        if (credentialRef is null)
            return null;

        foreach (var credentialProvider in _credentialProviders)
        {
            var credential = await credentialProvider.ResolveAsync(credentialRef, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(credential))
                return VoiceToolExecutionContextMapper.ToAgentToolContext(voiceContext, credential);
        }

        return null;
    }
}
