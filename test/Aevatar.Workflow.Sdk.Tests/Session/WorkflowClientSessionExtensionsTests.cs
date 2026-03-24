using System.Net;
using System.Text;
using Aevatar.Workflow.Sdk.Contracts;
using Aevatar.Workflow.Sdk.Options;
using Aevatar.Workflow.Sdk.Session;
using Aevatar.Workflow.Sdk.Streaming;
using FluentAssertions;

namespace Aevatar.Workflow.Sdk.Tests.Session;

public sealed class WorkflowClientSessionExtensionsTests
{
    [Fact]
    public async Task StartRunStreamWithTrackingAsync_ShouldTrackSessionContext()
    {
        const string ssePayload = """
data: {"type":"CUSTOM","name":"aevatar.run.context","value":{"actorId":"actor-1","workflowName":"auto","commandId":"cmd-1"}}

data: {"type":"CUSTOM","name":"aevatar.human_input.request","value":{"runId":"run-1","stepId":"approval-1","suspensionType":"human_approval"}}

data: {"type":"RUN_FINISHED","result":{"output":"ok"}}

""";

        var client = CreateClient((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ssePayload, Encoding.UTF8, "text/event-stream"),
            }));

        var tracker = new RunSessionTracker();
        var events = new List<WorkflowEvent>();

        await foreach (var evt in client.StartRunStreamWithTrackingAsync(
                           new ChatRunRequest { Prompt = "hello" },
                           tracker,
                           CancellationToken.None))
        {
            events.Add(evt);
        }

        events.Should().NotBeEmpty();
        var snapshot = tracker.Snapshot;
        snapshot.ActorId.Should().Be("actor-1");
        snapshot.CommandId.Should().Be("cmd-1");
        snapshot.RunId.Should().Be("run-1");
        snapshot.StepId.Should().Be("approval-1");
        snapshot.SuspensionType.Should().Be("human_approval");
    }

    private static IAevatarWorkflowClient CreateClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        var httpClient = new HttpClient(new TestHttpMessageHandler(handler))
        {
            BaseAddress = new Uri("http://localhost:5100"),
        };
        var options = Microsoft.Extensions.Options.Options.Create(new AevatarWorkflowClientOptions
        {
            BaseUrl = "http://localhost:5100",
        });
        return new AevatarWorkflowClient(httpClient, new SseChatTransport(), options);
    }
}
