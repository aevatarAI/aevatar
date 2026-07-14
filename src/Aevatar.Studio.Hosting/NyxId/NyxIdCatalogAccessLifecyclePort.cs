using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.Studio.Application.Authorization;
using Microsoft.Extensions.Configuration;

namespace Aevatar.Studio.Hosting.NyxId;

internal sealed class NyxIdCatalogAccessLifecyclePort(
    INyxIdCatalogSnapshotCommandPort commandPort,
    IConfiguration configuration,
    TimeProvider timeProvider) : INyxIdCatalogAccessLifecyclePort
{
    public Task InvalidateAsync(ExternalSubjectRef subject, string reason, CancellationToken ct = default)
    {
        var authority = NyxIdAuthorityResolver.ResolveNyxIdAuthorityBase(configuration);
        if (string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(subject.ExternalUserId))
            return Task.CompletedTask;
        return commandPort.InvalidateAsync(new NyxIdCatalogOwnerIdentity
        {
            Authority = authority,
            OwnerKind = NyxIdCatalogOwnerKind.Personal,
            OwnerSubject = subject.ExternalUserId.Trim(),
        }, timeProvider.GetUtcNow(), reason, ct);
    }
}
