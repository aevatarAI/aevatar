namespace Aevatar.Foundation.Abstractions.Runtime;

/// <summary>
/// Runtime-owned activation/turn context for an actor's persisted schema
/// identity. Business commands cannot provide this context.
/// </summary>
public interface IRuntimeActorStateSchemaContextReader
{
    RuntimeActorStateSchemaContext? Current { get; }
}

/// <summary>
/// Compatibility name for callers compiled against the previous accessor.
/// It is intentionally read-only; runtime binding is available only through
/// <c>IRuntimeActorStateSchemaContextBinder</c> in the runtime assembly.
/// </summary>
[Obsolete("Use IRuntimeActorStateSchemaContextReader.")]
public interface IRuntimeActorStateSchemaContextAccessor
    : IRuntimeActorStateSchemaContextReader
{
}

public sealed record RuntimeActorStateSchemaContext(
    string AgentKind,
    int StateSchemaVersion,
    IReadOnlyList<RuntimeActorStateSchemaAdoptionReceipt> AdoptionReceipts)
{
    public static RuntimeActorStateSchemaContext FromIdentity(RuntimeActorIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new RuntimeActorStateSchemaContext(
            identity.Kind,
            identity.StateSchemaVersion,
            identity.StateSchemaAdoptions.Select(static receipt => receipt.Clone()).ToArray());
    }
}
