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
    private volatile Lazy<Task<IReadOnlyDictionary<string, IAgentTool>>>? _toolIndex;

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

        var arguments = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;

        // Refactor (cluster-voice-tool-caller-credential): voice tool calls arrive on an actor turn
        // with no caller AsyncLocal context, so the caller's NyxID token is carried across that
        // boundary via VoiceCallerCredentialScope and mapped here onto AgentToolContextScope — that's
        // what lets NyxID-backed tools (nyxid_proxy, use_skill, ...) authenticate as the caller.
        var callerToken = VoiceCallerCredentialScope.CurrentNyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(callerToken))
            return await tool.ExecuteAsync(arguments, ct);

        using var _ = AgentToolContextScope.Push(
            AgentToolExecutionContext.Empty with
            {
                Credentials = new AgentToolCredentials(callerToken, null, null),
            });
        return await tool.ExecuteAsync(arguments, ct);
    }

    private Task<IReadOnlyDictionary<string, IAgentTool>> GetOrDiscoverAsync(CancellationToken ct)
    {
        while (true)
        {
            var current = _toolIndex;
            if (TryGetReusableTask(current, out var cached))
                return cached;

            // Refactor (iter88/cluster-088):
            // Old: first-use discovery started before CompareExchange, so losing callers still
            // discovered all sources.
            // New: publish a non-started Lazy<Task<T>> and evaluate only the winning value.
            var candidate = new Lazy<Task<IReadOnlyDictionary<string, IAgentTool>>>(
                () => DiscoverAllToolsAsync(_toolSources, _logger, ct),
                LazyThreadSafetyMode.ExecutionAndPublication);
            var winner = Interlocked.CompareExchange(ref _toolIndex, candidate, current);
            if (ReferenceEquals(winner, current))
                return candidate.Value;
        }
    }

    private static bool TryGetReusableTask(
        Lazy<Task<IReadOnlyDictionary<string, IAgentTool>>>? current,
        out Task<IReadOnlyDictionary<string, IAgentTool>> task)
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
