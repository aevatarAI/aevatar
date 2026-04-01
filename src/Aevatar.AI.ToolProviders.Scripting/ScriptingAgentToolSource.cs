using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Scripting.Ports;
using Aevatar.AI.ToolProviders.Scripting.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Scripting;

/// <summary>
/// Scripting tool source. Provides tools for compiling, executing, and managing
/// C# scripts that LLM agents can generate and run to solve problems.
/// Adapter port implementations must be registered by the infrastructure layer;
/// tools are conditionally registered based on which adapters are available.
/// </summary>
public sealed class ScriptingAgentToolSource : IAgentToolSource
{
    private readonly ScriptingToolOptions _options;
    private readonly IScriptToolCompilationAdapter? _compilationPort;
    private readonly IScriptToolSandboxExecutionAdapter? _executionPort;
    private readonly IScriptToolCatalogCommandAdapter? _catalogCommandPort;
    private readonly IScriptToolCatalogQueryAdapter? _catalogQueryPort;
    private readonly ILogger _logger;

    public ScriptingAgentToolSource(
        ScriptingToolOptions options,
        IScriptToolCompilationAdapter? compilationPort = null,
        IScriptToolSandboxExecutionAdapter? executionPort = null,
        IScriptToolCatalogCommandAdapter? catalogCommandPort = null,
        IScriptToolCatalogQueryAdapter? catalogQueryPort = null,
        ILogger<ScriptingAgentToolSource>? logger = null)
    {
        _options = options;
        _compilationPort = compilationPort;
        _executionPort = executionPort;
        _catalogCommandPort = catalogCommandPort;
        _catalogQueryPort = catalogQueryPort;
        _logger = logger ?? NullLogger<ScriptingAgentToolSource>.Instance;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        if (_catalogQueryPort == null && _compilationPort == null)
        {
            _logger.LogDebug("Scripting adapter implementations not registered, skipping scripting tools");
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);
        }

        var tools = new List<IAgentTool>();

        // Read-only discovery tools (require catalog query adapter)
        if (_catalogQueryPort != null)
        {
            tools.Add(new ScriptListTool(_catalogQueryPort, _options));
            tools.Add(new ScriptStatusTool(_catalogQueryPort));
            tools.Add(new ScriptSourceTool(_catalogQueryPort));
        }

        // Compile (requires compilation adapter)
        if (_compilationPort != null)
            tools.Add(new ScriptCompileTool(_compilationPort, _options));

        // Execute in sandbox (requires execution adapter)
        if (_executionPort != null)
            tools.Add(new ScriptExecuteTool(_executionPort, _options));

        // Catalog mutations (require catalog command adapter)
        if (_catalogCommandPort != null)
        {
            tools.Add(new ScriptPromoteTool(_catalogCommandPort));
            tools.Add(new ScriptRollbackTool(_catalogCommandPort));
        }

        _logger.LogInformation("Scripting tools registered ({Count} tools)", tools.Count);
        return Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
    }
}
