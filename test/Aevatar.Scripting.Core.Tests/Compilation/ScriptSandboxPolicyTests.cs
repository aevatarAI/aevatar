using Aevatar.Scripting.Core.Compilation;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Compilation;

public class ScriptSandboxPolicyTests
{
    [Theory]
    [InlineData("Task.Run(() => 1);")]
    [InlineData("new Timer(_ => {}, null, 0, 1000);")]
    [InlineData("new Thread(() => {}).Start();")]
    [InlineData("lock(obj){}")]
    public void Validate_ShouldRejectForbiddenApis(string source)
    {
        var policy = new ScriptSandboxPolicy();

        var result = policy.Validate(source);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().NotBeEmpty();
    }
}
