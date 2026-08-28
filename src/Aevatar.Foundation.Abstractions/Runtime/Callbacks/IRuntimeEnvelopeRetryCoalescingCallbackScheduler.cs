namespace Aevatar.Foundation.Abstractions.Runtime.Callbacks;

/// <summary>
/// Runtime-owned completion contract for retry-until-resolved callbacks. A successful newer
/// authoritative observation completes and removes an older pending callback for the same key.
/// </summary>
public interface IRuntimeEnvelopeRetryCoalescingCallbackScheduler
{
    Task CompleteRuntimeEnvelopeRetryAsync(
        string actorId,
        RuntimeEnvelopeRetryCoalescingCursor cursor,
        CancellationToken ct = default);
}

public static class RuntimeEnvelopeRetryCoalescingCallbackSlot
{
    private const string CallbackIdPrefix =
        "runtime-envelope-retry-until-resolved-coalesced";

    public static string BuildCallbackId(string coalescingKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coalescingKey);
        return RuntimeCallbackKeyComposer.BuildCallbackId(
            CallbackIdPrefix,
            coalescingKey);
    }
}
