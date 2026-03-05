using System.Text.RegularExpressions;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Architecture;

public class ScriptInheritanceGuardTests
{
    [Theory]
    [InlineData("src/Aevatar.Scripting.Core/ScriptDefinitionGAgent.cs", "ScriptDefinitionGAgent")]
    [InlineData("src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs", "ScriptRuntimeGAgent")]
    public void ScriptAgents_ShouldInheritDirectlyFromGAgentBase(
        string relativePath,
        string agentName)
    {
        var repoRoot = FindRepoRoot();
        var sourcePath = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(sourcePath).Should().BeTrue("script inheritance guard needs real source file: {0}", sourcePath);

        var source = File.ReadAllText(sourcePath);
        source.Should().MatchRegex($@"class\s+{Regex.Escape(agentName)}\s*:\s*GAgentBase<");
        source.Should().NotMatchRegex(
            $@"class\s+{Regex.Escape(agentName)}\s*:\s*(RoleGAgent|AIGAgentBase<)");
    }

    [Fact]
    public void GuardScript_ShouldTargetCurrentScriptAgents()
    {
        var repoRoot = FindRepoRoot();
        var guardPath = Path.Combine(repoRoot, "tools", "ci", "script_inheritance_guard.sh");
        File.Exists(guardPath).Should().BeTrue("guard script must exist at {0}", guardPath);

        var source = File.ReadAllText(guardPath);
        source.Should().Contain("ScriptDefinitionGAgent");
        source.Should().Contain("ScriptRuntimeGAgent");
        source.Should().NotContain("ScriptHostGAgent");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var marker = Path.Combine(dir.FullName, "aevatar.slnx");
            if (File.Exists(marker))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Cannot locate repository root from test base directory.");
    }
}
