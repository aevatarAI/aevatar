using Aevatar.GAgents.StudioMember;
using Aevatar.GAgents.StudioTeam;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Projection.Mapping;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Security.Cryptography;

namespace Aevatar.Studio.Projection.CommandServices;

/// <summary>
/// Dispatches StudioMember command events to the per-member
/// <see cref="StudioMemberGAgent"/> actor. Uses the canonical actor-id
/// convention (<c>studio-member:{scopeId}:{memberId}</c>) and ensures the
/// target actor exists before dispatch via
/// <see cref="IStudioActorBootstrap"/>.
/// </summary>
internal sealed class ActorDispatchStudioMemberCommandService : IStudioMemberCommandPort
{
    private const string MemberPublisherId = "aevatar.studio.projection.studio-member";
    private const string BindingRunPublisherId = "aevatar.studio.projection.studio-member-binding-run";

    private readonly IStudioActorBootstrap _bootstrap;
    private readonly StudioProjectionActorCommandDispatch _commandDispatch;

    public ActorDispatchStudioMemberCommandService(
        IStudioActorBootstrap bootstrap,
        StudioProjectionActorCommandDispatch commandDispatch)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _commandDispatch = commandDispatch ?? throw new ArgumentNullException(nameof(commandDispatch));
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
        // Reassigned event after the Created event to the member actor only.
        // The durable projection materializer later fans the committed
        // reassignment fact out to the Team actor inbox. That keeps command
        // ACK semantics honest and lets projection replay recover roster
        // fanout without this command service driving team roster updates.
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

        // Dispatch only the authority change to the member actor. Durable
        // Team roster fanout is driven from the committed member event by
        // the Studio materialization projection scope, not by this command
        // service. That keeps the command ACK honest: member reassignment was
        // accepted for dispatch, while Team roster visibility is recovered
        // from committed facts and projection watermarks.
        await DispatchAsync(normalizedScopeId, normalizedMemberId, evt, ct);
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

    public async Task StartBindingRunAsync(
        StudioMemberBindingRunStartRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedBindingRunId = StudioMemberConventions.NormalizeBindingRunId(request.BindingRunId);
        var normalizedScopeId = StudioMemberConventions.NormalizeScopeId(request.ScopeId);
        var normalizedMemberId = StudioMemberConventions.NormalizeMemberId(request.MemberId);
        var actorId = StudioMemberConventions.BuildBindingRunActorId(normalizedBindingRunId);
        // Refactor (iter56/cluster-910-projection-activation-cleanup):
        //   old=command-path pre-dispatch activation
        //   new=committed-state plan provider
        //   binding-run command ACK does not imply readmodel materialization.
        var actor = await _bootstrap.EnsureAsync<StudioMemberBindingRunGAgent>(actorId, ct);
        await _bootstrap.EnsureAsync<StudioMemberGAgent>(
            StudioMemberConventions.BuildActorId(normalizedScopeId, normalizedMemberId),
            ct);

        var payload = new StudioMemberBindingRunRequested
        {
            Request = BuildBindingRequest(
                normalizedBindingRunId,
                normalizedScopeId,
                normalizedMemberId,
                request.ImplementationKind,
                request.Binding),
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        await _commandDispatch.DispatchAsync(actor, payload, BindingRunPublisherId, ct: ct);
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

    private static StudioMemberBindingRequest BuildBindingRequest(
        string bindingRunId,
        string scopeId,
        string memberId,
        string implementationKindName,
        UpdateStudioMemberBindingRequest binding)
    {
        var request = new StudioMemberBindingRequest
        {
            BindingRunId = bindingRunId,
            ScopeId = scopeId,
            MemberId = memberId,
        };
        if (!string.IsNullOrWhiteSpace(binding.RevisionId))
            request.RevisionId = binding.RevisionId;

        switch (implementationKindName)
        {
            case MemberImplementationKindNames.Workflow:
                request.Workflow = new StudioMemberWorkflowBindingRequest();
                request.Workflow.WorkflowYamls.Add(binding.Workflow?.WorkflowYamls ?? []);
                break;
            case MemberImplementationKindNames.Script:
                request.Script = new StudioMemberScriptBindingRequest
                {
                    ScriptId = binding.Script?.ScriptId ?? string.Empty,
                };
                if (!string.IsNullOrWhiteSpace(binding.Script?.ScriptRevision))
                    request.Script.ScriptRevision = binding.Script.ScriptRevision;
                break;
            case MemberImplementationKindNames.GAgent:
                request.Gagent = new StudioMemberGAgentBindingRequest
                {
                    ActorTypeName = binding.GAgent?.ActorTypeName ?? string.Empty,
                };
                foreach (var endpoint in binding.GAgent?.Endpoints ?? [])
                {
                    request.Gagent.Endpoints.Add(new StudioMemberGAgentEndpointBindingRequest
                    {
                        EndpointId = endpoint.EndpointId ?? string.Empty,
                        DisplayName = endpoint.DisplayName ?? string.Empty,
                        Kind = ParseGAgentEndpointKind(endpoint.Kind),
                        RequestTypeUrl = endpoint.RequestTypeUrl ?? string.Empty,
                        ResponseTypeUrl = endpoint.ResponseTypeUrl ?? string.Empty,
                        Description = endpoint.Description ?? string.Empty,
                    });
                }
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown implementationKind '{implementationKindName}'.");
        }

        request.RequestHash = ComputeRequestHash(request);
        return request;
    }

    private static StudioMemberGAgentEndpointKind ParseGAgentEndpointKind(string? rawValue) =>
        rawValue?.Trim().ToLowerInvariant() switch
        {
            null or "" => throw new InvalidOperationException("gagent endpoint kind is required."),
            "command" => StudioMemberGAgentEndpointKind.Command,
            "chat" => StudioMemberGAgentEndpointKind.Chat,
            _ => throw new InvalidOperationException($"Unsupported gagent endpoint kind '{rawValue}'."),
        };

    private static string ComputeRequestHash(StudioMemberBindingRequest request)
    {
        var normalized = request.Clone();
        normalized.RequestHash = string.Empty;
        return Convert.ToHexString(SHA256.HashData(normalized.ToByteArray())).ToLowerInvariant();
    }

    private async Task DispatchAsync(string scopeId, string memberId, IMessage payload, CancellationToken ct)
    {
        var actorId = StudioMemberConventions.BuildActorId(scopeId, memberId);
        // Refactor (iter56/cluster-910-projection-activation-cleanup):
        //   old=command-path pre-dispatch activation
        //   new=committed-state plan provider
        //   member commands only provision actor and dispatch accepted work.
        var actor = await _bootstrap.EnsureAsync<StudioMemberGAgent>(actorId, ct);
        await _commandDispatch.DispatchAsync(actor, payload, MemberPublisherId, ct: ct);
    }

    private static string GenerateMemberId()
    {
        // Member ids are immutable identifiers; the publishedServiceId is
        // derived directly from this value, so keep the format URL-safe and
        // free of separators that StudioMemberConventions builds with (':').
        return $"m-{Guid.NewGuid():N}";
    }
}
