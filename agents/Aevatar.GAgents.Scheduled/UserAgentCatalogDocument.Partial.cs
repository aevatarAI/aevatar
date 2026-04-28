using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Scheduled;

public sealed partial class UserAgentCatalogDocument : IProjectionReadModel<UserAgentCatalogDocument>
{
    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtc != null ? UpdatedAtUtc.ToDateTimeOffset() : default;
        set => UpdatedAtUtc = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }

    public DateTimeOffset CreatedAt
    {
        get => CreatedAtUtc != null ? CreatedAtUtc.ToDateTimeOffset() : default;
        set => CreatedAtUtc = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}
