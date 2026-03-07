namespace Aevatar.Workflow.Core;

internal interface IWorkflowRunCapabilityDescriptor
{
    string Name { get; }

    IReadOnlyCollection<string> SupportedStepTypes { get; }

    IReadOnlyCollection<string> SupportedInternalSignalTypeUrls { get; }

    IReadOnlyCollection<string> SupportedResponseTypeUrls { get; }
}
