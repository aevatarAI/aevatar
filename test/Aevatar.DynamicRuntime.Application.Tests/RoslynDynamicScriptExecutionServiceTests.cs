using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Aevatar.DynamicRuntime.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Aevatar.DynamicRuntime.Application.Tests;

public sealed class RoslynDynamicScriptExecutionServiceTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldRunCSharpScriptEntrypoint()
    {
        var sut = new RoslynDynamicScriptExecutionService(
            new DefaultScriptCompilationPolicy(),
            new DefaultScriptAssemblyLoadPolicy(),
            new DefaultScriptSandboxPolicy(),
            new DefaultScriptResourceQuotaPolicy());

        var script = @"
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;

public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public Task<string> HandleAsync(string input, CancellationToken ct = default)
        => Task.FromResult($""echo:{input}"");
}

var entrypoint = new ScriptEntrypoint();
";

        var result = await sut.ExecuteAsync(new DynamicScriptExecutionRequest(script, "hello"));

        result.Success.Should().BeTrue();
        result.Output.Should().Be("echo:hello");
        result.Error.Should().BeNull();
    }
}
