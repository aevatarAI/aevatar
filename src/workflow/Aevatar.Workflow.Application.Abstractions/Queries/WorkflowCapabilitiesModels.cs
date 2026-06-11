namespace Aevatar.Workflow.Application.Abstractions.Queries;

// Refactor (iter72/cluster-072-workflow-closed-world-false-capability):
//   Old pattern: ClosedWorldBlocked flag retained as always-false compatibility field
//   New principle: Removed dead capability flag; output describes available primitives only
public sealed class WorkflowCapabilitiesDocument
{
    public string SchemaVersion { get; set; } = "capabilities.v1";

    public DateTimeOffset GeneratedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ProjectionWatermark { get; set; }

    public List<WorkflowPrimitiveCapability> Primitives { get; set; } = [];

    public List<WorkflowConnectorCapability> Connectors { get; set; } = [];

    public List<WorkflowCapabilityWorkflow> Workflows { get; set; } = [];
}

// Refactor (iter72/cluster-072-workflow-closed-world-false-capability):
//   Old pattern: ClosedWorldBlocked flag retained as always-false compatibility field
//   New principle: Removed dead capability flag; output describes available primitives only
public sealed class WorkflowPrimitiveCapability
{
    public string Name { get; set; } = string.Empty;

    public List<string> Aliases { get; set; } = [];

    public string Category { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string RuntimeModule { get; set; } = string.Empty;

    public List<WorkflowPrimitiveParameterCapability> Parameters { get; set; } = [];
}

public sealed class WorkflowPrimitiveParameterCapability
{
    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = "string";

    public bool Required { get; set; }

    public string Description { get; set; } = string.Empty;

    public string Default { get; set; } = string.Empty;

    public List<string> Enum { get; set; } = [];
}

public sealed class WorkflowConnectorCapability
{
    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public int TimeoutMs { get; set; }

    public int Retry { get; set; }

    public List<string> AllowedInputKeys { get; set; } = [];

    public List<string> AllowedOperations { get; set; } = [];

    public List<string> FixedArguments { get; set; } = [];
}

public sealed class WorkflowCapabilityWorkflow
{
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public bool ClosedWorldMode { get; set; }

    public bool RequiresLlmProvider { get; set; }

    public List<string> Primitives { get; set; } = [];

    public List<string> RequiredConnectors { get; set; } = [];

    public List<string> WorkflowCalls { get; set; } = [];

    public List<WorkflowCapabilityWorkflowStep> Steps { get; set; } = [];
    public long AuthorityStateVersion { get; set; }
    public DateTimeOffset ProjectionWatermark { get; set; }
}

public sealed class WorkflowCapabilityWorkflowStep
{
    public string Id { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string Next { get; set; } = string.Empty;
}
