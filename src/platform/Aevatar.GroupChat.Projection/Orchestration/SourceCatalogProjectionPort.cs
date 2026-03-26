using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GroupChat.Abstractions.Ports;
using Aevatar.GroupChat.Projection.Configuration;

namespace Aevatar.GroupChat.Projection.Orchestration;

public sealed class SourceCatalogProjectionPort
    : MaterializationProjectionPortBase<SourceCatalogProjectionRuntimeLease>,
      ISourceCatalogProjectionPort
{
    public SourceCatalogProjectionPort(
        GroupChatProjectionOptions options,
        IProjectionScopeActivationService<SourceCatalogProjectionRuntimeLease> activationService,
        IProjectionScopeReleaseService<SourceCatalogProjectionRuntimeLease> releaseService)
        : base(
            () => options.Enabled,
            activationService,
            releaseService)
    {
    }

    public async Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return;

        _ = await EnsureProjectionAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = GroupChatProjectionKinds.SourceCatalog,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
            ct);
    }
}
