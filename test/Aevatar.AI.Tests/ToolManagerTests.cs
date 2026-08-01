using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public class ToolManagerTests
{
    [Fact]
    public void RegisterAndUnregister_ShouldControlToolVisibility()
    {
        var manager = new ToolManager();
        manager.Register(new DelegateTool("one", _ => "1"));
        manager.HasTools.Should().BeTrue();
        manager.Get("one").Should().NotBeNull();

        var removed = manager.Unregister("one");

        removed.Should().BeTrue();
        manager.Get("one").Should().BeNull();
        manager.HasTools.Should().BeFalse();

    }

    [Fact]
    public void RegisterEnumerableAndClear_ShouldApplyExpectedState()
    {
        var manager = new ToolManager();
        manager.Register(
        [
            new DelegateTool("a", _ => "A"),
            new DelegateTool("b", _ => "B"),
        ]);

        manager.GetAll().Select(x => x.Name).Should().BeEquivalentTo(["a", "b"]);

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
