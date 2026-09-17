namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>
/// Assistant-action postcondition evidence reads. Evidence comes only from
/// NyxID's secret-free authorization projections (identity, lifecycle, and a
/// monotonic state_version) — never from the full detail routes, whose
/// user-controlled display text is not evidence.
/// </summary>
public interface INyxIdActionEvidenceReadPort
{
    Task<NyxIdApiAccessResult<NyxIdUserServiceAuthorizationEvidence>>
        GetUserServiceAuthorizationAsync(
            string bearerToken,
            string userServiceId,
            CancellationToken ct = default);

    Task<NyxIdApiAccessResult<NyxIdServiceAccessEvidence>> GetServiceAccessAsync(
        string bearerToken,
        string userServiceId,
        string serviceSlug,
        CancellationToken ct = default);

    Task<NyxIdApiAccessResult<NyxIdAgentApiKeyEvidence>> GetAgentApiKeyAsync(
        string bearerToken,
        string keyId,
        CancellationToken ct = default);
}

public sealed class NyxIdActionEvidenceReadPort : INyxIdActionEvidenceReadPort
{
    private readonly INyxIdApiClientFactory _clientFactory;
    private readonly NyxIdMcpOperationCatalogReader _catalogReader;

    public NyxIdActionEvidenceReadPort(INyxIdApiClientFactory clientFactory)
        : this(
            clientFactory,
            new NyxIdMcpOperationCatalogReader(clientFactory, TimeProvider.System))
    {
    }

    internal NyxIdActionEvidenceReadPort(
        INyxIdApiClientFactory clientFactory,
        NyxIdMcpOperationCatalogReader catalogReader)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _catalogReader = catalogReader ?? throw new ArgumentNullException(nameof(catalogReader));
    }

    public async Task<NyxIdApiAccessResult<NyxIdUserServiceAuthorizationEvidence>>
        GetUserServiceAuthorizationAsync(
            string bearerToken,
            string userServiceId,
            CancellationToken ct = default)
    {
        ValidateExactReadInput(bearerToken, userServiceId);
        try
        {
            using var client = _clientFactory.CreateClient();
            var response = await client
                .GetServiceAuthorizationAsync(bearerToken, userServiceId, ct)
                .ConfigureAwait(false);
            return NyxIdApiAccessResponseParser.ParseUserServiceAuthorization(response);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return NyxIdApiAccessResult<NyxIdUserServiceAuthorizationEvidence>.Failed(
                TransportFailure("nyxid_user_service_authorization_read_failed"));
        }
    }

    public async Task<NyxIdApiAccessResult<NyxIdServiceAccessEvidence>>
        GetServiceAccessAsync(
            string bearerToken,
            string userServiceId,
            string serviceSlug,
            CancellationToken ct = default)
    {
        ValidateExactReadInput(bearerToken, userServiceId);
        ValidateExactReadInput(bearerToken, serviceSlug);
        var read = await _catalogReader.ReadAsync(bearerToken, ct).ConfigureAwait(false);
        if (!read.Succeeded)
        {
            return read.Failure!.Kind switch
            {
                NyxIdMcpOperationCatalogReadFailureKind.AccessDenied =>
                    NyxIdApiAccessResult<NyxIdServiceAccessEvidence>.Failed(
                        new NyxIdApiAccessFailure(
                            NyxIdApiAccessFailureKind.Forbidden,
                            "nyxid_service_access_forbidden")),
                NyxIdMcpOperationCatalogReadFailureKind.SourceUnavailable =>
                    NyxIdApiAccessResult<NyxIdServiceAccessEvidence>.Failed(
                        new NyxIdApiAccessFailure(
                            NyxIdApiAccessFailureKind.MalformedResponse,
                            "nyxid_service_access_catalog_invalid")),
                NyxIdMcpOperationCatalogReadFailureKind.AmbiguousServiceIdentity =>
                    NyxIdApiAccessResult<NyxIdServiceAccessEvidence>.Failed(
                        new NyxIdApiAccessFailure(
                            NyxIdApiAccessFailureKind.Conflict,
                            "nyxid_service_access_conflict")),
                _ => NyxIdApiAccessResult<NyxIdServiceAccessEvidence>.Failed(
                    TransportFailure("nyxid_service_access_read_failed")),
            };
        }

        var catalog = read.Catalog!;
        var matches = catalog.Services
            .Where(service => string.Equals(
                    service.UserServiceId,
                    userServiceId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    service.ServiceSlug,
                    serviceSlug,
                    StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 1 && !catalog.Issues.Any(issue => string.Equals(
                issue.UserServiceId,
                userServiceId,
                StringComparison.Ordinal)))
        {
            return NyxIdApiAccessResult<NyxIdServiceAccessEvidence>.Success(
                new NyxIdServiceAccessEvidence(userServiceId, serviceSlug));
        }

        var conflictsWithExactIdentity = matches.Length > 1 ||
            catalog.Services.Any(service => string.Equals(
                service.UserServiceId,
                userServiceId,
                StringComparison.Ordinal)) ||
            catalog.Issues.Any(issue => string.Equals(
                issue.UserServiceId,
                userServiceId,
                StringComparison.Ordinal));
        return NyxIdApiAccessResult<NyxIdServiceAccessEvidence>.Failed(
            new NyxIdApiAccessFailure(
                conflictsWithExactIdentity
                    ? NyxIdApiAccessFailureKind.Conflict
                    : NyxIdApiAccessFailureKind.NotFound,
                conflictsWithExactIdentity
                    ? "nyxid_service_access_conflict"
                    : "nyxid_service_access_not_found"));
    }

    public async Task<NyxIdApiAccessResult<NyxIdAgentApiKeyEvidence>> GetAgentApiKeyAsync(
        string bearerToken,
        string keyId,
        CancellationToken ct = default)
    {
        ValidateExactReadInput(bearerToken, keyId);
        try
        {
            using var client = _clientFactory.CreateClient();
            var response = await client.GetApiKeyAuthorizationAsync(bearerToken, keyId, ct)
                .ConfigureAwait(false);
            return NyxIdApiAccessResponseParser.ParseAgentApiKey(response);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return NyxIdApiAccessResult<NyxIdAgentApiKeyEvidence>.Failed(
                TransportFailure("nyxid_agent_api_key_read_failed"));
        }
    }

    private static void ValidateExactReadInput(string bearerToken, string resourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        if (!string.Equals(bearerToken, bearerToken.Trim(), StringComparison.Ordinal) ||
            bearerToken.Any(char.IsWhiteSpace) ||
            !string.Equals(resourceId, resourceId.Trim(), StringComparison.Ordinal) ||
            resourceId.Any(char.IsControl))
        {
            throw new ArgumentException("NyxID exact read input must be canonical.");
        }
    }

    private static NyxIdApiAccessFailure TransportFailure(string code) =>
        new(NyxIdApiAccessFailureKind.Transport, code);
}
