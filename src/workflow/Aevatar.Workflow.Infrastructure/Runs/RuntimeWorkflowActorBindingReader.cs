using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Infrastructure.Runs;

internal sealed class RuntimeWorkflowActorBindingReader : IWorkflowActorBindingReader
{
    private static readonly TimeSpan DefaultQueryTimeout = TimeSpan.FromSeconds(5);
    private readonly RuntimeWorkflowActorAccessor _actorAccessor;
    private readonly RuntimeWorkflowQueryClient _queryClient;
    private readonly IAgentTypeVerifier _agentTypeVerifier;
    private readonly QueryWorkflowActorBindingRequestAdapter _queryAdapter = new();

    public RuntimeWorkflowActorBindingReader(
        RuntimeWorkflowActorAccessor actorAccessor,
        RuntimeWorkflowQueryClient queryClient,
        IAgentTypeVerifier agentTypeVerifier)
    {
        _actorAccessor = actorAccessor ?? throw new ArgumentNullException(nameof(actorAccessor));
        _queryClient = queryClient ?? throw new ArgumentNullException(nameof(queryClient));
        _agentTypeVerifier = agentTypeVerifier ?? throw new ArgumentNullException(nameof(agentTypeVerifier));
    }

    public async Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();

        var actor = await _actorAccessor.GetAsync(actorId, ct);
        if (actor == null)
            return null;

        if (!await IsWorkflowActorAsync(actor, ct))
            return WorkflowActorBinding.Unsupported(actor.Id);

        var response = await _queryClient.QueryActorAsync<WorkflowActorBindingRespondedEvent>(
            actor,
            WorkflowQueryRouteConventions.ActorBindingReplyStreamPrefix,
            DefaultQueryTimeout,
            (requestId, replyStreamId) => _queryAdapter.Map(actor.Id, requestId, replyStreamId),
            static (reply, requestId) => string.Equals(reply.RequestId, requestId, StringComparison.Ordinal),
            WorkflowQueryRouteConventions.BuildActorBindingTimeoutMessage,
            ct);

        return MapResponse(response, actor.Id);
    }

    private async Task<bool> IsWorkflowActorAsync(IActor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ct.ThrowIfCancellationRequested();

        var runtimeType = actor.Agent.GetType();
        if (typeof(WorkflowGAgent).IsAssignableFrom(runtimeType) ||
            typeof(WorkflowRunGAgent).IsAssignableFrom(runtimeType))
        {
            return true;
        }

        return await _agentTypeVerifier.IsExpectedAsync(actor.Id, typeof(WorkflowGAgent), ct) ||
               await _agentTypeVerifier.IsExpectedAsync(actor.Id, typeof(WorkflowRunGAgent), ct);
    }

    private static WorkflowActorBinding MapResponse(
        WorkflowActorBindingRespondedEvent response,
        string fallbackActorId)
    {
        ArgumentNullException.ThrowIfNull(response);

        var actorId = string.IsNullOrWhiteSpace(response.ActorId)
            ? fallbackActorId
            : response.ActorId;
        var actorKind = NormalizeActorKind(response.ActorKind);

        return new WorkflowActorBinding(
            actorKind,
            actorId,
            response.DefinitionActorId ?? string.Empty,
            response.RunId ?? string.Empty,
            response.WorkflowName ?? string.Empty,
            response.WorkflowYaml ?? string.Empty,
            response.InlineWorkflowYamls.ToDictionary(
                static x => x.Key,
                static x => x.Value,
                StringComparer.OrdinalIgnoreCase));
    }

    private static WorkflowActorKind NormalizeActorKind(string? actorKind) =>
        actorKind?.Trim().ToLowerInvariant() switch
        {
            "definition" => WorkflowActorKind.Definition,
            "run" => WorkflowActorKind.Run,
            _ => WorkflowActorKind.Unsupported,
        };
}
