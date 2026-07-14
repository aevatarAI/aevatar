using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Credentials;

namespace Aevatar.GAgentService.Infrastructure.Credentials;

public sealed class NyxIdWorkflowCallerAccessTokenProvider(INyxIdCapabilityBroker broker)
    : IWorkflowCallerAccessTokenProvider
{
    private readonly INyxIdCapabilityBroker _broker = broker ?? throw new ArgumentNullException(nameof(broker));

    public async Task<string> IssueAsync(
        WorkflowCallerNyxIdAuthority authority,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var handle = await _broker.IssueShortLivedAsync(
            new ExternalSubjectRef
            {
                Platform = Require(authority.Platform, nameof(authority.Platform)),
                Tenant = authority.Tenant?.Trim() ?? string.Empty,
                ExternalUserId = Require(authority.ExternalUserId, nameof(authority.ExternalUserId)),
            },
            new CapabilityScope { Value = Require(authority.Scope, nameof(authority.Scope)) },
            ct);
        return string.IsNullOrWhiteSpace(handle.AccessToken)
            ? throw new InvalidOperationException("NyxID credential exchange returned an empty access token.")
            : handle.AccessToken.Trim();
    }

    private static string Require(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("NyxID caller authority is incomplete.", name)
            : value.Trim();
}
