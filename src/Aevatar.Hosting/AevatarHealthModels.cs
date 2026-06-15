namespace Aevatar.Hosting;

public sealed class AevatarHostMetadata
{
    public string ServiceName { get; init; } = "Aevatar.Host.Api";
}

public static class AevatarHealthStatuses
{
    public const string Healthy = "healthy";
    public const string Degraded = "degraded";
    public const string Unhealthy = "unhealthy";

    public static bool IsHealthy(string status) =>
        string.Equals(status, Healthy, StringComparison.OrdinalIgnoreCase);
}

public sealed record AevatarHostStatusResponse(
    string Name,
    string Status);

public sealed record AevatarHealthComponentResponse(
    string Name,
    string Category,
    bool Critical,
    string Status,
    string Message,
    IReadOnlyDictionary<string, string> Details);

public sealed record AevatarHealthResponse(
    bool Ok,
    string Service,
    string Status,
    DateTimeOffset CheckedAtUtc,
    IReadOnlyList<AevatarHealthComponentResponse> Components);

public sealed record AevatarHealthContributorResult(
    string Status,
    string Message,
    IReadOnlyDictionary<string, string> Details)
{
    public static AevatarHealthContributorResult Healthy(
        string message,
        IReadOnlyDictionary<string, string>? details = null) =>
        new(
            AevatarHealthStatuses.Healthy,
            message,
            details ?? EmptyDetails);

    public static AevatarHealthContributorResult Degraded(
        string message,
        IReadOnlyDictionary<string, string>? details = null) =>
        new(
            AevatarHealthStatuses.Degraded,
            message,
            details ?? EmptyDetails);

    public static AevatarHealthContributorResult Unhealthy(
        string message,
        IReadOnlyDictionary<string, string>? details = null) =>
        new(
            AevatarHealthStatuses.Unhealthy,
            message,
            details ?? EmptyDetails);

    private static readonly IReadOnlyDictionary<string, string> EmptyDetails = new Dictionary<string, string>();
}
