using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Architecture;

public class ScriptInheritanceGuardTests
{
    [Fact]
    public void Pattern_ShouldDetectForbiddenInheritance()
    {
        const string line = "public class ScriptHostGAgent : AIGAgentBase<MyState>";
        line.Should().MatchRegex(@"ScriptHostGAgent\s*:\s*(RoleGAgent|AIGAgentBase<)");
    }
}
