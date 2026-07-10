namespace Aevatar.Workflow.Application.Abstractions.Observatory;

// 06-20-observatory-admin-cross-scope (G3/G4): the cross-scope OVERVIEW read, kept as a SEPARATE narrow contract
//   so the existing scope-bound IWorkflowRunObservatoryQueryService stays unchanged. Callers of this contract
//   MUST be authorized as a platform admin/operator by the endpoint BEFORE invoking it — the service itself does
//   not authorize. Like the scope-bound service, it depends only on query ports (no dispatch/runtime/HttpContext).
public interface IWorkflowRunAdminOverviewQueryService
{
    /// <summary>
    /// Lists runs across ALL scopes (no scope filter), most-recent first, each tagged with its <c>ScopeId</c>.
    /// This is a recent-N view bounded by <see cref="ObservatoryRunListFilter.Take"/>; any status filter is
    /// applied within that window (it is not an exhaustive scan of every run ever). Authorization is the
    /// endpoint's responsibility.
    /// </summary>
    Task<IReadOnlyList<ObservatoryRunSummary>> ListAllRunsAsync(
        ObservatoryRunListFilter filter,
        CancellationToken ct = default);
}
