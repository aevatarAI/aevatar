namespace Aevatar.Studio.Application.Delivery;

public sealed class WorkflowDeliveryOptions
{
    public const string SectionName = "Aevatar:Delivery";

    public IList<string> AllowedWorkflowNames { get; set; } = [];

    public string PackageDirectory { get; set; } = "delivery-workflows";

    public int DefaultExpiryHours { get; set; } = 168;

    public int MaximumExpiryHours { get; set; } = 720;

    public string ConsoleBaseUrl { get; set; } = string.Empty;
}
