using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Binding.Ports;
using Aevatar.AI.ToolProviders.Binding.Tools;
using Aevatar.AI.ToolProviders.Workflow.Ports;
using Aevatar.GAgentService.Abstractions.Ports;
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
    private readonly ILogger _logger;

    public BindingAgentToolSource(
        BindingToolOptions options,
        IScopeBindingCommandPort? commandPort = null,
        IScopeBindingQueryAdapter? queryAdapter = null,
        IScopeBindingUnbindAdapter? unbindAdapter = null,
        IWorkflowDefinitionCommandAdapter? definitionAdapter = null,
        ILogger<BindingAgentToolSource>? logger = null)
    {
        _options = options;
        _commandPort = commandPort;
        _queryAdapter = queryAdapter;
        _unbindAdapter = unbindAdapter;
        _definitionAdapter = definitionAdapter;
        _logger = logger ?? NullLogger<BindingAgentToolSource>.Instance;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        if (_commandPort == null && _queryAdapter == null)
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

        _logger.LogInformation("Binding tools registered ({Count} tools)", tools.Count);
        return Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
    }
}
