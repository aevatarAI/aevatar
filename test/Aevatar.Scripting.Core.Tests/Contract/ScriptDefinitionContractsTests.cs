using Aevatar.Scripting.Abstractions.Definitions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Contract;

public class ScriptDefinitionContractsTests
{
    [Fact]
    public async Task DecideAsync_ShouldReturnDomainEvents()
    {
        var definition = new FakeScriptDefinition();
        var result = await definition.DecideAsync(
            new ScriptExecutionContext("actor-1", "script-1", "r1"),
            CancellationToken.None);

        result.DomainEvents.Should().HaveCount(1);
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

    private sealed class FakeScriptDefinition : IScriptAgentDefinition
    {
        public string ScriptId => "script-1";
        public string Revision => "r1";
        public ScriptContractManifest ContractManifest { get; } =
            new("fake-input", ["FakeEvent"], "fake-state", "fake-readmodel");

        public Task<ScriptDecisionResult> DecideAsync(
            ScriptExecutionContext context,
            CancellationToken ct)
        {
            _ = context;
            _ = ct;
            return Task.FromResult(
                new ScriptDecisionResult([new StringValue { Value = "evt" }]));
        }
    }
}
