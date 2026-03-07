using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

internal sealed class RuntimeEnvelopeCompatibilityInjectionHook
{
    private readonly Func<string> _actorIdAccessor;
    private readonly CompatibilityFailureInjectionPolicy _policy;
    private readonly ILogger _logger;
    private readonly RuntimeEnvelopeRetryCoordinator _retryCoordinator;

    public RuntimeEnvelopeCompatibilityInjectionHook(
        Func<string> actorIdAccessor,
        CompatibilityFailureInjectionPolicy policy,
        ILogger logger,
        RuntimeEnvelopeRetryCoordinator retryCoordinator)
    {
        _actorIdAccessor = actorIdAccessor ?? throw new ArgumentNullException(nameof(actorIdAccessor));
        _policy = policy;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _retryCoordinator = retryCoordinator ?? throw new ArgumentNullException(nameof(retryCoordinator));
    }

    public async Task<bool> TryHandleAsync(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!_policy.ShouldInject(envelope.Payload?.TypeUrl))
            return false;

        _logger.LogWarning(
            "Injected compatibility failure for actor {ActorId}, event type '{EventTypeUrl}'.",
            _actorIdAccessor(),
            envelope.Payload?.TypeUrl ?? "(none)");

        var compatibilityException =
            new InvalidOperationException("Injected compatibility failure for mixed-version rollout testing.");
        if (await _retryCoordinator.TryScheduleRetryAsync(envelope, compatibilityException))
            return true;

        _logger.LogError(
            compatibilityException,
            "Runtime envelope handling failed after compatibility retry exhausted (or retry disabled) for actor {ActorId}, envelope {EnvelopeId}, event type '{EventTypeUrl}'.",
            _actorIdAccessor(),
            envelope.Id,
            envelope.Payload?.TypeUrl ?? "(none)");
        return true;
    }
}
