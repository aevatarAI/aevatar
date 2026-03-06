using Aevatar.Scripting.Abstractions.Definitions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Contract;

public class ScriptDefinitionContractsTests
{
    [Fact]
    public async Task HandleRequestedEventAsync_ShouldReturnDomainEvents()
    {
        var definition = new FakeScriptDefinition();
        var result = await definition.HandleRequestedEventAsync(
            new ScriptRequestedEventEnvelope(
                "claim.submitted",
                Any.Pack(new Struct()),
                "evt-1",
                "corr-1",
                "cause-1"),
            new ScriptExecutionContext("actor-1", "script-1", "r1"),
            CancellationToken.None);

        result.DomainEvents.Should().HaveCount(1);
    }

    [Fact]
    public async Task ApplyAndReduce_ShouldReturnTypedSnapshots()
    {
        var definition = new FakeScriptDefinition();

        var nextState = await definition.ApplyDomainEventAsync(
            new Dictionary<string, Any>(StringComparer.Ordinal)
            {
                ["counter"] = Any.Pack(new Int32Value { Value = 0 }),
            },
            new ScriptDomainEventEnvelope(
                "ClaimApprovedEvent",
                Any.Pack(new Struct()),
                "evt-2",
                "corr-1",
                "cause-1"),
            CancellationToken.None);
        var nextReadModel = await definition.ReduceReadModelAsync(
            new Dictionary<string, Any>(StringComparer.Ordinal)
            {
                ["summary"] = Any.Pack(new StringValue { Value = "init" }),
            },
            new ScriptDomainEventEnvelope(
                "ClaimApprovedEvent",
                Any.Pack(new Struct()),
                "evt-2",
                "corr-1",
                "cause-1"),
            CancellationToken.None);

        nextState.Should().NotBeNull();
        nextReadModel.Should().NotBeNull();
        nextState!.Should().ContainKey("state");
        nextState["state"].Unpack<StringValue>().Value.Should().Be("ClaimApprovedEvent");
        nextReadModel!.Should().ContainKey("view");
        nextReadModel["view"].Unpack<StringValue>().Value.Should().Be("projection:ClaimApprovedEvent");
    }

    [Fact]
    public void ScriptExecutionContext_ShouldContain_RuntimeCorrelationMetadata()
    {
        var context = new ScriptExecutionContext(
            ActorId: "runtime-1",
            ScriptId: "script-1",
            Revision: "r1",
            RunId: "run-1",
            CorrelationId: "corr-1",
            DefinitionActorId: "definition-1",
            InputPayload: Any.Pack(new Struct { Fields = { ["amount"] = Google.Protobuf.WellKnownTypes.Value.ForNumber(100) } }));

        context.ActorId.Should().Be("runtime-1");
        context.RunId.Should().Be("run-1");
        context.CorrelationId.Should().Be("corr-1");
        context.DefinitionActorId.Should().Be("definition-1");
        context.InputPayload.Should().NotBeNull();
        context.InputPayload!.Is(Struct.Descriptor).Should().BeTrue();
        context.InputPayload.Unpack<Struct>().Fields["amount"].NumberValue.Should().Be(100);
    }

    private sealed class FakeScriptDefinition : IScriptPackageDefinition
    {
        public string ScriptId => "script-1";
        public string Revision => "r1";
        public ScriptContractManifest ContractManifest { get; } =
            new("fake-input", ["FakeEvent"], "fake-state", "fake-readmodel");

        public Task<ScriptHandlerResult> HandleRequestedEventAsync(
            ScriptRequestedEventEnvelope requestedEvent,
            ScriptExecutionContext context,
            CancellationToken ct)
        {
            _ = requestedEvent;
            _ = context;
            _ = ct;
            return Task.FromResult(
                new ScriptHandlerResult([new StringValue { Value = "evt" }]));
        }

        public ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
            IReadOnlyDictionary<string, Any> currentState,
            ScriptDomainEventEnvelope domainEvent,
            CancellationToken ct)
        {
            _ = currentState;
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(
                new Dictionary<string, Any>(StringComparer.Ordinal)
                {
                    ["state"] = Any.Pack(new StringValue { Value = domainEvent.EventType }),
                });
        }

        public ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
            IReadOnlyDictionary<string, Any> currentReadModel,
            ScriptDomainEventEnvelope domainEvent,
            CancellationToken ct)
        {
            _ = currentReadModel;
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(
                new Dictionary<string, Any>(StringComparer.Ordinal)
                {
                    ["view"] = Any.Pack(new StringValue { Value = "projection:" + domainEvent.EventType }),
                });
        }
    }
}
