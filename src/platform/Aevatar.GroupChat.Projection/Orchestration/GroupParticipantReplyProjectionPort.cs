using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Ports;
using Aevatar.GroupChat.Projection.Configuration;

namespace Aevatar.GroupChat.Projection.Orchestration;

public sealed class GroupParticipantReplyProjectionPort
    : IGroupParticipantReplyProjectionPort
{
    private readonly Func<bool> _projectionEnabledAccessor;
    private readonly IProjectionScopeActivationService<GroupParticipantReplyProjectionRuntimeLease> _activationService;
    private readonly IProjectionScopeReleaseService<GroupParticipantReplyProjectionRuntimeLease> _releaseService;

    public GroupParticipantReplyProjectionPort(
        GroupChatProjectionOptions options,
        IProjectionScopeActivationService<GroupParticipantReplyProjectionRuntimeLease> activationService,
        IProjectionScopeReleaseService<GroupParticipantReplyProjectionRuntimeLease> releaseService)
    {
        _projectionEnabledAccessor = () => options.Enabled;
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
        _releaseService = releaseService ?? throw new ArgumentNullException(nameof(releaseService));
    }

    public bool ProjectionEnabled => _projectionEnabledAccessor();

    public async Task EnsureParticipantReplyProjectionAsync(
        string rootActorId,
        string sessionId,
        CancellationToken ct = default)
    {
        if (!ProjectionEnabled || string.IsNullOrWhiteSpace(rootActorId) || string.IsNullOrWhiteSpace(sessionId))
            return;

        _ = await _activationService.EnsureAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = rootActorId,
                ProjectionKind = GroupChatProjectionKinds.ParticipantReply,
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = sessionId,
            },
            ct);
    }

    public Task ReleaseParticipantReplyProjectionAsync(
        string rootActorId,
        string sessionId,
        CancellationToken ct = default)
    {
        if (!ProjectionEnabled || string.IsNullOrWhiteSpace(rootActorId) || string.IsNullOrWhiteSpace(sessionId))
            return Task.CompletedTask;

        return _releaseService.ReleaseIfIdleAsync(
            new GroupParticipantReplyProjectionRuntimeLease(
                new Contexts.GroupParticipantReplyProjectionContext
                {
                    RootActorId = rootActorId,
                    ProjectionKind = GroupChatProjectionKinds.ParticipantReply,
                    SessionId = sessionId,
                }),
            ct);
    }
}
