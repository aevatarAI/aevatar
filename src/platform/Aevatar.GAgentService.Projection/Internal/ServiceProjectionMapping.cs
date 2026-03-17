using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Projection.Internal;

internal static class ServiceProjectionMapping
{
    public static string ServiceKey(ServiceIdentity? identity) =>
        identity == null ? string.Empty : ServiceKeys.Build(identity);

    public static DateTimeOffset FromTimestamp(Timestamp? timestamp, DateTimeOffset fallback) =>
        timestamp == null ? fallback : timestamp.ToDateTimeOffset();

    public static ServiceServingTargetReadModel ToServingTargetReadModel(ServiceServingTargetSpec source)
    {
        return new ServiceServingTargetReadModel
        {
            DeploymentId = source.DeploymentId ?? string.Empty,
            RevisionId = source.RevisionId ?? string.Empty,
            PrimaryActorId = source.PrimaryActorId ?? string.Empty,
            AllocationWeight = source.AllocationWeight,
            ServingState = source.ServingState.ToString(),
            EnabledEndpointIds = [.. source.EnabledEndpointIds],
        };
    }

    public static ServiceServingTargetSnapshot ToServingTargetSnapshot(ServiceServingTargetReadModel source)
    {
        return new ServiceServingTargetSnapshot(
            source.DeploymentId,
            source.RevisionId,
            source.PrimaryActorId,
            source.AllocationWeight,
            source.ServingState,
            [.. source.EnabledEndpointIds]);
    }

    public static ServiceTrafficTargetReadModel ToTrafficTargetReadModel(ServiceServingTargetSpec source)
    {
        return new ServiceTrafficTargetReadModel
        {
            DeploymentId = source.DeploymentId ?? string.Empty,
            RevisionId = source.RevisionId ?? string.Empty,
            PrimaryActorId = source.PrimaryActorId ?? string.Empty,
            AllocationWeight = source.AllocationWeight,
            ServingState = source.ServingState.ToString(),
        };
    }

    public static ServiceTrafficTargetSnapshot ToTrafficTargetSnapshot(ServiceTrafficTargetReadModel source)
    {
        return new ServiceTrafficTargetSnapshot(
            source.DeploymentId,
            source.RevisionId,
            source.PrimaryActorId,
            source.AllocationWeight,
            source.ServingState);
    }
}
