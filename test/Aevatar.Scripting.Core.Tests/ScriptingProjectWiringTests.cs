using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests;

public class ScriptingProjectWiringTests
{
    [Fact]
    public void ScriptingAssemblies_ShouldBeLoadable()
    {
        typeof(Aevatar.Scripting.Core.ScriptHostGAgent).Assembly.Should().NotBeNull();
    }
}
