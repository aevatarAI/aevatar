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

    private sealed class FakeScriptDefinition : IScriptAgentDefinition
    {
        public string ScriptId => "script-1";
        public string Revision => "r1";

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
