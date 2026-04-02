namespace Aevatar.AI.Abstractions.LLMProviders;

/// <summary>
/// Thrown when NyxID authentication is required but no access token is available.
/// Endpoints should catch this and return HTTP 401 so the frontend can trigger the login flow.
/// </summary>
public sealed class NyxIdAuthenticationRequiredException : InvalidOperationException
{
    public NyxIdAuthenticationRequiredException(string providerName)
        : base($"NyxID authentication required for provider '{providerName}'. Please sign in.") { }
}
