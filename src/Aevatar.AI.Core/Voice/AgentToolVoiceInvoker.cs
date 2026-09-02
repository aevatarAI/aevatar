using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Credentials;
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
    private readonly IReadOnlyList<ICredentialProvider> _credentialProviders;
    private readonly IAgentToolExecutionPort _toolExecutionPort;
    private readonly ILogger _logger;
    private volatile Lazy<Task<IReadOnlyDictionary<string, IAgentTool>>>? _toolIndex;

    public AgentToolVoiceInvoker(
        IEnumerable<IAgentToolSource> toolSources,
        IAgentToolExecutionPort toolExecutionPort,
        ICredentialProvider? credentialProvider = null,
        ILogger<AgentToolVoiceInvoker>? logger = null)
        : this(
            toolSources,
            toolExecutionPort,
            credentialProvider is null ? [] : [credentialProvider],
            logger)
    {
    }

    public AgentToolVoiceInvoker(
        IEnumerable<IAgentToolSource> toolSources,
        IAgentToolExecutionPort toolExecutionPort,
        IEnumerable<ICredentialProvider> credentialProviders,
        ILogger<AgentToolVoiceInvoker>? logger = null)
    {
        _toolSources = toolSources ?? throw new ArgumentNullException(nameof(toolSources));
        _toolExecutionPort = toolExecutionPort ?? throw new ArgumentNullException(nameof(toolExecutionPort));
        _credentialProviders = credentialProviders?.ToList() ?? [];
        _logger = logger ?? NullLogger<AgentToolVoiceInvoker>.Instance;
    }

    public async Task<string> ExecuteAsync(
        string ownerActorId,
        string sessionId,
        string callId,
        long issuedAtUnixMs,
        string toolName,
        string argumentsJson,
        VoiceToolExecutionContext? toolContext = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerActorId))
            throw new ArgumentException("Owner actor id is required.", nameof(ownerActorId));
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Voice session id is required.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(callId))
            throw new ArgumentException("Provider call id is required.", nameof(callId));
        if (issuedAtUnixMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(issuedAtUnixMs));
        if (string.IsNullOrWhiteSpace(toolName))
            throw new ArgumentException("Tool name is required.", nameof(toolName));

        var normalizedOwnerActorId = ownerActorId.Trim();
        var normalizedSessionId = sessionId.Trim();
        var normalizedCallId = callId.Trim();

        var toolIndex = await GetOrDiscoverAsync(ct);
        if (!toolIndex.TryGetValue(toolName, out var tool))
            throw new InvalidOperationException($"Tool '{toolName}' not found");

        var arguments = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;

        if (toolContext is null ||
            !VoiceToolExecutionContextMapper.IsUsableCredentialRef(toolContext, DateTimeOffset.UtcNow))
            return await ExecuteAdmittedAsync(
                tool,
                arguments,
                normalizedOwnerActorId,
                normalizedSessionId,
                normalizedCallId,
                issuedAtUnixMs,
                AgentToolExecutionContext.Empty,
                ct);

        var agentToolContext = await ResolveToolContextAsync(toolContext, ct);
        if (agentToolContext is null)
            return await ExecuteAdmittedAsync(
                tool,
                arguments,
                normalizedOwnerActorId,
                normalizedSessionId,
                normalizedCallId,
                issuedAtUnixMs,
                AgentToolExecutionContext.Empty,
                ct);
        if (!agentToolContext.ToolVisibility.Allows(toolName))
            throw new InvalidOperationException($"Tool '{toolName}' not found");

        return await ExecuteAdmittedAsync(
            tool,
            arguments,
            normalizedOwnerActorId,
            normalizedSessionId,
            normalizedCallId,
            issuedAtUnixMs,
            agentToolContext,
            ct);
    }

    private async Task<string> ExecuteAdmittedAsync(
        IAgentTool tool,
        string argumentsJson,
        string ownerActorId,
        string sessionId,
        string callId,
        long issuedAtUnixMs,
        AgentToolExecutionContext executionContext,
        CancellationToken ct)
    {
        var requestId = CreateRequestId(sessionId, callId);
        var outcome = await _toolExecutionPort.ExecuteAsync(
            new AgentToolExecutionRequest(
                tool,
                argumentsJson,
                executionContext with
                {
                    Request = new AgentToolRequestIdentity(
                        requestId,
                        callId,
                        requestId,
                        issuedAtUnixMs),
                    ExecutionOwner = AgentToolExecutionOwners.Actor(ownerActorId),
                },
                AgentToolApprovalContinuationMode.None,
                null),
            ct).ConfigureAwait(false);
        return outcome.ResultJson;
    }

    private static string CreateRequestId(string sessionId, string callId) =>
        $"voice:v1:{Uri.EscapeDataString(sessionId)}:{Uri.EscapeDataString(callId)}";

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
