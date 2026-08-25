using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.Core.Voice;

/// <summary>
/// Maps the generic immutable turn catalog to the voice transport contract. Discovery, proof
/// construction, and exact-object selection remain owned by the shared voice materializer.
/// </summary>
public sealed class AgentToolVoiceCatalog : IVoiceToolCatalog
{
    private readonly VoiceAgentTurnToolCatalogMaterializer _materializer;

    public AgentToolVoiceCatalog(VoiceAgentTurnToolCatalogMaterializer materializer)
    {
        _materializer = materializer ?? throw new ArgumentNullException(nameof(materializer));
    }

    public AgentToolVoiceCatalog(
        IEnumerable<IAgentToolSource> toolSources,
        ICredentialProvider? credentialProvider = null,
        ILogger<AgentToolVoiceCatalog>? logger = null)
        : this(
            toolSources,
            credentialProvider is null ? [] : [credentialProvider],
            logger)
    {
    }

    public AgentToolVoiceCatalog(
        IEnumerable<IAgentToolSource> toolSources,
        IEnumerable<ICredentialProvider> credentialProviders,
        ILogger<AgentToolVoiceCatalog>? logger = null,
        IAgentToolDiscoveryService? toolDiscoveryService = null)
        : this(new VoiceAgentTurnToolCatalogMaterializer(
            toolSources,
            credentialProviders,
            toolDiscoveryService,
            logger: null))
    {
        _ = logger;
    }

    public async Task<VoiceToolCatalogSnapshot> DiscoverAsync(
        VoiceToolExecutionContext? toolContext = null,
        CancellationToken ct = default)
    {
        var catalog = await _materializer.MaterializeAsync(toolContext, ct).ConfigureAwait(false);
        var snapshot = new VoiceToolCatalogSnapshot
        {
            Proof = VoiceAgentTurnToolCatalogProofMapper.ToPayload(catalog.Proof),
            PolicyVersion = VoiceAgentTurnToolCatalogMaterializer.PolicyVersion,
        };
        snapshot.Tools.AddRange(catalog.Proof.ToolDescriptors.Select(descriptor =>
            new VoiceToolDefinition
            {
                Name = descriptor.Name,
                Description = descriptor.Description,
                ParametersSchema = descriptor.CanonicalSchemaJson,
                Owner = VoiceToolOwner.Actor,
            }));
        return snapshot;
    }
}
