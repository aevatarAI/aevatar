namespace Aevatar.GAgentService.Hosting.Demo;

public sealed class GAgentServiceDemoOptions
{
    public bool? Enabled { get; set; }

    public string TenantId { get; set; } = "demo";

    public string AppId { get; set; } = "gagent-service";

    public string Namespace { get; set; } = "samples";

    public TimeSpan ReadinessObservationTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan ReadinessObservationPollInterval { get; set; } = TimeSpan.FromMilliseconds(200);
}
