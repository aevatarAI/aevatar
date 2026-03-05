using System.Globalization;

namespace Aevatar.Foundation.Abstractions.Runtime.Callbacks;

public readonly record struct RuntimeCallbackEnvelopeMetadata(
    string CallbackId,
    long Generation,
    long FireIndex,
    long FiredAtUnixTimeMs);

public static class RuntimeCallbackEnvelopeMetadataReader
{
    public static bool TryRead(
        EventEnvelope envelope,
        out RuntimeCallbackEnvelopeMetadata metadata)
    {
        metadata = default;
        if (envelope == null)
            return false;

        if (!envelope.Metadata.TryGetValue(RuntimeCallbackMetadataKeys.CallbackId, out var callbackId) ||
            string.IsNullOrWhiteSpace(callbackId) ||
            !TryReadInt64(envelope, RuntimeCallbackMetadataKeys.CallbackGeneration, out var generation) ||
            !TryReadInt64(envelope, RuntimeCallbackMetadataKeys.CallbackFireIndex, out var fireIndex) ||
            !TryReadInt64(envelope, RuntimeCallbackMetadataKeys.CallbackFiredAtUnixTimeMs, out var firedAtUnixTimeMs))
        {
            return false;
        }

        metadata = new RuntimeCallbackEnvelopeMetadata(
            callbackId,
            generation,
            fireIndex,
            firedAtUnixTimeMs);
        return true;
    }

    public static bool MatchesLease(
        EventEnvelope envelope,
        RuntimeCallbackLease lease)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(lease);

        return TryRead(envelope, out var metadata) &&
               string.Equals(metadata.CallbackId, lease.CallbackId, StringComparison.Ordinal) &&
               metadata.Generation == lease.Generation;
    }

    private static bool TryReadInt64(
        EventEnvelope envelope,
        string key,
        out long value)
    {
        value = 0;
        return envelope.Metadata.TryGetValue(key, out var raw) &&
               long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
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
