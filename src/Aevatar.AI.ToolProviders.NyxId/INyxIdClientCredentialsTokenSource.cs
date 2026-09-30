namespace Aevatar.AI.ToolProviders.NyxId;

public interface INyxIdClientCredentialsTokenSource
{
    Task<string?> GetAccessTokenAsync(CancellationToken ct);
}
