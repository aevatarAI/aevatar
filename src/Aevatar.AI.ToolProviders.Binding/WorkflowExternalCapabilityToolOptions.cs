namespace Aevatar.AI.ToolProviders.Binding;

/// <summary>Configuration for workflow external-capability authoring tools.</summary>
public sealed class WorkflowExternalCapabilityToolOptions
{
    /// <summary>Maximum exact capability operations returned by one list call.</summary>
    public int MaxListResults { get; set; } = 100;
}
