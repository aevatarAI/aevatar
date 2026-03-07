namespace Aevatar.Workflow.Core;

internal sealed record WorkflowRunCapabilityDescriptor(
    string Name,
    IReadOnlyCollection<string>? SupportedStepTypes = null,
    IReadOnlyCollection<string>? SupportedInternalSignalTypeUrls = null,
    IReadOnlyCollection<string>? SupportedResponseTypeUrls = null)
    : IWorkflowRunCapabilityDescriptor
{
    public IReadOnlyCollection<string> SupportedStepTypes { get; init; } =
        SupportedStepTypes ?? Array.Empty<string>();

    public IReadOnlyCollection<string> SupportedInternalSignalTypeUrls { get; init; } =
        SupportedInternalSignalTypeUrls ?? Array.Empty<string>();

    public IReadOnlyCollection<string> SupportedResponseTypeUrls { get; init; } =
        SupportedResponseTypeUrls ?? Array.Empty<string>();
}
