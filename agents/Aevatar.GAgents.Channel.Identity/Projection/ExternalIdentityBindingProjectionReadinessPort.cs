using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Polls the binding projection until the document for
/// <paramref name="readmodelId"/> reports <c>LastEventId == eventId</c> or the
/// timeout elapses. Used by the OAuth callback handler on the write-side
/// completion path so the next inbound message after binding is guaranteed
/// to see the binding via <see cref="IExternalIdentityBindingQueryPort"/>.
/// See ADR-0017 §Projection Readiness.
/// </summary>
public sealed class ExternalIdentityBindingProjectionReadinessPort : IProjectionReadinessPort
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly IProjectionDocumentReader<ExternalIdentityBindingDocument, string> _reader;
    private readonly TimeProvider _timeProvider;

    public ExternalIdentityBindingProjectionReadinessPort(
        IProjectionDocumentReader<ExternalIdentityBindingDocument, string> reader,
        TimeProvider? timeProvider = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task WaitForEventAsync(
        string eventId,
        string readmodelId,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(readmodelId);

        var deadline = _timeProvider.GetUtcNow() + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var document = await _reader.GetAsync(readmodelId, ct).ConfigureAwait(false);
            if (document is not null && string.Equals(document.LastEventId, eventId, StringComparison.Ordinal))
                return;

            if (_timeProvider.GetUtcNow() >= deadline)
                throw new TimeoutException(
                    $"Projection readmodel {readmodelId} did not catch up to event {eventId} within {timeout.TotalSeconds:F1}s.");

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }
}
