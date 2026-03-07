using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Infrastructure.Workflows;

public sealed class ActorWorkflowDefinitionCatalog : IWorkflowDefinitionCatalog
{
    private const string CatalogActorId = "workflow-definition-catalog";
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

    private readonly IActorRuntime _runtime;
    private readonly RuntimeWorkflowQueryClient _queryClient;

    public ActorWorkflowDefinitionCatalog(
        IActorRuntime runtime,
        RuntimeWorkflowQueryClient queryClient)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _queryClient = queryClient ?? throw new ArgumentNullException(nameof(queryClient));
    }

    public async Task UpsertAsync(string name, string yaml, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var workflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(name);
        if (string.IsNullOrWhiteSpace(workflowName))
            throw new ArgumentException("Workflow name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(yaml))
            throw new ArgumentException("Workflow yaml is required.", nameof(yaml));

        var actor = await GetOrCreateCatalogActorAsync(ct);
        await actor.HandleEventAsync(
            CreateEnvelope(
                new UpsertWorkflowDefinitionRequestedEvent
                {
                    WorkflowName = workflowName,
                    WorkflowYaml = yaml,
                }),
            ct);
    }

    public async Task<string?> GetYamlAsync(string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var workflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(name);
        if (string.IsNullOrWhiteSpace(workflowName))
            return null;

        var actor = await _runtime.GetAsync(CatalogActorId);
        if (actor == null)
            return null;

        var response = await _queryClient.QueryActorAsync<WorkflowDefinitionRespondedEvent>(
            actor,
            WorkflowDefinitionCatalogQueryRouteConventions.DefinitionReplyStreamPrefix,
            QueryTimeout,
            (requestId, replyStreamId) => CreateEnvelope(
                new QueryWorkflowDefinitionRequestedEvent
                {
                    RequestId = requestId,
                    ReplyStreamId = replyStreamId,
                    WorkflowName = workflowName,
                }),
            static (reply, requestId) => string.Equals(reply.RequestId, requestId, StringComparison.Ordinal),
            WorkflowDefinitionCatalogQueryRouteConventions.BuildDefinitionTimeoutMessage,
            ct);

        return response.Found ? response.WorkflowYaml : null;
    }

    public async Task<IReadOnlyList<string>> GetNamesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var actor = await _runtime.GetAsync(CatalogActorId);
        if (actor == null)
            return [];

        var response = await _queryClient.QueryActorAsync<WorkflowDefinitionNamesRespondedEvent>(
            actor,
            WorkflowDefinitionCatalogQueryRouteConventions.NamesReplyStreamPrefix,
            QueryTimeout,
            (requestId, replyStreamId) => CreateEnvelope(
                new QueryWorkflowDefinitionNamesRequestedEvent
                {
                    RequestId = requestId,
                    ReplyStreamId = replyStreamId,
                }),
            static (reply, requestId) => string.Equals(reply.RequestId, requestId, StringComparison.Ordinal),
            WorkflowDefinitionCatalogQueryRouteConventions.BuildNamesTimeoutMessage,
            ct);

        return response.WorkflowNames
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IActor> GetOrCreateCatalogActorAsync(CancellationToken ct)
    {
        var existing = await _runtime.GetAsync(CatalogActorId);
        if (existing != null)
            return existing;

        return await _runtime.CreateAsync<WorkflowDefinitionCatalogGAgent>(CatalogActorId, ct);
    }

    private static EventEnvelope CreateEnvelope(IMessage message) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(message),
            PublisherId = CatalogActorId,
            Direction = EventDirection.Self,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
}
