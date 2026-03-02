using Aevatar.Foundation.Runtime.Implementations.Orleans.Context;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Filters;

public sealed class OrleansAgentContextIncomingFilter : IIncomingGrainCallFilter
{
    private readonly ILogger<OrleansAgentContextIncomingFilter> _logger;

    public OrleansAgentContextIncomingFilter(ILogger<OrleansAgentContextIncomingFilter>? logger = null)
    {
        _logger = logger ?? NullLogger<OrleansAgentContextIncomingFilter>.Instance;
    }

    public async Task Invoke(IIncomingGrainCallContext context)
    {
        IReadOnlyDictionary<string, object?>? snapshot = null;
        try
        {
            snapshot = OrleansAgentContextRequestContext.SnapshotContextValues();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgentContext snapshot failed; proceeding without context isolation.");
        }

        try
        {
            await context.Invoke();
        }
        finally
        {
            if (snapshot != null)
            {
                try
                {
                    OrleansAgentContextRequestContext.RestoreContextValues(snapshot);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AgentContext restore failed after grain invocation.");
                }
            }
        }
    }
}

public sealed class OrleansAgentContextOutgoingFilter : IOutgoingGrainCallFilter
{
    private readonly IAgentContextAccessor _contextAccessor;
    private readonly ILogger<OrleansAgentContextOutgoingFilter> _logger;

    public OrleansAgentContextOutgoingFilter(
        IAgentContextAccessor contextAccessor,
        ILogger<OrleansAgentContextOutgoingFilter>? logger = null)
    {
        _contextAccessor = contextAccessor;
        _logger = logger ?? NullLogger<OrleansAgentContextOutgoingFilter>.Instance;
    }

    public async Task Invoke(IOutgoingGrainCallContext context)
    {
        try
        {
            var agentContext = _contextAccessor.Context;
            if (agentContext != null)
                OrleansAgentContextRequestContext.UpsertFromContext(agentContext);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgentContext normalization failed before outgoing grain call; continuing.");
        }

        await context.Invoke();
    }
}
