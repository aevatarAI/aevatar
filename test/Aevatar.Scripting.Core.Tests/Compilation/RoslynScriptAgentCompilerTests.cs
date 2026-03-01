using Aevatar.Scripting.Core.Compilation;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Compilation;

public class RoslynScriptAgentCompilerTests
{
    [Fact]
    public async Task CompileAsync_ShouldReject_WhenSandboxPolicyFails()
    {
        var compiler = new RoslynScriptAgentCompiler(new ScriptSandboxPolicy());
        var request = new ScriptCompilationRequest(
            ScriptId: "script-1",
            Revision: "rev-1",
            Source: "Task.Run(() => 1);");

        var result = await compiler.CompileAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().NotBeEmpty();
        result.CompiledDefinition.Should().BeNull();
    }

    [Fact]
    public async Task CompileAsync_ShouldReject_WhenSourceHasSyntaxError()
    {
        var compiler = new RoslynScriptAgentCompiler(new ScriptSandboxPolicy());
        var request = new ScriptCompilationRequest(
            ScriptId: "script-1",
            Revision: "rev-2",
            Source: "if (true {");

        var result = await compiler.CompileAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().NotBeEmpty();
        result.CompiledDefinition.Should().BeNull();
    }

    [Fact]
    public async Task CompileAsync_ShouldCreateDefinition_WhenSourceIsValid()
    {
        var compiler = new RoslynScriptAgentCompiler(new ScriptSandboxPolicy());
        var request = new ScriptCompilationRequest(
            ScriptId: "script-1",
            Revision: "rev-3",
            Source: "var x = 1;");

        var result = await compiler.CompileAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Diagnostics.Should().BeEmpty();
        result.CompiledDefinition.Should().NotBeNull();
        result.CompiledDefinition!.ScriptId.Should().Be("script-1");
        result.CompiledDefinition!.Revision.Should().Be("rev-3");
    }
}
