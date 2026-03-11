using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Infrastructure.Runs;

internal sealed class RuntimeWorkflowActorBindingReader : IWorkflowActorBindingReader
{
    private static readonly TimeSpan DefaultQueryTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UnsupportedActorProbeTimeout = TimeSpan.FromSeconds(1);
    private readonly RuntimeWorkflowQueryClient _queryClient;
    private readonly IAgentTypeVerifier _agentTypeVerifier;
    private readonly QueryWorkflowActorBindingRequestAdapter _queryAdapter = new();

    public RuntimeWorkflowActorBindingReader(
        RuntimeWorkflowQueryClient queryClient,
        IAgentTypeVerifier agentTypeVerifier)
    {
        _queryClient = queryClient ?? throw new ArgumentNullException(nameof(queryClient));
        _agentTypeVerifier = agentTypeVerifier ?? throw new ArgumentNullException(nameof(agentTypeVerifier));
    }

    public async Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();

        var isWorkflowActor = await IsWorkflowActorAsync(actorId, ct);

        try
        {
            var response = await _queryClient.QueryActorAsync<WorkflowActorBindingRespondedEvent>(
                actorId,
            WorkflowQueryRouteConventions.ActorBindingReplyStreamPrefix,
                isWorkflowActor ? DefaultQueryTimeout : UnsupportedActorProbeTimeout,
                (requestId, replyStreamId) => _queryAdapter.Map(actorId, requestId, replyStreamId),
            static (reply, requestId) => string.Equals(reply.RequestId, requestId, StringComparison.Ordinal),
            WorkflowQueryRouteConventions.BuildActorBindingTimeoutMessage,
            ct);

            return MapResponse(response, actorId);
        }
        catch (TimeoutException) when (!isWorkflowActor)
        {
            return WorkflowActorBinding.Unsupported(actorId);
        }
        catch (InvalidOperationException ex) when (LooksLikeMissingActor(ex))
        {
            return null;
        }
    }

    private async Task<bool> IsWorkflowActorAsync(string actorId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();

        return await _agentTypeVerifier.IsExpectedAsync(actorId, typeof(WorkflowGAgent), ct) ||
               await _agentTypeVerifier.IsExpectedAsync(actorId, typeof(WorkflowRunGAgent), ct);
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

    private static bool LooksLikeMissingActor(InvalidOperationException ex)
    {
        var message = ex.Message ?? string.Empty;
        return message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("not initialized", StringComparison.OrdinalIgnoreCase);
    }
}
