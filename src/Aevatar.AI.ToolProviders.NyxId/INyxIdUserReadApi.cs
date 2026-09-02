namespace Aevatar.AI.ToolProviders.NyxId;

// Narrow read seam over NyxIdApiClient so the admin authorizer
// and user directory are unit-testable without HTTP. NyxIdApiClient (sealed) implements this; tests fake it.
// Both methods return the raw NyxID JSON string; non-2xx is encoded by NyxIdApiClient as {"error":true,...}.
public interface INyxIdUserReadApi
{
    Task<string> GetCurrentUserAsync(string token, CancellationToken ct);

    Task<string> SearchAdminUsersAsync(string token, string email, CancellationToken ct);
}
