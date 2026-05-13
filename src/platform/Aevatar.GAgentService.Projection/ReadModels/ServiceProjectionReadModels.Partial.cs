using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Projection.ReadModels;

public sealed partial class ServiceCatalogReadModel : IProjectionReadModel<ServiceCatalogReadModel>
{
    public DateTimeOffset UpdatedAt
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }

    public IList<ServiceCatalogEndpointReadModel> Endpoints
    {
        get => EndpointEntries;
        set => ServiceProjectionReadModelSupport.ReplaceCollection(EndpointEntries, value);
    }

    public IList<string> PolicyIds
    {
        get => PolicyIdEntries;
        set => ServiceProjectionReadModelSupport.ReplaceCollection(PolicyIdEntries, value);
    }
}

public sealed partial class ServiceDeploymentCatalogReadModel : IProjectionReadModel<ServiceDeploymentCatalogReadModel>
{
    public DateTimeOffset UpdatedAt
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }

    public IList<ServiceDeploymentReadModel> Deployments
    {
        get => DeploymentEntries;
        set => ServiceProjectionReadModelSupport.ReplaceCollection(DeploymentEntries, value);
    }
}

public sealed partial class ServiceDeploymentReadModel
{
    public DateTimeOffset? ActivatedAt
    {
        get => ServiceProjectionReadModelSupport.ToNullableDateTimeOffset(ActivatedAtUtcValue);
        set => ActivatedAtUtcValue = ServiceProjectionReadModelSupport.ToNullableTimestamp(value);
    }

    public DateTimeOffset UpdatedAt
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }
}

public sealed partial class ServiceRevisionCatalogReadModel : IProjectionReadModel<ServiceRevisionCatalogReadModel>
{
    public DateTimeOffset UpdatedAt
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }

    public IList<ServiceRevisionEntryReadModel> Revisions
    {
        get => RevisionEntries;
        set => ServiceProjectionReadModelSupport.ReplaceCollection(RevisionEntries, value);
    }
}

public sealed partial class ServiceRevisionEntryReadModel
{
    public DateTimeOffset? CreatedAt
    {
        get => ServiceProjectionReadModelSupport.ToNullableDateTimeOffset(CreatedAtUtcValue);
        set => CreatedAtUtcValue = ServiceProjectionReadModelSupport.ToNullableTimestamp(value);
    }

    public DateTimeOffset? PreparedAt
    {
        get => ServiceProjectionReadModelSupport.ToNullableDateTimeOffset(PreparedAtUtcValue);
        set => PreparedAtUtcValue = ServiceProjectionReadModelSupport.ToNullableTimestamp(value);
    }

    public DateTimeOffset? PublishedAt
    {
        get => ServiceProjectionReadModelSupport.ToNullableDateTimeOffset(PublishedAtUtcValue);
        set => PublishedAtUtcValue = ServiceProjectionReadModelSupport.ToNullableTimestamp(value);
    }

    public DateTimeOffset? RetiredAt
    {
        get => ServiceProjectionReadModelSupport.ToNullableDateTimeOffset(RetiredAtUtcValue);
        set => RetiredAtUtcValue = ServiceProjectionReadModelSupport.ToNullableTimestamp(value);
    }

    public IList<ServiceCatalogEndpointReadModel> Endpoints
    {
        get => EndpointEntries;
        set => ServiceProjectionReadModelSupport.ReplaceCollection(EndpointEntries, value);
    }
}

public sealed partial class ServiceRolloutReadModel : IProjectionReadModel<ServiceRolloutReadModel>
{
    public DateTimeOffset? StartedAt
    {
        get => ServiceProjectionReadModelSupport.ToNullableDateTimeOffset(StartedAtUtcValue);
        set => StartedAtUtcValue = ServiceProjectionReadModelSupport.ToNullableTimestamp(value);
    }

