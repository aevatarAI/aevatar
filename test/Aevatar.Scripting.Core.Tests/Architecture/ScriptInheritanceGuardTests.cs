using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Architecture;

public class ScriptInheritanceGuardTests
{
    [Fact]
    public void Pattern_ShouldDetectForbiddenInheritance_ForDefinitionAgent()
    {
        const string line = "public class ScriptDefinitionGAgent : AIGAgentBase<MyState>";
        line.Should().MatchRegex(@"ScriptDefinitionGAgent\s*:\s*(RoleGAgent|AIGAgentBase<)");
    }

    [Fact]
    public void Pattern_ShouldDetectForbiddenInheritance_ForRuntimeAgent()
    {
        const string line = "public class ScriptRuntimeGAgent : RoleGAgent";
        line.Should().MatchRegex(@"ScriptRuntimeGAgent\s*:\s*(RoleGAgent|AIGAgentBase<)");
    }
}
