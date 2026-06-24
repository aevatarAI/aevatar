using System.Collections.Immutable;
using Aevatar.Workflow.Application.Abstractions.Workflows;

namespace Aevatar.Workflow.Application.Workflows;

/// <summary>
/// Read-only catalog of workflow YAML definitions, populated identically on every node
/// at application startup from built-in constants and on-disk YAML files.
///
/// <para>
/// This is a <b>startup-loaded configuration cache</b> — not a runtime-mutable fact source.
/// <see cref="Register"/> is called only during the DI factory and
/// <c>WorkflowDefinitionBootstrapHostedService</c>. After startup completes, the catalog
/// is effectively frozen and all access is read-only.
/// </para>
///
/// <para>
/// <b>Multi-node deployment requirement:</b> All nodes must share the same set of workflow
/// definition files and built-in registration options so that every instance of this catalog
/// contains identical entries. The catalog is not replicated or synchronized at runtime.
/// </para>
///
/// <para>
/// The backing store uses <see cref="ImmutableDictionary{TKey,TValue}"/> to make the
/// read-only-after-startup semantics explicit. Writes during bootstrap use atomic swap
/// via <see cref="ImmutableInterlocked"/>.
/// </para>
/// </summary>
public sealed class WorkflowDefinitionCatalog : IWorkflowDefinitionCatalog
{
    private ImmutableDictionary<string, WorkflowDefinitionRegistration> _workflows =
        ImmutableDictionary.Create<string, WorkflowDefinitionRegistration>(StringComparer.OrdinalIgnoreCase);

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
    /// Built-in <c>studio</c> workflow for the channel-less <c>/workflow/studio</c> authoring surface.
    ///
    /// <para>
    /// Unlike <see cref="BuiltInDirectYaml"/> (a zero-bias "helpful assistant" used by every channel-less
    /// <c>Direct</c> caller — Lark/Telegram/bare), this workflow steers the agent to be <b>workflow-first</b>
    /// and <b>Observatory-delivered</b>: author a runnable workflow, run + observe it, and schedule recurring
    /// runs through the channel-free <c>aevatar_provision_workflow_schedule</c> tool so results surface in
    /// <c>/workflow/observatory</c> — never a chat/bot, never a prose ornn skill as the deliverable.
    /// </para>
    ///
    /// <para>
    /// The studio page selects this workflow by sending <c>workflow: "studio"</c> on <c>/api/chat</c>
    /// (legacy-name lookup → <c>WorkflowChatSource.CatalogWorkflow("studio")</c>). It is the ONLY studio
    /// scoping signal: no proto/surface field is added and the shared role agent is untouched, so other
    /// <c>Direct</c> callers keep the benign <c>direct</c> prompt + unrestricted tool surface.
    /// </para>
    ///
    /// <para>
    /// The role carries an <c>allowed_tools</c> allowlist (parsed by <c>WorkflowParser</c> →
    /// <c>RoleDefinition.AgentToolScope</c>, intersected with any step scope by the execution kernel →
    /// <c>ToolVisibility</c>). It INCLUDES the workflow-first tool set and EXCLUDES the Lark
    /// <c>scheduled_agent_creator</c>; the allowlist is the lever that keeps the failing Lark scheduler out of
    /// the studio surface entirely (prompt steering alone is unreliable while the tool is visible).
    /// </para>
    /// </summary>
    public static string BuiltInStudioYaml { get; } = """
        name: studio
        description: >
          Studio authoring surface: workflow-first, Observatory-delivered. Author a runnable workflow,
          run and observe it, and schedule recurring runs so results appear in /workflow/observatory.
        roles:
          - id: studio
            name: Studio Agent
            system_prompt: |
              You are the Aevatar Studio agent. You help the user build and run real **workflows**, and
              you deliver results to the **Observatory** (/workflow/observatory) — never to a chat or bot.

              How to work:
              1. To build automation, author a real workflow. Draft the workflow as YAML and create it with
                 `workflow_create_def` (use `workflow_read_def` / `workflow_list_defs` to inspect, and
                 `workflow_update_def` to revise). Inline workflow YAML is acceptable when creating a
                 definition is unavailable.
              2. To run it, call `aevatar_start_workflow`, then `aevatar_observe_run` (and
                 `aevatar_read_workflow_run_artifact` for outputs) to watch and report the run.
              3. To run it on a recurring schedule, call `aevatar_provision_workflow_schedule` with the
                 workflow YAML, a display name, and a cron + IANA timezone. Its runs appear in
                 /workflow/observatory. Tell the user to open /workflow/observatory to see the runs.
              4. You may use `ornn_search_skills` and `use_skill` to discover and load skills for genuinely
                 non-deterministic, language-driven subtasks — but the deliverable for an automation request
                 is a runnable workflow, not a published skill.

              Hard rules:
              - The deliverable is a runnable workflow whose runs are visible in /workflow/observatory. Do NOT
                publish a prose skill as the answer to "build/automate/schedule X".
              - Never deliver results to Lark/Telegram or any chat/bot, and never schedule a bot delivery.
                Scheduling goes exclusively through `aevatar_provision_workflow_schedule` (Observatory-delivered).
              - The owning scope and your credentials come from the session; do not ask the user for scope,
                channel, owner, or tokens.
            allowed_tools:
              - workflow_create_def
              - workflow_update_def
              - workflow_read_def
              - workflow_list_defs
              - aevatar_start_workflow
              - aevatar_observe_run
              - aevatar_read_workflow_run_artifact
              - aevatar_provision_workflow_schedule
              - ornn_search_skills
              - use_skill
        steps:
          - id: reply
            type: llm_call
            role: studio
            parameters: {}
        """;

