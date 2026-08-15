namespace Aevatar.Studio.Application.Delivery;

public sealed class WorkflowDeliveryOptions
{
    public const string SectionName = "Aevatar:Delivery";

    public static IReadOnlyList<string> ShippedWorkflowNames { get; } = Array.AsReadOnly(new[]
    {
        "hr_onboarding_email_approval",
        "hr_monthly_attendance_approval",
        "hr_attendance_fill_reminder",
        "fin_invoice_precheck_approval",
        "fin_budget_variance_monitor",
    });

    public bool UseShippedWorkflowAllowlist { get; set; }

    public IList<string> AllowedWorkflowNames { get; set; } = [];

    public string PackageDirectory { get; set; } = "delivery-workflows";

    public int DefaultExpiryHours { get; set; } = 168;

    public int MaximumExpiryHours { get; set; } = 720;

    public string ConsoleBaseUrl { get; set; } = string.Empty;
}
