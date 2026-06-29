namespace Aevatar.GAgentService.Hosting.Demo;

public sealed class GAgentServiceDemoOptions
{
    public bool? Enabled { get; set; }

    public string TenantId { get; set; } = "demo";

    public string AppId { get; set; } = "gagent-service";

    public string Namespace { get; set; } = "samples";

    public int ServingReadinessTimeoutSeconds { get; set; } = 30;

    public int ServingReadinessPollIntervalMilliseconds { get; set; } = 250;
}
