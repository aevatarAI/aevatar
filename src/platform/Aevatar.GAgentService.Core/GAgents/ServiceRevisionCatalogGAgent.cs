using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.Assemblers;
using Aevatar.GAgentService.Core.Ports;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Core.GAgents;

public sealed class ServiceRevisionCatalogGAgent : GAgentBase<ServiceRevisionCatalogState>
{
    private readonly IReadOnlyDictionary<ServiceImplementationKind, IServiceImplementationAdapter> _adapters;
    private readonly IServiceRevisionArtifactStore _artifactStore;
    private readonly PreparedServiceRevisionArtifactAssembler _artifactAssembler;

    public ServiceRevisionCatalogGAgent(
        IEnumerable<IServiceImplementationAdapter> adapters,
        IServiceRevisionArtifactStore artifactStore,
        PreparedServiceRevisionArtifactAssembler artifactAssembler)
    {
        _artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
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
            await _artifactStore.SaveAsync(serviceKey, command.RevisionId, assembled, CancellationToken.None);

            await PersistDomainEventAsync(new ServiceRevisionPreparedEvent
            {
                Identity = command.Identity.Clone(),
                RevisionId = command.RevisionId ?? string.Empty,
                ImplementationKind = assembled.ImplementationKind,
                ArtifactHash = assembled.ArtifactHash ?? string.Empty,
                Endpoints = { assembled.Endpoints.Select(x => x.Clone()) },
                PreparedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            });
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

        await PersistDomainEventAsync(new ServiceRevisionPublishedEvent
        {
            Identity = command.Identity.Clone(),
            RevisionId = command.RevisionId ?? string.Empty,
            PublishedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        });
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
}
