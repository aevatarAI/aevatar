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
    private const string CatalogClientPublisherId = "workflow-definition-catalog-client";
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

    private readonly IActorRuntime _runtime;
    private readonly RuntimeWorkflowQueryClient _queryClient;
    private readonly IActorStateSnapshotReader? _snapshotReader;
    private readonly IActorEnvelopeDispatcher? _envelopeDispatcher;

    public ActorWorkflowDefinitionCatalog(
        IActorRuntime runtime,
        RuntimeWorkflowQueryClient queryClient,
        IActorStateSnapshotReader? snapshotReader = null,
        IActorEnvelopeDispatcher? envelopeDispatcher = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _queryClient = queryClient ?? throw new ArgumentNullException(nameof(queryClient));
        _snapshotReader = snapshotReader;
        _envelopeDispatcher = envelopeDispatcher;
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
        var envelope = CreateEnvelope(
            new UpsertWorkflowDefinitionRequestedEvent
            {
                WorkflowName = workflowName,
                WorkflowYaml = yaml,
            });
        if (_envelopeDispatcher != null)
        {
            await _envelopeDispatcher.DispatchAsync(actor.Id, envelope, ct);
            return;
        }

        await actor.HandleEventAsync(envelope, ct);
    }

    public async Task<string?> GetYamlAsync(string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var workflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(name);
        if (string.IsNullOrWhiteSpace(workflowName))
            return null;

        var state = await TryGetCatalogStateAsync(ct);
        if (state != null)
        {
            return state.Entries.TryGetValue(workflowName, out var entry)
                ? entry.WorkflowYaml
                : null;
        }

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

        var state = await TryGetCatalogStateAsync(ct);
        if (state != null)
        {
            return state.Entries.Keys
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

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

    private Task<WorkflowDefinitionCatalogState?> TryGetCatalogStateAsync(CancellationToken ct)
    {
        if (_snapshotReader == null)
            return Task.FromResult<WorkflowDefinitionCatalogState?>(null);

        return _snapshotReader.GetStateAsync<WorkflowDefinitionCatalogState>(CatalogActorId, ct);
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
            PublisherId = CatalogClientPublisherId,
            Direction = EventDirection.Down,
            TargetActorId = CatalogActorId,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
}
