using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests;

public class ScriptingProjectWiringTests
{
    [Fact]
    public void ScriptingAssemblies_ShouldBeLoadable()
    {
        typeof(Aevatar.Scripting.Core.ScriptDefinitionGAgent).Assembly.Should().NotBeNull();
        typeof(Aevatar.Scripting.Core.ScriptRuntimeGAgent).Assembly.Should().NotBeNull();
    }
}
