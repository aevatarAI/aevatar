namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public sealed class WorkflowExternalApprovalCallbackOptions
{
    public const string SectionName = "WorkflowExternalApprovalCallbacks";

    public bool Enabled { get; set; }

    public int RetryAfterSeconds { get; set; } = 5;

    public List<WorkflowExternalApprovalCallbackBindingOptions> Bindings { get; } = [];
}

public sealed class WorkflowExternalApprovalCallbackBindingOptions
{
    public string? RouteKey { get; set; }

    public string? SourceId { get; set; }

    public string? ExternalIdKind { get; set; }

    public string? ExternalIdJsonPath { get; set; }

    public string? InstanceCodeJsonPath { get; set; }

    public string? RequestIdJsonPath { get; set; }

    public string? StatusJsonPath { get; set; }

    public string? DeliveryIdHeader { get; set; }

    public string? DeliveryIdJsonPath { get; set; }

    public string? HmacSecret { get; set; }

    public string? HmacSignatureHeader { get; set; }

    public string? HmacTimestampHeader { get; set; }

    public int MaxTimestampSkewSeconds { get; set; } = 300;
}
