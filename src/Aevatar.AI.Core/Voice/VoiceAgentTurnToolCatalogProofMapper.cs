using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Google.Protobuf;

namespace Aevatar.AI.Core.Voice;

internal static class VoiceAgentTurnToolCatalogProofMapper
{
    public static VoiceAgentTurnToolCatalogProof ToPayload(AgentTurnToolCatalogProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        var payload = new VoiceAgentTurnToolCatalogProof
        {
            ToolCount = proof.ToolCount,
            SchemaBytes = proof.SchemaBytes,
            CatalogDigest = proof.CatalogDigest,
            MaximumToolCount = proof.Budget.MaximumToolCount,
            MaximumSchemaBytes = proof.Budget.MaximumSchemaBytes,
            ConnectedReadToolCount = proof.ConnectedReadToolCount,
            ConnectedWriteToolCount = proof.ConnectedWriteToolCount,
        };
        payload.ToolDescriptors.AddRange(proof.ToolDescriptors.Select(static descriptor =>
            new VoiceAgentTurnToolDescriptorProof
            {
                Name = descriptor.Name,
                ExactDescription = descriptor.Description,
                CanonicalSchema = ByteString.CopyFrom(descriptor.CanonicalSchemaBytes.Span),
                SchemaSha256 = descriptor.SchemaSha256,
                OriginKind = descriptor.Origin.ToString(),
                SelectorDigest = descriptor.SelectorDigest,
            }));
        return payload;
    }

    public static void AssertMatchesIfPinned(
        VoiceToolExecutionContext? context,
        AgentTurnToolCatalogProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        if (context?.ToolCatalogProof is null)
            return;

        if (!string.Equals(
                context.ToolCatalogPolicyVersion,
                VoiceAgentTurnToolCatalogMaterializer.PolicyVersion,
                StringComparison.Ordinal))
        {
            throw ProofMismatch();
        }

        var expected = ToPayload(proof);
        if (!expected.Equals(context.ToolCatalogProof))
            throw ProofMismatch();
    }

    private static AgentTurnToolCatalogException ProofMismatch() =>
        new(new AgentTurnToolCatalogFailure(
            AgentTurnToolCatalogFailureCode.CatalogProofMismatch,
            "The rematerialized voice tool catalog does not match the pinned session proof."));
}
