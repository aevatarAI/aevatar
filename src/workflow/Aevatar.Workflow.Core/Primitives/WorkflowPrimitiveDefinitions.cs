using Aevatar.Workflow.Abstractions.Primitives;

namespace Aevatar.Workflow.Core.Primitives;

/// <summary>
/// Canonical module authoring contracts. Module packs contribute these contracts together
/// with their implementations; consumers do not maintain a separate list of editable fields.
/// Open-ended sub_param_* and provider-specific connector inputs remain extension parameters.
/// </summary>
public static class WorkflowPrimitiveDefinitions
{
    private static readonly IReadOnlyDictionary<string, WorkflowPrimitiveDefinition> Definitions =
        new Dictionary<string, WorkflowPrimitiveDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["assign"] = new("Assigns a value to a workflow variable.",
            [
                Text("target", "Target variable", placeholder: "result"),
                Text("value", "Value", placeholder: "$input"),
            ]),
            ["cache"] = new("Caches a child step result by key.",
            [
                Text("cache_key", "Cache key", description: "Defaults to the step input."),
                Integer("ttl_seconds", "TTL seconds", "3600"),
                StepType("child_step_type", "Cached step", "llm_call"),
                Text("child_target_role", "Cached step target role"),
            ]),
            ["checkpoint"] = new("Records a workflow checkpoint.",
            [Text("name", "Checkpoint name", description: "Defaults to the current step id.")]),
            ["conditional"] = new("Selects a branch by evaluating a condition.",
            [Text("condition", "Condition", defaultValue: "default", multiline: true)]),
            ["switch"] = new("Selects a branch matching a value.",
            [Text("on", "Switch value", placeholder: "$input")]),
            ["while"] = new("Repeats a child step while its condition holds.",
            [
                StepType("step", "Loop step", "llm_call"),
                Integer("max_iterations", "Max iterations", "10", 1, 1_000_000),
                Text("condition", "Condition", defaultValue: "true", multiline: true),
            ]),
            ["delay"] = new("Pauses execution for a duration.",
            [Integer("duration_ms", "Duration ms", "1000", 0, 300_000)]),
            ["wait_signal"] = new("Suspends workflow execution until an external signal arrives.",
            [
                Text("signal_name", "Signal name", defaultValue: "default"),
                Integer("timeout_ms", "Timeout ms", "0", 0, 86_400_000,
                    "Zero waits without a timeout; the maximum is 24 hours."),
                Text("prompt", "Prompt", multiline: true),
            ]),
            ["workflow_call"] = new("Invokes another workflow definition as a sub-workflow.",
            [
                Text("workflow", "Workflow", required: true, placeholder: "child_workflow"),
                Choice("lifecycle", "Lifecycle", WorkflowCallLifecycle.AllowedValues, WorkflowCallLifecycle.Singleton),
            ]),
            ["llm_call"] = new("Runs an LLM role step and returns generated output.",
            [
                Text("prompt_prefix", "Prompt", multiline: true),
                Integer("llm_timeout_ms", "Timeout ms", "1800000", 1),
            ]),
            ["tool_call"] = new("Invokes an allowed tool.",
            [
                Text("tool", "Tool", required: true),
                Text("arguments", "Arguments", description: "JSON text or a template. Defaults to the step input, or an empty object.", multiline: true),
                Integer("tool_timeout_ms", "Timeout ms", "360000", 100, 1_800_000),
            ]),
            ["evaluate"] = new("Evaluates the current input using an LLM judge.",
            [
                Text("criteria", "Criteria", "quality", multiline: true),
                Text("scale", "Scale", "1-5"),
                Number("threshold", "Threshold", "3"),
                Text("on_below", "Below threshold branch"),
            ]),
            ["reflect"] = new("Refines input through LLM reflection rounds.",
            [
                Integer("max_rounds", "Max rounds", "3", 1, 10),
                Text("criteria", "Criteria", "quality and correctness", multiline: true),
            ]),
            ["retrieve_facts"] = new("Retrieves matching facts from the configured fact source.",
            [
                Text("query", "Query"),
                Integer("top_k", "Top results", "5", 1, 100),
                Text("delimiter", "Delimiter", "\n"),
                Text("facts", "Fallback facts", multiline: true),
            ]),
            ["emit"] = new("Publishes a custom workflow event.",
            [
                Text("event_type", "Event type", "custom"),
                Text("payload", "Payload", description: "Defaults to the step input.", multiline: true),
            ]),
            ["guard"] = new("Checks input and applies a failure policy.",
            [
                Choice("check", "Check", ["not_empty", "json_valid", "regex", "max_length", "contains"], "not_empty"),
                Text("pattern", "Regex pattern", description: "Used by the regex check."),
                Integer("max", "Max length", minimum: 1, description: "Used by the max_length check."),
                Text("keyword", "Keyword", description: "Used by the contains check."),
                Choice("on_fail", "On failure", ["fail", "skip", "branch"], "fail"),
                Text("branch_target", "Failure branch", description: "Used when on_fail is branch."),
            ]),
            ["foreach"] = new("Runs a child step for each input item.",
            [
                Text("delimiter", "Delimiter", "\n---\n"),
                StepType("sub_step_type", "Item step", "parallel"),
                Text("sub_target_role", "Item target role"),
                Choice("items_source", "Items source", ["input_file_refs"]),
                Text("items", "Fallback items", multiline: true),
                ..ConcurrencyParameters(),
            ]),
            ["parallel"] = new("Runs child steps in parallel and optionally combines their votes.",
            [
                Text("workers", "Workers", description: "Comma-separated role ids or a JSON string list."),
                Integer("parallel_count", "Parallel count", "3"),
                StepType("sub_step_type", "Worker step", "llm_call"),
                Text("sub_target_role", "Worker target role"),
                StepType("vote_step_type", "Vote step"),
                ..ConcurrencyParameters(),
            ]),
            ["race"] = new("Runs competing branches and returns the first successful result.",
            [
                Text("workers", "Workers", description: "Comma-separated role ids or a JSON string list."),
                Integer("count", "Branch count", "2", 1, 10),
                StepType("sub_step_type", "Branch step", "llm_call"),
                Text("sub_target_role", "Branch target role"),
            ]),
            ["map_reduce"] = new("Maps input items to child steps and reduces their results.",
            [
                Text("delimiter", "Delimiter", "\n---\n"),
                StepType("map_step_type", "Map step", "llm_call"),
                Text("map_target_role", "Map target role"),
                StepType("reduce_step_type", "Reduce step", "llm_call"),
                Text("reduce_target_role", "Reduce target role"),
                Text("reduce_prompt_prefix", "Reduce prompt", multiline: true),
                Text("items", "Fallback items", multiline: true),
                ..ConcurrencyParameters(),
            ]),
            ["human_input"] = new("Waits for a person to provide input.",
            [
                Text("prompt", "Input prompt", "Please provide input:", multiline: true),
                Text("variable", "Output variable", "user_input"),
                Integer("timeout_seconds", "Timeout seconds", "1800", 1, 86_400),
                Choice("on_timeout", "On timeout", ["fail", "skip"], "fail"),
            ]),
            ["human_approval"] = new("Waits for a person to approve or reject an action.",
            [
                Text("prompt", "Approval prompt", "Approve this step?", multiline: true),
                Integer("timeout_seconds", "Timeout seconds", "3600", 1, 5400),
                Choice("on_reject", "On rejection", ["fail", "skip"], "fail"),
            ]),
            ["dynamic_workflow"] = new("Extracts and executes a generated workflow definition.",
            [Text("original_input", "Original input", description: "Optional input passed into the extracted workflow.")]),
            ["transform"] = new("Transforms the current input using deterministic operations.",
            [
                Choice("op", "Operation", ["identity", "count", "count_words", "take", "take_last", "join", "split",
                    "json_extract", "json_parse", "rss_extract_items", "distinct", "uppercase", "lowercase", "trim",
                    "reverse_lines", "sum", "subtract", "multiply", "divide", "round", "min", "max", "group_by", "template"], "identity"),
                Integer("n", "Item count", "5", 1, 10_000),
                Text("separator", "Separator", "\n"),
                Text("path", "JSON path"),
                Text("field", "Fields", description: "Comma-separated fields or a JSON string list."),
                Text("sort_by", "Sort by"),
                Choice("order", "Sort order", ["asc", "desc"], "asc"),
                Text("key", "Group key"),
                Text("value", "Value field"),
                Choice("aggregate", "Aggregation", ["sum", "count", "avg"]),
                Integer("precision", "Decimal places", minimum: 0, maximum: 28),
                Text("template", "Template", multiline: true),
                Text("source_id", "Source id"),
                Text("source_url", "Source URL"),
            ]),
            ["vote"] = new("Evaluates candidate agreement using the vote agreement rule.",
            [
                Choice("rule_mode", "Rule", ["all", "majority", "quorum", "label_count_constraints", "predicate"], "majority"),
                Choice("label_source", "Label source", ["success", "branch_key", "annotation"], "success"),
                Text("label_field", "Label field", description: "Required when label_source is annotation."),
                Integer("quorum_count", "Quorum count", minimum: 1),
                Number("quorum_ratio", "Quorum ratio", maximum: 1),
                Integer("min_approve_count", "Minimum approvals", minimum: 0),
                Integer("max_approve_count", "Maximum approvals", minimum: 0),
                Integer("min_reject_count", "Minimum rejections", minimum: 0),
                Integer("max_reject_count", "Maximum rejections", minimum: 0),
                Integer("min_abstain_count", "Minimum abstentions", minimum: 0),
                Integer("max_abstain_count", "Maximum abstentions", minimum: 0),
                Text("predicate_id", "Predicate", description: "Required when rule_mode is predicate."),
                Choice("winner_policy", "Winner policy", ["first_approved", "first_success", "first"], "first_approved"),
                Text("on_agreed", "Agreed branch"),
                Text("on_rejected", "Rejected branch"),
                Text("on_inconclusive", "Inconclusive branch"),
            ]),
            ["connector_call"] = new("Invokes a configured external connector.", ConnectorParameters()),
            ["secure_connector_call"] = new("Invokes a configured connector with secure payload handling.", ConnectorParameters()),
            ["secure_input"] = new("Captures a secure value without exposing it in workflow output.",
            [
                Text("prompt", "Input prompt", "Please provide the secure value:", multiline: true),
                Text("variable", "Variable", "secure_input"),
                Integer("timeout_seconds", "Timeout seconds", "1800", 1, 86_400),
                Choice("on_timeout", "On timeout", ["fail", "skip"], "fail"),
                Boolean("allow_empty", "Allow empty input"),
                Text("redacted_output", "Redacted output", "[secure input captured]"),
            ]),
            ["lease"] = new("Acquires, renews, or releases an actor-owned lease.",
            [
                Choice("action", "Action", ["acquire", "renew", "release"], "acquire"),
                Text("key", "Lease key", required: true),
                Integer("ttl_ms", "TTL ms", "300000", WorkflowLeaseGAgent.MinLeaseTtlMs, WorkflowLeaseGAgent.MaxLeaseTtlMs),
                Integer("wait_timeout_ms", "Wait timeout ms", "300000", WorkflowLeaseGAgent.MinWaitTimeoutMs, WorkflowLeaseGAgent.MaxWaitTimeoutMs),
                Choice("on_conflict", "On conflict", ["fail", "wait"], "fail"),
                Text("holder_token", "Holder token"),
                Text("holder_token_variable", "Holder token variable"),
                Integer("generation", "Generation", minimum: 1),
            ]),
            ["workflow_yaml_validate"] = new("Validates workflow YAML received as input.", []),
            ["notify"] = new("Delivers the step's typed interaction to its configured delivery target.", []),
            ["actor_send"] = new("Sends an actor message using the configured send state.",
            [Text("send_state_key", "Send state key")]),
            ["workflow_loop"] = new("Controls workflow loop execution.", []),
        };

    public static WorkflowPrimitiveDefinition? Find(string canonicalType) =>
        Definitions.GetValueOrDefault(canonicalType);

    public static WorkflowPrimitiveDefinition Get(string canonicalType) =>
        Find(canonicalType) ?? throw new ArgumentException($"No authoring contract for '{canonicalType}'.", nameof(canonicalType));

    private static WorkflowPrimitiveParameterDefinition Text(string name, string label, string defaultValue = "",
        bool required = false, string description = "", string placeholder = "", bool multiline = false) =>
        new(name, WorkflowPrimitiveParameterType.String, label, description, required, defaultValue,
            Control: multiline ? WorkflowPrimitiveParameterControl.Textarea : WorkflowPrimitiveParameterControl.Text,
            Placeholder: placeholder);

    private static WorkflowPrimitiveParameterDefinition Integer(string name, string label, string defaultValue = "",
        double? minimum = null, double? maximum = null, string description = "") =>
        new(name, WorkflowPrimitiveParameterType.Integer, label, description, DefaultValue: defaultValue,
            Control: WorkflowPrimitiveParameterControl.Number, Minimum: minimum, Maximum: maximum);

    private static WorkflowPrimitiveParameterDefinition Number(string name, string label, string defaultValue = "",
        double? minimum = null, double? maximum = null) =>
        new(name, WorkflowPrimitiveParameterType.Number, label, DefaultValue: defaultValue,
            Control: WorkflowPrimitiveParameterControl.Number, Minimum: minimum, Maximum: maximum);

    private static WorkflowPrimitiveParameterDefinition Boolean(string name, string label) =>
        new(name, WorkflowPrimitiveParameterType.Boolean, label, DefaultValue: "false",
            Control: WorkflowPrimitiveParameterControl.Boolean);

    private static WorkflowPrimitiveParameterDefinition Choice(string name, string label, IReadOnlyList<string> options,
        string defaultValue = "") =>
        new(name, WorkflowPrimitiveParameterType.String, label, DefaultValue: defaultValue,
            EnumValues: options, Control: WorkflowPrimitiveParameterControl.Select);

    private static WorkflowPrimitiveParameterDefinition StepType(string name, string label, string defaultValue = "") =>
        new(name, WorkflowPrimitiveParameterType.String, label, DefaultValue: defaultValue,
            Control: WorkflowPrimitiveParameterControl.Select, UsesPrimitiveNames: true);

    private static WorkflowPrimitiveParameterDefinition[] ConcurrencyParameters() =>
    [
        Integer("max_concurrent_workers", "Max concurrent workers", "20", 1, 200),
        Integer("min_concurrent_workers", "Min concurrent workers", "0", 0, 200),
    ];

    private static WorkflowPrimitiveParameterDefinition[] ConnectorParameters() =>
    [
        Text("connector", "Connector", required: true),
        Text("operation", "Operation"),
        Text("path", "Path", description: "Connector-specific path; the connector's input allowlist still applies."),
        Choice("method", "HTTP method", ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"]),
        Integer("timeout_ms", "Timeout ms", "30000", 100, 300_000),
        Integer("retry", "Retries", "0", 0, 5),
        Choice("on_error", "On error", ["fail", "continue"], "fail"),
        Choice("on_missing", "If connector is missing", ["fail", "skip"], "fail"),
        Boolean("optional", "Optional connector"),
        Boolean("pass_through_input", "Return original input"),
        Choice("approval.policy", "Approval policy", ["required"]),
        Text("approval.service_ref", "Approval service reference"),
        Text("approval.node_id", "Approval routing node"),
        Text("approval.http_verb", "Approval HTTP verb"),
        Text("approval.resource", "Approval resource"),
        Text("approval.permission_scope", "Approval permission scope"),
        Integer("approval.expiration_seconds", "Approval validity seconds"),
        Integer("approval.status_check_interval_seconds", "Approval status interval seconds",
            ConnectorApprovalOptionsDefinition.DefaultStatusCheckIntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        Boolean("approval.destructive", "Destructive action"),
        Text("approval.team_id", "Approval team"),
        Text("approval.member_id", "Approval member"),
        Text("approval.workflow_id", "Approval workflow"),
        Text("approval.published_service_id", "Approval published service"),
        Text("approval.policy_reason", "Approval reason"),
    ];
}
