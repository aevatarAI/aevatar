using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using FluentAssertions;

namespace Aevatar.AI.Core.Tests.Tools;

public class ToolManagerTests
{
    [Fact]
    public async Task ExecuteToolCallAsync_NotFound_ReturnsErrorMessage()
    {
        var manager = new ToolManager();

        var result = await manager.ExecuteToolCallAsync(new ToolCall
        {
            Id = "call-not-found",
            Name = "missing",
            ArgumentsJson = "{\"x\":1}",
        });

        result.Role.Should().Be("tool");
        result.ToolCallId.Should().Be("call-not-found");
        result.Content.Should().Be("{\"error\":\"Tool \\u0027missing\\u0027 not found\"}");
    }

    [Fact]
    public async Task ExecuteToolCallAsync_FoundTool_ReturnsToolResult()
    {
        var manager = new ToolManager();
        manager.Register(new FakeAgentTool("echo", _ => Task.FromResult("ok")));

        var result = await manager.ExecuteToolCallAsync(new ToolCall
        {
            Id = "call-1",
            Name = "echo",
            ArgumentsJson = "{}",
        });

        result.Content.Should().Be("ok");
    }

    [Fact]
    public async Task ExecuteToolCallAsync_FoundTool_ExceptionsBecomeErrorMessage()
    {
        var manager = new ToolManager();
        manager.Register(new FakeAgentTool("bad", _ => throw new InvalidOperationException("boom")));

        var result = await manager.ExecuteToolCallAsync(new ToolCall
        {
            Id = "call-2",
            Name = "bad",
            ArgumentsJson = "{}",
        });

        result.Content.Should().Be("{\"error\":\"boom\"}");
    }

    [Fact]
    public async Task ExecuteToolCallRawAsync_FoundTool_ReturnsResultAndNoError()
    {
        var manager = new ToolManager();
        manager.Register(new FakeAgentTool("sum", _ => Task.FromResult("{\"value\":2}")));

        var (result, error) = await manager.ExecuteToolCallRawAsync(new ToolCall
        {
            Id = "call-3",
            Name = "sum",
            ArgumentsJson = "{}",
        });

        result.Should().Be("{\"value\":2}");
        error.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteToolCallRawAsync_NotFound_ReturnsErrorAndException()
    {
        var manager = new ToolManager();

        var (result, error) = await manager.ExecuteToolCallRawAsync(new ToolCall
        {
            Id = "call-4",
            Name = "missing",
            ArgumentsJson = "{}",
        });

        result.Should().Be("{\"error\":\"Tool \\u0027missing\\u0027 not found\"}");
        error.Should().BeOfType<InvalidOperationException>();
        error!.Message.Should().Be("Tool 'missing' not found");
    }

    [Fact]
    public async Task ExecuteToolCallRawAsync_FoundTool_ExceptionsReturnedInTuple()
    {
        var manager = new ToolManager();
        manager.Register(new FakeAgentTool("bad-raw", _ => throw new ArgumentException("bad args")));

        var (result, error) = await manager.ExecuteToolCallRawAsync(new ToolCall
        {
            Id = "call-5",
            Name = "bad-raw",
            ArgumentsJson = "{}",
        });

        result.Should().Be("{\"error\":\"bad args\"}");
        error.Should().BeOfType<ArgumentException>();
        error!.Message.Should().Be("bad args");
    }

    [Fact]
    public void RegisterGetClearAndListTools_ManageToolsCorrectly()
    {
        var manager = new ToolManager();

        manager.Register(new FakeAgentTool("t1"));
        manager.Register(new FakeAgentTool("t2"));
        manager.HasTools.Should().BeTrue();

        manager.Register(new FakeAgentTool("t1"));
        var all = manager.GetAll();
        all.Should().HaveCount(2);

        manager.Get("t2").Should().NotBeNull();
        manager.Get("missing").Should().BeNull();

        manager.Unregister("t2").Should().BeTrue();
        manager.Unregister("missing").Should().BeFalse();

        manager.Clear();
        manager.HasTools.Should().BeFalse();
    }

    private sealed class FakeAgentTool(string name, Func<string, Task<string>>? executor = null) : IAgentTool
    {
        public string Name { get; } = name;
        public string Description => "fake";
        public string ParametersSchema => "{}";
        public bool IsReadOnly { get; init; }
        public bool IsDestructive { get; init; }
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            executor?.Invoke(argumentsJson) ?? Task.FromResult($"{{\"name\":\"{name}\"}}");
    }
}
