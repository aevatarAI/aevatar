using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Infrastructure.Compilation;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Compilation;

public class ScriptSandboxPolicyTests
{
    [Theory]
    [InlineData("Task.Run(() => 1);")]
    [InlineData("new Timer(_ => {}, null, 0, 1000);")]
    [InlineData("new Thread(() => {}).Start();")]
    [InlineData("lock(obj){}")]
    [InlineData("File.ReadAllText(\"/tmp/x.txt\");")]
    [InlineData("Directory.Exists(\"/tmp\");")]
    [InlineData("Assembly.LoadFrom(\"evil.dll\");")]
    [InlineData("typeof(object).Assembly.GetTypes();")]
    [InlineData("var client = new HttpClient();")]
    public void Validate_ShouldRejectForbiddenApis(string source)
    {
        var policy = new ScriptSandboxPolicy();

        var result = policy.Validate(source);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().NotBeEmpty();
    }
}
