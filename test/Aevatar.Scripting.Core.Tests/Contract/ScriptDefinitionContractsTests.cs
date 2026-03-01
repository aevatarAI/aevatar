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
            new ScriptRequestedEventEnvelope("claim.submitted", "{}", "evt-1", "corr-1", "cause-1"),
            new ScriptExecutionContext("actor-1", "script-1", "r1"),
            CancellationToken.None);

        result.DomainEvents.Should().HaveCount(1);
    }

    [Fact]
    public async Task ApplyAndReduce_ShouldReturnJsonSnapshots()
    {
        var definition = new FakeScriptDefinition();

        var nextState = await definition.ApplyDomainEventAsync(
            "{}",
            new ScriptDomainEventEnvelope("ClaimApprovedEvent", "{}", "evt-2", "corr-1", "cause-1"),
            CancellationToken.None);
        var nextReadModel = await definition.ReduceReadModelAsync(
            "{}",
            new ScriptDomainEventEnvelope("ClaimApprovedEvent", "{}", "evt-2", "corr-1", "cause-1"),
            CancellationToken.None);

        nextState.Should().Be("{\"state\":\"ClaimApprovedEvent\"}");
        nextReadModel.Should().Be("{\"projection\":\"ClaimApprovedEvent\"}");
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
            InputJson: "{\"amount\": 100}");

        context.ActorId.Should().Be("runtime-1");
        context.RunId.Should().Be("run-1");
        context.CorrelationId.Should().Be("corr-1");
        context.DefinitionActorId.Should().Be("definition-1");
        context.InputJson.Should().Be("{\"amount\": 100}");
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

        public ValueTask<string> ApplyDomainEventAsync(
            string currentStateJson,
            ScriptDomainEventEnvelope domainEvent,
            CancellationToken ct)
        {
            _ = currentStateJson;
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult("{\"state\":\"" + domainEvent.EventType + "\"}");
        }

        public ValueTask<string> ReduceReadModelAsync(
            string currentReadModelJson,
            ScriptDomainEventEnvelope domainEvent,
            CancellationToken ct)
        {
            _ = currentReadModelJson;
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult("{\"projection\":\"" + domainEvent.EventType + "\"}");
        }
    }
}
