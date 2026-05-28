using Aevatar.GAgents.StudioTeam;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.CommandServices;

/// <summary>
/// Dispatches StudioTeam command events to the per-team
/// <see cref="StudioTeamGAgent"/> actor (ADR-0017). Mirrors
/// <c>ActorDispatchStudioMemberCommandService</c> in shape — the actor-id is
/// canonical (<c>studio-team:{scopeId}:{teamId}</c>) and bootstrap only
/// provisions the target actor before dispatch.
/// </summary>
internal sealed class ActorDispatchStudioTeamCommandService : IStudioTeamCommandPort
{
    private const string PublisherId = "aevatar.studio.projection.studio-team";

    private readonly IStudioActorBootstrap _bootstrap;
    private readonly StudioProjectionActorCommandDispatch _commandDispatch;

    public ActorDispatchStudioTeamCommandService(
        IStudioActorBootstrap bootstrap,
        StudioProjectionActorCommandDispatch commandDispatch)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _commandDispatch = commandDispatch ?? throw new ArgumentNullException(nameof(commandDispatch));
    }

    public async Task<StudioTeamSummaryResponse> CreateAsync(
        string scopeId,
        CreateStudioTeamRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Bound checks live at the Application boundary (StudioTeamCreateRequestValidator).
        // The transport-level guards here only ensure the actor-id remains
        // derivable.
        var normalizedScopeId = StudioTeamConventions.NormalizeScopeId(scopeId);
        var teamId = string.IsNullOrWhiteSpace(request.TeamId)
            ? GenerateTeamId()
            : StudioTeamConventions.NormalizeTeamId(request.TeamId);

        var displayName = (request.DisplayName ?? string.Empty).Trim();
        var description = (request.Description ?? string.Empty).Trim();
        var createdAt = DateTimeOffset.UtcNow;

        var evt = new StudioTeamCreatedEvent
        {
            TeamId = teamId,
            ScopeId = normalizedScopeId,
            DisplayName = displayName,
            Description = description,
            CreatedAtUtc = Timestamp.FromDateTimeOffset(createdAt),
        };

        await DispatchAsync(normalizedScopeId, teamId, evt, ct);

        return new StudioTeamSummaryResponse(
            TeamId: teamId,
            ScopeId: normalizedScopeId,
            DisplayName: displayName,
            Description: description,
            LifecycleStage: TeamLifecycleStageNames.Active,
            MemberCount: 0,
            CreatedAt: createdAt,
            UpdatedAt: createdAt);
    }

    public async Task<StudioTeamCommandResponse> UpdateAsync(
        string scopeId,
        string teamId,
        UpdateStudioTeamRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedScopeId = StudioTeamConventions.NormalizeScopeId(scopeId);
        var normalizedTeamId = StudioTeamConventions.NormalizeTeamId(teamId);

        // No-op if the patch payload carries no field to change.
        if (!request.DisplayName.HasValue && !request.Description.HasValue)
        {
            return new StudioTeamCommandResponse(
                StudioTeamCommandStatusNames.NoChange,
                normalizedScopeId,
                normalizedTeamId);
        }

        var evt = new StudioTeamUpdatedEvent
        {
            TeamId = normalizedTeamId,
            ScopeId = normalizedScopeId,
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        if (request.DisplayName.HasValue)
        {
            // Validator already rejects empty / over-cap display_name; the
            // wire field is set to the trimmed value.
            evt.DisplayName = (request.DisplayName.Value ?? string.Empty).Trim();
        }

        if (request.Description.HasValue)
        {
            // Description allows present-and-empty (explicit clear).
            evt.Description = request.Description.Value ?? string.Empty;
        }

        var receipt = await DispatchAsync(normalizedScopeId, normalizedTeamId, evt, ct);
        return ToAcceptedResponse(normalizedScopeId, normalizedTeamId, receipt);
    }

    public async Task<StudioTeamCommandResponse> ArchiveAsync(
        string scopeId,
        string teamId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = StudioTeamConventions.NormalizeScopeId(scopeId);
        var normalizedTeamId = StudioTeamConventions.NormalizeTeamId(teamId);

        var evt = new StudioTeamArchivedEvent
        {
            TeamId = normalizedTeamId,
            ScopeId = normalizedScopeId,
            ArchivedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        var receipt = await DispatchAsync(normalizedScopeId, normalizedTeamId, evt, ct);
        return ToAcceptedResponse(normalizedScopeId, normalizedTeamId, receipt);
    }

    public async Task SetEntryMemberAsync(
        string scopeId,
        string teamId,
        string memberId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = StudioTeamConventions.NormalizeScopeId(scopeId);
        var normalizedTeamId = StudioTeamConventions.NormalizeTeamId(teamId);
        var normalizedMemberId = Aevatar.GAgents.StudioMember.StudioMemberConventions.NormalizeMemberId(memberId);

        var evt = new StudioTeamEntryMemberChangedEvent
        {
            TeamId = normalizedTeamId,
            ScopeId = normalizedScopeId,
            EntryMemberId = normalizedMemberId,
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        await DispatchAsync(normalizedScopeId, normalizedTeamId, evt, ct);
    }

    public async Task ClearEntryMemberAsync(
        string scopeId,
        string teamId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = StudioTeamConventions.NormalizeScopeId(scopeId);
        var normalizedTeamId = StudioTeamConventions.NormalizeTeamId(teamId);

        var evt = new StudioTeamEntryMemberChangedEvent
        {
            TeamId = normalizedTeamId,
            ScopeId = normalizedScopeId,
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        await DispatchAsync(normalizedScopeId, normalizedTeamId, evt, ct);
    }

    private async Task<StudioProjectionActorCommandReceipt> DispatchAsync(
        string scopeId,
        string teamId,
        IMessage payload,
        CancellationToken ct)
    {
        var actorId = StudioTeamConventions.BuildActorId(scopeId, teamId);
        // Refactor (iter56/cluster-910-projection-activation-cleanup):
        //   old=command-path pre-dispatch activation
        //   new=committed-state plan provider
        //   team commands return after accepted dispatch, not readmodel materialization.
        var actor = await _bootstrap.EnsureAsync<StudioTeamGAgent>(actorId, ct);
        return await _commandDispatch.DispatchAsync(actor, payload, PublisherId, ct: ct);
    }

    private static StudioTeamCommandResponse ToAcceptedResponse(
        string scopeId,
        string teamId,
        StudioProjectionActorCommandReceipt receipt) =>
        new(
            StudioTeamCommandStatusNames.Accepted,
            scopeId,
            teamId,
            receipt.CommandId,
            receipt.CorrelationId,
            receipt.AckedAt);

    private static string GenerateTeamId()
    {
        // Team ids are immutable identifiers; URL-safe and free of separators
        // that StudioTeamConventions builds with (':').
        return $"t-{Guid.NewGuid():N}";
    }
}
