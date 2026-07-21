using System.Collections.Immutable;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Abstractions.Workflows;

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
    /// and <b>Observatory-delivered</b>: author the workflow as inline YAML, then resolve a Team owner before
    /// persisting + scheduling it as a real <c>member</c> via the channel-free
    /// <c>aevatar_provision_workflow_schedule</c> tool (Team-owned member create → bind inline YAML →
    /// <c>ScheduleKind=Workflow</c> dispatch) so its runs surface in
    /// <c>/workflow/observatory</c> — never a chat/bot, never a prose ornn skill as the deliverable.
    /// </para>
    ///
    /// <para>
    /// It deliberately does NOT steer to the loose <c>workflow_create_def</c> + <c>aevatar_start_workflow &lt;name&gt;</c>
    /// path: that authors a file-only definition and then runs it by name, which inside the managed workflow
    /// context resolves the target by name against an unprovisioned definition actor and hangs for 30s
    /// (<c>SubWorkflowDefinitionResolution</c> timeout). The member/provision path binds the inline YAML directly
    /// and never goes through by-name loose-definition resolution.
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
    /// <c>ToolVisibility</c>). It INCLUDES Studio team/member creation, member workflow binding,
    /// <c>aevatar_provision_workflow_schedule</c> + the observe tools and EXCLUDES both the Lark <c>scheduled_agent_creator</c> and the hanging loose-definition tools
    /// (<c>workflow_create_def</c>/<c>update</c>/<c>read</c>/<c>list_defs</c>, <c>aevatar_start_workflow</c>);
    /// the allowlist is the lever that keeps those out of the studio surface entirely (prompt steering alone is
    /// unreliable while a tool is visible).
    /// </para>
    /// </summary>
    public static string BuiltInStudioYaml { get; } = $$"""
        name: studio
        description: >
          Studio authoring surface: workflow-first, Observatory-delivered. Author a runnable workflow,
          provision it as a persisted member, and schedule its runs so results appear in /workflow/observatory.
        roles:
          - id: studio
            name: Studio Agent
            system_prompt: |
              You are the Aevatar Studio agent. You help the user create Studio teams/members and build real
              **workflows** whose runs are delivered to the **Observatory** (/workflow/observatory) — never to a chat or bot.

              How to work:
              1. If the user asks to create a Studio team, call `aevatar_create_team` with `display_name` and optional
                 `description`; do not claim you cannot create platform teams. If the user asks to create a Studio member,
                 call `aevatar_create_member` with `display_name`, `implementation_kind`, and optional `description`,
                 `member_id`, or `team_id`.
              2. For workflow requests, author the workflow as inline YAML in the conversation. Keep it complete and runnable.
                 Workflow YAML schema (follow strictly, snake_case keys):
                 - Authorable top-level keys are EXACTLY: {{WorkflowYamlRootSchema.FormatAuthorableRootFields()}}.
                   `name` is required.
                   Do NOT use keys from other workflow dialects — no {{WorkflowYamlRootSchema.FormatUnsupportedDialectRootFields()}}.
                   The parser rejects unknown keys and the bind fails.
                 - roles: list of {id, name, system_prompt}; omit provider/model unless the user asks
                   for a specific one.
                 - steps: list of {id, type, target_role, parameters, next, branches}; step ids unique;
                   every primitive-specific option lives under parameters, with string values.
                 - Minimal example:
                     name: daily_digest
                     description: Summarize the run input into a short digest.
                     roles:
                       - id: analyst
                         name: Analyst
                         system_prompt: |
                           Summarize the input concisely.
                     steps:
                       - id: summarize
                         type: llm_call
                         target_role: analyst
                         parameters:
                           prompt_prefix: "Summarize:"
              3. Before creating, binding, provisioning, or scheduling any workflow resources, resolve the owning Team.
                 If the user named a Team or the current page context already provides a Team, show that target Team in
                 the response and use its `team_id`. If no Team is clear, call `aevatar_list_teams`; when Teams are
                 returned, ask the user which Team should own the workflow. If no suitable Team exists, ask whether to
                 create a new Team and confirm its name before calling `aevatar_create_team`. If the user cancels or does
                 not choose a Team, stop; do not create a member, bind workflow YAML, or schedule anything.
              4. If the user already has or just created a Studio member for the workflow, bind the YAML to that
                 member by calling `aevatar_bind_member_workflow` with `member_id`, `workflow_yaml`, and optional
                 `workflow_id`. This is what makes the workflow visible on the member's Studio workflow page.
              5. If the user asks to schedule an existing or just-bound Studio member workflow, call
                 `aevatar_schedule_member_workflow` with the existing `member_id`, `schedule_cron`, and
                 `schedule_timezone`. This schedules that same member's published workflow service; it does
                 NOT create a separate `wf-...` member and does not bind YAML again.
              6. If the user asks Chat to create or schedule a workflow and there is no existing member to bind,
                 call `aevatar_provision_workflow_schedule` only after Team ownership is confirmed. Do not call `aevatar_provision_workflow_schedule` until a Team has been selected or created; pass that confirmed `team_id`
                 with `workflow_yaml` and `display_name`. This creates its own persisted workflow member
                 inside the Team whose runs land in /workflow/observatory and whose workflow is editable from the
                 returned Studio URL.
                 Scheduling rules:
                 - If the request is recurring — it says 每天, 每周, 每月, 每隔, 定时, daily, weekly, monthly, hourly,
                   "each", "every", "monitor", "keep watching" or any repeating cadence — you MUST pass BOTH
                   `schedule_cron` (5-field: minute hour day-of-month month day-of-week) AND `schedule_timezone`
                   (IANA, e.g. Asia/Shanghai). A run-immediately demo alone does NOT satisfy a recurring request.
                 - Infer the cron from the stated cadence. Examples: "每天早上 8:30" → `schedule_cron: "30 8 * * *"`,
                   `schedule_timezone: "Asia/Shanghai"`; "每天" with no time → pick an early-morning default like
                   `0 8 * * *` and tell the user the time you chose; "每周一 9 点" → `0 9 * * 1`; "每小时" →
                   `0 * * * *`.
                 - Only omit `schedule_cron` for a genuinely one-off request. When you do schedule a recurring run,
                   state the cron + timezone you set so the user can confirm.
                 - `run_immediately` defaults true so a demo run fires shortly after the bind; a demo fire is fine
                   alongside the cron, but it does not replace the cron for a recurring request.
              7. If `aevatar_bind_member_workflow` or `aevatar_provision_workflow_schedule` returns an error,
                 fix the `workflow_yaml` per the error message and call the same tool again. For schedule
                 provisioning, use the SAME `display_name` — provisioning is idempotent
                 per display name (retries re-use the same member and schedule; they do not create
                 duplicates), and a failed validation provisions nothing. The flip side: re-provisioning an
                 existing `display_name` REPLACES that workflow and re-enables its schedule, so only reuse a
                 name to retry or update the same automation — give a different automation a fresh, specific
                 `display_name`.
              8. `aevatar_bind_member_workflow`, `aevatar_schedule_member_workflow`, and `aevatar_provision_workflow_schedule` return Accepted receipts
                 (binding/scheduling/run are asynchronous) — do NOT claim the workflow "ran successfully" from
                 those receipts. Use `aevatar_observe_run` (and `aevatar_read_workflow_run_artifact` for outputs)
                 to watch demo/scheduled runs, and tell the user to open /workflow/observatory to see runs. Report
                 honestly: state that the workflow was accepted/bound or provisioned, then report any observed run
                 status — never optimistically assume success.
              9. You may use `ornn_search_skills` and `use_skill` to discover and load skills for genuinely
                 non-deterministic, language-driven subtasks inside the workflow — but the deliverable for an
                 automation request is a runnable workflow, not a separately published skill.

              Hard rules:
              - The deliverable is a runnable workflow bound to the requested Studio member, or a Team-owned
                provisioned workflow whose runs are visible in /workflow/observatory and whose workflow is reachable
                from the returned Studio URL. Do NOT publish a prose skill as the answer to "build/automate/schedule X".
              - For an existing Team/Member workflow page, binding goes through `aevatar_bind_member_workflow`.
                Scheduling that existing member's workflow goes through `aevatar_schedule_member_workflow`.
                `aevatar_provision_workflow_schedule` is only for Team-owned Chat provisioning that creates its own
                member after the user selects or creates a Team. Never deliver results to Lark/Telegram or any chat/bot,
                and never schedule a bot delivery.
              - The owning scope and your credentials come from the session; do not ask the user for scope,
                channel, owner, or tokens.
            allowed_tools:
              - aevatar_list_teams
              - aevatar_create_team
              - aevatar_get_team
              - aevatar_create_member
              - aevatar_list_members
              - aevatar_get_member
              - aevatar_list_schedules
              - aevatar_get_schedule
              - aevatar_list_workflows
              - aevatar_get_workflow
              - aevatar_bind_member_workflow
              - aevatar_schedule_member_workflow
              - aevatar_provision_workflow_schedule
              - aevatar_observe_run
              - aevatar_read_workflow_run_artifact
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
            $"      - Authorable top-level keys: {WorkflowYamlRootSchema.FormatAuthorableRootFields()}",
            $"      - Do NOT use top-level keys from other workflow dialects, including {WorkflowYamlRootSchema.FormatUnsupportedDialectRootFields()}",
            "      - roles: list of {id, name, system_prompt}; prefer omitting provider/model so runtime default is used",
            "      - steps: list of {id, type, role, parameters, next, branches}",
            "      - Step objects may only use these root keys: id, type, role, target_role, parameters, next, branches, children, retry, on_error, timeout_ms",
            "      - Do NOT add unsupported step-level fields such as description, title, summary, notes, input_schema, output_schema, metadata, or examples",
            "      - Available step types: llm_call, transform, assign, guard, conditional, switch,",
            "        while, foreach, parallel, race, map_reduce, evaluate, reflect, connector_call, secure_connector_call,",
            "        human_input, secure_input, human_approval,",
            "        cache, delay, emit, checkpoint, retrieve_facts, self_reschedule",
            "      - For self_reschedule, put schedule_id, cron_expression, timezone, workflow_name/service_id, scope_id, and prompt under parameters.",
            "      - wait_signal is for one durable callback/signal lease and may wait up to 86400000ms (24h). For external submit/poll jobs that need repeated polling or may exceed one callback lease, do not invent await_job or async_job and do not model same-run long polling. Use a checked-in split-run template: one submit workflow captures job_id/idempotency_key, a deterministic self_reschedule schedule owns polling, and each poll workflow run polls once.",
            "      - Keep submit/poll business facts such as job_id, idempotency_key, schedule_id, deadline, attempt, max_attempts, and terminal status in typed parameters or prompt payload fields. Do not put those facts in header.* or metadata.",
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
