namespace Aevatar.AI.ToolProviders.NyxId;

public interface INyxIdActionEvidenceReadPort
{
    Task<NyxIdApiAccessResult<NyxIdUserServiceAuthorizationEvidence>>
        GetUserServiceAuthorizationAsync(
            string bearerToken,
            string userServiceId,
            CancellationToken ct = default);

    Task<NyxIdApiAccessResult<NyxIdAgentApiKeyEvidence>> GetAgentApiKeyAsync(
        string bearerToken,
        string keyId,
        CancellationToken ct = default);
}

public sealed class NyxIdActionEvidenceReadPort : INyxIdActionEvidenceReadPort
{
    private readonly INyxIdApiClientFactory _clientFactory;

    public NyxIdActionEvidenceReadPort(INyxIdApiClientFactory clientFactory)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
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
            var response = await client.GetServiceAsync(bearerToken, userServiceId, ct)
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

    public async Task<NyxIdApiAccessResult<NyxIdAgentApiKeyEvidence>> GetAgentApiKeyAsync(
        string bearerToken,
        string keyId,
        CancellationToken ct = default)
    {
        ValidateExactReadInput(bearerToken, keyId);
        try
        {
            using var client = _clientFactory.CreateClient();
            var response = await client.GetApiKeyAsync(bearerToken, keyId, ct)
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
