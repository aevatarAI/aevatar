using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.Workflow.Application.Abstractions.Runs;
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
data: {"custom":{"name":"aevatar.run.context","payload":{"@type":"type.googleapis.com/aevatar.workflow.runs.WorkflowRunContextPayload","actorId":"actor-1","workflowName":"auto","commandId":"cmd-1"}}}

data: {"runStarted":{"threadId":"actor-1","runId":"run-1"}}

data: {"runFinished":{"threadId":"actor-1","result":{"@type":"type.googleapis.com/aevatar.workflow.runs.WorkflowRunResultPayload","output":"done"}}}

data: {"stateSnapshot":{"snapshot":{"@type":"type.googleapis.com/aevatar.workflow.runs.WorkflowProjectionStateSnapshotPayload","actorId":"actor-1","projectionCompleted":true}}}

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

        events[0].Frame.Custom.Payload.Unpack<WorkflowRunContextPayload>()
            .CommandId.Should().Be("cmd-1");
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
    public async Task StreamAsync_ShouldNotSerializeAgentId()
    {
        string? capturedBody = null;
        var handler = new TestHttpMessageHandler(async (request, ct) =>
        {
            capturedBody = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "text/event-stream"),
            };
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5100") };
        var transport = new SseChatTransport();

        await foreach (var _ in transport.StreamAsync(
                           client,
                           new ChatRunRequest
                           {
                               Prompt = "hello",
                               ScopeId = "scope-a",
                               Workflow = "approval",
                           },
                           CreateJsonOptions(),
                           CancellationToken.None))
        {
        }

        using var document = JsonDocument.Parse(capturedBody!);
        document.RootElement.TryGetProperty("agentId", out _).Should().BeFalse();
    }

    [Fact]
    public async Task StreamAsync_WhenResponseStreamAcquisitionCanceled_ShouldPropagateCancellation()
    {
        using var cts = new CancellationTokenSource();
        var handler = new TestHttpMessageHandler((_, _) =>
        {
            cts.Cancel();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new CancelingStreamContent(),
            });
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5100") };
        var transport = new SseChatTransport();

        var act = async () =>
        {
            await foreach (var _ in transport.StreamAsync(
                               client,
                               new ChatRunRequest { Prompt = "hello", ScopeId = "scope-a", Workflow = "approval" },
                               CreateJsonOptions(),
                               cts.Token))
            {
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task StreamAsync_ShouldIgnoreUnknownWireFields()
    {
        const string ssePayload = """
data: {"runStarted":{"threadId":"actor-1","runId":"run-1"},"source":"playground","extra":{"attempt":2}}

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
        frame.RunStarted.ThreadId.Should().Be("actor-1");
        frame.RunStarted.RunId.Should().Be("run-1");
    }

    private static JsonSerializerOptions CreateJsonOptions() =>
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
        };

    private sealed class CancelingStreamContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.CompletedTask;

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromCanceled<Stream>(cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
