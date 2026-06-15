using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Application.Responses;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ResponsesCompletionApplicationServiceTests
{
    [Fact]
    public async Task CollectAsync_WaitCompleteInvocationTool_ShouldReturnTypedAcceptedReceipt()
    {
        var service = new ResponsesCompletionApplicationService();
        var tool = new TypedInvocationTool();
        var provider = new WaitCompleteInvocationProvider();

        var result = await service.CollectAsync(
            provider,
            BuildRequest(tool),
            BuildToolContext(),
            BuildClassification(tool));

        result.Text.Should().Be("typed completion consumed");
        provider.SecondRoundToolResult.Should().Contain("\"status\":\"accepted\"");
        provider.SecondRoundToolResult.Should().Contain("\"service_id\":\"service-1\"");
        tool.ExecuteCount.Should().Be(1);
    }

    [Fact]
    public async Task StreamAsync_WaitCompleteInvocationTool_ShouldReturnTypedAcceptedReceipt()
    {
        var service = new ResponsesCompletionApplicationService();
        var tool = new TypedInvocationTool();
        var provider = new WaitCompleteInvocationProvider();
        var textDeltas = new List<string>();

        var result = await service.StreamAsync(
            provider,
            BuildRequest(tool),
            BuildToolContext(),
            BuildClassification(tool),
            (delta, _) =>
            {
                textDeltas.Add(delta);
                return ValueTask.CompletedTask;
            });

        result.Text.Should().Be("typed completion consumed");
        textDeltas.Should().Equal("typed completion consumed");
        provider.SecondRoundToolResult.Should().Contain("\"status\":\"accepted\"");
        provider.SecondRoundToolResult.Should().Contain("\"service_id\":\"service-1\"");
        tool.ExecuteCount.Should().Be(1);
    }

    private static LLMRequest BuildRequest(IAgentTool tool) =>
        new()
        {
            Messages = [ChatMessage.User("run local invocation")],
            RequestId = "request-1",
            CallerContext = new LLMRequestCallerContext("scope-1", "owner-1", "resp_1"),
            Model = "test-model",
            Tools = [tool],
            ToolContext = BuildToolContext(),
        };

    private static AgentToolExecutionContext BuildToolContext() =>
        AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("request-1", null),
            Caller = new AgentToolCallerContext("scope-1", "owner-1", "resp_1"),
        };

    private static ResponsesToolClassification BuildClassification(IAgentTool tool) =>
        new([], [tool], [tool.Name], []);

    private sealed class WaitCompleteInvocationProvider : ILLMProvider
    {
        private int _round;

        public string Name => "test";

        public string? SecondRoundToolResult { get; private set; }

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            _round++;
            if (_round == 1)
            {
                yield return new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_team",
                        Name = "aevatar_invoke_team",
                        ArgumentsJson = """{"team_id":"team-1","endpoint_id":"entry","wait":"complete"}""",
                    },
                };
                yield return new LLMStreamChunk { IsLast = true, FinishReason = "tool_calls" };
                yield break;
            }

            SecondRoundToolResult = request.Messages.Last(message => message.Role == "tool").Content;
            yield return new LLMStreamChunk
            {
                DeltaContent = "typed completion consumed",
                IsLast = true,
                FinishReason = "stop",
            };
        }
    }

    private sealed class TypedInvocationTool : IAgentTool
    {
        public string Name => "aevatar_invoke_team";

        public string Description => "Invoke a team.";

        public string ParametersSchema => """{"type":"object","properties":{}}""";

        public int ExecuteCount { get; private set; }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ExecuteCount++;
            return Task.FromResult("""{"run_id":"team-command","status":"accepted","service_id":"service-1","endpoint_id":"entry","wait":"complete"}""");
        }
    }

}