    /// <summary>
    /// Built-in auto-route workflow. Classifies user intent: simple questions get a
    /// direct LLM answer; complex requests trigger workflow YAML generation, multi-round
    /// human approval, and then dynamic execution of the confirmed workflow.
    /// </summary>
    public static string BuiltInAutoYaml { get; } = CreateBuiltInAutoYaml();

    /// <summary>
    /// Built-in auto-route workflow for review/finalization mode. It keeps the same
    /// planning/refinement behavior as <see cref="BuiltInAutoYaml"/>, but approval only
    /// finalizes YAML (manual run), instead of executing immediately.
    /// </summary>
    public static string BuiltInAutoReviewYaml { get; } = CreateBuiltInAutoReviewYaml();

    public static string CreateBuiltInAutoYaml() =>
        BuildAutoWorkflowYaml(
            workflowName: "auto",
            descriptionLine: "Auto-route: classify user intent, answer directly or generate a workflow YAML",
            descriptionLine2: "for human approval and dynamic execution.",
            approvalPrompt: "Please review the generated workflow YAML. Approve to execute, or reject with modification feedback.",
            approveTarget: "extract_and_execute",
            includeExecuteStep: true);

    public static string CreateBuiltInAutoReviewYaml() =>
        BuildAutoWorkflowYaml(
            workflowName: "auto_review",
            descriptionLine: "Auto-route: classify user intent, answer directly or generate a workflow YAML",
            descriptionLine2: "for human approval and manual finalization.",
            approvalPrompt: "Please review the generated workflow YAML. Approve to finalize YAML for manual run, or reject with modification feedback.",
            approveTarget: "done",
            includeExecuteStep: false);

    /// <inheritdoc />
    public void Register(string name, string yaml)
    {
        Register(name, yaml, "builtin");
    }

    public void Register(string name, string yaml, string sourceKind)
    {
        var normalizedName = NormalizeName(name);
        var normalizedSourceKind = string.IsNullOrWhiteSpace(sourceKind)
            ? "builtin"
            : sourceKind.Trim();
        ImmutableInterlocked.AddOrUpdate(
            ref _workflows,
            normalizedName,
            _ => new WorkflowDefinitionRegistration(
                normalizedName,
                yaml,
                WorkflowDefinitionActorId.Format(normalizedName),
                normalizedSourceKind),
            (_, _) => new WorkflowDefinitionRegistration(
                normalizedName,
                yaml,
                WorkflowDefinitionActorId.Format(normalizedName),
                normalizedSourceKind));
    }

    /// <inheritdoc />
    public WorkflowDefinitionRegistration? GetDefinition(string name)
    {
        var normalizedName = NormalizeName(name);
        return _workflows.TryGetValue(normalizedName, out var registration)
            ? registration
            : null;
    }

    /// <inheritdoc />
    public string? GetYaml(string name) =>
        GetDefinition(name)?.WorkflowYaml;

    /// <inheritdoc />
    public IReadOnlyList<string> GetNames() =>
        _workflows.Keys.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToList();

