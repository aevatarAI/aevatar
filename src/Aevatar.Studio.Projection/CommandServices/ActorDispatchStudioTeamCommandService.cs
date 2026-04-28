using Aevatar.Foundation.Abstractions;
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
/// canonical (<c>studio-team:{scopeId}:{teamId}</c>) and the projection scope
/// is activated before dispatch via <see cref="IStudioActorBootstrap"/>.
/// </summary>
internal sealed class ActorDispatchStudioTeamCommandService : IStudioTeamCommandPort
{
    private const string DirectRoute = "aevatar.studio.projection.studio-team";

    private readonly IStudioActorBootstrap _bootstrap;
    private readonly IActorDispatchPort _dispatchPort;

    public ActorDispatchStudioTeamCommandService(
        IStudioActorBootstrap bootstrap,
        IActorDispatchPort dispatchPort)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
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

    public async Task UpdateAsync(
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
            return;

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

        await DispatchAsync(normalizedScopeId, normalizedTeamId, evt, ct);
    }

    public async Task ArchiveAsync(
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

        await DispatchAsync(normalizedScopeId, normalizedTeamId, evt, ct);
    }

    private async Task DispatchAsync(string scopeId, string teamId, IMessage payload, CancellationToken ct)
    {
        var actorId = StudioTeamConventions.BuildActorId(scopeId, teamId);
        var actor = await _bootstrap.EnsureAsync<StudioTeamGAgent>(actorId, ct);

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect(DirectRoute, actor.Id),
        };

        await _dispatchPort.DispatchAsync(actor.Id, envelope, ct);
    }

    private static string GenerateTeamId()
    {
        // Team ids are immutable identifiers; URL-safe and free of separators
        // that StudioTeamConventions builds with (':').
        return $"t-{Guid.NewGuid():N}";
    }
}
