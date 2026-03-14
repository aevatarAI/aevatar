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
            doc.RootElement.GetProperty("metadata").GetProperty("source").GetString().Should().Be("sdk");

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
            Metadata = new Dictionary<string, string>
            {
                ["source"] = "sdk",
            },
        });

        result.Accepted.Should().BeTrue();
        result.ActorId.Should().Be("actor-1");
        result.CommandId.Should().Be("cmd-1");
    }

    [Fact]
    public async Task SignalAsync_ShouldSerializeOptionalStepId()
    {
        var client = CreateClient(async (request, ct) =>
        {
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri?.AbsolutePath.Should().Be("/api/workflows/signal");

            var body = await request.Content!.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            doc.RootElement.GetProperty("actorId").GetString().Should().Be("actor-1");
            doc.RootElement.GetProperty("runId").GetString().Should().Be("run-1");
            doc.RootElement.GetProperty("signalName").GetString().Should().Be("ops_window_open");
            doc.RootElement.GetProperty("stepId").GetString().Should().Be("wait-1");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"accepted":true,"actorId":"actor-1","runId":"run-1","signalName":"ops_window_open","stepId":"wait-1","commandId":"cmd-s1"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });

        var result = await client.SignalAsync(new WorkflowSignalRequest
        {
            ActorId = "actor-1",
            RunId = "run-1",
            SignalName = "ops_window_open",
            StepId = "wait-1",
            CommandId = "cmd-s1",
        });

        result.Accepted.Should().BeTrue();
        result.StepId.Should().Be("wait-1");
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

    [Fact]
    public async Task GetWorkflowCatalogAsync_ShouldParseCatalogArray()
    {
        var client = CreateClient((request, _) =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri?.AbsolutePath.Should().Be("/api/workflow-catalog");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """[{"name":"workflow_install","source":"repo","sourceLabel":"Starter"}]""",
                    Encoding.UTF8,
                    "application/json"),
            });
        });

        var catalog = await client.GetWorkflowCatalogAsync(CancellationToken.None);

        catalog.Should().HaveCount(1);
        catalog[0].GetProperty("name").GetString().Should().Be("workflow_install");
        catalog[0].GetProperty("sourceLabel").GetString().Should().Be("Starter");
    }

    [Fact]
    public async Task GetCapabilitiesAsync_ShouldParseCapabilitiesPayload()
    {
        var client = CreateClient((request, _) =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri?.AbsolutePath.Should().Be("/api/capabilities");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"schemaVersion":"capabilities.v1","primitives":[{"name":"connector_call"}],"connectors":[{"name":"aevatar_cli","type":"cli"}]}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        });

        var capabilities = await client.GetCapabilitiesAsync(CancellationToken.None);

        capabilities.Should().NotBeNull();
        capabilities!.Value.GetProperty("schemaVersion").GetString().Should().Be("capabilities.v1");
        capabilities.Value.GetProperty("primitives")[0].GetProperty("name").GetString().Should().Be("connector_call");
        capabilities.Value.GetProperty("connectors")[0].GetProperty("name").GetString().Should().Be("aevatar_cli");
    }

    [Fact]
    public async Task GetWorkflowDetailAsync_WhenNotFound_ShouldReturnNull()
    {
        var client = CreateClient((request, _) =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri?.AbsolutePath.Should().Be("/api/workflows/missing");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"error":"missing"}""", Encoding.UTF8, "application/json"),
            });
        });

        var detail = await client.GetWorkflowDetailAsync("missing", CancellationToken.None);

        detail.Should().BeNull();
    }

    [Fact]
    public async Task GetWorkflowDetailAsync_ShouldParseDetailPayload()
    {
        var client = CreateClient((request, _) =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri?.AbsolutePath.Should().Be("/api/workflows/workflow_install");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"catalog":{"name":"workflow_install","source":"repo"},"definition":{"name":"workflow_install"},"yaml":"name: workflow_install"}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        });

        var detail = await client.GetWorkflowDetailAsync("workflow_install", CancellationToken.None);

        detail.Should().NotBeNull();
        detail!.Value.GetProperty("catalog").GetProperty("name").GetString().Should().Be("workflow_install");
        detail.Value.GetProperty("definition").GetProperty("name").GetString().Should().Be("workflow_install");
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
