using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Integration.Tests;

internal static class ScriptRunCommittedObservationTestHelper
{
    public static async Task<ScriptDomainFactCommitted> WaitForCommittedAsync(
        IEventSink<EventEnvelope> sink,
        string runId,
        CancellationToken ct,
        TimeSpan? timeoutOverride = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutOverride ?? TimeSpan.FromSeconds(10));

        try
        {
            await foreach (var envelope in sink.ReadAllAsync(timeout.Token))
            {
                if (!TryUnpackCommittedFact(envelope, out var fact))
                    continue;

                if (string.Equals(fact.RunId, runId, StringComparison.Ordinal))
                    return fact;
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Timed out waiting for committed script fact. run_id={runId}");
        }

        throw new InvalidOperationException($"Timed out waiting for committed script fact. run_id={runId}");
    }

    public static async Task<ScriptDomainFactCommitted> WaitForAnyCommittedAsync(
        IEventSink<EventEnvelope> sink,
        CancellationToken ct,
        TimeSpan? timeoutOverride = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutOverride ?? TimeSpan.FromSeconds(10));

        try
        {
            await foreach (var envelope in sink.ReadAllAsync(timeout.Token))
            {
                if (TryUnpackCommittedFact(envelope, out var fact))
                    return fact;
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Timed out waiting for committed script fact.");
        }

        throw new InvalidOperationException("Timed out waiting for committed script fact.");
    }

    internal static bool TryUnpackCommittedFact(EventEnvelope envelope, out ScriptDomainFactCommitted fact)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        fact = default!;
        if (envelope.Payload?.Is(ScriptDomainFactCommitted.Descriptor) == true)
        {
            fact = envelope.Payload.Unpack<ScriptDomainFactCommitted>();
            return true;
        }

        if (envelope.Payload?.Is(CommittedStateEventPublished.Descriptor) != true)
            return false;

        var published = envelope.Payload.Unpack<CommittedStateEventPublished>();
        if (published.StateEvent?.EventData?.Is(ScriptDomainFactCommitted.Descriptor) != true)
            return false;

        fact = published.StateEvent.EventData.Unpack<ScriptDomainFactCommitted>();
        return true;
    }
}
