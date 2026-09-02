using Aevatar.GAgentService.Abstractions;

namespace Aevatar.GAgentService.Core.Models;

public sealed record NyxIdServiceRegistrationRequest(
    ServiceIdentity Identity,
    string DisplayName,
    string OpenApiUrl,
    string DesiredSpecHash,
    string AccessToken,
    string ServiceCredential,
    string CredentialKid,
    string? ExistingNyxIdServiceId = null,
    string? ExistingNyxIdSlug = null);

public sealed record NyxIdServiceLookupRequest(
    string AccessToken,
    string NyxIdServiceId);

public sealed record NyxIdServiceRetirementRequest(
    string AccessToken,
    string NyxIdServiceId);

public sealed record NyxIdServiceRegistrationResult(
    bool Succeeded,
    string? NyxIdServiceId = null,
    string? NyxIdSlug = null,
    string? RegisteredSpecHash = null,
    NyxIdRegistrationFailure? Failure = null,
    bool AlreadyExists = false)
{
    public static NyxIdServiceRegistrationResult Success(
        string nyxIdServiceId,
        string nyxIdSlug,
        string registeredSpecHash) =>
        new(true, nyxIdServiceId, nyxIdSlug, registeredSpecHash);

    public static NyxIdServiceRegistrationResult Failed(
        NyxIdRegistrationFailure failure,
        bool alreadyExists = false) =>
        new(false, Failure: failure, AlreadyExists: alreadyExists);
}

public sealed record NyxIdServiceLookupResult(
    bool Found,
    string? NyxIdServiceId = null,
    string? NyxIdSlug = null,
    string? RegisteredSpecHash = null,
    NyxIdRegistrationFailure? Failure = null)
{
    public static NyxIdServiceLookupResult Success(
        string nyxIdServiceId,
        string nyxIdSlug,
        string registeredSpecHash) =>
        new(true, nyxIdServiceId, nyxIdSlug, registeredSpecHash);

    public static NyxIdServiceLookupResult Missing() => new(false);

    public static NyxIdServiceLookupResult Failed(NyxIdRegistrationFailure failure) =>
        new(false, Failure: failure);
}

public sealed record NyxIdServiceRetirementResult(
    bool Succeeded,
    NyxIdRegistrationFailure? Failure = null)
{
    public static NyxIdServiceRetirementResult Success() => new(true);

    public static NyxIdServiceRetirementResult Failed(NyxIdRegistrationFailure failure) =>
        new(false, failure);
}

public sealed record NyxIdRegistrationFailure(
    NyxIdRegistrationFailureKind Kind,
    string Reason,
    bool Retryable);

public enum NyxIdRegistrationFailureKind
{
    Unspecified = 0,
    MissingToken = 1,
    Conflict = 2,
    NotFound = 3,
    Validation = 4,
    Unauthorized = 5,
    Transient = 6,
    Adapter = 7,
}
