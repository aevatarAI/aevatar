using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdProxyToolFileArtifactTests
{
    [Fact]
    public async Task ExecuteAsync_WithMissingResponseMode_ShouldKeepTextProxyBehavior()
    {
        var handler = new ProxyRecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
        });
        var tool = CreateTool(handler);
        var previous = AgentToolRequestContext.Current;
        try
        {
            AgentToolRequestContext.Current = BuildContext();

            var result = await tool.ExecuteAsync(
                """{"slug":"storage","path":"/records","method":"GET"}""");

            result.Should().Be("""{"ok":true}""");
            handler.Requests.Should().ContainSingle();
            handler.Requests.Last().RequestUri!.AbsolutePath.Should().Be("/api/v1/proxy/s/storage/records");
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithTextResponseMode_ShouldKeepTextProxyBehavior()
    {
        var handler = new ProxyRecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
        });
        var ingress = new RecordingNyxIdProxyFileArtifactIngress();
        var tool = CreateTool(handler, ingress);
        var previous = AgentToolRequestContext.Current;
        try
        {
            AgentToolRequestContext.Current = BuildContext();

            var result = await tool.ExecuteAsync(
                """{"slug":"storage","path":"/records","method":"GET","response_mode":"text"}""");

            result.Should().Be("""{"ok":true}""");
            ingress.Requests.Should().BeEmpty();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithFileArtifactResponseMode_ShouldStoreBinaryThroughIngressAndReturnSanitizedFileRef()
    {
        var body = Encoding.UTF8.GetBytes("downloaded bytes");
        var handler = new ProxyRecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/proxy/services")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""[{"slug":"storage"}]""", Encoding.UTF8, "application/json"),
                };
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            };
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/csv");
            response.Content.Headers.ContentDisposition =
                ContentDispositionHeaderValue.Parse("attachment; filename=\"report.csv\"");
            return response;
        });
        var ingress = new RecordingNyxIdProxyFileArtifactIngress();
        var tool = CreateTool(handler, ingress);
        var previous = AgentToolRequestContext.Current;
        try
        {
            AgentToolRequestContext.Current = BuildContext(
                scopeId: "scope-a",
                parentRunId: "run-a",
                parentStepId: "step-a",
                rootRunId: "root-a");

            var result = await tool.ExecuteAsync(
                """{"slug":"storage","path":"/reports/1","method":"GET","response_mode":"file_artifact","headers":{"X-Trace":"trace-1"}}""");

            using var document = JsonDocument.Parse(result);
            var root = document.RootElement;
            root.GetProperty("success").GetBoolean().Should().BeTrue();
            root.GetProperty("response_mode").GetString().Should().Be("file_artifact");
            root.GetProperty("slug").GetString().Should().Be("storage");
            root.GetProperty("path").GetString().Should().Be("/reports/1");
            root.GetProperty("http_status").GetInt32().Should().Be(200);
            root.GetProperty("file_ref").GetProperty("file_id").GetString().Should().Be("file-1");
            root.GetProperty("file_ref").GetProperty("artifact_id").GetString().Should().Be("artifact-1");
            root.GetProperty("file_ref").GetProperty("source_kind").GetString().Should().Be("ConnectedServiceResource");
            result.Should().NotContain("downloaded bytes");
            result.Contains("base64", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
            result.Should().NotContain("data:");

            ingress.Requests.Should().ContainSingle();
            var ingressRequest = ingress.Requests[0];
            ingressRequest.Content.ToArray().Should().Equal(body);
            ingressRequest.SourceKind.Should().Be(WorkflowFileSourceKind.ConnectedServiceResource);
            ingressRequest.SourceMessageId.Should().Be("nyxid_proxy:storage");
            ingressRequest.SourceResourceKey.Should().Be("/reports/1");
            ingressRequest.FileName.Should().Be("report.csv");
            ingressRequest.MediaType.Should().Be("text/csv");
            ingressRequest.OwnerRunId.Should().Be("run-a");
            ingressRequest.OwnerScopeId.Should().Be("scope-a");

            var binaryRequest = handler.Requests.Last();
            binaryRequest.Method.Should().Be(HttpMethod.Get);
            binaryRequest.Headers.GetValues("X-Trace").Should().ContainSingle().Which.Should().Be("trace-1");
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Theory]
    [InlineData("POST", """{"name":"report"}""", "file_artifact_requires_get")]
    [InlineData("GET", """{"name":"report"}""", "file_artifact_disallows_body")]
    [InlineData("GET", "", "file_artifact_disallows_body")]
    public async Task ExecuteAsync_WithFileArtifactResponseMode_ShouldRejectUnsafeRequestShapes(
        string method,
        string body,
        string expectedError)
    {
        var ingress = new RecordingNyxIdProxyFileArtifactIngress();
        var tool = CreateTool(new ProxyRecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""[{"slug":"storage"}]""", Encoding.UTF8, "application/json"),
        }), ingress);
        var previous = AgentToolRequestContext.Current;
        try
        {
            AgentToolRequestContext.Current = BuildContext();

            var result = await tool.ExecuteAsync(
                JsonSerializer.Serialize(new
                {
                    slug = "storage",
                    path = "/reports/1",
                    method,
                    body,
                    response_mode = "file_artifact",
                }));

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
            document.RootElement.GetProperty("response_mode").GetString().Should().Be("file_artifact");
            document.RootElement.GetProperty("error").GetString().Should().Be(expectedError);
            ingress.Requests.Should().BeEmpty();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithFileArtifactResponseMode_ShouldRequireManagedWorkflowContextAndIngress()
    {
        var toolWithoutIngress = CreateTool(new ProxyRecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""[{"slug":"storage"}]""", Encoding.UTF8, "application/json"),
        }));
        var previous = AgentToolRequestContext.Current;
        try
        {
            AgentToolRequestContext.Current = BuildContext();

            var missingIngress = await toolWithoutIngress.ExecuteAsync(
                """{"slug":"storage","path":"/reports/1","response_mode":"file_artifact"}""");
            using (var document = JsonDocument.Parse(missingIngress))
            {
                document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
                document.RootElement.GetProperty("error").GetString().Should().Be("file_artifact_ingress_unavailable");
            }

            AgentToolRequestContext.Current = AgentToolExecutionContext.Empty with
            {
                Credentials = new AgentToolCredentials("access-token", null, null),
                Caller = new AgentToolCallerContext("scope-a", null, null),
            };
            var toolWithIngress = CreateTool(
                new ProxyRecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""[{"slug":"storage"}]""", Encoding.UTF8, "application/json"),
                }),
                new RecordingNyxIdProxyFileArtifactIngress());

            var missingWorkflowRuntime = await toolWithIngress.ExecuteAsync(
                """{"slug":"storage","path":"/reports/1","response_mode":"file_artifact"}""");
            using (var document = JsonDocument.Parse(missingWorkflowRuntime))
            {
                document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
                document.RootElement.GetProperty("error").GetString().Should().Be("managed_workflow_context_required");
            }
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithInvalidResponseMode_ShouldFailClosedWithoutProxyCall()
    {
        var handler = new ProxyRecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
        });
        var tool = CreateTool(handler, new RecordingNyxIdProxyFileArtifactIngress());
        var previous = AgentToolRequestContext.Current;
        try
        {
            AgentToolRequestContext.Current = BuildContext();

            var result = await tool.ExecuteAsync(
                """{"slug":"storage","path":"/reports/1","response_mode":"binary_base64"}""");

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
            document.RootElement.GetProperty("error").GetString().Should().Be("invalid_response_mode");
            handler.Requests.Should().BeEmpty();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    private static NyxIdProxyTool CreateTool(
        ProxyRecordingHandler handler,
        INyxIdProxyFileArtifactIngress? ingress = null)
    {
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));
        return new NyxIdProxyTool(client, null, ingress);
    }

    private static AgentToolExecutionContext BuildContext(
        string scopeId = "scope-a",
        string parentActorId = "workflow-run-actor",
        string parentRunId = "run-a",
        string parentStepId = "step-a",
        string rootRunId = "run-a") =>
        AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials("access-token", null, null),
            Caller = new AgentToolCallerContext(scopeId, null, null),
            WorkflowRuntime = new AgentWorkflowRuntimeContext(
                parentActorId,
                parentRunId,
                parentStepId,
                rootRunId,
                1),
        };

    private sealed class RecordingNyxIdProxyFileArtifactIngress : INyxIdProxyFileArtifactIngress
    {
        public List<WorkflowFileIngressRequest> Requests { get; } = [];

        public ValueTask<WorkflowFileIngressResult> IngestAsync(
            WorkflowFileIngressRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(new WorkflowFileIngressResult(new WorkflowFileRef
            {
                FileId = "file-1",
                ArtifactId = "artifact-1",
                SourceKind = request.SourceKind,
                SourceMessageId = request.SourceMessageId,
                SourceResourceKey = request.SourceResourceKey,
                FileName = request.FileName,
                MediaType = request.MediaType,
                SizeBytes = request.Content.Length,
                Sha256 = "sha",
                CreatedAtUnixMs = 10,
                ExpiresAtUnixMs = 20,
                OwnerRunId = request.OwnerRunId,
                OwnerScopeId = request.OwnerScopeId,
            }));
        }
    }

    private sealed class ProxyRecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responder(request));
        }
    }
}
