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
    private volatile Lazy<Task<IReadOnlyList<VoiceToolDefinition>>>? _toolDefinitions;

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
            if (TryGetReusableTask(current, out var cached))
                return cached;

            // Refactor (iter88/cluster-088):
            // Old: first-use discovery started before CompareExchange, multiplying source discovery
            // under parallel callers.
            // New: publish Lazy<Task<T>> first and let ExecutionAndPublication start discovery once.
            var candidate = new Lazy<Task<IReadOnlyList<VoiceToolDefinition>>>(
                () => DiscoverAllToolsAsync(_toolSources, _logger, ct),
                LazyThreadSafetyMode.ExecutionAndPublication);
            var winner = Interlocked.CompareExchange(ref _toolDefinitions, candidate, current);
            if (ReferenceEquals(winner, current))
                return candidate.Value;
        }
    }

    private static bool TryGetReusableTask(
        Lazy<Task<IReadOnlyList<VoiceToolDefinition>>>? current,
        out Task<IReadOnlyList<VoiceToolDefinition>> task)
    {
        task = null!;
        if (current == null)
            return false;

        if (!current.IsValueCreated)
        {
            task = current.Value;
            return true;
        }

        var existing = current.Value;
        if (existing.IsFaulted || existing.IsCanceled)
            return false;

        task = existing;
        return true;
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
