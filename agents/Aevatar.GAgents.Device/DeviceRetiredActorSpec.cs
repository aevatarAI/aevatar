using Aevatar.Foundation.Abstractions.Maintenance;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.Device;

/// <summary>
/// Retired-actor declaration for the device-registration surface previously
/// hosted by the deleted <c>Aevatar.GAgents.ChannelRuntime</c> assembly.
/// </summary>
public sealed class DeviceRetiredActorSpec : RetiredActorSpec
{
    public override string SpecId => "device";

    public override IReadOnlyList<RetiredActorTarget> Targets { get; } =
    [
        new(
            DeviceRegistrationGAgent.WellKnownId,
            ["Aevatar.GAgents.ChannelRuntime.DeviceRegistrationGAgent"],
            CleanupReadModels: true),
        new(
            $"projection.durable.scope:device-registration:{DeviceRegistrationGAgent.WellKnownId}",
            ["Aevatar.GAgents.ChannelRuntime.DeviceRegistrationMaterializationContext"],
            SourceStreamId: DeviceRegistrationGAgent.WellKnownId),
    ];

    public override Task DeleteReadModelsForActorAsync(
        IServiceProvider services,
        string actorId,
        CancellationToken ct) =>
        RetiredActorReadModelHelpers.DeleteByActorAsync<DeviceRegistrationDocument>(
            services, actorId, ct);
}
