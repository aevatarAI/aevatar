namespace Aevatar.Workflow.Abstractions;

/// <summary>
/// Transient NyxID caller credential selected at an ingress boundary. This type must not be
/// serialized, persisted, logged, or included in generated output.
/// </summary>
public sealed class NyxIdCallerCredentialSelection
{
    private readonly string _bearerToken;

    private NyxIdCallerCredentialSelection(
        NyxIdCallerCredentialKind kind,
        string bearerToken,
        bool canManageUserServices)
    {
        Kind = kind;
        _bearerToken = NormalizeRequired(bearerToken);
        CanManageUserServices = canManageUserServices;
    }

    public NyxIdCallerCredentialKind Kind { get; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanManageUserServices { get; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? SourceReadableUserBearerToken =>
        Kind == NyxIdCallerCredentialKind.SourceReadableUserBearer ? _bearerToken : null;

    [System.Text.Json.Serialization.JsonIgnore]
    public string? ProxyDelegationToken =>
        Kind == NyxIdCallerCredentialKind.ProxyDelegation ? _bearerToken : null;

    public static NyxIdCallerCredentialSelection SourceReadableUserBearer(string bearerToken) =>
        new(NyxIdCallerCredentialKind.SourceReadableUserBearer, bearerToken, false);

    public static NyxIdCallerCredentialSelection DirectUserBearer(string bearerToken) =>
        new(NyxIdCallerCredentialKind.SourceReadableUserBearer, bearerToken, true);

    public static NyxIdCallerCredentialSelection ProxyDelegation(string bearerToken) =>
        new(NyxIdCallerCredentialKind.ProxyDelegation, bearerToken, false);

    public static NyxIdCallerCredentialSelection? SourceReadableUserBearerOrNull(string? bearerToken) =>
        string.IsNullOrWhiteSpace(bearerToken) ? null : SourceReadableUserBearer(bearerToken);

    public override string ToString() =>
        $"{nameof(NyxIdCallerCredentialSelection)} {{ Kind = {Kind}, BearerToken = [REDACTED] }}";

    private static string NormalizeRequired(string? bearerToken)
    {
        var parsed = WorkflowCallerCredentialTokens.ParseOptional(bearerToken);
        if (!parsed.IsValid || string.IsNullOrWhiteSpace(parsed.NormalizedBearerToken))
            throw new ArgumentException("A valid bearer token is required.", nameof(bearerToken));

        return parsed.NormalizedBearerToken;
    }
}
