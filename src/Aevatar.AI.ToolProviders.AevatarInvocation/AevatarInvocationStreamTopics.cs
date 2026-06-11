namespace Aevatar.AI.ToolProviders.AevatarInvocation;

internal static class AevatarInvocationStreamTopics
{
    public static string ForActorRun(string actorId, string runId) =>
        $"aevatar://actors/{Uri.EscapeDataString(actorId)}/runs/{Uri.EscapeDataString(runId)}";

    public static string ForServiceRun(string scopeId, string serviceId, string runId) =>
        $"aevatar://scopes/{Uri.EscapeDataString(scopeId)}/services/{Uri.EscapeDataString(serviceId)}/runs/{Uri.EscapeDataString(runId)}";
}
