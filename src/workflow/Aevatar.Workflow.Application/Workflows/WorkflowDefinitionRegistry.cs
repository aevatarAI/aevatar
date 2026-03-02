using System.Collections.Concurrent;
using Aevatar.Workflow.Application.Abstractions.Workflows;

namespace Aevatar.Workflow.Application.Workflows;

/// <summary>
/// Workflow YAML definition registry keyed by workflow name.
/// </summary>
public sealed class WorkflowDefinitionRegistry : IWorkflowDefinitionRegistry
{
    private readonly ConcurrentDictionary<string, string> _workflows = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Built-in direct workflow used for explicit direct runs and fallback recovery.
    /// </summary>
    public static string BuiltInDirectYaml { get; } = """
        name: direct
        description: Direct chat workflow for explicit direct runs and fallback recovery.
        roles:
          - id: assistant
            name: Assistant
            system_prompt: |
              You are a helpful assistant. Answer the user clearly and concisely.
        steps:
          - id: reply
            type: llm_call
            role: assistant
            parameters: {}
        """;

    /// <summary>
    /// Built-in auto-route workflow. Classifies user intent: simple questions get a
    /// direct LLM answer; complex requests trigger workflow YAML generation, multi-round
    /// human approval, and then dynamic execution of the confirmed workflow.
    /// </summary>
    public static string BuiltInAutoYaml { get; } = BuildAutoWorkflowYaml(
        workflowName: "auto",
        descriptionLine: "Auto-route: classify user intent, answer directly or generate a workflow YAML",
        descriptionLine2: "for human approval and dynamic execution.",
        approvalPrompt: "Please review the generated workflow YAML. Approve to execute, or reject with modification feedback.",
        approveTarget: "extract_and_execute",
        includeExecuteStep: true);

    /// <summary>
    /// Built-in auto-route workflow for review/finalization mode. It keeps the same
    /// planning/refinement behavior as <see cref="BuiltInAutoYaml"/>, but approval only
    /// finalizes YAML (manual run), instead of executing immediately.
    /// </summary>
    public static string BuiltInAutoReviewYaml { get; } = BuildAutoWorkflowYaml(
        workflowName: "auto_review",
        descriptionLine: "Auto-route: classify user intent, answer directly or generate a workflow YAML",
        descriptionLine2: "for human approval and manual finalization.",
        approvalPrompt: "Please review the generated workflow YAML. Approve to finalize YAML for manual run, or reject with modification feedback.",
        approveTarget: "done",
        includeExecuteStep: false);

    public void Register(string name, string yaml) => _workflows[name] = yaml;

    public string? GetYaml(string name) =>
        _workflows.GetValueOrDefault(name);

    public IReadOnlyList<string> GetNames() => _workflows.Keys.ToList();

    private static string BuildAutoWorkflowYaml(
        string workflowName,
        string descriptionLine,
        string descriptionLine2,
        string approvalPrompt,
        string approveTarget,
        bool includeExecuteStep)
    {
        var lines = new List<string>
        {
            $"name: {workflowName}",
            "description: >",
            $"  {descriptionLine}",
            $"  {descriptionLine2}",
            string.Empty,
            "roles:",
            "  - id: planner",
            "    name: Planner",
            "    system_prompt: |",
            "      You are an intelligent workflow planner. Analyze the user's request and decide:",
            string.Empty,
            "      1. **Simple question / conversation** — answer directly.",
            "         Do NOT wrap your answer in a YAML code block.",
            string.Empty,
            "      2. **Complex multi-step task** (multi-role collaboration, data pipelines,",
            "         iterative refinement, parallel processing, etc.) — generate a complete",
            "         workflow YAML definition. Wrap the YAML in a ```yaml code block.",
            string.Empty,
            "      When generating YAML, follow this schema strictly:",
            "      - Top-level keys: name, description, roles, steps",
            "      - roles: list of {id, name, system_prompt}; prefer omitting provider/model so runtime default is used",
            "      - steps: list of {id, type, role, parameters, next, branches}",
            "      - Available step types: llm_call, transform, assign, guard, conditional, switch,",
            "        while, foreach, parallel, race, map_reduce, evaluate, reflect, connector_call,",
            "        human_input, human_approval, cache, delay, emit, checkpoint, retrieve_facts",
            "      - Use snake_case for all keys.",
            "      - Ensure step IDs are unique and flow connections (next / branches) are valid.",
            "      - Never invent provider names. Only set provider if user explicitly asks for a specific one.",
            "      - YAML syntax safety rules:",
            "        * If a plain text value contains ':' then quote it, or use a block scalar '|'.",
            "        * Keep consistent indentation (2 spaces for nested keys).",
            "        * Always return a single ```yaml fenced block for workflow output.",
            string.Empty,
            "      Always output exactly one of: a direct answer OR a ```yaml block. Never both.",
            "  - id: assistant",
            "    name: Assistant",
            "    system_prompt: |",
            "      You are a helpful assistant. Answer the user clearly and concisely.",
            string.Empty,
            "steps:",
            "  - id: capture_input",
            "    type: assign",
            "    parameters:",
            "      target: \"user_request\"",
            "      value: \"$input\"",
            "    next: classify",
            string.Empty,
            "  - id: classify",
            "    type: llm_call",
            "    role: planner",
            "    next: check_is_yaml",
            string.Empty,
            "  - id: check_is_yaml",
            "    type: conditional",
            "    parameters:",
            "      condition: \"```y\"",
            "    branches:",
            "      \"true\": validate_yaml",
            "      \"false\": done",
            string.Empty,
            "  - id: validate_yaml",
            "    type: workflow_yaml_validate",
            "    on_error:",
            "      strategy: fallback",
            "      fallback_step: refine_yaml",
            "    next: show_for_approval",
            string.Empty,
            "  - id: show_for_approval",
            "    type: human_approval",
            "    parameters:",
            $"      prompt: \"{approvalPrompt}\"",
            "      on_reject: skip",
            "    branches:",
            $"      \"true\": {approveTarget}",
            "      \"false\": refine_yaml",
            string.Empty,
            "  - id: refine_yaml",
            "    type: llm_call",
            "    role: planner",
            "    parameters:",
            "      prompt_prefix: \"Please refine the workflow YAML based on user feedback:\\n\"",
            "    next: validate_yaml",
        };

        if (includeExecuteStep)
        {
            lines.AddRange(
            [
                string.Empty,
                "  - id: extract_and_execute",
                "    type: dynamic_workflow",
                "    parameters:",
                "      original_input: \"{{user_request}}\"",
                "    next: done",
            ]);
        }

        lines.AddRange(
        [
            string.Empty,
            "  - id: done",
            "    type: assign",
            "    parameters:",
            "      target: \"result\"",
            "      value: \"$input\"",
            string.Empty,
        ]);

        return string.Join('\n', lines);
    }
}
