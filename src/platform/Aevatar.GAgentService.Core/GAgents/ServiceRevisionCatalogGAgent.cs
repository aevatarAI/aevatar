using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.Assemblers;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.Workflow.Abstractions;
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
        if (State.Revisions.TryGetValue(revisionId, out var existing))
        {
            if (existing.Status == ServiceRevisionStatus.Retired)
                throw new InvalidOperationException($"Revision '{revisionId}' has been retired.");

            if (existing.Spec != null && existing.Spec.Equals(command.Spec))
                return;

            if (existing.Spec != null &&
                WorkflowServiceRevisionEquivalence.AreEquivalent(existing.Spec, command.Spec))
            {
                var currentPlan = existing.Spec.WorkflowSpec?.CapabilityAdmissionPlan
                    ?? throw new InvalidOperationException(
                        $"Workflow revision '{revisionId}' has no persisted capability admission plan.");
                var replayedPlan = command.Spec.WorkflowSpec?.CapabilityAdmissionPlan
                    ?? throw new InvalidOperationException(
                        $"Workflow revision '{revisionId}' replay has no capability admission plan.");
                WorkflowServiceRevisionEquivalence.EnsureRenewableAdmissionEvidenceMovesForward(
                    currentPlan,
                    replayedPlan);
                return;
            }

            throw new InvalidOperationException(
                $"Revision '{revisionId}' already exists for service '{ServiceKeys.Build(command.Spec.Identity)}' with a conflicting spec.");
        }

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
        var storedSpec = record.Spec?.Clone()
            ?? throw new InvalidOperationException($"Revision '{command.RevisionId}' has no authoring spec.");
        var preparationSpec = ResolvePreparationSpec(command, storedSpec);
        var refreshAdmissionEvidence = !storedSpec.Equals(preparationSpec);

        if (record.Status == ServiceRevisionStatus.Retired)
            throw new InvalidOperationException($"Revision '{command.RevisionId}' has been retired.");

        if (record.Status is ServiceRevisionStatus.Prepared or ServiceRevisionStatus.Published)
        {
            EnsureReusablePreparedArtifact(record, storedSpec, command.RevisionId);
            if (refreshAdmissionEvidence)
            {
                ValidatePreparedWorkflowArtifactForRefresh(
                    record,
                    storedSpec,
                    command.RevisionId);
                if (record.Status == ServiceRevisionStatus.Published)
                {
                    await DispatchInvocationRevisionObservationAsync(CancellationToken.None);
                    return;
                }

                var refreshedArtifact = await PrepareArtifactAsync(command, preparationSpec);
                ValidateRefreshedWorkflowArtifact(
                    record.PreparedArtifact!,
                    refreshedArtifact,
                    preparationSpec,
                    command.RevisionId);
                await PersistAdmissionEvidenceRefreshAsync(
                    command,
                    preparationSpec,
                    refreshedArtifact,
                    record.ArtifactHash);
                return;
            }

            if (RequiresWorkflowPreparedArtifactRepair(record, command.RevisionId))
            {
                await RepairWorkflowPreparedArtifactAsync(command, record);
                return;
            }

            ValidatePreparedArtifactForSpec(
                record.PreparedArtifact!,
                storedSpec,
                command.RevisionId);
            await DispatchInvocationRevisionObservationAsync(CancellationToken.None);
            return;
        }

        var assembled = await PrepareArtifactAsync(command, preparationSpec);
        if (refreshAdmissionEvidence)
        {
            ValidatePreparedArtifactForSpec(
                assembled,
                preparationSpec,
                command.RevisionId);
            await PersistAdmissionEvidenceRefreshAsync(
                command,
                preparationSpec,
                assembled,
                previousArtifactHash: string.Empty);
            return;
        }

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

    [EventHandler]
    public async Task HandlePublishRevisionAsync(PublishServiceRevisionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureCatalogIdentity(command.Identity, allowInitialize: false);
        var record = GetRequiredRevision(command.RevisionId);
        var storedSpec = record.Spec?.Clone()
            ?? throw new InvalidOperationException($"Revision '{command.RevisionId}' has no authoring spec.");
        ValidatePublicationSpec(command, storedSpec, record.Status);
        if (record.Status == ServiceRevisionStatus.Published)
        {
            EnsureReusablePreparedArtifact(record, storedSpec, command.RevisionId);
            ValidatePreparedArtifactForSpec(
                record.PreparedArtifact!,
                storedSpec,
                command.RevisionId);
            await DispatchInvocationRevisionObservationAsync(CancellationToken.None);
            return;
        }

        if (record.Status != ServiceRevisionStatus.Prepared)
        {
            throw new InvalidOperationException($"Revision '{command.RevisionId}' must be prepared before publish.");
        }

        EnsureReusablePreparedArtifact(record, storedSpec, command.RevisionId);
        ValidatePreparedArtifactForSpec(
            record.PreparedArtifact!,
            storedSpec,
            command.RevisionId);

        var adapter = GetRequiredAdapter(storedSpec.ImplementationKind);
        var revalidated = await adapter.PrepareRevisionAsync(
            new PrepareServiceRevisionRequest
            {
                ServiceKey = ServiceKeys.Build(command.Identity),
                Spec = storedSpec,
            },
            CancellationToken.None);
        var revalidatedArtifact = _artifactAssembler.Assemble(revalidated);
        ValidatePreparedArtifactForSpec(
            revalidatedArtifact,
            storedSpec,
            command.RevisionId);
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

    [EventHandler]
    public Task HandleRefreshInvocationCatalogObservationAsync(
        RefreshServiceInvocationCatalogObservationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureCatalogIdentity(command.Identity, allowInitialize: false);
        return DispatchInvocationRevisionObservationAsync(CancellationToken.None);
    }

    protected override ServiceRevisionCatalogState TransitionState(ServiceRevisionCatalogState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ServiceRevisionCreatedEvent>(ApplyCreated)
            .On<ServiceRevisionAdmissionEvidenceRefreshedEvent>(ApplyAdmissionEvidenceRefreshed)
            .On<ServiceRevisionPreparedEvent>(ApplyPrepared)
            .On<ServiceRevisionPreparedArtifactRepairedEvent>(ApplyPreparedArtifactRepaired)
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

    private static ServiceRevisionCatalogState ApplyAdmissionEvidenceRefreshed(
        ServiceRevisionCatalogState state,
        ServiceRevisionAdmissionEvidenceRefreshedEvent evt)
    {
        var next = state.Clone();
        if (!next.Revisions.TryGetValue(evt.RevisionId, out var record))
        {
            throw new InvalidOperationException(
                $"Revision '{evt.RevisionId}' was not found for admission evidence refresh.");
        }
        if (record.Status == ServiceRevisionStatus.Retired)
            throw new InvalidOperationException($"Revision '{evt.RevisionId}' has been retired.");

        var currentSpec = record.Spec
            ?? throw new InvalidOperationException(
                $"Revision '{evt.RevisionId}' has no authoring spec.");
        var refreshedSpec = evt.Spec
            ?? throw new InvalidOperationException(
                $"Workflow revision '{evt.RevisionId}' admission evidence refresh has no authoring spec.");
        ValidateRevisionSpec(refreshedSpec);
        if (!Equals(evt.Identity, refreshedSpec.Identity) ||
            !string.Equals(evt.RevisionId, refreshedSpec.RevisionId, StringComparison.Ordinal) ||
            !WorkflowServiceRevisionEquivalence.AreEquivalent(currentSpec, refreshedSpec))
        {
            throw new InvalidOperationException(
                $"Workflow revision '{evt.RevisionId}' admission evidence refresh conflicts with its authoring spec.");
        }

        var previousPlan = currentSpec.WorkflowSpec?.CapabilityAdmissionPlan
            ?? throw new InvalidOperationException(
                $"Workflow revision '{evt.RevisionId}' has no persisted capability admission plan.");
        var refreshedPlan = refreshedSpec.WorkflowSpec?.CapabilityAdmissionPlan
            ?? throw new InvalidOperationException(
                $"Workflow revision '{evt.RevisionId}' has no capability admission plan.");
        WorkflowServiceRevisionEquivalence.EnsureRenewableAdmissionEvidenceMovesForward(
            previousPlan,
            refreshedPlan);

        var refreshedArtifact = evt.PreparedArtifact
            ?? throw new InvalidOperationException(
                $"Workflow revision '{evt.RevisionId}' admission evidence refresh has no prepared artifact.");
        ValidatePreparedArtifactForSpec(
            refreshedArtifact,
            refreshedSpec,
            evt.RevisionId);

        if (record.Status == ServiceRevisionStatus.Prepared)
        {
            ApplyPreparedAdmissionEvidenceRefresh(
                record,
                currentSpec,
                refreshedArtifact,
                evt);
        }
        else if (record.Status is ServiceRevisionStatus.Created or
                 ServiceRevisionStatus.PreparationFailed)
        {
            if (!string.IsNullOrEmpty(evt.PreviousArtifactHash))
            {
                throw new InvalidOperationException(
                    $"Unprepared workflow revision '{evt.RevisionId}' admission evidence refresh has an unexpected previous artifact hash.");
            }

            record.Status = ServiceRevisionStatus.Prepared;
            record.PreparedAt = evt.RefreshedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
        }
        else
        {
            throw new InvalidOperationException(
                $"Workflow revision '{evt.RevisionId}' cannot apply admission evidence refresh while '{record.Status}'.");
        }

        record.Spec = refreshedSpec.Clone();
        record.ArtifactHash = refreshedArtifact.ArtifactHash;
        record.Endpoints.Clear();
        record.Endpoints.Add(refreshedArtifact.Endpoints.Select(static endpoint => endpoint.Clone()));
        record.PreparedArtifact = refreshedArtifact.Clone();
        record.FailureReason = string.Empty;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.RevisionId, "admission-evidence-refreshed");
        return next;
    }

    private static void ApplyPreparedAdmissionEvidenceRefresh(
        ServiceRevisionRecordState record,
        ServiceRevisionSpec currentSpec,
        PreparedServiceRevisionArtifact refreshedArtifact,
        ServiceRevisionAdmissionEvidenceRefreshedEvent evt)
    {
        var existingArtifact = record.PreparedArtifact
            ?? throw new InvalidOperationException(
                $"Prepared workflow revision '{evt.RevisionId}' has no prepared artifact.");
        if (string.IsNullOrWhiteSpace(record.ArtifactHash) ||
            !string.Equals(record.ArtifactHash, existingArtifact.ArtifactHash, StringComparison.Ordinal) ||
            !string.Equals(record.ArtifactHash, evt.PreviousArtifactHash, StringComparison.Ordinal) ||
            !WorkflowServiceRevisionEquivalence.HasValidArtifactHash(existingArtifact))
        {
            throw new InvalidOperationException(
                $"Prepared workflow revision '{evt.RevisionId}' artifact hash is inconsistent.");
        }

        if (!IsWorkflowArtifactBoundToSpec(existingArtifact, currentSpec, evt.RevisionId))
        {
            throw new InvalidOperationException(
                $"Prepared workflow revision '{evt.RevisionId}' artifact does not match its authoring admission evidence.");
        }

        if (!WorkflowServiceRevisionEquivalence.AreEquivalent(existingArtifact, refreshedArtifact))
        {
            throw new InvalidOperationException(
                $"Prepared workflow revision '{evt.RevisionId}' replacement artifact is not an equivalent evidence refresh.");
        }
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
        if (record.Status is not (ServiceRevisionStatus.Prepared or ServiceRevisionStatus.Published or ServiceRevisionStatus.Retired))
        {
            record.Status = ServiceRevisionStatus.PreparationFailed;
            record.FailureReason = evt.FailureReason ?? string.Empty;
        }
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.RevisionId, "prepare-failed");
        return next;
    }

    private static ServiceRevisionCatalogState ApplyPreparedArtifactRepaired(
        ServiceRevisionCatalogState state,
        ServiceRevisionPreparedArtifactRepairedEvent evt)
    {
        var next = state.Clone();
        var record = next.Revisions[evt.RevisionId];
        record.ArtifactHash = evt.ArtifactHash ?? string.Empty;
        record.Endpoints.Clear();
        record.Endpoints.Add(evt.Endpoints.Select(x => x.Clone()));
        record.PreparedArtifact = evt.PreparedArtifact?.Clone() ?? new PreparedServiceRevisionArtifact();
        record.PreparedAt = evt.RepairedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
        record.FailureReason = string.Empty;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.RevisionId, "prepared-artifact-repaired");
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

    private static void EnsureReusablePreparedArtifact(
        ServiceRevisionRecordState record,
        ServiceRevisionSpec spec,
        string revisionId)
    {
        if (string.IsNullOrWhiteSpace(record.ArtifactHash) ||
            record.PreparedArtifact == null ||
            string.IsNullOrWhiteSpace(record.PreparedArtifact.ArtifactHash) ||
            !string.Equals(
                record.ArtifactHash,
                record.PreparedArtifact.ArtifactHash,
                StringComparison.Ordinal) ||
            !WorkflowServiceRevisionEquivalence.HasValidArtifactHash(record.PreparedArtifact) ||
            !IsPreparedArtifactTargetBoundToSpec(record.PreparedArtifact, spec, revisionId))
        {
            throw new InvalidOperationException(
                $"Revision '{revisionId}' is marked prepared but its prepared artifact is inconsistent with its authoring spec.");
        }
    }

    private static void ValidatePreparedWorkflowArtifactForRefresh(
        ServiceRevisionRecordState record,
        ServiceRevisionSpec spec,
        string revisionId)
    {
        EnsureReusablePreparedArtifact(record, spec, revisionId);
        var artifact = record.PreparedArtifact!;
        if (!IsWorkflowArtifactBoundToSpec(artifact, spec, revisionId))
        {
            throw new InvalidOperationException(
                $"Prepared workflow revision '{revisionId}' artifact is inconsistent with its authoring spec.");
        }
    }

    private static ServiceRevisionSpec ResolvePreparationSpec(
        PrepareServiceRevisionCommand command,
        ServiceRevisionSpec storedSpec)
    {
        var preparationSpec = ValidateCommandRevisionSpec(
            command.PreparationSpec,
            command.Identity,
            command.RevisionId,
            "preparation_spec");
        if (preparationSpec == null || storedSpec.Equals(preparationSpec))
            return preparationSpec ?? storedSpec;

        if (!WorkflowServiceRevisionEquivalence.AreEquivalent(storedSpec, preparationSpec))
        {
            throw new InvalidOperationException(
                $"Revision '{command.RevisionId}' preparation_spec conflicts with its persisted authoring spec.");
        }

        var currentPlan = storedSpec.WorkflowSpec?.CapabilityAdmissionPlan
            ?? throw new InvalidOperationException(
                $"Workflow revision '{command.RevisionId}' has no persisted capability admission plan.");
        var refreshedPlan = preparationSpec.WorkflowSpec?.CapabilityAdmissionPlan
            ?? throw new InvalidOperationException(
                $"Workflow revision '{command.RevisionId}' preparation_spec has no capability admission plan.");
        WorkflowServiceRevisionEquivalence.EnsureRenewableAdmissionEvidenceMovesForward(
            currentPlan,
            refreshedPlan);
        return preparationSpec;
    }

    private static void ValidatePublicationSpec(
        PublishServiceRevisionCommand command,
        ServiceRevisionSpec storedSpec,
        ServiceRevisionStatus status)
    {
        var publicationSpec = ValidateCommandRevisionSpec(
            command.PublicationSpec,
            command.Identity,
            command.RevisionId,
            "publication_spec");
        if (publicationSpec == null)
            return;

        if (storedSpec.Equals(publicationSpec))
            return;

        if (status != ServiceRevisionStatus.Published ||
            !WorkflowServiceRevisionEquivalence.AreEquivalent(storedSpec, publicationSpec))
        {
            throw new InvalidOperationException(
                $"Revision '{command.RevisionId}' publication_spec does not match its persisted authoring spec.");
        }

        var currentPlan = storedSpec.WorkflowSpec?.CapabilityAdmissionPlan
            ?? throw new InvalidOperationException(
                $"Workflow revision '{command.RevisionId}' has no persisted capability admission plan.");
        var publicationPlan = publicationSpec.WorkflowSpec?.CapabilityAdmissionPlan
            ?? throw new InvalidOperationException(
                $"Workflow revision '{command.RevisionId}' publication_spec has no capability admission plan.");
        WorkflowServiceRevisionEquivalence.EnsureRenewableAdmissionEvidenceMovesForward(
            currentPlan,
            publicationPlan);
    }

    private static ServiceRevisionSpec? ValidateCommandRevisionSpec(
        ServiceRevisionSpec? spec,
        ServiceIdentity identity,
        string revisionId,
        string fieldName)
    {
        if (spec == null)
            return null;

        ValidateRevisionSpec(spec);
        if (!Equals(spec.Identity, identity) ||
            !string.Equals(spec.RevisionId, revisionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{fieldName} must target the command service identity and revision_id.");
        }

        return spec.Clone();
    }

    private async Task PersistAdmissionEvidenceRefreshAsync(
        PrepareServiceRevisionCommand command,
        ServiceRevisionSpec spec,
        PreparedServiceRevisionArtifact preparedArtifact,
        string previousArtifactHash)
    {
        await PersistDomainEventAsync(new ServiceRevisionAdmissionEvidenceRefreshedEvent
        {
            Identity = command.Identity.Clone(),
            RevisionId = command.RevisionId ?? string.Empty,
            Spec = spec.Clone(),
            PreparedArtifact = preparedArtifact.Clone(),
            PreviousArtifactHash = previousArtifactHash ?? string.Empty,
            RefreshedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        });
        await DispatchInvocationRevisionObservationAsync(CancellationToken.None);
    }

    private static void ValidateRefreshedWorkflowArtifact(
        PreparedServiceRevisionArtifact existingArtifact,
        PreparedServiceRevisionArtifact refreshedArtifact,
        ServiceRevisionSpec refreshedSpec,
        string revisionId)
    {
        ValidatePreparedArtifactForSpec(
            refreshedArtifact,
            refreshedSpec,
            revisionId);
        if (!WorkflowServiceRevisionEquivalence.AreEquivalent(existingArtifact, refreshedArtifact))
        {
            throw new InvalidOperationException(
                $"Prepared workflow revision '{revisionId}' refreshed to a different artifact.");
        }
    }

    private static void ValidatePreparedArtifactForSpec(
        PreparedServiceRevisionArtifact artifact,
        ServiceRevisionSpec spec,
        string revisionId)
    {
        if (string.IsNullOrWhiteSpace(artifact.ArtifactHash) ||
            !WorkflowServiceRevisionEquivalence.HasValidArtifactHash(artifact) ||
            !IsPreparedArtifactTargetBoundToSpec(artifact, spec, revisionId) ||
            !HasMatchingDeploymentPlanKind(artifact, spec) ||
            (spec.ImplementationKind == ServiceImplementationKind.Workflow &&
             !IsWorkflowArtifactBoundToSpec(artifact, spec, revisionId)))
        {
            throw new InvalidOperationException(
                $"Prepared revision '{revisionId}' artifact is inconsistent with its authoring spec.");
        }
    }

    private static bool IsPreparedArtifactTargetBoundToSpec(
        PreparedServiceRevisionArtifact artifact,
        ServiceRevisionSpec spec,
        string revisionId) =>
        artifact.ImplementationKind == spec.ImplementationKind &&
        Equals(artifact.Identity, spec.Identity) &&
        string.Equals(artifact.RevisionId, revisionId, StringComparison.Ordinal);

    private static bool HasMatchingDeploymentPlanKind(
        PreparedServiceRevisionArtifact artifact,
        ServiceRevisionSpec spec) =>
        spec.ImplementationKind switch
        {
            ServiceImplementationKind.Static =>
                spec.StaticSpec != null && artifact.DeploymentPlan?.StaticPlan != null,
            ServiceImplementationKind.Scripting =>
                spec.ScriptingSpec != null && artifact.DeploymentPlan?.ScriptingPlan != null,
            ServiceImplementationKind.Workflow =>
                spec.WorkflowSpec != null && artifact.DeploymentPlan?.WorkflowPlan != null,
            _ => false,
        };

    private static bool IsWorkflowArtifactBoundToSpec(
        PreparedServiceRevisionArtifact artifact,
        ServiceRevisionSpec spec,
        string revisionId)
    {
        var workflowSpec = spec.WorkflowSpec;
        var workflowPlan = artifact.DeploymentPlan?.WorkflowPlan;
        if (workflowSpec == null || workflowPlan == null)
            return false;

        var specPlan = workflowSpec.CapabilityAdmissionPlan;
        var artifactPlan = workflowPlan.CapabilityAdmissionPlan;
        var resolvedExecutionMode = ResolveExpectedWorkflowExecutionMode(workflowSpec);
        return IsPreparedArtifactTargetBoundToSpec(artifact, spec, revisionId) &&
               WorkflowServiceDeploymentPlanIntegrity.IsCompatible(artifact, revisionId) &&
               IsWorkflowDefinitionBoundToSpec(workflowPlan, workflowSpec) &&
               IsWorkflowBindingIdentityBoundToSpec(workflowPlan, workflowSpec, revisionId) &&
               resolvedExecutionMode != ExternalCapabilityExecutionMode.Unspecified &&
               System.Enum.IsDefined(resolvedExecutionMode) &&
               workflowPlan.ExecutionMode == resolvedExecutionMode &&
               artifactPlan != null &&
               (specPlan == null || specPlan.Equals(artifactPlan));
    }

    private static bool IsWorkflowDefinitionBoundToSpec(
        WorkflowServiceDeploymentPlan plan,
        WorkflowServiceRevisionSpec spec) =>
        (string.IsNullOrWhiteSpace(spec.WorkflowName) ||
         string.Equals(plan.WorkflowName, spec.WorkflowName.Trim(), StringComparison.Ordinal)) &&
        string.Equals(plan.WorkflowYaml, spec.WorkflowYaml, StringComparison.Ordinal) &&
        string.Equals(
            plan.DefinitionActorId,
            spec.DefinitionActorId ?? string.Empty,
            StringComparison.Ordinal) &&
        plan.InlineWorkflowYamls.Equals(spec.InlineWorkflowYamls);

    private static bool IsWorkflowBindingIdentityBoundToSpec(
        WorkflowServiceDeploymentPlan plan,
        WorkflowServiceRevisionSpec spec,
        string revisionId) =>
        (string.IsNullOrWhiteSpace(spec.WorkflowId)
            ? string.IsNullOrWhiteSpace(plan.WorkflowId) ||
              string.Equals(plan.WorkflowId, revisionId, StringComparison.Ordinal)
            : string.Equals(plan.WorkflowId, spec.WorkflowId, StringComparison.Ordinal)) &&
        (string.IsNullOrWhiteSpace(plan.RevisionId) ||
         string.Equals(plan.RevisionId, revisionId, StringComparison.Ordinal));

    private static ExternalCapabilityExecutionMode ResolveExpectedWorkflowExecutionMode(
        WorkflowServiceRevisionSpec spec) =>
        spec.CapabilityAdmissionPlan == null &&
        spec.ExpectedExecutionMode == ExternalCapabilityExecutionMode.Unspecified
            ? ExternalCapabilityExecutionMode.Interactive
            : spec.ExpectedExecutionMode;

    private static bool RequiresWorkflowPreparedArtifactRepair(
        ServiceRevisionRecordState record,
        string revisionId) =>
        record.Spec?.ImplementationKind == ServiceImplementationKind.Workflow &&
        !WorkflowServiceDeploymentPlanIntegrity.IsCompatible(record.PreparedArtifact, revisionId);

    private async Task RepairWorkflowPreparedArtifactAsync(
        PrepareServiceRevisionCommand command,
        ServiceRevisionRecordState record)
    {
        var spec = record.Spec?.Clone()
            ?? throw new InvalidOperationException($"Revision '{command.RevisionId}' has no authoring spec.");
        var assembled = await PrepareArtifactAsync(command, spec);
        if (!WorkflowServiceDeploymentPlanIntegrity.IsCompatible(assembled, command.RevisionId))
        {
            throw new InvalidOperationException(
                $"Revision '{command.RevisionId}' prepared an incompatible workflow deployment plan.");
        }

        await PersistDomainEventAsync(new ServiceRevisionPreparedArtifactRepairedEvent
        {
            Identity = command.Identity.Clone(),
            RevisionId = command.RevisionId ?? string.Empty,
            ImplementationKind = assembled.ImplementationKind,
            PreviousArtifactHash = record.ArtifactHash ?? string.Empty,
            ArtifactHash = assembled.ArtifactHash ?? string.Empty,
            Endpoints = { assembled.Endpoints.Select(x => x.Clone()) },
            RepairedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            PreparedArtifact = assembled.Clone(),
            RepairReason = ServiceRevisionPreparedArtifactRepairReason.WorkflowDeploymentPlanIncompatible,
        });
        await DispatchInvocationRevisionObservationAsync(CancellationToken.None);
    }

    private async Task<PreparedServiceRevisionArtifact> PrepareArtifactAsync(
        PrepareServiceRevisionCommand command,
        ServiceRevisionSpec spec)
    {
        var adapter = GetRequiredAdapter(spec.ImplementationKind);
        try
        {
            var prepared = await adapter.PrepareRevisionAsync(
                new PrepareServiceRevisionRequest
                {
                    ServiceKey = ServiceKeys.Build(command.Identity),
                    Spec = spec,
                },
                CancellationToken.None);
            var assembled = _artifactAssembler.Assemble(prepared);
            ValidatePreparedArtifactForSpec(assembled, spec, command.RevisionId);
            return assembled;
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
