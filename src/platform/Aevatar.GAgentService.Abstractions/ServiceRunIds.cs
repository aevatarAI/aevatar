namespace Aevatar.GAgentService.Abstractions;

public static class ServiceRunIds
{
    public const string ActorPrefix = "service-run:";

    public static string BuildKey(string scopeId, string serviceId, string runId)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
            throw new ArgumentException("scopeId is required.", nameof(scopeId));
        if (string.IsNullOrWhiteSpace(serviceId))
            throw new ArgumentException("serviceId is required.", nameof(serviceId));
        if (string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("runId is required.", nameof(runId));

        return $"{Normalize(scopeId)}:{Normalize(serviceId)}:{Normalize(runId)}";
    }

    public static string BuildActorId(string scopeId, string serviceId, string runId) =>
        ActorPrefix + BuildKey(scopeId, serviceId, runId);

    private static string Normalize(string value) => value.Trim();
}
