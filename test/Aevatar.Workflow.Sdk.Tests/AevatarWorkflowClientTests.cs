using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.Workflow.Sdk.Contracts;
using Aevatar.Workflow.Sdk.Errors;
using Aevatar.Workflow.Sdk.Options;
using Aevatar.Workflow.Sdk.Streaming;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Sdk.Tests;

public sealed class AevatarWorkflowClientTests
{
    [Fact]
    public async Task RunToCompletionAsync_WhenRunErrorFramePresent_ShouldThrowRunFailedException()
    {
        const string ssePayload = """
data: {"type":"RUN_STARTED","threadId":"actor-1"}

data: {"type":"RUN_ERROR","code":"EXECUTION_FAILED","message":"Workflow execution failed."}

data: {"type":"STATE_SNAPSHOT","snapshot":{"actorId":"actor-1","projectionCompleted":false}}

""";

        var client = CreateClient((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ssePayload, Encoding.UTF8, "text/event-stream"),
            }));

        var act = () => client.RunToCompletionAsync(new ChatRunRequest { Prompt = "hello" }, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<AevatarWorkflowException>();
        ex.Which.Kind.Should().Be(AevatarWorkflowErrorKind.RunFailed);
        ex.Which.ErrorCode.Should().Be("EXECUTION_FAILED");
    }

    [Fact]
    public async Task ResumeAsync_ShouldSerializeRequestAndParseResponse()
    {
        var client = CreateClient(async (request, ct) =>
        {
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri?.AbsolutePath.Should().Be("/api/workflows/resume");

            var body = await request.Content!.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            doc.RootElement.GetProperty("actorId").GetString().Should().Be("actor-1");
            doc.RootElement.GetProperty("runId").GetString().Should().Be("run-1");
            doc.RootElement.GetProperty("stepId").GetString().Should().Be("approval-1");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"accepted":true,"actorId":"actor-1","runId":"run-1","stepId":"approval-1","commandId":"cmd-1"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });

        var result = await client.ResumeAsync(new WorkflowResumeRequest
        {
            ActorId = "actor-1",
            RunId = "run-1",
            StepId = "approval-1",
            Approved = true,
            CommandId = "cmd-1",
        });

        result.Accepted.Should().BeTrue();
        result.ActorId.Should().Be("actor-1");
        result.CommandId.Should().Be("cmd-1");
    }

    [Fact]
    public async Task SignalAsync_WhenRunIdMissing_ShouldThrowInvalidRequestWithoutCallingServer()
    {
        var called = false;
        var client = CreateClient((_, _) =>
        {
            called = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var act = () => client.SignalAsync(new WorkflowSignalRequest
        {
            ActorId = "actor-1",
            RunId = "",
            SignalName = "continue",
        });

        var ex = await act.Should().ThrowAsync<AevatarWorkflowException>();
        ex.Which.Kind.Should().Be(AevatarWorkflowErrorKind.InvalidRequest);
        called.Should().BeFalse();
    }

    [Fact]
    public async Task GetActorSnapshotAsync_WhenNotFound_ShouldReturnNull()
    {
        var client = CreateClient((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"error":"missing"}""", Encoding.UTF8, "application/json"),
            }));

        var snapshot = await client.GetActorSnapshotAsync("missing-actor", CancellationToken.None);
        snapshot.Should().BeNull();
    }

    private static IAevatarWorkflowClient CreateClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        var httpClient = new HttpClient(new TestHttpMessageHandler(handler))
        {
            BaseAddress = new Uri("http://localhost:5000"),
        };
        var options = Microsoft.Extensions.Options.Options.Create(new AevatarWorkflowClientOptions
        {
            BaseUrl = "http://localhost:5000",
        });
        return new AevatarWorkflowClient(httpClient, new SseChatTransport(), options);
    }
}
