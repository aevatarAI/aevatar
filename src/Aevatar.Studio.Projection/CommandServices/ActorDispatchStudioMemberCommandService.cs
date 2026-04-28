using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StudioMember;
using Aevatar.GAgents.StudioTeam;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Projection.Mapping;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.CommandServices;

/// <summary>
/// Dispatches StudioMember command events to the per-member
/// <see cref="StudioMemberGAgent"/> actor. Uses the canonical actor-id
/// convention (<c>studio-member:{scopeId}:{memberId}</c>) and ensures the
/// actor + projection scope are activated before dispatch via
/// <see cref="IStudioActorBootstrap"/>.
/// </summary>
internal sealed class ActorDispatchStudioMemberCommandService : IStudioMemberCommandPort
{
    private const string DirectRoute = "aevatar.studio.projection.studio-member";

    private readonly IStudioActorBootstrap _bootstrap;
    private readonly IActorDispatchPort _dispatchPort;

    public ActorDispatchStudioMemberCommandService(
        IStudioActorBootstrap bootstrap,
        IActorDispatchPort dispatchPort)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public async Task<StudioMemberSummaryResponse> CreateAsync(
        string scopeId,
        CreateStudioMemberRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Length caps + slug pattern are enforced at the Application
        // boundary (StudioMemberCreateRequestValidator). The transport-
        // level guards here only ensure the actor-id remains derivable —
        // scope normalization rejects ':', and an empty memberId is
        // replaced by a generated one. Anything else is the caller's
        // already-validated input.
        var normalizedScopeId = StudioMemberConventions.NormalizeScopeId(scopeId);
        var memberId = string.IsNullOrWhiteSpace(request.MemberId)
            ? GenerateMemberId()
            : StudioMemberConventions.NormalizeMemberId(request.MemberId);

        var displayName = (request.DisplayName ?? string.Empty).Trim();
        var description = (request.Description ?? string.Empty).Trim();

        var implementationKind = MemberImplementationKindMapper.Parse(request.ImplementationKind);
        var publishedServiceId = StudioMemberConventions.BuildPublishedServiceId(memberId);
        var createdAt = DateTimeOffset.UtcNow;

        var evt = new StudioMemberCreatedEvent
        {
            MemberId = memberId,
            ScopeId = normalizedScopeId,
            DisplayName = displayName,
            Description = description,
            ImplementationKind = implementationKind,
            PublishedServiceId = publishedServiceId,
            CreatedAtUtc = Timestamp.FromDateTimeOffset(createdAt),
        };

        await DispatchAsync(normalizedScopeId, memberId, evt, ct);

        // Two-event create-with-team protocol (ADR-0017 §Locked Rule 3).
        // When the request carries a non-empty teamId, dispatch a
        // Reassigned event after the Created event. The two dispatches
        // are sequential — not atomic within one actor turn — so there
        // is a brief window where the member exists without a team
        // assignment. The team's roster update is eventually consistent:
        // idempotent set ops ensure duplicates/retries collapse to NOOP.
        if (!string.IsNullOrEmpty(request.TeamId))
        {
            var initialTeamId = StudioTeamConventions.NormalizeTeamId(request.TeamId);
            await ReassignTeamInternalAsync(
                normalizedScopeId,
                memberId,
                fromTeamIdNormalized: null,
                toTeamIdNormalized: initialTeamId,
                ct);
        }

        var responseTeamId = string.IsNullOrEmpty(request.TeamId)
            ? null
            : StudioTeamConventions.NormalizeTeamId(request.TeamId);

        return new StudioMemberSummaryResponse(
            MemberId: memberId,
            ScopeId: normalizedScopeId,
            DisplayName: displayName,
            Description: evt.Description,
            ImplementationKind: MemberImplementationKindMapper.ToWireName(implementationKind),
            LifecycleStage: MemberLifecycleStageNames.Created,
            PublishedServiceId: publishedServiceId,
            LastBoundRevisionId: null,
            CreatedAt: createdAt,
            UpdatedAt: createdAt)
        { TeamId = responseTeamId };
    }

    public Task ReassignTeamAsync(
        string scopeId,
        string memberId,
        string? fromTeamId,
        string? toTeamId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = StudioMemberConventions.NormalizeScopeId(scopeId);
        var normalizedMemberId = StudioMemberConventions.NormalizeMemberId(memberId);

        // At least one side must be present (ADR-0017 §Locked Rule 4). Wire
        // values arrive here already shaped — null means absent.
        if (fromTeamId == null && toTeamId == null)
        {
            throw new InvalidOperationException(
                "reassign requires at least one of fromTeamId / toTeamId.");
        }

        // Both present and equal is rejected — that's a no-op move that
        // never appears as a wire event.
        if (fromTeamId != null && toTeamId != null
            && string.Equals(fromTeamId, toTeamId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "fromTeamId and toTeamId must differ when both are present.");
        }

        var fromNormalized = fromTeamId == null
            ? null
            : StudioTeamConventions.NormalizeTeamId(fromTeamId);
        var toNormalized = toTeamId == null
            ? null
            : StudioTeamConventions.NormalizeTeamId(toTeamId);

        return ReassignTeamInternalAsync(
            normalizedScopeId, normalizedMemberId, fromNormalized, toNormalized, ct);
    }

