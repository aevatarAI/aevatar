namespace Aevatar.Workflow.Application.Abstractions.Authoring;

public sealed class PlaygroundWorkflowParseRequest
{
    public string Yaml { get; set; } = string.Empty;
}

public sealed class PlaygroundWorkflowParseResult
{
    public bool Valid { get; set; }

    public string? Error { get; set; }

    public List<string> Errors { get; set; } = [];

    public WorkflowAuthoringDefinition? Definition { get; set; }

    public List<WorkflowAuthoringEdge> Edges { get; set; } = [];
}

public sealed class WorkflowAuthoringDefinition
{
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public bool ClosedWorldMode { get; set; }

    public List<WorkflowAuthoringRole> Roles { get; set; } = [];

    public List<WorkflowAuthoringStep> Steps { get; set; } = [];
}

public sealed class WorkflowAuthoringRole
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string SystemPrompt { get; set; } = string.Empty;

    public string? Provider { get; set; }

    public string? Model { get; set; }

    public float? Temperature { get; set; }

    public int? MaxTokens { get; set; }

    public int? MaxToolRounds { get; set; }

    public int? MaxHistoryMessages { get; set; }

    public int? StreamBufferCapacity { get; set; }

    public List<string> EventModules { get; set; } = [];

    public string EventRoutes { get; set; } = string.Empty;

    public List<string> Connectors { get; set; } = [];
}

public sealed class WorkflowAuthoringStep
{
    public string Id { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string TargetRole { get; set; } = string.Empty;

    public Dictionary<string, string> Parameters { get; set; } = [];

    public string? Next { get; set; }

    public Dictionary<string, string> Branches { get; set; } = [];

    public List<WorkflowAuthoringStep> Children { get; set; } = [];

    public WorkflowAuthoringRetryPolicy? Retry { get; set; }

    public WorkflowAuthoringErrorPolicy? OnError { get; set; }

    public int? TimeoutMs { get; set; }
}

public sealed class WorkflowAuthoringRetryPolicy
{
    public int MaxAttempts { get; set; }

    public string Backoff { get; set; } = string.Empty;

    public int DelayMs { get; set; }
}

public sealed class WorkflowAuthoringErrorPolicy
{
    public string Strategy { get; set; } = string.Empty;

    public string? FallbackStep { get; set; }

    public string? DefaultOutput { get; set; }
}

public sealed class WorkflowAuthoringEdge
{
    public string From { get; set; } = string.Empty;

    public string To { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;
}

public sealed class PlaygroundWorkflowSaveRequest
{
    public string Yaml { get; set; } = string.Empty;

    public string? Filename { get; set; }

    public bool Overwrite { get; set; }
}

public sealed class PlaygroundWorkflowSaveResult
{
    public bool Saved { get; set; }

    public string Filename { get; set; } = string.Empty;

    public string SavedPath { get; set; } = string.Empty;

    public string WorkflowName { get; set; } = string.Empty;

    public bool Overwritten { get; set; }

    public string SavedSource { get; set; } = string.Empty;

    public string EffectiveSource { get; set; } = string.Empty;

    public string EffectivePath { get; set; } = string.Empty;
}

public sealed class WorkflowPrimitiveDescriptor
{
    public string Name { get; set; } = string.Empty;

    public List<string> Aliases { get; set; } = [];

    public string Category { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public List<WorkflowPrimitiveParameterDescriptor> Parameters { get; set; } = [];

    public List<string> ExampleWorkflows { get; set; } = [];
}

public sealed class WorkflowPrimitiveParameterDescriptor
{
    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = "string";

    public bool Required { get; set; }

    public string Description { get; set; } = string.Empty;

    public string Default { get; set; } = string.Empty;

    public List<string> EnumValues { get; set; } = [];
}

public sealed class WorkflowLlmStatus
{
    public bool Available { get; set; }

    public string? Provider { get; set; }

    public string? Model { get; set; }

    public List<string> Providers { get; set; } = [];
}
