namespace Aevatar.Workflow.Infrastructure.Workflows;

public static class WorkflowLibraryClassifier
{
    private static readonly IReadOnlyDictionary<string, int> LegacyWorkflowIndexes =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["transform"] = 1,
            ["guard"] = 2,
            ["conditional"] = 3,
            ["switch"] = 4,
            ["assign"] = 5,
            ["retrieve_facts"] = 6,
            ["pipeline"] = 7,
            ["llm_call"] = 8,
            ["llm_chain"] = 9,
            ["parallel"] = 10,
            ["race"] = 11,
            ["map_reduce"] = 12,
            ["foreach"] = 13,
            ["evaluate"] = 14,
            ["reflect"] = 15,
            ["cache"] = 16,
            ["demo_template"] = 17,
            ["demo_csv_markdown"] = 18,
            ["demo_json_pick"] = 19,
            ["role_event_module_template"] = 20,
            ["role_event_module_csv_markdown"] = 21,
            ["role_event_module_json_pick"] = 22,
            ["role_event_module_multiplex_template"] = 23,
            ["role_event_module_multiplex_csv"] = 24,
            ["role_event_module_multiplex_json"] = 25,
            ["role_event_module_multi_role_chain"] = 26,
            ["role_event_module_extensions_template"] = 27,
            ["role_event_module_extensions_csv"] = 28,
            ["role_event_module_top_level_overrides_extensions"] = 29,
            ["role_event_module_extensions_multi_role_chain"] = 30,
            ["role_event_module_extensions_multiplex_json"] = 31,
            ["role_event_module_top_level_overrides_extensions_multiplex"] = 32,
            ["role_event_module_no_routes_template"] = 33,
            ["role_event_module_route_dsl_csv"] = 34,
            ["role_event_module_unknown_ignored_template"] = 35,
            ["mixed_step_json_pick_then_role_template"] = 36,
            ["mixed_step_csv_markdown_then_role_template"] = 37,
            ["mixed_step_template_then_role_csv_markdown"] = 38,
            ["human_input_basic_auto_resume"] = 39,
            ["human_approval_approved_auto_resume"] = 40,
            ["human_approval_rejected_fail_auto_resume"] = 41,
            ["human_approval_rejected_skip_auto_resume"] = 42,
            ["human_input_manual_triage"] = 43,
            ["wait_signal_manual_success"] = 44,
            ["wait_signal_timeout_failure"] = 45,
            ["human_approval_release_gate"] = 46,
            ["mixed_human_approval_wait_signal"] = 47,
            ["subworkflow_level1"] = 48,
            ["subworkflow_level2"] = 48,
            ["subworkflow_level3"] = 48,
            ["workflow_call_multilevel"] = 49,
            ["connector_cli_demo"] = 50,
            ["cli_call_alias"] = 51,
            ["foreach_llm_alias"] = 52,
            ["map_reduce_llm_alias"] = 53,
            ["emit_publish_demo"] = 54,
            ["tool_call_fallback_demo"] = 55,
            ["delay_checkpoint_demo"] = 56,
        };

    public static WorkflowLibraryClassification Classify(
        string workflowName,
        string sourceKind,
        string category)
    {
        var index = TryGetWorkflowIndex(workflowName);
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

    public static int? TryGetWorkflowIndex(string workflowName)
    {
        if (string.IsNullOrWhiteSpace(workflowName))
            return null;

        var normalizedName = workflowName.Trim();
        if (LegacyWorkflowIndexes.TryGetValue(normalizedName, out var knownIndex))
            return knownIndex;

        var span = normalizedName.AsSpan();
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
