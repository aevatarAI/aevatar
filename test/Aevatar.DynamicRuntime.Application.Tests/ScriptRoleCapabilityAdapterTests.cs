using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Aevatar.DynamicRuntime.Core.Adapters;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace Aevatar.DynamicRuntime.Application.Tests;

public sealed class ScriptRoleCapabilityAdapterTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldInvokeEntrypoint()
    {
        var adapter = new ScriptRoleCapabilityAdapter(
            new FakeEntrypoint(),
            new ScriptRoleCapabilitySnapshot("svc.adapter", "v1", "FakeEntrypoint", DynamicServiceMode.Hybrid, "cap:v1"));

        var result = await adapter.ExecuteAsync(CreateJsonEnvelope("""{"text":"hello"}"""));

        result.Should().Be("""adapter:{"text":"hello"}""");
    }

    private static EventEnvelope CreateJsonEnvelope(string value)
    {
        var payload = Any.Pack(new StringValue { Value = value });
        var correlationId = Guid.NewGuid().ToString("N");
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = payload,
            PublisherId = "dynamic-runtime.test",
            Direction = EventDirection.Self,
            CorrelationId = correlationId,
            Metadata =
            {
                ["type_url"] = payload.TypeUrl,
                ["trace_id"] = Guid.NewGuid().ToString("N"),
                ["correlation_id"] = correlationId,
                ["causation_id"] = Guid.NewGuid().ToString("N"),
                ["dedup_key"] = $"{payload.TypeUrl}:{Guid.NewGuid():N}",
                ["occurred_at"] = DateTime.UtcNow.ToString("O"),
            },
        };
    }

    private sealed class FakeEntrypoint : IScriptRoleEntrypoint
    {
        public Task<ScriptRoleExecutionResult> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            var text = envelope.Payload.Is(StringValue.Descriptor)
                ? envelope.Payload.Unpack<StringValue>().Value
                : string.Empty;
            return Task.FromResult(new ScriptRoleExecutionResult($"adapter:{text}"));
        }
    }
}
