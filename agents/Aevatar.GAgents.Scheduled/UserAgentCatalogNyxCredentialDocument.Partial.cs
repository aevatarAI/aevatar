using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Scheduled;

public sealed partial class UserAgentCatalogNyxCredentialDocument : IProjectionReadModel<UserAgentCatalogNyxCredentialDocument>
{
    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtc != null ? UpdatedAtUtc.ToDateTimeOffset() : default;
        set => UpdatedAtUtc = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}
