using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Core.Voice;

/// <summary>
/// Adapts <see cref="IAgentToolSource"/> discovery to the narrow
/// <see cref="IVoiceToolCatalog"/> port used by voice sessions.
/// </summary>
public sealed class AgentToolVoiceCatalog : IVoiceToolCatalog
{
    private readonly IEnumerable<IAgentToolSource> _toolSources;
    private readonly ILogger _logger;
    private volatile Task<IReadOnlyList<VoiceToolDefinition>>? _toolDefinitions;

    public AgentToolVoiceCatalog(
        IEnumerable<IAgentToolSource> toolSources,
        ILogger<AgentToolVoiceCatalog>? logger = null)
    {
        _toolSources = toolSources ?? throw new ArgumentNullException(nameof(toolSources));
        _logger = logger ?? NullLogger<AgentToolVoiceCatalog>.Instance;
    }

    public Task<IReadOnlyList<VoiceToolDefinition>> DiscoverAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var current = _toolDefinitions;
            if (current != null && !current.IsFaulted && !current.IsCanceled)
                return current;

            var discoveryTask = DiscoverAllToolsAsync(_toolSources, _logger, ct);
            var winner = Interlocked.CompareExchange(ref _toolDefinitions, discoveryTask, current);
            if (winner == current)
                return discoveryTask;
        }
    }

    private static async Task<IReadOnlyList<VoiceToolDefinition>> DiscoverAllToolsAsync(
        IEnumerable<IAgentToolSource> toolSources,
        ILogger logger,
        CancellationToken ct)
    {
        var definitions = new Dictionary<string, VoiceToolDefinition>(StringComparer.OrdinalIgnoreCase);
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
            {
                if (string.IsNullOrWhiteSpace(tool.Name))
                    continue;

                definitions[tool.Name] = new VoiceToolDefinition
                {
                    Name = tool.Name,
                    Description = tool.Description ?? string.Empty,
                    ParametersSchema = string.IsNullOrWhiteSpace(tool.ParametersSchema)
                        ? "{}"
                        : tool.ParametersSchema,
                };
            }
        }

        return definitions.Values.ToList();
    }
}
