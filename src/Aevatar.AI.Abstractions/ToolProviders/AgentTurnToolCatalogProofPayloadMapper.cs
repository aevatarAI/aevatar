using System.Text;
using Google.Protobuf;

namespace Aevatar.AI.Abstractions.ToolProviders;

public static class AgentTurnToolCatalogProofPayloadMapper
{
    public static AgentTurnToolCatalogProofPayload ToPayload(this AgentTurnToolCatalogProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        var payload = new AgentTurnToolCatalogProofPayload
        {
            Budget = new AgentTurnToolCatalogBudgetPayload
            {
                MaximumToolCount = proof.Budget.MaximumToolCount,
                MaximumSchemaBytes = proof.Budget.MaximumSchemaBytes,
                MaximumConnectedReadToolCount = proof.Budget.MaximumConnectedReadToolCount,
                MaximumConnectedWriteToolCount = proof.Budget.MaximumConnectedWriteToolCount,
            },
            ToolCount = proof.ToolCount,
            SchemaBytes = proof.SchemaBytes,
            ConnectedReadToolCount = proof.ConnectedReadToolCount,
            ConnectedWriteToolCount = proof.ConnectedWriteToolCount,
            CatalogDigest = proof.CatalogDigest,
        };
        payload.ToolDescriptors.AddRange(proof.ToolDescriptors.Select(static descriptor =>
            new AgentTurnToolDescriptorPayload
            {
                Name = descriptor.Name,
                Description = descriptor.Description,
                CanonicalSchemaJson = ByteString.CopyFrom(descriptor.CanonicalSchemaBytes.Span),
                SchemaSha256 = descriptor.SchemaSha256,
                Origin = ToPayload(descriptor.Origin),
                SelectorDigest = descriptor.SelectorDigest,
            }));
        return payload;
    }

    public static AgentTurnToolCatalogProof FromPayload(AgentTurnToolCatalogProofPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Budget is null)
            throw ProofMismatch("catalog proof budget is missing");

        var budget = new AgentTurnToolCatalogBudget(
            payload.Budget.MaximumToolCount,
            payload.Budget.MaximumSchemaBytes,
            payload.Budget.MaximumConnectedReadToolCount,
            payload.Budget.MaximumConnectedWriteToolCount);
        budget.Validate();

        var descriptors = payload.ToolDescriptors.Select(FromPayload).ToArray();
        if (descriptors.Select(static descriptor => descriptor.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != descriptors.Length)
        {
            throw ProofMismatch("catalog proof contains duplicate tool names");
        }

        var proof = new AgentTurnToolCatalogProof(descriptors, budget)
        {
            ConnectedReadToolCount = payload.ConnectedReadToolCount,
            ConnectedWriteToolCount = payload.ConnectedWriteToolCount,
        };
        if (payload.ToolCount != proof.ToolCount ||
            payload.SchemaBytes != proof.SchemaBytes ||
            payload.ConnectedReadToolCount < 0 ||
            payload.ConnectedWriteToolCount < 0 ||
            proof.SchemaBytes > budget.MaximumSchemaBytes ||
            payload.ConnectedReadToolCount > budget.MaximumConnectedReadToolCount ||
            payload.ConnectedWriteToolCount > budget.MaximumConnectedWriteToolCount ||
            !string.Equals(payload.CatalogDigest, proof.CatalogDigest, StringComparison.Ordinal))
        {
            throw ProofMismatch("catalog proof summary does not match its descriptors");
        }

        return proof;
    }

    private static AgentTurnToolDescriptor FromPayload(AgentTurnToolDescriptorPayload payload)
    {
        var canonicalName = AgentTurnToolCatalogProof.NormalizeToolName(payload.Name);
        if (!string.Equals(payload.Name, canonicalName, StringComparison.Ordinal) ||
            payload.Origin == AgentTurnToolOriginPayload.Unspecified ||
            !string.Equals(payload.SelectorDigest, payload.SelectorDigest.Trim(), StringComparison.Ordinal))
        {
            throw ProofMismatch("catalog proof descriptor is not canonical");
        }

        var canonicalSchema = payload.CanonicalSchemaJson.ToByteArray();
        var canonicalized = AgentTurnToolCatalogProof.CanonicalizeSchema(
            canonicalName,
            Encoding.UTF8.GetString(canonicalSchema));
        if (!canonicalSchema.AsSpan().SequenceEqual(canonicalized))
            throw ProofMismatch("catalog proof schema bytes are not canonical");

        var origin = FromPayload(payload.Origin);
        if (origin == AgentTurnToolOrigin.Unspecified)
            throw ProofMismatch("catalog proof descriptor origin is unsupported");

        var descriptor = new AgentTurnToolDescriptor(
            canonicalName,
            payload.Description ?? string.Empty,
            canonicalSchema,
            origin,
            payload.SelectorDigest ?? string.Empty);
        if (!string.Equals(payload.SchemaSha256, descriptor.SchemaSha256, StringComparison.Ordinal))
            throw ProofMismatch("catalog proof schema digest does not match its canonical bytes");

        return descriptor;
    }

    private static AgentTurnToolOriginPayload ToPayload(AgentTurnToolOrigin origin) => origin switch
    {
        AgentTurnToolOrigin.AgentRuntime => AgentTurnToolOriginPayload.AgentRuntime,
        AgentTurnToolOrigin.RouteToolSet => AgentTurnToolOriginPayload.RouteToolSet,
        AgentTurnToolOrigin.AgentProfile => AgentTurnToolOriginPayload.AgentProfile,
        AgentTurnToolOrigin.ConnectedService => AgentTurnToolOriginPayload.ConnectedService,
        AgentTurnToolOrigin.ResponsesState => AgentTurnToolOriginPayload.ResponsesState,
        AgentTurnToolOrigin.CallerForwarded => AgentTurnToolOriginPayload.CallerForwarded,
        AgentTurnToolOrigin.Workflow => AgentTurnToolOriginPayload.Workflow,
        AgentTurnToolOrigin.Voice => AgentTurnToolOriginPayload.Voice,
        _ => AgentTurnToolOriginPayload.Unspecified,
    };

    private static AgentTurnToolOrigin FromPayload(AgentTurnToolOriginPayload origin) => origin switch
    {
        AgentTurnToolOriginPayload.AgentRuntime => AgentTurnToolOrigin.AgentRuntime,
        AgentTurnToolOriginPayload.RouteToolSet => AgentTurnToolOrigin.RouteToolSet,
        AgentTurnToolOriginPayload.AgentProfile => AgentTurnToolOrigin.AgentProfile,
        AgentTurnToolOriginPayload.ConnectedService => AgentTurnToolOrigin.ConnectedService,
        AgentTurnToolOriginPayload.ResponsesState => AgentTurnToolOrigin.ResponsesState,
        AgentTurnToolOriginPayload.CallerForwarded => AgentTurnToolOrigin.CallerForwarded,
        AgentTurnToolOriginPayload.Workflow => AgentTurnToolOrigin.Workflow,
        AgentTurnToolOriginPayload.Voice => AgentTurnToolOrigin.Voice,
        _ => AgentTurnToolOrigin.Unspecified,
    };

    private static AgentTurnToolCatalogException ProofMismatch(string detail) =>
        new(new AgentTurnToolCatalogFailure(
            AgentTurnToolCatalogFailureCode.CatalogProofMismatch,
            detail));
}
