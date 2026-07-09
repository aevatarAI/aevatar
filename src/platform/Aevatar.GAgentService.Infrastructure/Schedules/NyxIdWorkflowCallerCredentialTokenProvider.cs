using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.Workflow.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Infrastructure.Schedules;

public sealed class NyxIdWorkflowCallerCredentialTokenProvider : IWorkflowCallerCredentialTokenProvider
{
    private readonly INyxIdCapabilityBroker _broker;
    private readonly ILogger<NyxIdWorkflowCallerCredentialTokenProvider> _logger;

    public NyxIdWorkflowCallerCredentialTokenProvider(
        INyxIdCapabilityBroker broker,
        ILogger<NyxIdWorkflowCallerCredentialTokenProvider> logger)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<WorkflowCallerCredentialTokenResolution> ResolveAsync(
        WorkflowNyxIdCredentialSource source,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Subject == null)
            throw new ArgumentException("Workflow caller NyxID credential subject is required.", nameof(source));

        try
        {
            var handle = await _broker.IssueShortLivedAsync(
                new ExternalSubjectRef
                {
                    Platform = source.Subject.Platform,
                    Tenant = source.Subject.Tenant,
                    ExternalUserId = source.Subject.ExternalUserId,
                },
                new CapabilityScope { Value = source.Scope },
                ct);
            if (string.IsNullOrWhiteSpace(handle.AccessToken))
                throw new InvalidOperationException("NyxID credential exchange returned an empty access token.");

            return new WorkflowCallerCredentialTokenResolution(
                handle.AccessToken,
                ResolveExpiresAtUtc(handle));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Workflow caller NyxID credential token refresh failed. platform={Platform} tenant={Tenant} scope={Scope}",
                source.Subject.Platform ?? string.Empty,
                source.Subject.Tenant ?? string.Empty,
                source.Scope ?? string.Empty);
            throw;
        }
    }

    private static DateTimeOffset? ResolveExpiresAtUtc(CapabilityHandle handle)
    {
        if (handle.ExpiresAtUnix <= 0)
            return null;

        return DateTimeOffset.FromUnixTimeSeconds(handle.ExpiresAtUnix).ToUniversalTime();
    }
}
