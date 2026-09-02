namespace Aevatar.AI.ToolProviders.AevatarInvocation;

internal static class AevatarInvocationToolSchemas
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> WaitValues =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["wait"] = ["ack", "stream", "complete"],
            ["kind"] = ["text", "image", "audio", "video", "file"],
        };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> AcceptedOnlyWaitValues =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["wait"] = ["ack", "stream"],
            ["kind"] = ["text", "image", "audio", "video", "file"],
        };

    public static readonly string InvokeGAgent = ProtoToolSchema.Build(
        InvokeGAgentToolRequest.Descriptor,
        requiredFields: new HashSet<string>(StringComparer.Ordinal) { "payload" },
        stringEnums: WaitValues,
        oneOfRequiredGroups:
        [
            ["actor_id"],
            ["agent_kind"],
        ],
        emitTopLevelOneOf: false);

    public static readonly string InvokeTeam = ProtoToolSchema.Build(
        InvokeTeamToolRequest.Descriptor,
        requiredFields: new HashSet<string>(StringComparer.Ordinal)
        {
            "team_id",
            "endpoint_id",
            "payload",
        },
        stringEnums: WaitValues);

    public static readonly string InvokeMember = ProtoToolSchema.Build(
        InvokeMemberToolRequest.Descriptor,
        requiredFields: new HashSet<string>(StringComparer.Ordinal)
        {
            "member_id",
            "payload",
        },
        stringEnums: AcceptedOnlyWaitValues);

    public static readonly string StartWorkflow = ProtoToolSchema.Build(
        StartWorkflowToolRequest.Descriptor,
        requiredFields: new HashSet<string>(StringComparer.Ordinal)
        {
            "workflow_id",
            "inputs",
        },
        stringEnums: WaitValues,
        fieldDescriptions: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workflow_id"] =
                "Exact workflow id, resolved beforehand (for example via scope_workflows_get or the active sealed Agent Profile instructions naming a configured managed workflow). Never guess.",
            ["inputs.prompt"] =
                "The workflow's run input. Typed workflows require a NON-EMPTY serialized JSON string " +
                "matching the workflow's input contract, for example {\"period_label\":\"2026年8月\",\"submit\":false}. " +
                "Build the JSON from the user's request and any relevant read-only scoped connected-service " +
                "results already exposed in this turn; leave unavailable fields explicit instead of guessing. " +
                "Never pass an empty string, an unserialized object, or the user's natural-language sentence.",
        });

    public static readonly string ObserveRun = ProtoToolSchema.Build(
        ObserveRunToolRequest.Descriptor,
        oneOfRequiredGroups:
        [
            ["service_run"],
            ["gagent_terminal_correlation"],
            ["gagent_terminal_session"],
            ["workflow_current_state"],
        ],
        emitTopLevelOneOf: false);
}
