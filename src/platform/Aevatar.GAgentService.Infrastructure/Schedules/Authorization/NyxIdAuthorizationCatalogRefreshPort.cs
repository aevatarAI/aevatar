using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Infrastructure.Schedules.Authorization;

public sealed class NyxIdAuthorizationCatalogRefreshPort : INyxIdAuthorizationCatalogRefreshPort
{
    public const string PublishedContractMissingFailureCode =
        "nyxid_catalog_published_contract_missing";

    private readonly INyxIdAuthorizationCatalogCommandPort _commandPort;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NyxIdAuthorizationCatalogRefreshPort> _logger;

    public NyxIdAuthorizationCatalogRefreshPort(
        INyxIdAuthorizationCatalogCommandPort commandPort,
        TimeProvider timeProvider,
        ILogger<NyxIdAuthorizationCatalogRefreshPort> logger)
    {
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<NyxIdAuthorizationCatalogRefreshResult> RefreshPersonalAsync(
        string verifiedOwnerSubject,
        string bearerToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedOwnerSubject);
        return RefreshAsync(new AuthorizationOwnerIdentity
        {
            Authority = NyxIdAuthorizationAuthorities.NyxId,
            OwnerKind = AuthorizationOwnerKind.Personal,
            OwnerSubject = verifiedOwnerSubject.Trim(),
        }, bearerToken, ct);
    }

    public async Task<NyxIdAuthorizationCatalogRefreshResult> RefreshAsync(
        AuthorizationOwnerIdentity owner,
        string bearerToken,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        if (!string.Equals(
                owner.Authority?.Trim(),
                NyxIdAuthorizationAuthorities.NyxId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("NyxID authorization catalog owner authority is not supported.");
        }
        if (owner.OwnerKind != AuthorizationOwnerKind.Personal)
        {
            return new NyxIdAuthorizationCatalogRefreshResult(
                NyxIdAuthorizationCatalogRefreshStatus.OwnerNotSupported,
                "nyxid_catalog_organization_owner_not_supported");
        }
        if (string.IsNullOrWhiteSpace(owner.OwnerSubject))
            throw new InvalidOperationException("NyxID authorization catalog owner subject is required.");

        var normalizedOwner = owner.Clone();
        normalizedOwner.Authority = NyxIdAuthorizationAuthorities.NyxId;
        normalizedOwner.OwnerSubject = owner.OwnerSubject.Trim();
        var now = _timeProvider.GetUtcNow();
        await _commandPort.ActivateAsync(normalizedOwner, now, ct);
        await _commandPort.InvalidateAsync(
            normalizedOwner,
            now,
            PublishedContractMissingFailureCode,
            ct);
        _logger.LogWarning(
            "NyxID authorization catalog refresh is blocked because exact owner topology is not published. ownerKind={OwnerKind}",
            normalizedOwner.OwnerKind);
        return new NyxIdAuthorizationCatalogRefreshResult(
            NyxIdAuthorizationCatalogRefreshStatus.PublishedContractMissing,
            PublishedContractMissingFailureCode);
    }
}
