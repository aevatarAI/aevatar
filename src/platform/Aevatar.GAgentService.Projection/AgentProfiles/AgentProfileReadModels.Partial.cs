using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Projection.AgentProfiles;

public sealed partial class AgentProfileNamespaceCatalogDocument
    : IProjectionReadModel<AgentProfileNamespaceCatalogDocument>
{
    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtcValue?.ToDateTimeOffset() ?? default;
        set => UpdatedAtUtcValue = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}

public sealed partial class AgentProfileOwnerDocument
    : IProjectionReadModel<AgentProfileOwnerDocument>
{
    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtcValue?.ToDateTimeOffset() ?? default;
        set => UpdatedAtUtcValue = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}

public sealed partial class AgentProfileExecutionDocument
    : IProjectionReadModel<AgentProfileExecutionDocument>
{
    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtcValue?.ToDateTimeOffset() ?? default;
        set => UpdatedAtUtcValue = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}
