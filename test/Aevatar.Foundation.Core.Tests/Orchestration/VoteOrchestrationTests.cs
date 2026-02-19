using Aevatar.Foundation.Core.Orchestration;
using FluentAssertions;

namespace Aevatar.Foundation.Core.Tests.Orchestration;

public class VoteOrchestrationTests
{
    [Fact]
    public async Task ExecuteAsync_DefaultVote_SelectsLongestOutput()
    {
        var orch = new VoteOrchestration((agentId, _, _) =>
            Task.FromResult(agentId == "B" ? "this is the longest answer by far" : "short"));

        var result = await orch.ExecuteAsync(new OrchestrationInput
        {
            Prompt = "question", AgentIds = ["A", "B", "C"],
        });

        result.Success.Should().BeTrue();
        result.Output.Should().Be("this is the longest answer by far");
    }

    [Fact]
    public async Task ExecuteAsync_CustomVoteStrategy()
    {
        var orch = new VoteOrchestration(
            (agentId, _, _) => Task.FromResult($"answer-{agentId}"),
            (results, ct) => Task.FromResult(results.First(r => r.AgentId == "C")));

        var result = await orch.ExecuteAsync(new OrchestrationInput
        {
            Prompt = "q", AgentIds = ["A", "B", "C"],
        });

        result.Success.Should().BeTrue();
        result.Output.Should().Be("answer-C");
    }

    [Fact]
    public async Task ExecuteAsync_AllFail_ReturnsError()
    {
        var orch = new VoteOrchestration((_, _, _) =>
            throw new Exception("fail"));

        var result = await orch.ExecuteAsync(new OrchestrationInput
        {
            Prompt = "q", AgentIds = ["A", "B"],
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("All agents failed");
    }
}
