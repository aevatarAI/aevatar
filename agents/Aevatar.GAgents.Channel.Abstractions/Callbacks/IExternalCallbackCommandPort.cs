namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>Write-side admission for a durable, operation-scoped external continuation.</summary>
public interface IExternalCallbackCommandPort
{
    /// <summary>Returns inbox admission only. The actor publishes a link after its registration and exact request commit.</summary>
    Task<ExternalCallbackRegistration> AdmitAsync(ExternalCallbackRegistration registration, CancellationToken ct = default);

    /// <summary>Accepts an already authenticated OAuth return for serialized processing.</summary>
    Task SubmitOAuthAsync(OAuthContinuationSubmission submission, CancellationToken ct = default);

    /// <summary>Accepts an untrusted browser hint; authoritative verification still occurs in the actor.</summary>
    Task HintAsync(string? callbackId, string externalRequestId, CancellationToken ct = default);
}

/// <summary>External creation boundary invoked only after the operation actor commits its registration.</summary>
public interface IConnectLinkCreationPort
{
    /// <summary>Creates under the saved original user; returns a typed exact request and URL.</summary>
    Task<ConnectLinkCreationResult> CreateAsync(ExternalCallbackRegistration registration, CancellationToken ct = default);
}

/// <summary>OAuth protocol and binding dispatch adapter used by the operation authority.</summary>
public interface IOAuthContinuationExecutionPort
{
    /// <summary>Exchanges a code once and returns token-free binding references.</summary>
    Task<OAuthBindingPreparation> ExchangeAsync(OAuthContinuationSubmission submission, CancellationToken ct = default);

    /// <summary>Verifies the saved preparation without exchanging the one-use code again.</summary>
    Task<OAuthBindingPreparation> ValidateAsync(OAuthBindingPreparation preparation, CancellationToken ct = default);

    /// <summary>Dispatches to the binding authority; completion arrives as a separate business message.</summary>
    Task DispatchBindingAsync(OAuthBindingPreparation preparation, string callbackId, string operationActorId, CancellationToken ct = default);

    /// <summary>Asks the binding authority to retire an unadopted preparation; acknowledgment is a typed outcome.</summary>
    Task AbandonAsync(OAuthBindingPreparation preparation, string callbackId, string operationActorId, CancellationToken ct = default);
}

/// <summary>Exact Connect Link and connected-instance verification under the original user.</summary>
public interface IConnectLinkVerificationPort
{
    /// <summary>Returns typed evidence; pending includes temporary provider unavailability.</summary>
    Task<ConnectLinkVerificationResult> VerifyAsync(ExternalCallbackRegistration registration, CancellationToken ct = default);
}
