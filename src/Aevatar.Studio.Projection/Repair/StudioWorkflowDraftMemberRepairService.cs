using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.CommandServices;
using Aevatar.Studio.Projection.Projectors;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.Repair;

public sealed record StudioWorkflowDraftMemberRepairResult(
    string ScopeId,
    int DraftCount,
    int AcceptedCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<StudioWorkflowDraftMemberRepairItem> Items);

public sealed record StudioWorkflowDraftMemberRepairItem(
    string WorkflowId,
    string MemberId,
    string ActorId,
    string Status,
    string? CommandId = null,
    string? Error = null)
{
    public const string Accepted = "accepted";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}

/// <summary>
/// Explicit one-shot repair for historical scoped workflow drafts whose
/// StudioMember authority may predate the committed-draft projection fanout.
/// It keeps repair input on the workspace read model and writes only by
/// dispatching the existing typed EnsureStudioMember command to the member
/// actor. No progress, retry, or cross-request state is retained here.
/// </summary>
// Refactor (iter1357/cluster-explicit-scope-draft-member-repair):
//   Old pattern: historical scoped workflow drafts could miss StudioMember
//   authority creation until another committed draft event replayed.
//   New principle: an explicit scope-bounded repair reads the workspace
//   readmodel and reuses the committed-draft EnsureStudioMember command path.
public sealed class StudioWorkflowDraftMemberRepairService
{
    private readonly IStudioWorkspaceQueryPort _workspaceQueryPort;
    private readonly IStudioActorBootstrap _bootstrap;
    private readonly StudioProjectionActorCommandDispatch _commandDispatch;
    private readonly StudioWorkflowDraftMemberEnsureCommandFactory _commandFactory;

    internal StudioWorkflowDraftMemberRepairService(
        IStudioWorkspaceQueryPort workspaceQueryPort,
        IStudioActorBootstrap bootstrap,
        StudioProjectionActorCommandDispatch commandDispatch,
        StudioWorkflowDraftMemberEnsureCommandFactory commandFactory)
    {
        _workspaceQueryPort = workspaceQueryPort ?? throw new ArgumentNullException(nameof(workspaceQueryPort));
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _commandDispatch = commandDispatch ?? throw new ArgumentNullException(nameof(commandDispatch));
        _commandFactory = commandFactory ?? throw new ArgumentNullException(nameof(commandFactory));
    }

    public async Task<StudioWorkflowDraftMemberRepairResult> RepairScopeAsync(
        string scopeId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = StudioMemberConventions.NormalizeScopeId(scopeId);
        var workspace = await _workspaceQueryPort.GetAsync(normalizedScopeId, ct).ConfigureAwait(false);
        var requestedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        var items = new List<StudioWorkflowDraftMemberRepairItem>(workspace.Drafts.Count);
        var accepted = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var draft in workspace.Drafts)
        {
            var plan = _commandFactory.TryCreate(
                normalizedScopeId,
                draft.WorkflowId,
                draft.Name,
                requestedAt);
            if (plan == null)
            {
                skipped++;
                items.Add(new StudioWorkflowDraftMemberRepairItem(
                    draft.WorkflowId,
                    string.Empty,
                    string.Empty,
                    StudioWorkflowDraftMemberRepairItem.Skipped,
                    Error: "workflowId is required."));
                continue;
            }

            try
            {
                var actor = await _bootstrap.EnsureAsync<StudioMemberGAgent>(plan.ActorId, ct)
                    .ConfigureAwait(false);
                await _commandDispatch.DispatchAsync(
                    actor,
                    plan.Command,
                    plan.PublisherId,
                    commandId: plan.CommandId,
                    deduplicationOperationId: plan.DeduplicationOperationId,
                    ct: ct).ConfigureAwait(false);

                accepted++;
                items.Add(new StudioWorkflowDraftMemberRepairItem(
                    draft.WorkflowId,
                    plan.MemberId,
                    plan.ActorId,
                    StudioWorkflowDraftMemberRepairItem.Accepted,
                    CommandId: plan.CommandId));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                items.Add(new StudioWorkflowDraftMemberRepairItem(
                    draft.WorkflowId,
                    plan.MemberId,
                    plan.ActorId,
                    StudioWorkflowDraftMemberRepairItem.Failed,
                    CommandId: plan.CommandId,
                    Error: ex.Message));
            }
        }

        return new StudioWorkflowDraftMemberRepairResult(
            normalizedScopeId,
            workspace.Drafts.Count,
            accepted,
            skipped,
            failed,
            items);
    }
}
