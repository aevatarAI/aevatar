using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Contracts;

public class ScriptHostProtoContractsTests
{
    [Fact]
    public void ScriptHostState_ShouldContainRevisionAndPayload()
    {
        var state = new ScriptHostState
        {
            ScriptId = "script-1",
            Revision = "r1",
            StatePayloadJson = "{}",
        };

        state.ScriptId.Should().Be("script-1");
        state.Revision.Should().Be("r1");
        state.StatePayloadJson.Should().Be("{}");
    }
}
