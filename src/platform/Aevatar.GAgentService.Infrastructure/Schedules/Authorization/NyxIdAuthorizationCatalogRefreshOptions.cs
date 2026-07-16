namespace Aevatar.GAgentService.Infrastructure.Schedules.Authorization;

public sealed class NyxIdAuthorizationCatalogRefreshOptions
{
    public string EndpointBaseUrl { get; set; } = string.Empty;

    public TimeSpan Freshness { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan ObservationTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan ObservationPollInterval { get; set; } = TimeSpan.FromMilliseconds(50);
}
