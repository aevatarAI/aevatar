using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.Assemblers;
using Aevatar.GAgentService.Core.Ports;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Core.GAgents;

[GAgent("gagent.service.revision-catalog")]
public sealed class ServiceRevisionCatalogGAgent : GAgentBase<ServiceRevisionCatalogState>
{
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IReadOnlyDictionary<ServiceImplementationKind, IServiceImplementationAdapter> _adapters;
    private readonly PreparedServiceRevisionArtifactAssembler _artifactAssembler;

    public ServiceRevisionCatalogGAgent(
        IActorDispatchPort dispatchPort,
        IEnumerable<IServiceImplementationAdapter> adapters,
        PreparedServiceRevisionArtifactAssembler artifactAssembler)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _artifactAssembler = artifactAssembler ?? throw new ArgumentNullException(nameof(artifactAssembler));
        _adapters = (adapters ?? throw new ArgumentNullException(nameof(adapters)))
            .ToDictionary(x => x.ImplementationKind, x => x);
        InitializeId();
    }

    [EventHandler]
    public async Task HandleCreateRevisionAsync(CreateServiceRevisionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateRevisionSpec(command.Spec);
        EnsureCatalogIdentity(command.Spec.Identity, allowInitialize: true);

        var revisionId = command.Spec.RevisionId.Trim();
        if (State.Revisions.ContainsKey(revisionId))
            throw new InvalidOperationException($"Revision '{revisionId}' already exists for service '{ServiceKeys.Build(command.Spec.Identity)}'.");

        await PersistDomainEventAsync(new ServiceRevisionCreatedEvent
        {
            Spec = command.Spec.Clone(),
            CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        });
        await DispatchInvocationRevisionObservationAsync(CancellationToken.None);
    }

    [EventHandler]
    public async Task HandlePrepareRevisionAsync(PrepareServiceRevisionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureCatalogIdentity(command.Identity, allowInitialize: false);
        var record = GetRequiredRevision(command.RevisionId);
        var spec = record.Spec?.Clone() ?? throw new InvalidOperationException($"Revision '{command.RevisionId}' has no authoring spec.");
        var adapter = GetRequiredAdapter(spec.ImplementationKind);
        var serviceKey = ServiceKeys.Build(command.Identity);

        try
        {
            var prepared = await adapter.PrepareRevisionAsync(
                new PrepareServiceRevisionRequest
                {
                    ServiceKey = serviceKey,
                    Spec = spec,
                },
                CancellationToken.None);
            var assembled = _artifactAssembler.Assemble(prepared);

            await PersistDomainEventAsync(new ServiceRevisionPreparedEvent
            {
                Identity = command.Identity.Clone(),
                RevisionId = command.RevisionId ?? string.Empty,
                ImplementationKind = assembled.ImplementationKind,
                ArtifactHash = assembled.ArtifactHash ?? string.Empty,
                Endpoints = { assembled.Endpoints.Select(x => x.Clone()) },
                PreparedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                // Refactor (iter100/cluster-100): Old callers rehydrated this from a process-local artifact store. / New the committed prepared event is the artifact authority.
                PreparedArtifact = assembled.Clone(),
            });
            await DispatchInvocationRevisionObservationAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            await PersistDomainEventAsync(new ServiceRevisionPreparationFailedEvent
            {
                Identity = command.Identity.Clone(),
                RevisionId = command.RevisionId ?? string.Empty,
                FailureReason = ex.Message,
                OccurredAt = Timestamp.FromDateTime(DateTime.UtcNow),
            });
            await DispatchInvocationRevisionObservationAsync(CancellationToken.None);
            throw;
        }
    }

    [EventHandler]
    public async Task HandlePublishRevisionAsync(PublishServiceRevisionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureCatalogIdentity(command.Identity, allowInitialize: false);
        var record = GetRequiredRevision(command.RevisionId);
        if (record.Status != ServiceRevisionStatus.Prepared &&
            record.Status != ServiceRevisionStatus.Published)
        {
            throw new InvalidOperationException($"Revision '{command.RevisionId}' must be prepared before publish.");
        }

        var spec = record.Spec?.Clone()
            ?? throw new InvalidOperationException($"Revision '{command.RevisionId}' has no authoring spec.");
        var adapter = GetRequiredAdapter(spec.ImplementationKind);
        var revalidated = await adapter.PrepareRevisionAsync(
            new PrepareServiceRevisionRequest
            {
                ServiceKey = ServiceKeys.Build(command.Identity),
                Spec = spec,
            },
            CancellationToken.None);
        var revalidatedArtifact = _artifactAssembler.Assemble(revalidated);
        if (!string.Equals(record.ArtifactHash, revalidatedArtifact.ArtifactHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Revision '{command.RevisionId}' revalidated to a different prepared artifact.");
        }

        await PersistDomainEventAsync(new ServiceRevisionPublishedEvent
        {
            Identity = command.Identity.Clone(),
            RevisionId = command.RevisionId ?? string.Empty,
            PublishedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        });
        await DispatchInvocationRevisionObservationAsync(CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleRetireRevisionAsync(RetireServiceRevisionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureCatalogIdentity(command.Identity, allowInitialize: false);
        _ = GetRequiredRevision(command.RevisionId);
        await PersistDomainEventAsync(new ServiceRevisionRetiredEvent
        {
            Identity = command.Identity.Clone(),
            RevisionId = command.RevisionId ?? string.Empty,
            RetiredAt = Timestamp.FromDateTime(DateTime.UtcNow),
        });
        await DispatchInvocationRevisionObservationAsync(CancellationToken.None);
    }

    protected override ServiceRevisionCatalogState TransitionState(ServiceRevisionCatalogState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ServiceRevisionCreatedEvent>(ApplyCreated)
            .On<ServiceRevisionPreparedEvent>(ApplyPrepared)
            .On<ServiceRevisionPreparationFailedEvent>(ApplyPreparationFailed)
            .On<ServiceRevisionPublishedEvent>(ApplyPublished)
            .On<ServiceRevisionRetiredEvent>(ApplyRetired)
            .OrCurrent();

    private static ServiceRevisionCatalogState ApplyCreated(ServiceRevisionCatalogState state, ServiceRevisionCreatedEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Spec?.Identity?.Clone() ?? new ServiceIdentity();
        next.Revisions[evt.Spec?.RevisionId ?? string.Empty] = new ServiceRevisionRecordState
        {
            Spec = evt.Spec?.Clone() ?? new ServiceRevisionSpec(),
            Status = ServiceRevisionStatus.Created,
            CreatedAt = evt.CreatedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow),
        };
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Spec?.Identity, evt.Spec?.RevisionId, "created");
        return next;
    }

    private static ServiceRevisionCatalogState ApplyPrepared(ServiceRevisionCatalogState state, ServiceRevisionPreparedEvent evt)
    {
        var next = state.Clone();
        var record = next.Revisions[evt.RevisionId];
        record.Status = ServiceRevisionStatus.Prepared;
        record.ArtifactHash = evt.ArtifactHash ?? string.Empty;
        record.Endpoints.Clear();
        record.Endpoints.Add(evt.Endpoints.Select(x => x.Clone()));
        // Refactor (iter100/cluster-100): Old prepared artifacts lived beside the actor in a singleton. / New replay restores them from committed catalog state.
        record.PreparedArtifact = evt.PreparedArtifact?.Clone() ?? new PreparedServiceRevisionArtifact();
        record.PreparedAt = evt.PreparedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
        record.FailureReason = string.Empty;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.RevisionId, "prepared");
        return next;
    }

    private static ServiceRevisionCatalogState ApplyPreparationFailed(ServiceRevisionCatalogState state, ServiceRevisionPreparationFailedEvent evt)
    {
        var next = state.Clone();
        var record = next.Revisions[evt.RevisionId];
        record.Status = ServiceRevisionStatus.PreparationFailed;
        record.FailureReason = evt.FailureReason ?? string.Empty;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.RevisionId, "prepare-failed");
        return next;
    }

    private static ServiceRevisionCatalogState ApplyPublished(ServiceRevisionCatalogState state, ServiceRevisionPublishedEvent evt)
    {
        var next = state.Clone();
        var record = next.Revisions[evt.RevisionId];
        record.Status = ServiceRevisionStatus.Published;
        record.PublishedAt = evt.PublishedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.RevisionId, "published");
        return next;
    }

    private static ServiceRevisionCatalogState ApplyRetired(ServiceRevisionCatalogState state, ServiceRevisionRetiredEvent evt)
    {
        var next = state.Clone();
        var record = next.Revisions[evt.RevisionId];
        record.Status = ServiceRevisionStatus.Retired;
        record.RetiredAt = evt.RetiredAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.RevisionId, "retired");
        return next;
    }

    private ServiceRevisionRecordState GetRequiredRevision(string revisionId)
    {
        if (string.IsNullOrWhiteSpace(revisionId))
            throw new InvalidOperationException("revision_id is required.");
        if (!State.Revisions.TryGetValue(revisionId, out var record))
            throw new InvalidOperationException($"Revision '{revisionId}' was not found.");

        return record;
    }

    private IServiceImplementationAdapter GetRequiredAdapter(ServiceImplementationKind implementationKind)
    {
        if (!_adapters.TryGetValue(implementationKind, out var adapter))
            throw new InvalidOperationException($"No service implementation adapter is registered for '{implementationKind}'.");

        return adapter;
    }

    private void EnsureCatalogIdentity(ServiceIdentity identity, bool allowInitialize)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var requested = ServiceKeys.Build(identity);
        var currentIdentity = State.Identity?.Clone();
        if (currentIdentity == null || string.IsNullOrWhiteSpace(currentIdentity.ServiceId))
        {
            if (allowInitialize)
                return;

            throw new InvalidOperationException($"Service revision catalog '{requested}' does not exist.");
        }

        var existing = ServiceKeys.Build(currentIdentity);
        if (!string.Equals(existing, requested, StringComparison.Ordinal))
            throw new InvalidOperationException($"Service revision catalog actor '{Id}' is bound to '{existing}', but got '{requested}'.");
    }

    private static void ValidateRevisionSpec(ServiceRevisionSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Identity == null)
            throw new InvalidOperationException("service identity is required.");
        _ = ServiceKeys.Build(spec.Identity);
        if (string.IsNullOrWhiteSpace(spec.RevisionId))
            throw new InvalidOperationException("revision_id is required.");
        if (spec.ImplementationKind == ServiceImplementationKind.Unspecified)
            throw new InvalidOperationException("implementation_kind is required.");
        if (spec.ImplementationSpecCase == ServiceRevisionSpec.ImplementationSpecOneofCase.None)
            throw new InvalidOperationException("implementation_spec is required.");
    }

    private static string BuildEventId(ServiceIdentity? identity, string? revisionId, string suffix)
    {
        var serviceKey = identity == null ? "unbound" : ServiceKeys.Build(identity);
        return $"{serviceKey}:{revisionId ?? "unknown"}:{suffix}";
    }

    private Task DispatchInvocationRevisionObservationAsync(CancellationToken ct)
    {
        var identity = State.Identity;
        if (identity == null || string.IsNullOrWhiteSpace(identity.ServiceId))
            return Task.CompletedTask;

        var actorId = ServiceActorIds.InvocationCatalog(identity);
        return _dispatchPort.DispatchAsync(
            actorId,
            CreateEnvelope(
                actorId,
                new ObserveServiceInvocationRevisionsCommand
                {
                    Identity = identity.Clone(),
                    Revisions = { State.Revisions.ToDictionary(x => x.Key, x => x.Value.Clone(), StringComparer.Ordinal) },
                    SourceRevisionVersion = State.LastAppliedEventVersion,
                    ObservedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                }),
            ct);
    }

    private static EventEnvelope CreateEnvelope(string actorId, IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect("gagent-service.revisions", actorId),
            Propagation = new EnvelopePropagation(),
        };
}
