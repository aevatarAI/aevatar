using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgentService.Projection.ReadModels;

public sealed partial class NyxIdAuthorizationCatalogDocument
    : IProjectionReadModel<NyxIdAuthorizationCatalogDocument>
{
    public DateTimeOffset UpdatedAt
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }

    public DateTimeOffset ObservedAt
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(ObservedAtUtcValue);
        set => ObservedAtUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }

    public DateTimeOffset FreshUntil
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(FreshUntilUtcValue);
        set => FreshUntilUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }

    public DateTimeOffset EvaluatedAt
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(EvaluatedAtUtcValue);
        set => EvaluatedAtUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }

    public DateTimeOffset? InvalidatedAt
    {
        get => ServiceProjectionReadModelSupport.ToNullableDateTimeOffset(InvalidatedAtUtcValue);
        set => InvalidatedAtUtcValue = ServiceProjectionReadModelSupport.ToNullableTimestamp(value);
    }

    public DateTimeOffset? LastRefreshFailedAt
    {
        get => ServiceProjectionReadModelSupport.ToNullableDateTimeOffset(LastRefreshFailedAtUtcValue);
        set => LastRefreshFailedAtUtcValue = ServiceProjectionReadModelSupport.ToNullableTimestamp(value);
    }

    public DateTimeOffset? ActivatedAt
    {
        get => ServiceProjectionReadModelSupport.ToNullableDateTimeOffset(ActivatedAtUtcValue);
        set => ActivatedAtUtcValue = ServiceProjectionReadModelSupport.ToNullableTimestamp(value);
    }

    public DateTimeOffset? CleanedAt
    {
        get => ServiceProjectionReadModelSupport.ToNullableDateTimeOffset(CleanedAtUtcValue);
        set => CleanedAtUtcValue = ServiceProjectionReadModelSupport.ToNullableTimestamp(value);
    }
}
