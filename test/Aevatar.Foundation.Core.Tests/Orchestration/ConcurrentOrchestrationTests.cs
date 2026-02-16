using Aevatar.Foundation.Core.Orchestration;
using FluentAssertions;

namespace Aevatar.Foundation.Core.Tests.Orchestration;

public class ConcurrentOrchestrationTests
{
    [Fact]
    public async Task ExecuteAsync_RunsAllAgentsInParallel()
    {
        var orch = new ConcurrentOrchestration(async (agentId, input, ct) =>
        {
            await Task.Yield();
            return $"result-{agentId}";
        });

        var result = await orch.ExecuteAsync(new OrchestrationInput
        {
            Prompt = "task", AgentIds = ["A", "B", "C"],
        });

        result.Success.Should().BeTrue();
        result.AgentResults.Should().HaveCount(3);
        result.Output.Should().Contain("result-A");
        result.Output.Should().Contain("result-B");
        result.Output.Should().Contain("result-C");
    }

    [Fact]
    public async Task ExecuteAsync_PartialFailure_ReportsError()
    {
        var orch = new ConcurrentOrchestration((agentId, input, ct) =>
        {
            if (agentId == "B") throw new Exception("B exploded");
            return Task.FromResult($"ok-{agentId}");
        });

        var result = await orch.ExecuteAsync(new OrchestrationInput
        {
            Prompt = "task", AgentIds = ["A", "B", "C"],
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("failed");
        result.AgentResults.Count(r => r.Success).Should().Be(2);
        result.AgentResults.Count(r => !r.Success).Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_CustomMerge()
    {
        var orch = new ConcurrentOrchestration(
            (agentId, _, _) => Task.FromResult($"v{agentId}"),
            results => string.Join(",", results.Select(r => r.Output)));

        var result = await orch.ExecuteAsync(new OrchestrationInput
        {
            Prompt = "x", AgentIds = ["1", "2"],
        });

        result.Success.Should().BeTrue();
        result.Output.Should().Be("v1,v2");
    }
}
