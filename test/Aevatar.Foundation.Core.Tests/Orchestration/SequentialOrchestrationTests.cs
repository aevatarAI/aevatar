using Aevatar.Foundation.Core.Orchestration;
using FluentAssertions;

namespace Aevatar.Foundation.Core.Tests.Orchestration;

public class SequentialOrchestrationTests
{
    [Fact]
    public async Task ExecuteAsync_ChainsAgentsInOrder()
    {
        var callOrder = new List<string>();
        var orch = new SequentialOrchestration(async (agentId, input, ct) =>
        {
            callOrder.Add(agentId);
            await Task.Yield();
            return $"{input}+{agentId}";
        });

        var result = await orch.ExecuteAsync(new OrchestrationInput
        {
            Prompt = "start", AgentIds = ["A", "B", "C"],
        });

        result.Success.Should().BeTrue();
        result.Output.Should().Be("start+A+B+C");
        result.AgentResults.Should().HaveCount(3);
        callOrder.Should().Equal("A", "B", "C");
    }

    [Fact]
    public async Task ExecuteAsync_StopsOnFailure()
    {
        var orch = new SequentialOrchestration((agentId, input, ct) =>
        {
            if (agentId == "B") throw new InvalidOperationException("B failed");
            return Task.FromResult($"{input}+{agentId}");
        });

        var result = await orch.ExecuteAsync(new OrchestrationInput
        {
            Prompt = "x", AgentIds = ["A", "B", "C"],
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("B failed");
        result.AgentResults.Should().HaveCount(2);
        result.AgentResults[0].Success.Should().BeTrue();
        result.AgentResults[1].Success.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_EmptyAgents_ReturnsFalse()
    {
        var orch = new SequentialOrchestration((_, _, _) => Task.FromResult(""));
        var result = await orch.ExecuteAsync(new OrchestrationInput
        {
            Prompt = "x", AgentIds = [],
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("No agents");
    }
}
