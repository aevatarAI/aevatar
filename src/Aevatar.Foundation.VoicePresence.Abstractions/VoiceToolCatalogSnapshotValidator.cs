using System.Text;

namespace Aevatar.Foundation.VoicePresence.Abstractions;

/// <summary>
/// Enforces the transport-level invariants of the sealed voice tool catalog. The AI adapter
/// remains responsible for computing and cryptographically validating the catalog proof.
/// </summary>
public static class VoiceToolCatalogSnapshotValidator
{
    // Persisted optimization target only. It is not a catalog validity ceiling.
    public const int MaximumToolCount = 6;
    public const int MaximumSchemaBytes = 32 * 1024;

    public static void Validate(VoiceToolCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var proof = snapshot.Proof ?? throw Invalid("Voice tool catalog proof is required.");
        if (string.IsNullOrWhiteSpace(snapshot.PolicyVersion))
            throw Invalid("Voice tool catalog policy version is required.");
        if (proof.MaximumToolCount != MaximumToolCount ||
            proof.MaximumSchemaBytes != MaximumSchemaBytes)
        {
            throw Invalid("Voice tool catalog budget does not match the reviewed voice policy.");
        }

        if (proof.ToolCount < 0 ||
            proof.SchemaBytes < 0 || proof.SchemaBytes > MaximumSchemaBytes ||
            proof.ToolCount != snapshot.Tools.Count ||
            proof.ToolCount != proof.ToolDescriptors.Count)
        {
            throw Invalid("Voice tool catalog proof counts are inconsistent or its schema is over budget.");
        }

        if (string.IsNullOrWhiteSpace(proof.CatalogDigest))
            throw Invalid("Voice tool catalog digest is required.");

        var definitions = new Dictionary<string, VoiceToolDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in snapshot.Tools)
        {
            var name = definition.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name) || !definitions.TryAdd(name, definition))
                throw Invalid("Voice tool names must be non-empty and unique.");
            if (definition.Owner is not (VoiceToolOwner.Actor or VoiceToolOwner.Client))
                throw Invalid($"Voice catalog tool '{name}' must declare an execution owner.");
        }

        var descriptorNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var schemaBytes = 0;
        foreach (var descriptor in proof.ToolDescriptors)
        {
            var name = descriptor.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name) || !descriptorNames.Add(name) ||
                !definitions.TryGetValue(name, out var definition))
            {
                throw Invalid("Voice tool descriptor names must map one-to-one to tool definitions.");
            }

            if (!string.Equals(descriptor.OriginKind, "Voice", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(descriptor.SchemaSha256) ||
                !string.Equals(definition.Description ?? string.Empty, descriptor.ExactDescription, StringComparison.Ordinal) ||
                !string.Equals(definition.ParametersSchema ?? string.Empty, descriptor.CanonicalSchema.ToStringUtf8(), StringComparison.Ordinal))
            {
                throw Invalid($"Voice tool descriptor '{name}' does not match its exact definition.");
            }

            schemaBytes += descriptor.CanonicalSchema.Length;
        }

        if (schemaBytes != proof.SchemaBytes)
            throw Invalid("Voice tool catalog schema byte count does not match its descriptors.");
    }

    private static InvalidOperationException Invalid(string message) => new(message);
}
