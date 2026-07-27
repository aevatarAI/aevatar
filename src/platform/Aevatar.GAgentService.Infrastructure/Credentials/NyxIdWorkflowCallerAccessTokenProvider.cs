using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Credentials;

namespace Aevatar.GAgentService.Infrastructure.Credentials;

public sealed class NyxIdWorkflowCallerAccessTokenProvider(INyxIdCapabilityBroker? broker = null)
    : IWorkflowCallerAccessTokenProvider
{
    private readonly INyxIdCapabilityBroker? _broker = broker;

    public async Task<string> IssueAsync(
        WorkflowCallerNyxIdAuthority authority,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var broker = _broker ?? throw new InvalidOperationException(
            "NyxID caller access token issuance requires a configured NyxID capability broker.");
        var externalSubject = new ExternalSubjectRef
        {
            Platform = Require(authority.Platform, nameof(authority.Platform)),
            Tenant = authority.Tenant?.Trim() ?? string.Empty,
            ExternalUserId = Require(authority.ExternalUserId, nameof(authority.ExternalUserId)),
        };
        var scope = new CapabilityScope { Value = Require(authority.Scope, nameof(authority.Scope)) };
        var bindingId = authority.BindingId?.Trim() ?? string.Empty;
        var handle = bindingId.Length > 0
            ? await broker.IssueShortLivedByBindingIdAsync(externalSubject, bindingId, scope, ct)
            : await broker.IssueShortLivedAsync(externalSubject, scope, ct);
        return string.IsNullOrWhiteSpace(handle.AccessToken)
            ? throw new InvalidOperationException("NyxID credential exchange returned an empty access token.")
            : handle.AccessToken.Trim();
    }

    private static string Require(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("NyxID caller authority is incomplete.", name)
            : value.Trim();
}
