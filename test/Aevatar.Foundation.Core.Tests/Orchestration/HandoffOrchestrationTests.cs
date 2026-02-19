using Aevatar.Foundation.Core.Orchestration;
using FluentAssertions;

namespace Aevatar.Foundation.Core.Tests.Orchestration;

public class HandoffOrchestrationTests
{
    [Fact]
    public async Task ExecuteAsync_HandsOffThroughChain()
    {
        var orch = new HandoffOrchestration(
            (agentId, input, ct) => Task.FromResult($"{input}>{agentId}"),
            (currentAgent, output) => currentAgent switch
            {
                "A" => "B",
                "B" => "C",
                _ => null,
            });

        var result = await orch.ExecuteAsync(new OrchestrationInput
        {
            Prompt = "start", AgentIds = ["A"],
        });

        result.Success.Should().BeTrue();
        result.Output.Should().Be("start>A>B>C");
        result.AgentResults.Should().HaveCount(3);
    }

    [Fact]
    public async Task ExecuteAsync_NoHandoff_StopsImmediately()
    {
        var orch = new HandoffOrchestration(
            (_, input, _) => Task.FromResult("done"),
            (_, _) => null);

        var result = await orch.ExecuteAsync(new OrchestrationInput
        {
            Prompt = "x", AgentIds = ["A"],
        });

        result.Success.Should().BeTrue();
        result.AgentResults.Should().HaveCount(1);
    }

    [Fact]
    public async Task ExecuteAsync_MaxHandoffs_ExceededReturnsError()
    {
        var orch = new HandoffOrchestration(
            (_, _, _) => Task.FromResult("loop"),
            (_, _) => "A",
            maxHandoffs: 3);

        var result = await orch.ExecuteAsync(new OrchestrationInput
        {
            Prompt = "x", AgentIds = ["A"],
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Max handoffs");
        result.AgentResults.Should().HaveCount(4);
    }

    [Fact]
    public async Task ExecuteAsync_AgentFailure_StopsChain()
    {
        var callCount = 0;
        var orch = new HandoffOrchestration(
            (agentId, _, _) =>
            {
                callCount++;
                if (agentId == "B") throw new Exception("B broke");
                return Task.FromResult("ok");
            },
            (current, _) => current == "A" ? "B" : null);

        var result = await orch.ExecuteAsync(new OrchestrationInput
        {
            Prompt = "x", AgentIds = ["A"],
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("B broke");
        callCount.Should().Be(2);
    }
}
