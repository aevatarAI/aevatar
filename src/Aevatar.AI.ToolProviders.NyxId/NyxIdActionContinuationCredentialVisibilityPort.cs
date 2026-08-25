namespace Aevatar.AI.ToolProviders.NyxId;

public enum NyxIdActionContinuationCredentialVisibilityStatus
{
    Unspecified = 0,
    Visible = 1,
    CredentialRefreshRequired = 2,
    SourceUnavailable = 3,
}

public sealed record NyxIdActionContinuationCredentialVisibilityResult(
    NyxIdActionContinuationCredentialVisibilityStatus Status,
    string UserServiceId,
    string ReasonCode);

public interface INyxIdActionContinuationCredentialVisibilityPort
{
    Task<NyxIdActionContinuationCredentialVisibilityResult> InspectUserServiceAsync(
        string bearerToken,
        string userServiceId,
        CancellationToken ct = default);
}

public sealed class NyxIdActionContinuationCredentialVisibilityPort
    : INyxIdActionContinuationCredentialVisibilityPort
{
    private readonly NyxIdMcpOperationCatalogReader _catalogReader;

    public NyxIdActionContinuationCredentialVisibilityPort(
        INyxIdApiClientFactory clientFactory)
        : this(new NyxIdMcpOperationCatalogReader(clientFactory, TimeProvider.System))
    {
    }

    internal NyxIdActionContinuationCredentialVisibilityPort(
        NyxIdMcpOperationCatalogReader catalogReader)
    {
        _catalogReader = catalogReader ?? throw new ArgumentNullException(nameof(catalogReader));
    }

    public async Task<NyxIdActionContinuationCredentialVisibilityResult> InspectUserServiceAsync(
        string bearerToken,
        string userServiceId,
        CancellationToken ct = default)
    {
        ValidateExactInput(bearerToken, userServiceId);
        var read = await _catalogReader.ReadAsync(bearerToken, ct).ConfigureAwait(false);
        if (!read.Succeeded)
        {
            return read.Failure!.Kind switch
            {
                NyxIdMcpOperationCatalogReadFailureKind.AccessDenied => Result(
                    NyxIdActionContinuationCredentialVisibilityStatus.CredentialRefreshRequired,
                    userServiceId,
                    "nyxid_action_continuation_credential_denied"),
                NyxIdMcpOperationCatalogReadFailureKind.SourceUnavailable or
                    NyxIdMcpOperationCatalogReadFailureKind.AmbiguousServiceIdentity => Result(
                    NyxIdActionContinuationCredentialVisibilityStatus.SourceUnavailable,
                    userServiceId,
                    "nyxid_action_continuation_catalog_unavailable"),
                _ => Result(
                    NyxIdActionContinuationCredentialVisibilityStatus.SourceUnavailable,
                    userServiceId,
                    "nyxid_action_continuation_catalog_read_failed"),
            };
        }

        var catalog = read.Catalog!;
        var matches = catalog.Services
            .Where(service => string.Equals(
                service.UserServiceId,
                userServiceId,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        var exactIdentityIssue = catalog.Issues.Any(issue => string.Equals(
            issue.UserServiceId,
            userServiceId,
            StringComparison.Ordinal));
        if (matches.Length == 1 && !exactIdentityIssue)
        {
            return Result(
                NyxIdActionContinuationCredentialVisibilityStatus.Visible,
                userServiceId,
                "nyxid_action_continuation_user_service_visible");
        }

        if (matches.Length > 1 || exactIdentityIssue)
        {
            return Result(
                NyxIdActionContinuationCredentialVisibilityStatus.SourceUnavailable,
                userServiceId,
                "nyxid_action_continuation_catalog_identity_invalid");
        }

        return Result(
            NyxIdActionContinuationCredentialVisibilityStatus.CredentialRefreshRequired,
            userServiceId,
            "nyxid_action_continuation_user_service_not_visible");
    }

    private static NyxIdActionContinuationCredentialVisibilityResult Result(
        NyxIdActionContinuationCredentialVisibilityStatus status,
        string userServiceId,
        string reasonCode) =>
        new(status, userServiceId, reasonCode);

    private static void ValidateExactInput(string bearerToken, string userServiceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(userServiceId);
        if (!string.Equals(bearerToken, bearerToken.Trim(), StringComparison.Ordinal) ||
            bearerToken.Any(char.IsWhiteSpace) ||
            !string.Equals(userServiceId, userServiceId.Trim(), StringComparison.Ordinal) ||
            userServiceId.Any(char.IsControl))
        {
            throw new ArgumentException("NyxID action continuation visibility input must be canonical.");
        }
    }
}
