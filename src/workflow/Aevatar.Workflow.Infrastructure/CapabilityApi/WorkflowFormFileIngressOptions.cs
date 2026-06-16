namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public sealed class WorkflowFormFileIngressOptions
{
    public const string SectionName = "WorkflowFormFileIngress";

    public string FileFieldName { get; set; } = "file";

    public string PayloadFieldName { get; set; } = "payload";
}
