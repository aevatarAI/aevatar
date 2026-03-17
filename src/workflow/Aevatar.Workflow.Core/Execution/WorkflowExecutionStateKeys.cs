namespace Aevatar.Workflow.Core.Execution;

internal static class WorkflowExecutionStateKeys
{
    public static string Engine(string name) =>
        $"engine/{Normalize(name)}";

    public static string Component(string name) =>
        $"components/{Normalize(name)}";

    public static string Step(string stepId) =>
        $"steps/{Normalize(stepId)}";

    public static string StepPrefix => "steps/";

    private static string Normalize(string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("State scope key requires a non-empty value.", nameof(value));

        return normalized;
    }
}
