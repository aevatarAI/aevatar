using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.Workflow.Sdk.Contracts;
using Aevatar.Workflow.Sdk.Errors;
using Aevatar.Workflow.Sdk.Streaming;
using FluentAssertions;

namespace Aevatar.Workflow.Sdk.Tests.Streaming;

public sealed class SseChatTransportTests
{
    [Fact]
    public async Task StreamAsync_ShouldParseFramesInOrder()
    {
        const string ssePayload = """
data: {"type":"CUSTOM","name":"aevatar.run.context","value":{"actorId":"actor-1","workflowName":"auto","commandId":"cmd-1"}}

data: {"type":"RUN_STARTED","threadId":"actor-1"}

data: {"type":"RUN_FINISHED","result":{"output":"done"}}

data: {"type":"STATE_SNAPSHOT","snapshot":{"actorId":"actor-1","projectionCompleted":true}}

""";

        var handler = new TestHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ssePayload, Encoding.UTF8, "text/event-stream"),
            }));
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5100") };
        var transport = new SseChatTransport();

        var events = new List<WorkflowEvent>();
        await foreach (var evt in transport.StreamAsync(
                           client,
                           new ChatRunRequest { Prompt = "hello", ScopeId = "scope-a", Workflow = "approval" },
                           CreateJsonOptions(),
                           CancellationToken.None))
        {
            events.Add(evt);
        }

        events.Select(x => x.Type).Should().ContainInOrder(
            WorkflowEventTypes.Custom,
            WorkflowEventTypes.RunStarted,
            WorkflowEventTypes.RunFinished,
            WorkflowEventTypes.StateSnapshot);

        var contextValue = events[0].Frame.Value;
        contextValue.HasValue.Should().BeTrue();
        contextValue!.Value.GetProperty("commandId").GetString().Should().Be("cmd-1");
    }

    [Fact]
    public async Task StreamAsync_WhenHttpError_ShouldThrowStructuredException()
    {
        var handler = new TestHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    """{"code":"WORKFLOW_NOT_FOUND","message":"Workflow not found."}""",
                    Encoding.UTF8,
                    "application/json"),
            }));
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5100") };
        var transport = new SseChatTransport();

        var act = async () =>
        {
            await foreach (var _ in transport.StreamAsync(
                               client,
                               new ChatRunRequest { Prompt = "hello", ScopeId = "scope-a", Workflow = "missing" },
                               CreateJsonOptions(),
                               CancellationToken.None))
            {
            }
        };

        var ex = await act.Should().ThrowAsync<AevatarWorkflowException>();
        ex.Which.Kind.Should().Be(AevatarWorkflowErrorKind.Http);
        ex.Which.ErrorCode.Should().Be("WORKFLOW_NOT_FOUND");
    }

    [Fact]
    public async Task StreamAsync_ShouldPreserveUnknownFrameFieldsInExtensionData()
    {
        const string ssePayload = """
data: {"type":"RUN_STARTED","threadId":"actor-1","source":"playground","extra":{"attempt":2}}

""";

        var handler = new TestHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ssePayload, Encoding.UTF8, "text/event-stream"),
            }));
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5100") };
        var transport = new SseChatTransport();

        var events = new List<WorkflowEvent>();
        await foreach (var evt in transport.StreamAsync(
                           client,
                           new ChatRunRequest { Prompt = "hello", ScopeId = "scope-a", Workflow = "approval" },
                           CreateJsonOptions(),
                           CancellationToken.None))
        {
            events.Add(evt);
        }

        events.Should().HaveCount(1);
        var frame = events[0].Frame;
        frame.AdditionalProperties.Should().NotBeNull();
        frame.AdditionalProperties!.Should().ContainKey("source");
        frame.AdditionalProperties["source"].GetString().Should().Be("playground");
        frame.AdditionalProperties.Should().ContainKey("extra");
        frame.AdditionalProperties["extra"].GetProperty("attempt").GetInt32().Should().Be(2);
    }

    private static JsonSerializerOptions CreateJsonOptions() =>
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
        };
}
