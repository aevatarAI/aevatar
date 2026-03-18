namespace Aevatar.GAgentService.Hosting.Demo;

public sealed class GAgentServiceDemoOptions
{
    public bool? Enabled { get; set; }

    public string TenantId { get; set; } = "demo";

    public string AppId { get; set; } = "gagent-service";

    public string Namespace { get; set; } = "samples";
}
