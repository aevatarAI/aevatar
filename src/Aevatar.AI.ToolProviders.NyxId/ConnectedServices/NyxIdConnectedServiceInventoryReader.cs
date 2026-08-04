namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

public sealed class NyxIdConnectedServiceInventoryReader
{
    private readonly NyxIdServiceInstanceClient _client;

    public NyxIdConnectedServiceInventoryReader(NyxIdServiceInstanceClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<NyxIdServiceInventoryResult> ReadAsync(
        string userToken,
        string? organizationToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userToken);
        var bindings = await _client
            .DiscoverAsync(userToken, organizationToken, ct)
            .ConfigureAwait(false);
        var result = new NyxIdServiceInventoryResult();
        result.Instances.Add(bindings
            .Where(static binding => binding.Instance.IsActive && binding.Instance.CredentialAllowed)
            .Select(static binding => binding.Instance.Clone()));
        return result;
    }
}
