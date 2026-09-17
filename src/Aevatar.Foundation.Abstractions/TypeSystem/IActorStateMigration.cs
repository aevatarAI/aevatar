using Aevatar.Foundation.Abstractions.Runtime;
using Google.Protobuf;

namespace Aevatar.Foundation.Abstractions.TypeSystem;

/// <summary>
/// Declares which actor kind owns a typed state migration. A migration class
/// must have a public parameterless constructor and implement exactly one
/// <see cref="IActorStateMigration{TState}"/> contract.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ActorStateMigrationAttribute : Attribute
{
    public ActorStateMigrationAttribute(string agentKind)
    {
        AgentKindToken.Validate(agentKind, nameof(agentKind));
        AgentKind = agentKind;
    }

    public string AgentKind { get; }

    /// <summary>
    /// Exact fleet capability which must be open before this migration step
    /// may be durably adopted by an actor.
    /// </summary>
    public RuntimeFleetCapability RequiredCapability { get; set; }

    /// <summary>
    /// Stable contract identity advertised by every active runtime reader.
    /// </summary>
    public string RequiredContractId { get; set; } = string.Empty;

    /// <summary>
    /// Minimum version of <see cref="RequiredContractId"/> required by this
    /// migration step.
    /// </summary>
    public int RequiredContractVersion { get; set; }

    /// <summary>
    /// Exact gate status accepted as migration evidence. OPEN is the default; QUIESCED is valid
    /// only for one-way bridge migrations whose historical evidence is intentionally terminal.
    /// </summary>
    public RuntimeFleetCapabilityGateStatus RequiredGateStatus { get; set; } =
        RuntimeFleetCapabilityGateStatus.Open;
}

/// <summary>
/// One pure, typed, consecutive state-schema migration step. Implementations
/// receive only the historical protobuf state and may not perform I/O.
/// </summary>
public interface IActorStateMigration<TState>
    where TState : class, IMessage<TState>, new()
{
    int FromStateVersion { get; }

    int ToStateVersion { get; }

    TState Apply(TState state);
}

/// <summary>
/// Runtime-compiled migration step attached to one registered implementation.
/// </summary>
public sealed record ActorStateMigrationStep(
    int FromStateVersion,
    int ToStateVersion,
    Type StateContractType,
    Type MigrationType,
    Func<byte[], byte[]> Apply,
    RuntimeFleetCapability RequiredCapability = RuntimeFleetCapability.Unspecified,
    string RequiredContractId = "",
    int RequiredContractVersion = 0,
    RuntimeFleetCapabilityGateStatus RequiredGateStatus = RuntimeFleetCapabilityGateStatus.Open);
