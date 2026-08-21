using Aevatar.AI.Abstractions.CodeExecution;
using Aevatar.AI.Abstractions.CodexExecution;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>Opt-in NyxID SSH, code, and Codex execution tools.</summary>
public sealed class NyxIdExecutionAgentToolSource : IAgentToolSource
{
    private readonly NyxIdToolOptions _options;
    private readonly NyxIdApiClient _client;
    private readonly IReadOnlyList<ICodexExecutionPort> _codexExecutionPorts;
    private readonly IReadOnlyList<ICodeExecutionPort> _codeExecutionPorts;
    private readonly ILogger _logger;

    public NyxIdExecutionAgentToolSource(
        NyxIdToolOptions options,
        NyxIdApiClient client,
        IEnumerable<ICodexExecutionPort>? codexExecutionPorts = null,
        IEnumerable<ICodeExecutionPort>? codeExecutionPorts = null,
        ILogger<NyxIdExecutionAgentToolSource>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _codexExecutionPorts = codexExecutionPorts?.ToArray() ?? [];
        _codeExecutionPorts = codeExecutionPorts?.ToArray() ?? [];
        _logger = logger ?? NullLogger<NyxIdExecutionAgentToolSource>.Instance;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.EffectiveTransportBaseUrl))
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);

        var tools = new List<IAgentTool>();
        if (_options.EnableSshExecTool)
        {
            tools.Add(new NyxIdSshExecTool(
                new NyxIdSshCommandExecutor(_client, _logger),
                _options));
        }

        AddCodeExecutionTool(tools);
        AddCodexExecutionTool(tools);
        _logger.LogInformation("NyxID execution tools registered ({Count} tools)", tools.Count);
        return Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
    }

    private void AddCodeExecutionTool(List<IAgentTool> tools)
    {
        if (_codeExecutionPorts.Count == 0)
            return;
        if (_codeExecutionPorts.Count != 1)
            throw new InvalidOperationException("code_execute requires exactly one ICodeExecutionPort registration.");

        tools.Add(new NyxIdCodeExecuteTool(_codeExecutionPorts[0]));
    }

    private void AddCodexExecutionTool(List<IAgentTool> tools)
    {
        var ports = new List<ICodexExecutionPort>();
        if (_options.EnableSshExecTool)
        {
            ports.Add(new PrivateSshCodexExecutionAdapter(
                new NyxIdSshCommandExecutor(_client, _logger)));
        }

        if (_options.EnableManagedCodexExecTool)
        {
            var managedPorts = _codexExecutionPorts
                .Where(static port => port.TargetKind == CodexExecutionTarget.TargetOneofCase.ManagedSandbox)
                .ToArray();
            if (managedPorts.Length != 1)
            {
                throw new InvalidOperationException(
                    "Managed codex_exec requires exactly one managed-sandbox ICodexExecutionPort registration.");
            }

            ports.Add(managedPorts[0]);
        }

        if (ports.Count > 0)
            tools.Add(new NyxIdCodexExecTool(ports, _options));
    }
}
