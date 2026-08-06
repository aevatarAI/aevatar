using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Google.Protobuf;

namespace Aevatar.AI.Core.Chat;

public enum ChatToolRecoveryPayloadKind
{
    Arguments = 1,
    Result = 2,
}

public sealed record ChatToolRecoveryResultPayload(
    string ResultJson,
    bool Success,
    string SafeErrorCode,
    AgentToolReceipt? Receipt);

public sealed record StoredChatToolRecoveryResult(
    SecretReference Reference,
    ChatToolRecoveryResultPayload Payload);

public sealed class ChatToolRecoveryPayloadMaterialException : InvalidOperationException
{
    public ChatToolRecoveryPayloadMaterialException(string message)
        : base(message)
    {
    }

    public ChatToolRecoveryPayloadMaterialException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public interface IChatToolRecoveryPayloadStore
{
    Task<SecretReference> StoreAsync(
        string actorId,
        string sessionId,
        string operationId,
        ChatToolRecoveryPayloadKind kind,
        string payload,
        DateTimeOffset expiresAt,
        CancellationToken ct = default);

    Task<string> ResolveAsync(
        SecretReference reference,
        string actorId,
        string sessionId,
        string operationId,
        ChatToolRecoveryPayloadKind kind,
        DateTimeOffset now,
        CancellationToken ct = default);

    Task<StoredChatToolRecoveryResult> StoreResultAsync(
        string actorId,
        string sessionId,
        string operationId,
        ChatToolRecoveryResultPayload payload,
        DateTimeOffset expiresAt,
        CancellationToken ct = default);

    Task<StoredChatToolRecoveryResult?> TryResolveStoredResultAsync(
        string actorId,
        string sessionId,
        string operationId,
        DateTimeOffset now,
        CancellationToken ct = default);

    Task<ChatToolRecoveryResultPayload> ResolveResultAsync(
        SecretReference reference,
        string actorId,
        string sessionId,
        string operationId,
        DateTimeOffset now,
        CancellationToken ct = default);
}

public sealed class SecretVaultChatToolRecoveryPayloadStore : IChatToolRecoveryPayloadStore
{
    private const string ArgumentsPurpose = "aevatar.chat.tool-recovery.arguments.v1";
    private const string ResultPurpose = "aevatar.chat.tool-recovery.result.v1";
    private readonly ISecretVault _vault;

    public SecretVaultChatToolRecoveryPayloadStore(ISecretVault vault)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
    }

    public async Task<SecretReference> StoreAsync(
        string actorId,
        string sessionId,
        string operationId,
        ChatToolRecoveryPayloadKind kind,
        string payload,
        DateTimeOffset expiresAt,
        CancellationToken ct = default)
    {
        ValidateIdentity(actorId, sessionId, operationId);
        var purpose = ResolvePurpose(kind);
        var subject = BuildSubject(sessionId, operationId, kind);
        var requestedReference = BuildReference(actorId, sessionId, operationId, kind);
        var stored = await _vault.PutAsync(
            new StoreSecretRequest(
                purpose,
                actorId,
                subject,
                payload,
                "Persist replay-safe chat tool recovery payload.",
                expiresAt,
                requestedReference),
            ct).ConfigureAwait(false);
        ValidateReference(
            stored.Reference,
            actorId,
            purpose,
            expiresAt,
            expectedReference: requestedReference);
        return stored.Reference.Clone();
    }

    public async Task<string> ResolveAsync(
        SecretReference reference,
        string actorId,
        string sessionId,
        string operationId,
        ChatToolRecoveryPayloadKind kind,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ValidateIdentity(actorId, sessionId, operationId);
        var purpose = ResolvePurpose(kind);
        ValidateReference(reference, actorId, purpose, now, requireFutureExpiry: true);
        var resolved = await _vault.ResolveAsync(
            new ResolveSecretRequest(
                reference.Ref,
                purpose,
                actorId,
                BuildSubject(sessionId, operationId, kind),
                "Resolve replay-safe chat tool recovery payload."),
            ct).ConfigureAwait(false);
        if (!resolved.Resolved ||
            resolved.Reference is null ||
            !ReferencesEqual(reference, resolved.Reference))
        {
            throw new ChatToolRecoveryPayloadMaterialException(
                $"The chat tool recovery payload reference could not be resolved ({resolved.FailureReason}).");
        }

        return resolved.Secret!;
    }

    public async Task<StoredChatToolRecoveryResult> StoreResultAsync(
        string actorId,
        string sessionId,
        string operationId,
        ChatToolRecoveryResultPayload payload,
        DateTimeOffset expiresAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var storedPayload = new RoleChatToolStoredResultPayload
        {
            ResultJson = payload.ResultJson,
            Success = payload.Success,
            SafeErrorCode = payload.SafeErrorCode,
            Receipt = payload.Receipt?.Clone(),
        };
        var reference = await StoreAsync(
            actorId,
            sessionId,
            operationId,
            ChatToolRecoveryPayloadKind.Result,
            Convert.ToBase64String(storedPayload.ToByteArray()),
            expiresAt,
            ct).ConfigureAwait(false);
        return new StoredChatToolRecoveryResult(reference, payload);
    }

