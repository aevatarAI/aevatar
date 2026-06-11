namespace Aevatar.AI.ToolProviders.AevatarInvocation;

internal static class AevatarInvocationToolSchemas
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> WaitValues =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["wait"] = ["ack", "stream", "complete"],
            ["kind"] = ["text", "image", "audio", "video"],
        };

    public static readonly string InvokeGAgent = ProtoToolSchema.Build(
        InvokeGAgentToolRequest.Descriptor,
        requiredFields: new HashSet<string>(StringComparer.Ordinal) { "payload" },
        stringEnums: WaitValues,
        oneOfRequiredGroups:
        [
            ["actor_id"],
            ["actor_name"],
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
        requiredFields: new HashSet<string>(StringComparer.Ordinal) { "run_id" });
}
