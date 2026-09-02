using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Microsoft.Extensions.Configuration;

namespace Aevatar.Studio.Hosting.NyxId;

internal sealed class NyxIdCatalogAccessLifecyclePort(
    INyxIdAuthorizationCatalogCommandPort commandPort,
    IConfiguration configuration,
    TimeProvider timeProvider) : INyxIdCatalogAccessLifecyclePort
{
    public Task InvalidateAsync(ExternalSubjectRef subject, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(NyxIdAuthorityResolver.ResolveNyxIdAuthorityBase(configuration)) ||
            !string.Equals(subject.Platform, Aevatar.Foundation.Abstractions.OwnerScope.NyxIdPlatform, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(subject.ExternalUserId))
            return Task.CompletedTask;
        return commandPort.InvalidateAsync(new AuthorizationOwnerIdentity
        {
            Authority = NyxIdAuthorizationAuthorities.NyxId,
            OwnerKind = AuthorizationOwnerKind.Personal,
            OwnerSubject = subject.ExternalUserId.Trim(),
        }, timeProvider.GetUtcNow(), reason, ct);
    }
}
