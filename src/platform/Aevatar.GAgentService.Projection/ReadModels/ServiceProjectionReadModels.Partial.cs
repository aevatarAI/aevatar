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
