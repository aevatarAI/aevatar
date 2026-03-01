using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Contracts;

public class ScriptProtoContractsTests
{
    [Fact]
    public void ScriptDefinitionState_ShouldContainSourceAndRevision()
    {
        var state = new ScriptDefinitionState
        {
            ScriptId = "script-1",
            Revision = "rev-1",
            SourceText = "return 1;",
            SourceHash = "hash-1",
        };

        state.ScriptId.Should().Be("script-1");
        state.Revision.Should().Be("rev-1");
        state.SourceText.Should().Be("return 1;");
        state.SourceHash.Should().Be("hash-1");
    }

    [Fact]
    public void ScriptRuntimeState_ShouldContainRunFacts()
    {
        var state = new ScriptRuntimeState
        {
            DefinitionActorId = "definition-1",
            Revision = "rev-1",
            LastRunId = "run-1",
            StatePayloadJson = "{\"ok\":true}",
            ReadModelPayloadJson = "{\"status\":\"ok\"}",
        };

        state.DefinitionActorId.Should().Be("definition-1");
        state.Revision.Should().Be("rev-1");
        state.LastRunId.Should().Be("run-1");
        state.StatePayloadJson.Should().Be("{\"ok\":true}");
        state.ReadModelPayloadJson.Should().Be("{\"status\":\"ok\"}");
    }
}
