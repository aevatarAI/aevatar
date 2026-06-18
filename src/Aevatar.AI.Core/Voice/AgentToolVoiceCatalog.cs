using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Credentials;
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
    private readonly IReadOnlyList<ICredentialProvider> _credentialProviders;
    private readonly ILogger _logger;
    private volatile Lazy<Task<IReadOnlyList<VoiceToolDefinition>>>? _toolDefinitions;

    public AgentToolVoiceCatalog(
        IEnumerable<IAgentToolSource> toolSources,
        ICredentialProvider? credentialProvider = null,
        ILogger<AgentToolVoiceCatalog>? logger = null)
        : this(
            toolSources,
            credentialProvider is null ? [] : [credentialProvider],
            logger)
    {
    }

    public AgentToolVoiceCatalog(
        IEnumerable<IAgentToolSource> toolSources,
        IEnumerable<ICredentialProvider> credentialProviders,
        ILogger<AgentToolVoiceCatalog>? logger = null)
    {
        _toolSources = toolSources ?? throw new ArgumentNullException(nameof(toolSources));
        _credentialProviders = credentialProviders?.ToList() ?? [];
        _logger = logger ?? NullLogger<AgentToolVoiceCatalog>.Instance;
    }

    public async Task<IReadOnlyList<VoiceToolDefinition>> DiscoverAsync(
        VoiceToolExecutionContext? toolContext = null,
        CancellationToken ct = default)
    {
        if (toolContext is not null &&
            VoiceToolExecutionContextMapper.IsUsableCredentialRef(toolContext, DateTimeOffset.UtcNow))
        {
            var agentToolContext = await ResolveToolContextAsync(toolContext, ct);
            if (agentToolContext is null)
                return [];

            using var scope = AgentToolContextScope.Push(agentToolContext);
            return FilterVisibleDefinitions(
                await DiscoverAllToolsAsync(_toolSources, _logger, ct),
                agentToolContext.ToolVisibility);
        }

        while (true)
        {
            var current = _toolDefinitions;
            if (TryGetReusableTask(current, out var cached))
                return await cached;

            // Refactor (iter88/cluster-088):
            // Old: first-use discovery started before CompareExchange, multiplying source discovery
            // under parallel callers.
            // New: publish Lazy<Task<T>> first and let ExecutionAndPublication start discovery once.
            var candidate = new Lazy<Task<IReadOnlyList<VoiceToolDefinition>>>(
                () => DiscoverAllToolsAsync(_toolSources, _logger, ct),
                LazyThreadSafetyMode.ExecutionAndPublication);
            var winner = Interlocked.CompareExchange(ref _toolDefinitions, candidate, current);
            if (ReferenceEquals(winner, current))
                return await candidate.Value;
        }
    }

    private async Task<AgentToolExecutionContext?> ResolveToolContextAsync(
        VoiceToolExecutionContext toolContext,
        CancellationToken ct)
    {
        if (_credentialProviders.Count == 0)
            return null;

        var credentialRef = VoiceToolExecutionContextMapper.Normalize(toolContext.CredentialRef);
        if (credentialRef is null)
            return null;

        var nyxIdAccessToken = await ResolveCredentialRefAsync(credentialRef, ct);
        if (string.IsNullOrWhiteSpace(nyxIdAccessToken))
            return null;

        return VoiceToolExecutionContextMapper.ToAgentToolContext(toolContext, nyxIdAccessToken);
    }

    private async Task<string?> ResolveCredentialRefAsync(string credentialRef, CancellationToken ct)
    {
        foreach (var credentialProvider in _credentialProviders)
        {
            var credential = await credentialProvider.ResolveAsync(credentialRef, ct);
            if (!string.IsNullOrWhiteSpace(credential))
                return credential;
        }

        return null;
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

    private static IReadOnlyList<VoiceToolDefinition> FilterVisibleDefinitions(
        IReadOnlyList<VoiceToolDefinition> definitions,
        AgentToolVisibilityScope visibility)
    {
        if (!visibility.IsRestricted)
            return definitions;

        return definitions
            .Where(definition => visibility.Allows(definition.Name))
            .ToList();
    }
}
