using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Application.Schedules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Infrastructure.Schedules;

public sealed class NyxIdWorkflowScheduleCredentialExchangePort : IWorkflowScheduleCredentialExchangePort
{
    private readonly INyxIdCapabilityBroker? _broker;
    private readonly ILogger<NyxIdWorkflowScheduleCredentialExchangePort> _logger;

    public NyxIdWorkflowScheduleCredentialExchangePort(
        INyxIdCapabilityBroker? broker,
        ILogger<NyxIdWorkflowScheduleCredentialExchangePort> logger)
    {
        _broker = broker;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<WorkflowScheduleCredentialExchangeResult> IssueSenderNyxIdAsync(
        WorkflowScheduleNyxIdCredentialSource source,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ct.ThrowIfCancellationRequested();

        if (_broker == null)
        {
            return WorkflowScheduleCredentialExchangeResult.Failure(
                "Workflow schedule NyxID credential exchange is not configured.");
        }

        var subject = new ExternalSubjectRef
        {
            Platform = source.Subject.Platform,
            Tenant = source.Subject.Tenant,
            ExternalUserId = source.Subject.ExternalUserId,
        };

        try
        {
            var handle = await _broker.IssueShortLivedAsync(
                subject,
                new CapabilityScope { Value = source.Scope },
                ct);
            if (string.IsNullOrWhiteSpace(handle.AccessToken))
            {
                return WorkflowScheduleCredentialExchangeResult.Failure(
                    "NyxID credential exchange returned an empty access token.");
            }

            return WorkflowScheduleCredentialExchangeResult.Success(handle.AccessToken);
        }
        catch (BindingNotFoundException)
        {
            return WorkflowScheduleCredentialExchangeResult.Failure(
                "NyxID binding was not found for the scheduled subject.");
        }
        catch (BindingRevokedException)
        {
            return WorkflowScheduleCredentialExchangeResult.Failure(
                "NyxID binding was revoked for the scheduled subject.");
        }
        catch (BindingScopeMismatchException)
        {
            return WorkflowScheduleCredentialExchangeResult.Failure(
                "NyxID binding does not grant the requested schedule scope.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Workflow schedule NyxID credential exchange failed.");
            return WorkflowScheduleCredentialExchangeResult.Failure(
                "NyxID credential exchange failed.");
        }
    }
}
