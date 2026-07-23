using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Infrastructure.Schedules;

public sealed class NyxIdScheduledServiceInvocationCredentialExchangePort : IScheduledServiceInvocationCredentialExchangePort
{
    private readonly INyxIdCapabilityBroker _broker;
    private readonly ILogger<NyxIdScheduledServiceInvocationCredentialExchangePort> _logger;

    public NyxIdScheduledServiceInvocationCredentialExchangePort(
        INyxIdCapabilityBroker broker,
        ILogger<NyxIdScheduledServiceInvocationCredentialExchangePort> logger)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ScheduledServiceInvocationCredentialExchangeResult> IssueNyxIdAsync(
        ScheduledServiceInvocationNyxIdCredentialSource source,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ct.ThrowIfCancellationRequested();
        if (source.Subject == null)
            throw new ArgumentException(
                $"Schedule {ToErrorSubject(source.Role)} NyxID subject is required for credential exchange.",
                nameof(source));

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
                return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                    "NyxID credential exchange returned an empty access token.");
            }

            return ScheduledServiceInvocationCredentialExchangeResult.Success(
                handle.AccessToken,
                handle.ExpiresAtUnix > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(handle.ExpiresAtUnix)
                    : null);
        }
        catch (BindingNotFoundException)
        {
            return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                $"NyxID binding was not found for the scheduled {ToErrorSubject(source.Role)}.");
        }
        catch (BindingRevokedException)
        {
            return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                $"NyxID binding was revoked for the scheduled {ToErrorSubject(source.Role)}.");
        }
        catch (BindingScopeMismatchException)
        {
            return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                "NyxID binding does not grant the requested schedule scope.");
        }
        catch (BindingServiceAccessMismatchException)
        {
            return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                "NyxID binding does not grant the required Aevatar service.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Scheduled service invocation NyxID credential exchange failed.");
            return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                "NyxID credential exchange failed.");
        }
    }

    private static string ToErrorSubject(ScheduledServiceInvocationNyxIdCredentialRole role) =>
        role == ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner ? "scope owner" : "subject";
}
