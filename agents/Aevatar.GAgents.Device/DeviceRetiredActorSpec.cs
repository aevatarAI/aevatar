using Aevatar.Foundation.Abstractions.Maintenance;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.Device;

/// <summary>
/// Retired-actor declaration for the device-registration surface.
/// </summary>
public sealed class DeviceRetiredActorSpec : RetiredActorSpec
{
    public override string SpecId => "device";

    // Retire only the legacy actor body left by the deleted Aevatar.GAgents.ChannelRuntime
    // assembly. The durable materialization scope MUST NOT be a retired target: its runtime
    // kind is derived from the context's simple type name, so the legacy and current
    // DeviceRegistrationMaterializationContext collapse to the same
    // "projection.materialization-scope.device-registration-materialization-context" kind.
    // Retiring it would destroy the live projection scope on every startup cleanup pass and
    // leave the device-registration read model un-materialized (#1763 regression).
    public override IReadOnlyList<RetiredActorTarget> Targets { get; } =
    [
        new(
            DeviceRegistrationGAgent.WellKnownId,
            ["channel-runtime.device-registration"],
            CleanupReadModels: true),
    ];

    public override Task DeleteReadModelsForActorAsync(
        IServiceProvider services,
        string actorId,
        CancellationToken ct) =>
        RetiredActorReadModelHelpers.DeleteByActorAsync<DeviceRegistrationDocument>(
            services, actorId, ct);
}
