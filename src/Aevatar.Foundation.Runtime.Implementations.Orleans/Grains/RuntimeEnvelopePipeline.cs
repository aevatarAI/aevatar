namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

internal sealed class RuntimeEnvelopePipeline
{
    private readonly RuntimeEnvelopeCompatibilityInjectionHook _compatibilityHook;
    private readonly RuntimeEnvelopeDedupGuard _dedupGuard;
    private readonly RuntimeEnvelopeForwardingGuard _forwardingGuard;

    public RuntimeEnvelopePipeline(
        RuntimeEnvelopeCompatibilityInjectionHook compatibilityHook,
        RuntimeEnvelopeDedupGuard dedupGuard,
        RuntimeEnvelopeForwardingGuard forwardingGuard)
    {
        _compatibilityHook = compatibilityHook ?? throw new ArgumentNullException(nameof(compatibilityHook));
        _dedupGuard = dedupGuard ?? throw new ArgumentNullException(nameof(dedupGuard));
        _forwardingGuard = forwardingGuard ?? throw new ArgumentNullException(nameof(forwardingGuard));
    }

    public async Task<bool> ShouldDeliverAsync(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (await _compatibilityHook.TryHandleAsync(envelope))
            return false;

        if (await _dedupGuard.ShouldDropAsync(envelope))
            return false;

        return !_forwardingGuard.ShouldDrop(envelope);
    }
}