    public async Task<StoredChatToolRecoveryResult?> TryResolveStoredResultAsync(
        string actorId,
        string sessionId,
        string operationId,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ValidateIdentity(actorId, sessionId, operationId);
        var purpose = ResolvePurpose(ChatToolRecoveryPayloadKind.Result);
        var reference = BuildReference(
            actorId,
            sessionId,
            operationId,
            ChatToolRecoveryPayloadKind.Result);
        var resolved = await _vault.ResolveAsync(
            new ResolveSecretRequest(
                reference,
                purpose,
                actorId,
                BuildSubject(sessionId, operationId, ChatToolRecoveryPayloadKind.Result),
                "Adopt a previously stored replay-safe chat tool result."),
            ct).ConfigureAwait(false);
        if (!resolved.Resolved)
        {
            if (resolved.FailureReason == SecretResolutionFailureReason.NotFound)
                return null;
            throw new ChatToolRecoveryPayloadMaterialException(
                $"The deterministic chat tool result cannot be resolved ({resolved.FailureReason}).");
        }

        ValidateResolvedReference(resolved.Reference, reference, actorId, purpose, now);
        return new StoredChatToolRecoveryResult(
            resolved.Reference!.Clone(),
            DeserializeResult(resolved.Secret!));
    }

    public async Task<ChatToolRecoveryResultPayload> ResolveResultAsync(
        SecretReference reference,
        string actorId,
        string sessionId,
        string operationId,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var payload = await ResolveAsync(
            reference,
            actorId,
            sessionId,
            operationId,
            ChatToolRecoveryPayloadKind.Result,
            now,
            ct).ConfigureAwait(false);
        return DeserializeResult(payload);
    }

    private static void ValidateIdentity(string actorId, string sessionId, string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
    }

    private static string ResolvePurpose(ChatToolRecoveryPayloadKind kind) => kind switch
    {
        ChatToolRecoveryPayloadKind.Arguments => ArgumentsPurpose,
        ChatToolRecoveryPayloadKind.Result => ResultPurpose,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static string BuildSubject(
        string sessionId,
        string operationId,
        ChatToolRecoveryPayloadKind kind) =>
        $"session:{sessionId}:operation:{operationId}:payload:{(int)kind}";

    private static string BuildReference(
        string actorId,
        string sessionId,
        string operationId,
        ChatToolRecoveryPayloadKind kind)
    {
        var material = $"{actorId}\n{sessionId}\n{operationId}\n{(int)kind}";
        return "chat-tool-recovery:v1:" +
               Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static void ValidateReference(
        SecretReference? reference,
        string actorId,
        string purpose,
        DateTimeOffset boundary,
        bool requireFutureExpiry = false,
        string? expectedReference = null)
    {
        if (reference is null ||
            string.IsNullOrWhiteSpace(reference.Ref) ||
            expectedReference is not null &&
            !string.Equals(reference.Ref, expectedReference, StringComparison.Ordinal) ||
            !string.Equals(reference.Purpose, purpose, StringComparison.Ordinal) ||
            !string.Equals(reference.OwnerScopeKey, actorId, StringComparison.Ordinal) ||
            reference.Version <= 0 ||
            string.IsNullOrWhiteSpace(reference.Fingerprint) ||
            reference.ExpiresAtUnixMs <= 0 ||
            (requireFutureExpiry && reference.ExpiresAtUnixMs <= boundary.ToUnixTimeMilliseconds()) ||
            (!requireFutureExpiry && reference.ExpiresAtUnixMs != boundary.ToUnixTimeMilliseconds()))
        {
            throw new ChatToolRecoveryPayloadMaterialException(
                "The chat tool recovery payload reference is invalid.");
        }
    }

    private static void ValidateResolvedReference(
        SecretReference? reference,
        string expectedReference,
        string actorId,
        string purpose,
        DateTimeOffset now)
    {
        ValidateReference(
            reference,
            actorId,
            purpose,
            now,
            requireFutureExpiry: true,
            expectedReference: expectedReference);
    }

    private static ChatToolRecoveryResultPayload DeserializeResult(string payload)
    {
        try
        {
            var stored = RoleChatToolStoredResultPayload.Parser.ParseFrom(
                Convert.FromBase64String(payload));
            return new ChatToolRecoveryResultPayload(
                stored.ResultJson,
                stored.Success,
                stored.SafeErrorCode,
                stored.Receipt?.Clone());
        }
        catch (Exception ex) when (ex is FormatException or InvalidProtocolBufferException)
        {
            throw new ChatToolRecoveryPayloadMaterialException(
                "The stored chat tool result payload is corrupt.",
                ex);
        }
    }

    private static bool ReferencesEqual(SecretReference left, SecretReference right) =>
        string.Equals(left.Ref, right.Ref, StringComparison.Ordinal) &&
        string.Equals(left.Purpose, right.Purpose, StringComparison.Ordinal) &&
        string.Equals(left.Fingerprint, right.Fingerprint, StringComparison.Ordinal) &&
        left.Version == right.Version &&
        string.Equals(left.OwnerScopeKey, right.OwnerScopeKey, StringComparison.Ordinal) &&
        left.CreatedAtUnixMs == right.CreatedAtUnixMs &&
        left.ExpiresAtUnixMs == right.ExpiresAtUnixMs;
}
