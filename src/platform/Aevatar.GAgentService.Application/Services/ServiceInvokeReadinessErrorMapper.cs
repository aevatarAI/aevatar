using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Application.Services;

public sealed class ServiceInvokeReadinessException : InvalidOperationException
{
    public ServiceInvokeReadinessException(
        string message,
        ServiceInvokeReadinessSnapshot snapshot)
        : base(message)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public ServiceInvokeReadinessSnapshot Snapshot { get; }
}

public sealed class ServiceInvokeReadinessErrorMapper
{
    public ServiceInvokeErrorBody Map(ServiceInvokeReadinessException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Map(exception.Snapshot, exception.Message);
    }

    public ServiceInvokeErrorBody Map(ServiceInvokeReadinessSnapshot snapshot, string message)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new ServiceInvokeErrorBody(
            Code: "SERVICE_INVOKE_UNAVAILABLE",
            Message: message ?? string.Empty,
            ReadinessStatus: snapshot.ReadinessStatus.ToString(),
            Reason: snapshot.UnavailableReason == ServiceInvokeUnavailableReason.Unspecified
                ? null
                : snapshot.UnavailableReason.ToString(),
            ServiceKey: snapshot.ServiceKey,
            RevisionId: snapshot.SelectedRevisionId,
            EndpointId: snapshot.EndpointId,
            DeploymentId: snapshot.SelectedDeploymentId,
            ObservedAt: snapshot.ObservedAt,
            AggregateStateVersion: snapshot.AggregateStateVersion,
            SourceCatalogVersion: snapshot.SourceCatalogVersion,
            SourceServingVersion: snapshot.SourceServingVersion,
            SourceRevisionVersion: snapshot.SourceRevisionVersion);
    }
}

public sealed record ServiceInvokeErrorBody(
    string Code,
    string Message,
    string ReadinessStatus,
    string? Reason,
    string ServiceKey,
    string RevisionId,
    string EndpointId,
    string DeploymentId,
    DateTimeOffset ObservedAt,
    long AggregateStateVersion,
    long SourceCatalogVersion,
    long SourceServingVersion,
    long SourceRevisionVersion);
