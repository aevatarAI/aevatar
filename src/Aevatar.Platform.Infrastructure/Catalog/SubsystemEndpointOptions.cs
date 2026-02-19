namespace Aevatar.Platform.Infrastructure.Catalog;

public sealed class SubsystemEndpointOptions
{
    /// <summary>
    /// Legacy field (optional): base URL for workflow subsystem.
    /// </summary>
    public string? WorkflowBaseUrl { get; set; }

    /// <summary>
    /// Legacy field (optional): base URL for maker subsystem.
    /// </summary>
    public string? MakerBaseUrl { get; set; }

    /// <summary>
    /// Subsystem registrations (preferred).
    /// </summary>
    public List<SubsystemEndpointRegistration> Subsystems { get; set; } = [];

    public IReadOnlyList<SubsystemEndpointRegistration> ResolveRegistrations()
    {
        if (Subsystems.Count > 0)
            return Subsystems;

        var workflowBaseUrl = string.IsNullOrWhiteSpace(WorkflowBaseUrl)
            ? "http://localhost:5201"
            : WorkflowBaseUrl;
        var makerBaseUrl = string.IsNullOrWhiteSpace(MakerBaseUrl)
            ? "http://localhost:5202"
            : MakerBaseUrl;

        return
        [
            new SubsystemEndpointRegistration
            {
                Subsystem = "workflow",
                AgentType = "WorkflowGAgent",
                BaseUrl = workflowBaseUrl,
                CommandEndpointPath = "/api/commands",
                QueryEndpointPath = "/api/actors/{actorId}",
                StreamEndpointPath = "/api/ws/chat",
                CommandResolveTemplate = "/api/{path}",
                QueryResolveTemplate = "/api/{path}",
            },
            new SubsystemEndpointRegistration
            {
                Subsystem = "maker",
                AgentType = "MakerWorkflowGAgent",
                BaseUrl = makerBaseUrl,
                CommandEndpointPath = "/api/maker/runs",
                QueryEndpointPath = "/api/maker/runs/{actorId}",
                StreamEndpointPath = string.Empty,
                CommandResolveTemplate = "/api/{path}",
                QueryResolveTemplate = "/api/{path}",
            },
        ];
    }
}

public sealed class SubsystemEndpointRegistration
{
    public string Subsystem { get; set; } = string.Empty;
    public string AgentType { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Exposed capability endpoint path (for catalog list).
    /// </summary>
    public string CommandEndpointPath { get; set; } = string.Empty;

    /// <summary>
    /// Exposed capability endpoint path (for catalog list).
    /// </summary>
    public string QueryEndpointPath { get; set; } = string.Empty;

    /// <summary>
    /// Exposed capability endpoint path (for catalog list).
    /// </summary>
    public string StreamEndpointPath { get; set; } = string.Empty;

    /// <summary>
    /// Resolve template. Supports {path}, default "/api/{path}".
    /// </summary>
    public string CommandResolveTemplate { get; set; } = "/api/{path}";

    /// <summary>
    /// Resolve template. Supports {path}, default "/api/{path}".
    /// </summary>
    public string QueryResolveTemplate { get; set; } = "/api/{path}";
}