    public DateTimeOffset UpdatedAt
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }

    public IList<ServiceRolloutStageReadModel> Stages
    {
        get => StageEntries;
        set => ServiceProjectionReadModelSupport.ReplaceCollection(StageEntries, value);
    }

    public IList<ServiceServingTargetReadModel> BaselineTargets
    {
        get => BaselineTargetEntries;
        set => ServiceProjectionReadModelSupport.ReplaceCollection(BaselineTargetEntries, value);
    }
}

public sealed partial class ServiceRolloutStageReadModel
{
    public IList<ServiceServingTargetReadModel> Targets
    {
        get => TargetEntries;
        set => ServiceProjectionReadModelSupport.ReplaceCollection(TargetEntries, value);
    }
}

public sealed partial class ServiceRolloutCommandObservationReadModel : IProjectionReadModel<ServiceRolloutCommandObservationReadModel>
{
    public DateTimeOffset UpdatedAt
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(ObservedAtUtcValue);
        set => ObservedAtUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }

    public DateTimeOffset ObservedAt
    {
        get => UpdatedAt;
        set => UpdatedAt = value;
    }
}

public sealed partial class ServiceServingTargetReadModel
{
    public IList<string> EnabledEndpointIds
    {
        get => EnabledEndpointIdEntries;
        set => ServiceProjectionReadModelSupport.ReplaceCollection(EnabledEndpointIdEntries, value);
    }
}

public sealed partial class ServiceServingSetReadModel : IProjectionReadModel<ServiceServingSetReadModel>
{
    public DateTimeOffset UpdatedAt
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }

    public IList<ServiceServingTargetReadModel> Targets
    {
        get => TargetEntries;
        set => ServiceProjectionReadModelSupport.ReplaceCollection(TargetEntries, value);
    }
}

public sealed partial class ServiceTrafficViewReadModel : IProjectionReadModel<ServiceTrafficViewReadModel>
{
    public DateTimeOffset UpdatedAt
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }

    public IList<ServiceTrafficEndpointReadModel> Endpoints
    {
        get => EndpointEntries;
        set => ServiceProjectionReadModelSupport.ReplaceCollection(EndpointEntries, value);
    }
}

public sealed partial class ServiceTrafficEndpointReadModel
{
    public IList<ServiceTrafficTargetReadModel> Targets
    {
        get => TargetEntries;
        set => ServiceProjectionReadModelSupport.ReplaceCollection(TargetEntries, value);
    }
}

public sealed partial class ServiceRunCurrentStateReadModel : IProjectionReadModel<ServiceRunCurrentStateReadModel>
{
    public DateTimeOffset CreatedAt
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(CreatedAtUtcValue);
        set => CreatedAtUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }

    public DateTimeOffset UpdatedAt
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }
}

public sealed partial class ResponseSessionCurrentStateReadModel : IProjectionReadModel<ResponseSessionCurrentStateReadModel>
{
    public DateTimeOffset CreatedAt
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(CreatedAtUtcValue);
        set => CreatedAtUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }

    public DateTimeOffset UpdatedAt
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }

    public DateTimeOffset? CancelledAt
    {
        get => ServiceProjectionReadModelSupport.ToNullableDateTimeOffset(CancelledAtUtcValue);
        set => CancelledAtUtcValue = ServiceProjectionReadModelSupport.ToNullableTimestamp(value);
    }

    public IList<ResponseSessionForwardedToolCallReadModel> ForwardedToolCalls
    {
        get => ForwardedToolCallEntries;
        set => ServiceProjectionReadModelSupport.ReplaceCollection(ForwardedToolCallEntries, value);
    }
}

public sealed partial class ResponseSessionForwardedToolCallReadModel
{
    public DateTimeOffset? Expiry
    {
        get => ServiceProjectionReadModelSupport.ToNullableDateTimeOffset(ExpiryUtcValue);
        set => ExpiryUtcValue = ServiceProjectionReadModelSupport.ToNullableTimestamp(value);
    }

