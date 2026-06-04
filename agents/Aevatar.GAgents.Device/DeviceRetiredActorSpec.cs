using Aevatar.Foundation.Abstractions.Maintenance;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.Device;

/// <summary>
/// Retired-actor declaration for the device-registration surface.
/// </summary>
public sealed class DeviceRetiredActorSpec : RetiredActorSpec
{
    public override string SpecId => "device";

    public override IReadOnlyList<RetiredActorTarget> Targets { get; } =
    [
        new(
            DeviceRegistrationGAgent.WellKnownId,
            ["channel-runtime.device-registration"],
            CleanupReadModels: true),
        new(
            $"projection.durable.scope:device-registration:{DeviceRegistrationGAgent.WellKnownId}",
            ["projection.materialization-scope.device-registration-materialization-context"],
            SourceStreamId: DeviceRegistrationGAgent.WellKnownId),
    ];

    public override Task DeleteReadModelsForActorAsync(
        IServiceProvider services,
        string actorId,
        CancellationToken ct) =>
        RetiredActorReadModelHelpers.DeleteByActorAsync<DeviceRegistrationDocument>(
            services, actorId, ct);
}
