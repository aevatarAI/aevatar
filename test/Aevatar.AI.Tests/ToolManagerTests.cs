using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public class ToolManagerTests
{
    [Fact]
    public async Task ExecuteToolCallAsync_WhenToolMissing_ShouldReturnErrorToolMessage()
    {
        var manager = new ToolManager();

        var result = await manager.ExecuteToolCallAsync(new ToolCall
        {
            Id = "tc-1",
            Name = "missing",
            ArgumentsJson = "{}",
        });

        result.Role.Should().Be("tool");
        result.ToolCallId.Should().Be("tc-1");
        result.Content.Should().Contain("not found");
    }

    [Fact]
    public async Task ExecuteToolCallAsync_WhenToolThrows_ShouldReturnErrorToolMessage()
    {
        var manager = new ToolManager();
        manager.Register(new DelegateTool("bad", _ => throw new InvalidOperationException("boom")));

        var result = await manager.ExecuteToolCallAsync(new ToolCall
        {
            Id = "tc-2",
            Name = "bad",
            ArgumentsJson = "{}",
        });

        result.Content.Should().Contain("boom");
    }

    [Fact]
    public async Task RegisterAndUnregister_ShouldControlToolVisibility()
    {
        var manager = new ToolManager();
        manager.Register(new DelegateTool("one", _ => "1"));
        manager.HasTools.Should().BeTrue();
        manager.Get("one").Should().NotBeNull();

        var removed = manager.Unregister("one");

        removed.Should().BeTrue();
        manager.Get("one").Should().BeNull();
        manager.HasTools.Should().BeFalse();

        var result = await manager.ExecuteToolCallAsync(new ToolCall
        {
            Id = "tc-3",
            Name = "one",
            ArgumentsJson = "{}",
        });
        result.Content.Should().Contain("not found");
    }

    [Fact]
    public async Task RegisterEnumerableAndClear_ShouldApplyExpectedState()
    {
        var manager = new ToolManager();
        manager.Register(
        [
            new DelegateTool("a", _ => "A"),
            new DelegateTool("b", _ => "B"),
        ]);

        manager.GetAll().Select(x => x.Name).Should().BeEquivalentTo(["a", "b"]);

        var ok = await manager.ExecuteToolCallAsync(new ToolCall
        {
            Id = "tc-4",
            Name = "b",
            ArgumentsJson = "{}",
        });
        ok.Content.Should().Be("B");

        manager.Clear();
        manager.HasTools.Should().BeFalse();
    }

    private sealed class DelegateTool : IAgentTool
    {
        private readonly Func<string, string> _execute;

        public DelegateTool(string name, Func<string, string> execute)
        {
            Name = name;
            _execute = execute;
        }

        public string Name { get; }
        public string Description => "delegate";
        public string ParametersSchema => "{}";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_execute(argumentsJson));
        }
    }
}
