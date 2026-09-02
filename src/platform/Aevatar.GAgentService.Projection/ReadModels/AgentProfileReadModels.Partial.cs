using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgentService.Projection.ReadModels;

public sealed partial class AgentProfileCatalogReadModel : IProjectionReadModel<AgentProfileCatalogReadModel>
{
    public DateTimeOffset UpdatedAt
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }
}

public sealed partial class AgentProfileManagementReadModel : IProjectionReadModel<AgentProfileManagementReadModel>
{
    public DateTimeOffset UpdatedAt
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }
}

public sealed partial class AgentProfileExecutionReadModel : IProjectionReadModel<AgentProfileExecutionReadModel>
{
    public DateTimeOffset UpdatedAt
    {
        get => ServiceProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ServiceProjectionReadModelSupport.ToTimestamp(value);
    }
}
