using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StudioMember;
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
            UpdatedAt: createdAt);
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
