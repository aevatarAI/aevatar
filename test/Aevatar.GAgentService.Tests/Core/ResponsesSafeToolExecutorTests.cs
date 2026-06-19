using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Application.Responses;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ResponsesSafeToolExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_WhenToolThrows_ShouldReturnRedactedJsonError()
    {
        var tool = new ThrowingTool("local_tool", new InvalidOperationException("secret token /Users/me/path"));

        var result = await ResponsesSafeToolExecutor.ExecuteAsync(tool, """{"x":1}""");

        using var document = JsonDocument.Parse(result);
        var error = document.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("aevatar_local_tool_execution_failed");
        error.GetProperty("message").GetString().Should().Be("Aevatar local tool execution failed.");
        error.GetProperty("tool_name").GetString().Should().Be("local_tool");
        error.GetProperty("exception_type").GetString().Should().Be(nameof(InvalidOperationException));
        result.Should().NotContain("secret");
        result.Should().NotContain("/Users/me/path");
    }

    [Fact]
    public async Task ExecuteAsync_WhenCallerCancels_ShouldPropagateCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var tool = new ThrowingTool("local_tool", new OperationCanceledException(cts.Token));

        var act = () => ResponsesSafeToolExecutor.ExecuteAsync(tool, "{}", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class ThrowingTool(string name, Exception exception) : IAgentTool
    {
        public string Name { get; } = name;

        public string Description => "Throwing test tool";

        public string ParametersSchema => """{"type":"object"}""";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromException<string>(exception);
    }
}
