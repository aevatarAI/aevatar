using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Polls the binding projection until the document for the given external
/// subject reports the expected state (active binding with the expected id,
/// or post-revoke when <paramref name="expectedBindingId"/> is null) or the
/// timeout elapses. Used by the OAuth callback handler on the write-side
/// completion path so the next inbound message after binding is guaranteed
/// to see the binding via <see cref="IExternalIdentityBindingQueryPort"/>.
/// See ADR-0018 §Projection Readiness.
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

    public async Task WaitForBindingStateAsync(
        ExternalSubjectRef externalSubject,
        string? expectedBindingId,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        ExternalSubjectRefExtensions.EnsureValid(externalSubject);

        var actorId = externalSubject.ToActorId();
        var deadline = _timeProvider.GetUtcNow() + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var document = await _reader.GetAsync(actorId, ct).ConfigureAwait(false);
            if (Matches(document, expectedBindingId))
                return;

            if (_timeProvider.GetUtcNow() >= deadline)
                throw new TimeoutException(
                    expectedBindingId is null
                        ? $"Binding readmodel for {actorId} did not observe the revoke within {timeout.TotalSeconds:F1}s."
                        : $"Binding readmodel for {actorId} did not observe binding_id={expectedBindingId} within {timeout.TotalSeconds:F1}s.");

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    private static bool Matches(ExternalIdentityBindingDocument? document, string? expectedBindingId)
    {
        if (document is null)
            return false;
        if (expectedBindingId is null)
            return string.IsNullOrEmpty(document.BindingId);
        return string.Equals(document.BindingId, expectedBindingId, StringComparison.Ordinal);
    }
}