    private static string NormalizeName(string? workflowName)
    {
        if (string.IsNullOrWhiteSpace(workflowName))
            throw new ArgumentException("Workflow name is required.", nameof(workflowName));

        return workflowName.Trim();
    }

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
            "      - Step objects may only use these root keys: id, type, role, target_role, parameters, next, branches, children, retry, on_error, timeout_ms",
            "      - Do NOT add unsupported step-level fields such as description, title, summary, notes, input_schema, output_schema, metadata, or examples",
            "      - Available step types: llm_call, transform, assign, guard, conditional, switch,",
            "        while, foreach, parallel, race, map_reduce, evaluate, reflect, connector_call, secure_connector_call,",
            "        human_input, secure_input, human_approval,",
            "        cache, delay, emit, checkpoint, retrieve_facts, self_reschedule",
            "      - For self_reschedule, put schedule_id, cron_expression, timezone, workflow_name/service_id, scope_id, and prompt under parameters.",
            "      - Do NOT add a top-level schedule key; scheduling is a normal step type.",
            "      - NEVER use dynamic_workflow in generated YAML. dynamic_workflow is engine-internal and expects a nested ```yaml block input.",
            "      - Use snake_case for all keys.",
            "      - Ensure step IDs are unique and flow connections (next / branches) are valid.",
            "      - Never invent provider names. Only set provider if user explicitly asks for a specific one.",
            "      - Every primitive-specific option must live under step.parameters, not at step root unless it is one of the allowed root keys above.",
            "      - YAML syntax safety rules:",
            "        * If a plain text value contains ':' then quote it, or use a block scalar '|'.",
            "        * Keep consistent indentation (2 spaces for nested keys).",
            "        * Always return a single ```yaml fenced block for workflow output.",
            string.Empty,
            "      Example valid workflow YAML (simple deterministic flow):",
            "      ```yaml",
            "      name: normalize_text",
            "      description: Normalize incoming text and store the result.",
            "      steps:",
            "        - id: trim_input",
            "          type: transform",
            "          parameters:",
            "            op: \"trim\"",
            "          next: save_result",
            "        - id: save_result",
            "          type: assign",
            "          parameters:",
            "            target: \"result\"",
            "            value: \"$input\"",
            "      ```",
            string.Empty,
            "      Example valid workflow YAML (roles + branching):",
            "      ```yaml",
            "      name: review_summary",
            "      description: Summarize a review and gate urgent items for approval.",
            "      roles:",
            "        - id: reviewer",
            "          name: Reviewer",
            "          system_prompt: |",
            "            Summarize the incoming review and mention urgent when escalation is needed.",
            "      steps:",
            "        - id: summarize_review",
            "          type: llm_call",
            "          role: reviewer",
            "          parameters:",
            "            prompt: \"Summarize the input and call out urgent issues.\"",
            "          next: gate_urgent",
            "        - id: gate_urgent",
            "          type: conditional",
            "          parameters:",
            "            condition: \"urgent\"",
            "          branches:",
            "            \"true\": request_approval",
            "            \"false\": store_result",
            "        - id: request_approval",
            "          type: human_approval",
            "          parameters:",
            "            prompt: \"Urgent review detected. Approve escalation?\"",
            "          branches:",
            "            \"true\": store_result",
            "            \"false\": store_result",
            "        - id: store_result",
            "          type: assign",
            "          parameters:",
            "            target: \"result\"",
            "            value: \"$input\"",
            "      ```",
            string.Empty,
            "      Copy the field structure from the examples above. Do not invent extra fields.",
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
            "    next: classify_route",
            string.Empty,
        };

        lines.AddRange(
        [
            "  - id: classify_route",
            "    type: llm_call",
            "    role: planner",
            "    parameters:",
            "      prompt_prefix: \"Classify the user request into exactly one route token: direct or workflow. Respond with only one lowercase token and no extra text.\\n\\n\"",
            "    next: route_intent",
            string.Empty,
            "  - id: route_intent",
            "    type: switch",
            "    branches:",
            "      \"workflow\": prepare_workflow_request",
            "      \"direct\": prepare_direct_response",
            "      \"_default\": prepare_direct_response",
            string.Empty,
            "  - id: prepare_workflow_request",
            "    type: assign",
            "    parameters:",
            "      target: \"user_request\"",
            "      value: \"${user_request}\"",
            "    next: generate_workflow_yaml",
            string.Empty,
            "  - id: generate_workflow_yaml",
            "    type: llm_call",
            "    role: planner",
            "    parameters:",
            "      prompt_prefix: \"Generate a complete workflow YAML for the user request below. Return only a single ```yaml fenced block.\\n\\n\"",
            "    next: validate_yaml",
            string.Empty,
            "  - id: prepare_direct_response",
            "    type: assign",
            "    parameters:",
            "      target: \"user_request\"",
            "      value: \"${user_request}\"",
            "    next: reply_direct",
            string.Empty,
            "  - id: reply_direct",
            "    type: llm_call",
            "    role: assistant",
            "    next: done",
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
            "      prompt_prefix: \"Please refine the workflow YAML based on the validation error or user feedback below. Return a corrected full workflow YAML only in a single ```yaml fenced block. Do not include unsupported step-level fields such as description, title, summary, notes, metadata, input_schema, or output_schema. Primitive-specific options must be placed under step.parameters.\\n\\n\"",
            "    next: validate_yaml",
        ]);

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
