namespace Aevatar.GAgentService.Infrastructure.Orchestration;

public sealed class ServiceExternalExposureOptions
{
    public const string SectionName = "GAgentService:ExternalExposure";

    public bool Enabled { get; set; }

    public string PublicBaseUrl { get; set; } = string.Empty;

    public bool RegisterAllPublishedServices { get; set; }

    public string[] OptInPolicyIds { get; set; } = [];
}