    public DateTimeOffset? EmittedAt
    {
        get => ServiceProjectionReadModelSupport.ToNullableDateTimeOffset(EmittedAtUtcValue);
        set => EmittedAtUtcValue = ServiceProjectionReadModelSupport.ToNullableTimestamp(value);
    }

    public DateTimeOffset? ReceivedAt
    {
        get => ServiceProjectionReadModelSupport.ToNullableDateTimeOffset(ReceivedAtUtcValue);
        set => ReceivedAtUtcValue = ServiceProjectionReadModelSupport.ToNullableTimestamp(value);
    }

    public DateTimeOffset? ResolvedAt
    {
        get => ServiceProjectionReadModelSupport.ToNullableDateTimeOffset(ResolvedAtUtcValue);
        set => ResolvedAtUtcValue = ServiceProjectionReadModelSupport.ToNullableTimestamp(value);
    }
}

public sealed partial class ResponsesAgentToolStateCurrentStateReadModel
    : IProjectionReadModel<ResponsesAgentToolStateCurrentStateReadModel>
{
    public DateTimeOffset CreatedAt
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(CreatedAtUtcValue);
        set => CreatedAtUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }

    public DateTimeOffset UpdatedAt
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }

    public IList<ResponsesTodoItemReadModel> Todos
    {
        get => TodoItemEntries;
        set => ServiceProjectionReadModelSupport.ReplaceCollection(TodoItemEntries, value);
    }

    public IList<ResponsesTaskTraceReadModel> Tasks
    {
        get => TaskTraceEntries;
        set => ServiceProjectionReadModelSupport.ReplaceCollection(TaskTraceEntries, value);
    }

    public IList<ResponsesWebTraceReadModel> WebTraces
    {
        get => WebTraceEntries;
        set => ServiceProjectionReadModelSupport.ReplaceCollection(WebTraceEntries, value);
    }

    public IList<ResponsesWebCacheEntryReadModel> WebCacheEntries
    {
        get => WebCacheEntryEntries;
        set => ServiceProjectionReadModelSupport.ReplaceCollection(WebCacheEntryEntries, value);
    }
}

public sealed partial class ResponsesTodoItemReadModel
{
    public DateTimeOffset CreatedAt
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(CreatedAtUtcValue);
        set => CreatedAtUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }

    public DateTimeOffset UpdatedAt
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }
}

public sealed partial class ResponsesTaskTraceReadModel
{
    public DateTimeOffset CreatedAt
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(CreatedAtUtcValue);
        set => CreatedAtUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }

    public DateTimeOffset UpdatedAt
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }
}

public sealed partial class ResponsesWebTraceReadModel
{
    public DateTimeOffset ObservedAt
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(ObservedAtUtcValue);
        set => ObservedAtUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }
}

public sealed partial class ResponsesWebCacheEntryReadModel
{
    public DateTimeOffset CachedAt
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(CachedAtUtcValue);
        set => CachedAtUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }

    public DateTimeOffset? LastHitAt
    {
        get => ServiceProjectionReadModelSupport.ToNullableDateTimeOffset(LastHitAtUtcValue);
        set => LastHitAtUtcValue = ServiceProjectionReadModelSupport.ToNullableTimestamp(value);
    }
}

internal static class ServiceProjectionReadModelSupport
{
    public static Timestamp ToTimestamp(DateTimeOffset value) =>
        Timestamp.FromDateTimeOffset(value.ToUniversalTime());

    public static Timestamp? ToNullableTimestamp(DateTimeOffset? value) =>
        value.HasValue ? Timestamp.FromDateTimeOffset(value.Value.ToUniversalTime()) : null;

    public static DateTimeOffset ToDateTimeOffset(Timestamp? value) =>
        value == null ? default : value.ToDateTimeOffset();

    public static DateTimeOffset? ToNullableDateTimeOffset(Timestamp? value) =>
        value == null ? null : value.ToDateTimeOffset();

    public static void ReplaceCollection<T>(RepeatedField<T> target, IEnumerable<T>? source)
    {
        target.Clear();
        if (source != null)
            target.Add(source);
    }
}
