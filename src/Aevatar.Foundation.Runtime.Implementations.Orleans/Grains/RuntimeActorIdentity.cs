namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

/// <summary>
/// Persisted business identity for an actor. Replaces the runtime-incidental
/// <c>RuntimeActorGrainState.AgentTypeName</c> as the primary handle the
/// activation path uses to find an implementation.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Kind"/> is empty for legacy state written before this envelope
/// existed; the activation path looks up the kind via the legacy CLR-name
/// fallback, then lazy-tags this field on first successful activation.
/// </para>
/// <para>
/// <see cref="StateSchemaVersion"/> is the runtime-owned schema marker for
/// the actor's persisted business state. Business state protos themselves
/// stay pure domain artifacts and do not carry a version field — see ADR
/// 0020 for rationale and the consumer contract from issue #500.
/// </para>
/// <para>
/// <see cref="LegacyClrTypeName"/> is populated only during the Phase 1/2
/// transition window; once Phase 3 lands and CLR-name identity is removed,
/// this field becomes <c>reserved</c>.
/// </para>
/// </remarks>
[GenerateSerializer]
public sealed class RuntimeActorIdentity
{
    [Id(0)]
    public string Kind { get; set; } = string.Empty;

    [Id(1)]
    public int StateSchemaVersion { get; set; }

    [Id(2)]
    public string? LegacyClrTypeName { get; set; }
}
