using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.CommandServices;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Workspace;
using Google.Protobuf;

namespace Aevatar.Studio.Projection.Projectors;

/// <summary>
/// Ensures a workflow draft has a canonical StudioMember authority after the
/// workspace actor commits <see cref="StudioWorkflowDraftSaved"/>.
/// </summary>
// Refactor (iter1345/cluster-519-draft-member-authority):
//   Old pattern: draft save callers could stitch member creation together
//   outside the committed-state projection chain.
//   New principle: the committed StudioWorkflowDraftSaved fact is the single
//   trigger; this materializer fans out one typed EnsureStudioMember command
//   through the standard actor dispatch path.
internal sealed class StudioWorkflowDraftMemberEnsureMaterializer
    : ICurrentStateProjectionMaterializer<StudioMaterializationContext>
{
    private const string PublisherId = "aevatar.studio.projection.workflow-draft-member-ensure";

    private readonly IStudioActorBootstrap _bootstrap;
    private readonly StudioProjectionActorCommandDispatch _commandDispatch;

    public StudioWorkflowDraftMemberEnsureMaterializer(
        IStudioActorBootstrap bootstrap,
        StudioProjectionActorCommandDispatch commandDispatch)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _commandDispatch = commandDispatch ?? throw new ArgumentNullException(nameof(commandDispatch));
    }

    public async ValueTask ProjectAsync(
        StudioMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpack(envelope, out var published) ||
            published?.StateEvent?.EventData == null ||
            !published.StateEvent.EventData.Is(StudioWorkflowDraftSaved.Descriptor))
        {
            return;
        }

        var evt = published.StateEvent.EventData.Unpack<StudioWorkflowDraftSaved>();
        if (evt.Draft == null || string.IsNullOrWhiteSpace(evt.Draft.WorkflowId))
            return;

        var scopeId = StudioMemberConventions.NormalizeScopeId(evt.ScopeId);
        var memberId = StudioMemberConventions.NormalizeMemberId(evt.Draft.WorkflowId);
        var actorId = StudioMemberConventions.BuildActorId(scopeId, memberId);
        var actor = await _bootstrap.EnsureAsync<StudioMemberGAgent>(actorId, ct).ConfigureAwait(false);
        var command = new EnsureStudioMember
        {
            MemberId = memberId,
            ScopeId = scopeId,
            DisplayName = string.IsNullOrWhiteSpace(evt.Draft.Name)
                ? memberId
                : evt.Draft.Name.Trim(),
            Description = string.Empty,
            RequestedAtUtc = evt.SavedAtUtc,
        };

        await _commandDispatch.DispatchAsync(
            actor,
            command,
            PublisherId,
            commandId: BuildCommandId(scopeId, memberId),
            deduplicationOperationId: BuildCommandId(scopeId, memberId),
            ct: ct).ConfigureAwait(false);
    }

    private static string BuildCommandId(string scopeId, string memberId) =>
        $"{PublisherId}:{scopeId}:{memberId}";
}
