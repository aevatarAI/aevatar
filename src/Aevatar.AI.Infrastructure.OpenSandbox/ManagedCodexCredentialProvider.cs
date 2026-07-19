using Aevatar.AI.Abstractions.CodexExecution;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;

namespace Aevatar.AI.Infrastructure.OpenSandbox;

internal interface IManagedCodexCredentialProvider
{
    Task<ManagedCodexCredential> IssueAsync(
        CodexExecutionNyxIdAuthority authority,
        int executionTimeoutSeconds,
        CancellationToken ct = default);
}

internal sealed record ManagedCodexCredential(string AccessToken, long ExpiresAtUnix);

internal sealed class ManagedCodexCredentialException(
    CodexExecutionFailure failure,
    Exception? innerException = null) : Exception(failure.Message, innerException)
{
    public CodexExecutionFailure Failure { get; } = failure;
}

internal sealed class NyxIdManagedCodexCredentialProvider(
    INyxIdCapabilityBroker broker,
    TimeProvider timeProvider) : IManagedCodexCredentialProvider
{
    internal const string RequiredScope = "llm:proxy";
    private const int MinimumExpiryMarginSeconds = 30;

    private readonly INyxIdCapabilityBroker _broker =
        broker ?? throw new ArgumentNullException(nameof(broker));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<ManagedCodexCredential> IssueAsync(
        CodexExecutionNyxIdAuthority authority,
        int executionTimeoutSeconds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var subject = new ExternalSubjectRef
        {
            Platform = Require(authority.Platform),
            Tenant = authority.Tenant?.Trim() ?? string.Empty,
            ExternalUserId = Require(authority.ExternalUserId),
        };

        CapabilityHandle handle;
        try
        {
            handle = await _broker.IssueShortLivedAsync(
                subject,
                new CapabilityScope { Value = RequiredScope },
                ct).ConfigureAwait(false);
        }
        catch (BindingNotFoundException exception)
        {
            throw Failure("nyxid_binding_required", "The NyxID identity has no active Aevatar binding.", exception);
        }
        catch (BindingRevokedException exception)
        {
            throw Failure("nyxid_binding_revoked", "The NyxID binding was revoked; bind the account again.", exception);
        }
        catch (BindingScopeMismatchException exception)
        {
            throw Failure(
                "llm_proxy_scope_missing",
                "The NyxID broker did not grant the llm:proxy capability.",
                exception);
        }
        catch (BindingServiceAccessMismatchException exception)
        {
            throw Failure("llm_service_access_missing", "The NyxID binding does not grant the configured LLM service.", exception);
        }

        var token = handle.AccessToken?.Trim();
        var minimumExpiry = _timeProvider.GetUtcNow().ToUnixTimeSeconds() +
                            executionTimeoutSeconds + MinimumExpiryMarginSeconds;
        if (string.IsNullOrWhiteSpace(token) || handle.ExpiresAtUnix < minimumExpiry)
        {
            throw Failure(
                "llm_credential_lifetime_insufficient",
                "NyxID did not issue a usable credential for the full managed execution window.");
        }

        return new ManagedCodexCredential(token, handle.ExpiresAtUnix);
    }

    private static ManagedCodexCredentialException Failure(
        string code,
        string message,
        Exception? innerException = null) =>
        new(
            new CodexExecutionFailure(
                CodexExecutionFailureKind.AdmissionDenied,
                code,
                message),
            innerException);

    private static string Require(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw Failure("nyxid_identity_missing", "A complete authenticated NyxID identity is required.")
            : value.Trim();
}
