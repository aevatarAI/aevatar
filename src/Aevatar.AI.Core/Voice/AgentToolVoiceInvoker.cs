using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Core.Voice;

/// <summary>
/// Adapts the existing <see cref="IAgentToolSource"/> discovery model to the narrow
/// <see cref="IVoiceToolInvoker"/> port used by voice sessions.
/// </summary>
public sealed class AgentToolVoiceInvoker : IVoiceToolInvoker
{
    private readonly IEnumerable<IAgentToolSource> _toolSources;
    private readonly ILogger _logger;
    private volatile Task<IReadOnlyDictionary<string, IAgentTool>>? _toolIndex;

    public AgentToolVoiceInvoker(
        IEnumerable<IAgentToolSource> toolSources,
        ILogger<AgentToolVoiceInvoker>? logger = null)
    {
        _toolSources = toolSources ?? throw new ArgumentNullException(nameof(toolSources));
        _logger = logger ?? NullLogger<AgentToolVoiceInvoker>.Instance;
    }

    public async Task<string> ExecuteAsync(string toolName, string argumentsJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            throw new ArgumentException("Tool name is required.", nameof(toolName));

        var toolIndex = await GetOrDiscoverAsync(ct);
        if (!toolIndex.TryGetValue(toolName, out var tool))
            throw new InvalidOperationException($"Tool '{toolName}' not found");

        return await tool.ExecuteAsync(
            string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson,
            ct);
    }

    private Task<IReadOnlyDictionary<string, IAgentTool>> GetOrDiscoverAsync(CancellationToken ct)
    {
        while (true)
        {
            var current = _toolIndex;
            if (current != null && !current.IsFaulted && !current.IsCanceled)
                return current;

            var discoveryTask = DiscoverAllToolsAsync(_toolSources, _logger, ct);
            var winner = Interlocked.CompareExchange(ref _toolIndex, discoveryTask, current);
            if (winner == current)
                return discoveryTask;
        }
    }

    private static async Task<IReadOnlyDictionary<string, IAgentTool>> DiscoverAllToolsAsync(
        IEnumerable<IAgentToolSource> toolSources,
        ILogger logger,
        CancellationToken ct)
    {
        var index = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in toolSources)
        {
            IReadOnlyList<IAgentTool> tools;
            try
            {
                tools = await source.DiscoverToolsAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Voice tool source discovery failed: {Source}", source.GetType().Name);
                continue;
            }

            foreach (var tool in tools)
                index[tool.Name] = tool;
        }

        return index;
    }
}
