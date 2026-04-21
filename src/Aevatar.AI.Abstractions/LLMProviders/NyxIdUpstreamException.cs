namespace Aevatar.AI.Abstractions.LLMProviders;

/// <summary>
/// Thrown when a request to a NyxID upstream route fails in a way that should surface to the user
/// as a classified, actionable error rather than a raw HTTP/SDK error. Carries the upstream HTTP
/// status code, the NyxID route identifier, and the resolved model so downstream layers can present
/// consistent diagnostics without re-parsing the provider error text.
/// </summary>
public sealed class NyxIdUpstreamException : InvalidOperationException
{
    public NyxIdUpstreamException(
        NyxIdUpstreamFailureKind kind,
        int? status,
        string routeName,
        string? model,
        string userMessage,
        Exception? innerException = null)
        : base(userMessage, innerException)
    {
        Kind = kind;
        Status = status;
        RouteName = routeName;
        Model = model;
    }

    public NyxIdUpstreamFailureKind Kind { get; }

    public int? Status { get; }

    public string RouteName { get; }

    public string? Model { get; }
}

public enum NyxIdUpstreamFailureKind
{
    Unknown = 0,
    ServiceUnavailable = 1,
    RateLimited = 2,
    AuthenticationFailed = 3,
    RequestRejected = 4,
    UpstreamServerError = 5,
    NoResponse = 6,
}
