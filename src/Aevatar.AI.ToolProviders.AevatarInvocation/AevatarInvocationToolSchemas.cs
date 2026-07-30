namespace Aevatar.AI.ToolProviders.AevatarInvocation;

internal static class AevatarInvocationToolSchemas
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> WaitValues =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["wait"] = ["ack", "stream", "complete"],
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
        stringEnums: WaitValues);

    public static readonly string StartWorkflow = ProtoToolSchema.Build(
        StartWorkflowToolRequest.Descriptor,
        requiredFields: new HashSet<string>(StringComparer.Ordinal)
        {
            "workflow_id",
            "inputs",
        },
        stringEnums: WaitValues);

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
