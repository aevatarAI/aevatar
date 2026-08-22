using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Binding.Tools;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;

namespace Aevatar.AI.ToolProviders.Binding;

/// <summary>
/// Provides the read-only tools used to select and inspect exact external capabilities while
/// authoring workflows. The source is composed through an explicit tool set and is not registered
/// as a global agent tool source.
/// </summary>
public sealed class WorkflowExternalCapabilityAuthoringToolSource(
    WorkflowExternalCapabilityToolOptions options,
    IExternalWorkflowCapabilityListPort externalCapabilityListPort,
    IExternalWorkflowCapabilityReadinessPort externalCapabilityReadinessPort,
    IWorkflowExplicitRequestPreviewService explicitRequestPreviewService) : IAgentToolSource
{
    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IAgentTool>>(
        [
            new ListExternalWorkflowCapabilitiesTool(externalCapabilityListPort, options),
            new InspectExternalWorkflowCapabilityReadinessTool(externalCapabilityReadinessPort),
            new PreviewWorkflowExplicitRequestsTool(explicitRequestPreviewService),
        ]);
}
