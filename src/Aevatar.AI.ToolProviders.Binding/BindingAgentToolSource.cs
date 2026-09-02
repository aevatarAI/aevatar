using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Binding.Ports;
using Aevatar.AI.ToolProviders.Binding.Tools;
using Aevatar.AI.ToolProviders.Workflow.Ports;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Binding;

/// <summary>
/// Binding tool source. Provides tools for listing, inspecting, binding,
/// and unbinding services in a scope.
/// Adapter port implementations must be registered by the infrastructure layer;
/// tools are conditionally registered based on which adapters are available.
/// </summary>
public sealed class BindingAgentToolSource : IAgentToolSource
{
    private readonly BindingToolOptions _options;
    private readonly IScopeBindingCommandPort? _commandPort;
    private readonly IScopeBindingQueryAdapter? _queryAdapter;
    private readonly IScopeBindingUnbindAdapter? _unbindAdapter;
    private readonly IWorkflowDefinitionCommandAdapter? _definitionAdapter;
    private readonly IScopeWorkflowCommandPort? _scopeWorkflowCommandPort;
    private readonly IScopeWorkflowQueryPort? _scopeWorkflowQueryPort;
    private readonly IExternalWorkflowCapabilityListPort? _externalCapabilityListPort;
    private readonly IExternalWorkflowCapabilityReadinessPort? _externalCapabilityReadinessPort;
    private readonly ILogger _logger;

    public BindingAgentToolSource(
        BindingToolOptions options,
        IScopeBindingCommandPort? commandPort = null,
        IScopeBindingQueryAdapter? queryAdapter = null,
        IScopeBindingUnbindAdapter? unbindAdapter = null,
        IWorkflowDefinitionCommandAdapter? definitionAdapter = null,
        IScopeWorkflowCommandPort? scopeWorkflowCommandPort = null,
        IScopeWorkflowQueryPort? scopeWorkflowQueryPort = null,
        IExternalWorkflowCapabilityListPort? externalCapabilityListPort = null,
        IExternalWorkflowCapabilityReadinessPort? externalCapabilityReadinessPort = null,
        ILogger<BindingAgentToolSource>? logger = null)
    {
        _options = options;
        _commandPort = commandPort;
        _queryAdapter = queryAdapter;
        _unbindAdapter = unbindAdapter;
        _definitionAdapter = definitionAdapter;
        _scopeWorkflowCommandPort = scopeWorkflowCommandPort;
        _scopeWorkflowQueryPort = scopeWorkflowQueryPort;
        _externalCapabilityListPort = externalCapabilityListPort;
        _externalCapabilityReadinessPort = externalCapabilityReadinessPort;
        _logger = logger ?? NullLogger<BindingAgentToolSource>.Instance;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        if (_commandPort == null && _queryAdapter == null &&
            _scopeWorkflowCommandPort == null && _scopeWorkflowQueryPort == null &&
            _externalCapabilityListPort == null && _externalCapabilityReadinessPort == null)
        {
            _logger.LogDebug("Binding adapter implementations not registered, skipping binding tools");
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);
        }

        var tools = new List<IAgentTool>();

        // Read-only tools (require query adapter)
        if (_queryAdapter != null)
        {
            tools.Add(new BindingListTool(_queryAdapter, _options));
            tools.Add(new BindingStatusTool(_queryAdapter));
        }

        // Bind (requires command port)
        if (_commandPort != null)
            tools.Add(new BindingBindTool(_commandPort, _definitionAdapter));

        // Unbind (requires unbind adapter)
        if (_unbindAdapter != null)
            tools.Add(new BindingUnbindTool(_unbindAdapter));

        // Refactor (iter97/cluster-598): Old/New
        //   Old pattern: Ornn skill recipes could not reach typed scope workflow ports through LLM tools.
        //   New principle: expose thin scope_workflows_* adapters without changing binding_* generic service tools.
        if (_scopeWorkflowCommandPort != null)
            tools.Add(new ScopeWorkflowsUpsertTool(_scopeWorkflowCommandPort));

        if (_scopeWorkflowQueryPort != null)
        {
            tools.Add(new ScopeWorkflowsListTool(_scopeWorkflowQueryPort, _options));
            tools.Add(new ScopeWorkflowsGetTool(_scopeWorkflowQueryPort));
        }

        if (_externalCapabilityListPort != null)
            tools.Add(new ListExternalWorkflowCapabilitiesTool(_externalCapabilityListPort, _options));

        if (_externalCapabilityReadinessPort != null)
            tools.Add(new InspectExternalWorkflowCapabilityReadinessTool(_externalCapabilityReadinessPort));

        _logger.LogInformation("Binding tools registered ({Count} tools)", tools.Count);
        return Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
    }
}
