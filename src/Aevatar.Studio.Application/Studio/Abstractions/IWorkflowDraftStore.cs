namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Scoped workflow draft catalog for the Studio editor.
/// Drafts are the authoritative editor state for in-flight workflow edits and are
/// separate from committed runtime workflows owned by <c>IScopeWorkflowCommandPort</c>.
/// </summary>
public interface IWorkflowDraftStore
{
    Task SaveDraftAsync(string scopeId, string workflowId, string workflowName, string yaml, CancellationToken ct);

    Task<IReadOnlyList<WorkflowDraft>> ListDraftsAsync(string scopeId, CancellationToken ct);

    Task<WorkflowDraft?> GetDraftAsync(string scopeId, string workflowId, CancellationToken ct);

    Task DeleteDraftAsync(string scopeId, string workflowId, CancellationToken ct);
}

public sealed record WorkflowDraft(
    string WorkflowId,
    string WorkflowName,
    string Yaml,
    DateTimeOffset? UpdatedAtUtc);
