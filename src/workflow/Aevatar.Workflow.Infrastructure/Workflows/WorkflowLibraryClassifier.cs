namespace Aevatar.Workflow.Infrastructure.Workflows;

public static class WorkflowLibraryClassifier
{
    public static WorkflowLibraryClassification Classify(
        string workflowName,
        string sourceKind,
        string category)
    {
        var index = TryParseWorkflowIndex(workflowName);
        var normalizedSource = sourceKind ?? string.Empty;

        if (string.Equals(normalizedSource, "home", StringComparison.OrdinalIgnoreCase))
        {
            return new WorkflowLibraryClassification(
                Group: "your-workflows",
                GroupLabel: "Your Workflows",
                SortOrder: index ?? 0,
                ShowInLibrary: true,
                IsPrimitiveExample: false,
                SourceLabel: "Saved");
        }

        if (string.Equals(normalizedSource, "cwd", StringComparison.OrdinalIgnoreCase))
        {
            return new WorkflowLibraryClassification(
                Group: "your-workflows",
                GroupLabel: "Your Workflows",
                SortOrder: index ?? 0,
                ShowInLibrary: true,
                IsPrimitiveExample: false,
                SourceLabel: "Workspace");
        }

        if (string.Equals(normalizedSource, "turing", StringComparison.OrdinalIgnoreCase))
        {
            var turingOrder = workflowName.Contains("counter", StringComparison.OrdinalIgnoreCase) ? 901
                : workflowName.Contains("minsky", StringComparison.OrdinalIgnoreCase) ? 902
                : 999;
            return new WorkflowLibraryClassification(
                Group: "advanced-patterns",
                GroupLabel: "Advanced Patterns",
                SortOrder: turingOrder,
                ShowInLibrary: true,
                IsPrimitiveExample: false,
                SourceLabel: "Advanced");
        }

        if (index is >= 1 and <= 7)
        {
            return new WorkflowLibraryClassification(
                Group: "primitive-examples",
                GroupLabel: "Primitive Mini Examples",
                SortOrder: index.Value,
                ShowInLibrary: false,
                IsPrimitiveExample: true,
                SourceLabel: "Mini");
        }

        if (index is >= 8 and <= 16)
        {
            return new WorkflowLibraryClassification(
                Group: "ai-workflows",
                GroupLabel: "AI & Human Workflows",
                SortOrder: index.Value,
                ShowInLibrary: true,
                IsPrimitiveExample: false,
                SourceLabel: "Starter");
        }

        if (index is >= 39 and <= 47)
        {
            return new WorkflowLibraryClassification(
                Group: "ai-workflows",
                GroupLabel: "AI & Human Workflows",
                SortOrder: index.Value,
                ShowInLibrary: true,
                IsPrimitiveExample: false,
                SourceLabel: "Interactive");
        }

        if (index is >= 50 and <= 67)
        {
            return new WorkflowLibraryClassification(
                Group: "integration-workflows",
                GroupLabel: "Integrations & Tools",
                SortOrder: index.Value,
                ShowInLibrary: true,
                IsPrimitiveExample: false,
                SourceLabel: "Integration");
        }

        if (index is >= 17 and <= 38 or 48 or 49)
        {
            return new WorkflowLibraryClassification(
                Group: "advanced-patterns",
                GroupLabel: "Advanced Patterns",
                SortOrder: index.Value,
                ShowInLibrary: true,
                IsPrimitiveExample: false,
                SourceLabel: "Advanced");
        }

        var sourceLabel = normalizedSource switch
        {
            "builtin" => "Built-in",
            "app" => "Bundled",
            "repo" => "Starter",
            "demo" => "Starter",
            _ => "Workflow",
        };

        return new WorkflowLibraryClassification(
            Group: "starter-workflows",
            GroupLabel: "Starter Workflows",
            SortOrder: index ?? (string.Equals(category, "llm", StringComparison.OrdinalIgnoreCase) ? 100 : 200),
            ShowInLibrary: true,
            IsPrimitiveExample: false,
            SourceLabel: sourceLabel);
    }

    private static int? TryParseWorkflowIndex(string workflowName)
    {
        if (string.IsNullOrWhiteSpace(workflowName))
            return null;

        var span = workflowName.AsSpan().Trim();
        var index = 0;
        while (index < span.Length && char.IsDigit(span[index]))
            index++;

        if (index == 0 || index >= span.Length || span[index] != '_')
            return null;

        return int.TryParse(span[..index], out var value) ? value : null;
    }
}

public sealed record WorkflowLibraryClassification(
    string Group,
    string GroupLabel,
    int SortOrder,
    bool ShowInLibrary,
    bool IsPrimitiveExample,
    string SourceLabel);
