namespace Aevatar.Workflow.Application.Abstractions.Observatory;

// 06-20-observatory-admin-cross-scope (G3/G4) + admin run drilldown: cross-scope admin reads are kept as a
//   SEPARATE narrow contract so the existing scope-bound IWorkflowRunObservatoryQueryService stays unchanged.
//   Callers of this contract MUST be authorized as a platform admin/operator by the endpoint BEFORE invoking it —
//   the service itself does not authorize. Like the scope-bound service, it depends only on query ports
//   (no dispatch/runtime/HttpContext).
public interface IWorkflowRunAdminQueryService
{
    /// <summary>
    /// Lists runs across ALL scopes (no scope filter), most-recent first, each tagged with its <c>ScopeId</c>.
    /// Filters are applied by the projection store before the result is bounded by
    /// <see cref="ObservatoryRunListFilter.Take"/>. Authorization is the endpoint's responsibility.
    /// </summary>
    Task<IReadOnlyList<ObservatoryRunSummary>> ListAllRunsAsync(
        ObservatoryRunListFilter filter,
        CancellationToken ct = default);

    /// <summary>
    /// Gets one run across all scopes by its stable workflow run id. Authorization is the endpoint's responsibility.
    /// </summary>
    Task<ObservatoryRunDetail?> GetRunAsync(
        string runId,
        CancellationToken ct = default);

    /// <summary>
    /// Gets one run graph across all scopes by its stable workflow run id. Authorization is the endpoint's responsibility.
    /// </summary>
    Task<ObservatoryRunGraph?> GetRunGraphAsync(
        string runId,
        CancellationToken ct = default);
}
