namespace Aevatar.Studio.Application.Studio.Contracts;

/// <summary>
/// Stable wire status values returned in
/// <see cref="ProvisionWorkflowResponse.BindingStatus"/>. The provision flow is
/// a thin composition over the existing member create + bind + invoke services,
/// so it reuses the binding-run terminal vocabulary rather than inventing a
/// parallel one:
/// <list type="bullet">
///   <item><c>succeeded</c> — the member bound in time; if
///   <see cref="ProvisionWorkflowRequest.RunImmediately"/> was set a run was
///   started and <see cref="ProvisionWorkflowResponse.RunId"/> is populated.</item>
///   <item><c>pending</c> — the bind was accepted but did not reach a terminal
///   state within <see cref="ProvisionWorkflowRequest.BindTimeoutSeconds"/>; the
///   member exists and will bind, the caller can poll the binding run and
///   re-invoke later. No run was started.</item>
/// </list>
/// A bind that reaches a terminal failure (<c>failed</c>/<c>rejected</c>) is
/// surfaced as an exception, not a status value, so the endpoint maps it to a
/// 4xx/5xx with the binding error detail.
/// </summary>
public static class ProvisionWorkflowBindingStatusNames
{
    public const string Succeeded = "succeeded";
    public const string Pending = "pending";
}

/// <summary>
/// Single-call workflow provisioning request. The caller supplies the workflow
/// body inline (YAML) plus a prompt; the service composes member create + bind +
/// (optional) invoke so a Claude Code session reaches a runnable, scope-owned
/// workflow in one proxied call. No serviceId / memberId / workflowId is
/// accepted — those are minted internally and returned.
/// </summary>
public sealed record ProvisionWorkflowRequest(
    string DisplayName,
    string WorkflowYaml,
    string? Prompt = null,
    bool RunImmediately = true,
    int BindTimeoutSeconds = ProvisionWorkflowRequest.DefaultBindTimeoutSeconds)
{
    public const int DefaultBindTimeoutSeconds = 60;
}

/// <summary>
/// Result of a single-call provision. <see cref="RunId"/> is only populated when
/// the member bound in time and <see cref="ProvisionWorkflowRequest.RunImmediately"/>
/// was set. <see cref="ObservatoryUrl"/> is the run viewer page (a "my runs"
/// surface, no per-run deep URL — locate the run by <see cref="RunId"/>).
/// <see cref="StudioUrl"/> is the editable Studio member page and is null until
/// the member is assigned to a team (a freshly provisioned member has no team).
/// </summary>
public sealed record ProvisionWorkflowResponse(
    string MemberId,
    string ScopeId,
    string BindingStatus,
    string ObservatoryUrl)
{
    public string? PublishedServiceId { get; init; }

    public string? RevisionId { get; init; }

    public string? RunId { get; init; }

    public string? StudioUrl { get; init; }
}
