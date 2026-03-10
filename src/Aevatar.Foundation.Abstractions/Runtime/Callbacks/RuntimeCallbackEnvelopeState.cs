using System.Globalization;

namespace Aevatar.Foundation.Abstractions.Runtime.Callbacks;

public readonly record struct RuntimeCallbackEnvelopeState(
    string CallbackId,
    long Generation,
    long FireIndex,
    long FiredAtUnixTimeMs);

public static class RuntimeCallbackEnvelopeStateReader
{
    public static bool TryRead(
        EventEnvelope envelope,
        out RuntimeCallbackEnvelopeState state)
    {
        state = default;
        if (envelope == null)
            return false;

        var callback = envelope.Runtime?.Callback;
        if (callback == null || string.IsNullOrWhiteSpace(callback.CallbackId))
        {
            return false;
        }

        state = new RuntimeCallbackEnvelopeState(
            callback.CallbackId,
            callback.Generation,
            callback.FireIndex,
            callback.FiredAtUnixTimeMs);
        return true;
    }

    public static bool MatchesLease(
        EventEnvelope envelope,
        RuntimeCallbackLease lease)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(lease);

        return TryRead(envelope, out var state) &&
               string.Equals(state.CallbackId, lease.CallbackId, StringComparison.Ordinal) &&
               state.Generation == lease.Generation;
    }
}

public static class RuntimeCallbackKeyComposer
{
    public static string BuildCallbackId(string prefix, params string[] segments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        return string.Concat(prefix, ':', string.Join(':', segments.Select(EncodeSegment)));
    }

    public static string BuildKey(char separator, params string[] segments) =>
        string.Join(separator, segments.Select(EncodeSegment));

    public static string EncodeSegment(string value) =>
        Uri.EscapeDataString(value ?? string.Empty);
}
