using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Binding.Tools;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;

namespace Aevatar.AI.ToolProviders.Binding;

/// <summary>
/// Narrow workflow-authoring discovery surface. It exposes only the two read-only
/// capability tools required to select and inspect an exact external workflow route.
/// </summary>
public sealed class WorkflowAuthoringCapabilityReadAgentToolSource : IAgentToolSource
{
    private readonly IExternalWorkflowCapabilityListPort _listPort;
    private readonly IExternalWorkflowCapabilityReadinessPort _readinessPort;
    private readonly BindingToolOptions _options;

    public WorkflowAuthoringCapabilityReadAgentToolSource(
        IExternalWorkflowCapabilityListPort listPort,
        IExternalWorkflowCapabilityReadinessPort readinessPort,
        BindingToolOptions options)
    {
        _listPort = listPort ?? throw new ArgumentNullException(nameof(listPort));
        _readinessPort = readinessPort ?? throw new ArgumentNullException(nameof(readinessPort));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IAgentTool>>(
        [
            new ListExternalWorkflowCapabilitiesTool(_listPort, _options),
            new InspectExternalWorkflowCapabilityReadinessTool(_readinessPort),
        ]);
}
