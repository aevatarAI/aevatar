using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using FluentAssertions;

namespace Aevatar.AI.Core.Tests.Tools;

public class ToolManagerTests
{
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

    private sealed class FakeAgentTool(string name) : IAgentTool
    {
        public string Name { get; } = name;
        public string Description => "fake";
        public string ParametersSchema => "{}";
        public bool IsReadOnly { get; init; }
        public bool IsDestructive { get; init; }
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult($"{{\"name\":\"{name}\"}}");
    }
}
