using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
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

    public async Task<ScheduledServiceInvocationCredentialExchangeResult> IssueSenderNyxIdAsync(
        ScheduledServiceInvocationNyxIdCredentialSource source,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ct.ThrowIfCancellationRequested();

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
                ResolveExpiresAtUtc(handle));
        }
        catch (BindingNotFoundException)
        {
            return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                "NyxID binding was not found for the scheduled subject.");
        }
        catch (BindingRevokedException)
        {
            return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                "NyxID binding was revoked for the scheduled subject.");
        }
        catch (BindingScopeMismatchException)
        {
            return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                "NyxID binding does not grant the requested schedule scope.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Scheduled service invocation NyxID credential exchange failed.");
            return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                "NyxID credential exchange failed.");
        }
    }

    public Task<ScheduledServiceInvocationCredentialExchangeResult> IssueScopeOwnerNyxIdAsync(
        ScheduledServiceInvocationScopeOwnerNyxIdCredentialSource source,
        ServiceIdentity serviceIdentity,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(serviceIdentity);
        ct.ThrowIfCancellationRequested();

        var ownerSubject = new ScheduledServiceInvocationNyxIdCredentialSource(
            ResolveScopeOwnerSubject(source),
            source.Scope);
        return IssueSenderNyxIdAsync(ownerSubject, ct);
    }

    private static ScheduledServiceInvocationNyxIdSubjectRef ResolveScopeOwnerSubject(
        ScheduledServiceInvocationScopeOwnerNyxIdCredentialSource source)
    {
        if (source.OwnerSubject == null)
            throw new ArgumentException("Schedule scope owner NyxID subject is required for scope owner credential exchange.", nameof(source));

        return source.OwnerSubject;
    }

    private static DateTimeOffset? ResolveExpiresAtUtc(CapabilityHandle handle)
    {
        if (handle.ExpiresAtUnix <= 0)
            return null;

        return DateTimeOffset.FromUnixTimeSeconds(handle.ExpiresAtUnix).ToUniversalTime();
    }
}
