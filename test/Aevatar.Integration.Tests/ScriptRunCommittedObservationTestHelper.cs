using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Integration.Tests;

internal static class ScriptRunCommittedObservationTestHelper
{
    public static async Task<ScriptDomainFactCommitted> WaitForCommittedAsync(
        IEventSink<EventEnvelope> sink,
        string runId,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        await foreach (var envelope in sink.ReadAllAsync(timeout.Token))
        {
            if (envelope.Payload?.Is(ScriptDomainFactCommitted.Descriptor) != true)
                continue;

            var fact = envelope.Payload.Unpack<ScriptDomainFactCommitted>();
            if (string.Equals(fact.RunId, runId, StringComparison.Ordinal))
                return fact;
        }

        throw new InvalidOperationException($"Timed out waiting for committed script fact. run_id={runId}");
    }
}
