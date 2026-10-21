namespace Aevatar.Workflow.Abstractions.Primitives;

/// <summary>Authoring contract contributed by a workflow module, independent of any client UI.</summary>
public sealed record WorkflowPrimitiveDefinition(
    string Description,
    IReadOnlyList<WorkflowPrimitiveParameterDefinition> Parameters);

public enum WorkflowPrimitiveParameterType
{
    String,
    Integer,
    Number,
    Boolean,
    Array,
    Object,
}

public enum WorkflowPrimitiveParameterControl
{
    Auto,
    Text,
    Textarea,
    Number,
    Boolean,
    Select,
    Json,
}

public sealed record WorkflowPrimitiveParameterDefinition(
    string Name,
    WorkflowPrimitiveParameterType Type,
    string Label,
    string Description = "",
    bool Required = false,
    string DefaultValue = "",
    IReadOnlyList<string>? EnumValues = null,
    WorkflowPrimitiveParameterControl Control = WorkflowPrimitiveParameterControl.Auto,
    string Placeholder = "",
    double? Minimum = null,
    double? Maximum = null,
    bool UsesPrimitiveNames = false);