    private async Task ReassignTeamInternalAsync(
        string normalizedScopeId,
        string normalizedMemberId,
        string? fromTeamIdNormalized,
        string? toTeamIdNormalized,
        CancellationToken ct)
    {
        var reassignedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        var evt = new StudioMemberReassignedEvent
        {
            MemberId = normalizedMemberId,
            ScopeId = normalizedScopeId,
            ReassignedAtUtc = reassignedAt,
        };
        if (fromTeamIdNormalized != null)
            evt.FromTeamId = fromTeamIdNormalized;
        if (toTeamIdNormalized != null)
            evt.ToTeamId = toTeamIdNormalized;

        // Step 1: dispatch the authority change to the member actor.
        // MemberGAgent owns the team_id fact and rejects events whose
        // from_team_id disagrees with the member's current state.
        await DispatchAsync(normalizedScopeId, normalizedMemberId, evt, ct);

        // Step 2: fan out the same event to the affected TeamGAgents.
        // Each team applies an idempotent set operation to its roster —
        // duplicate deliveries collapse to NOOP by construction. Cross-
        // actor consistency relies on the idempotency, not on transactional
        // delivery: a re-run of this method (e.g. retry after a transient
        // failure) lands on a NOOP for already-applied sides.
        if (fromTeamIdNormalized != null)
        {
            await DispatchToTeamAsync(normalizedScopeId, fromTeamIdNormalized, evt, ct);
        }
        if (toTeamIdNormalized != null)
        {
            await DispatchToTeamAsync(normalizedScopeId, toTeamIdNormalized, evt, ct);
        }
    }

    private async Task DispatchToTeamAsync(
        string scopeId, string teamId, IMessage payload, CancellationToken ct)
    {
        const string TeamDirectRoute = "aevatar.studio.projection.studio-team";
        var actorId = StudioTeamConventions.BuildActorId(scopeId, teamId);
        var actor = await _bootstrap.EnsureAsync<StudioTeamGAgent>(actorId, ct);

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect(TeamDirectRoute, actor.Id),
        };

        await _dispatchPort.DispatchAsync(actor.Id, envelope, ct);
    }

    public async Task UpdateImplementationAsync(
        string scopeId,
        string memberId,
        StudioMemberImplementationRefResponse implementation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(implementation);

        var normalizedScopeId = StudioMemberConventions.NormalizeScopeId(scopeId);
        var normalizedMemberId = StudioMemberConventions.NormalizeMemberId(memberId);
        var implementationKind = MemberImplementationKindMapper.Parse(implementation.ImplementationKind);

        var evt = new StudioMemberImplementationUpdatedEvent
        {
            ImplementationKind = implementationKind,
            ImplementationRef = BuildImplementationRefMessage(implementation),
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        await DispatchAsync(normalizedScopeId, normalizedMemberId, evt, ct);
    }

    public async Task RecordBindingAsync(
        string scopeId,
        string memberId,
        string publishedServiceId,
        string revisionId,
        string implementationKindName,
        CancellationToken ct = default)
    {
        var normalizedScopeId = StudioMemberConventions.NormalizeScopeId(scopeId);
        var normalizedMemberId = StudioMemberConventions.NormalizeMemberId(memberId);

        if (string.IsNullOrWhiteSpace(publishedServiceId))
        {
            throw new InvalidOperationException(
                $"member '{normalizedMemberId}' bind: publishedServiceId is required to record binding.");
        }

        if (string.IsNullOrWhiteSpace(revisionId))
        {
            throw new InvalidOperationException(
                $"member '{normalizedMemberId}' bind: revisionId is required to record binding.");
        }

        var evt = new StudioMemberBoundEvent
        {
            PublishedServiceId = publishedServiceId,
            RevisionId = revisionId,
            ImplementationKind = MemberImplementationKindMapper.Parse(implementationKindName),
            BoundAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        await DispatchAsync(normalizedScopeId, normalizedMemberId, evt, ct);
    }

    private static StudioMemberImplementationRef BuildImplementationRefMessage(
        StudioMemberImplementationRefResponse implementation)
    {
        var message = new StudioMemberImplementationRef();
        switch (implementation.ImplementationKind)
        {
            case MemberImplementationKindNames.Workflow:
                message.Workflow = new StudioMemberWorkflowRef
                {
                    WorkflowId = implementation.WorkflowId ?? string.Empty,
                    WorkflowRevision = implementation.WorkflowRevision ?? string.Empty,
                };
                break;
            case MemberImplementationKindNames.Script:
                message.Script = new StudioMemberScriptRef
                {
                    ScriptId = implementation.ScriptId ?? string.Empty,
                    ScriptRevision = implementation.ScriptRevision ?? string.Empty,
                };
                break;
            case MemberImplementationKindNames.GAgent:
                message.Gagent = new StudioMemberGAgentRef
                {
                    ActorTypeName = implementation.ActorTypeName ?? string.Empty,
                };
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown implementationKind '{implementation.ImplementationKind}'.");
        }

        return message;
    }

    private async Task DispatchAsync(string scopeId, string memberId, IMessage payload, CancellationToken ct)
    {
        var actorId = StudioMemberConventions.BuildActorId(scopeId, memberId);
        var actor = await _bootstrap.EnsureAsync<StudioMemberGAgent>(actorId, ct);

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect(DirectRoute, actor.Id),
        };

        await _dispatchPort.DispatchAsync(actor.Id, envelope, ct);
    }

    private static string GenerateMemberId()
    {
        // Member ids are immutable identifiers; the publishedServiceId is
        // derived directly from this value, so keep the format URL-safe and
        // free of separators that StudioMemberConventions builds with (':').
        return $"m-{Guid.NewGuid():N}";
    }
}
