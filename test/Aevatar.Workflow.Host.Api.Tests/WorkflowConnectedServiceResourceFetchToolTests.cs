using System.Text;
using System.Text.Json;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Aevatar.Workflow.Infrastructure.Runs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using AppWorkflowCallerCredential = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential;
using AppWorkflowFileRef = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowFileRef;
using AppWorkflowFileSourceKind = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowFileSourceKind;
using ProtoWorkflowCallerCredential = Aevatar.Workflow.Abstractions.WorkflowCallerCredential;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowConnectedServiceResourceFetchToolTests
{
    [Fact]
    public async Task AddWorkflowInfrastructure_ShouldRegisterCanonicalFetchToolSource()
    {
        var services = new ServiceCollection();

        services.AddWorkflowInfrastructure();

        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(IWorkflowToolSource) &&
            x.ImplementationType == typeof(WorkflowConnectedServiceResourceFetchToolSource));
        using var provider = services.BuildServiceProvider();
        var toolNames = new List<string>();
        foreach (var toolSource in provider.GetServices<IWorkflowToolSource>())
        {
            var tools = await toolSource.GetToolsAsync();
            toolNames.AddRange(tools.Select(x => x.Name));
        }

        toolNames.Should().ContainSingle(name => name == "workflow_connected_service_resource_fetch");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldIngestProviderBytesAndReturnSanitizedFileRef()
    {
        var ingressPort = new RecordingWorkflowFileIngressPort();
        var adapter = new RecordingConnectedServiceResourceFetchAdapter(
            new WorkflowConnectedServiceResourceFetchRoute("lark", "message_resource_download", "image"))
        {
            Result = new WorkflowConnectedServiceResourceFetchResult(
                Succeeded: true,
                Content: Encoding.UTF8.GetBytes("image-bytes"),
                MediaType: "image/png",
                FileName: "photo.png"),
        };
        var source = new WorkflowConnectedServiceResourceFetchToolSource([adapter], ingressPort);

        var tool = (await source.GetToolsAsync()).Should()
            .ContainSingle(x => x.Name == "workflow_connected_service_resource_fetch")
            .Subject;

        var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
            ArgumentsJson: """
            {
              "provider": "lark",
              "operation": "message_resource_download",
              "resource_kind": "image",
              "message_id": "om_123",
              "resource_key": "img_v3_456"
            }
            """,
            RunId: "run-1",
            StepId: "step-1",
            ExecutionId: "exec-1",
            CallId: "call-1",
            ScopeId: "scope-1",
            CallerCredential: new ProtoWorkflowCallerCredential { BearerToken = "token-123" }));

        adapter.Requests.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new WorkflowConnectedServiceResourceFetchRequest(
                Route: new WorkflowConnectedServiceResourceFetchRoute("lark", "message_resource_download", "image"),
                SourceMessageId: "om_123",
                SourceResourceKey: "img_v3_456",
                CallerCredential: new AppWorkflowCallerCredential("token-123")));
        ingressPort.Requests.Should().ContainSingle();
        var ingressRequest = ingressPort.Requests[0];
        ingressRequest.Content.ToArray().Should().Equal(Encoding.UTF8.GetBytes("image-bytes"));
        ingressRequest.SourceKind.Should().Be(AppWorkflowFileSourceKind.ConnectedServiceResource);
        ingressRequest.SourceMessageId.Should().Be("om_123");
        ingressRequest.SourceResourceKey.Should().Be("img_v3_456");
        ingressRequest.FileName.Should().Be("photo.png");
        ingressRequest.MediaType.Should().Be("image/png");
        ingressRequest.OwnerRunId.Should().Be("run-1");
        ingressRequest.OwnerScopeId.Should().Be("scope-1");

        using var document = JsonDocument.Parse(output.ResultJson);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("provider").GetString().Should().Be("lark");
        root.GetProperty("operation").GetString().Should().Be("message_resource_download");
        root.GetProperty("resource_kind").GetString().Should().Be("image");
        var fileRef = root.GetProperty("file_ref");
        fileRef.GetProperty("file_id").GetString().Should().Be("wf-file-1");
        fileRef.GetProperty("artifact_id").GetString().Should().Be("workflow-file://wf-file-1");
        fileRef.GetProperty("source_kind").GetString().Should().Be("ConnectedServiceResource");
        fileRef.GetProperty("source_message_id").GetString().Should().Be("om_123");
        fileRef.GetProperty("source_resource_key").GetString().Should().Be("img_v3_456");
        root.ToString().Should().NotContain("image-bytes");
        root.ToString().Contains("base64", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectUnsupportedRoutesBeforeProviderCall()
    {
        var ingressPort = new RecordingWorkflowFileIngressPort();
        var adapter = new RecordingConnectedServiceResourceFetchAdapter(
            new WorkflowConnectedServiceResourceFetchRoute("lark", "message_resource_download", "image"));
        var source = new WorkflowConnectedServiceResourceFetchToolSource([adapter], ingressPort);
        var tool = (await source.GetToolsAsync()).Single();

        var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
            ArgumentsJson: """
            {
              "provider": "lark",
              "operation": "message_resource_download",
              "resource_kind": "video",
              "message_id": "om_123",
              "resource_key": "video_456"
            }
            """,
            RunId: "run-1",
            StepId: "step-1",
            ExecutionId: "exec-1",
            CallId: "call-1",
            ScopeId: "scope-1",
            CallerCredential: new ProtoWorkflowCallerCredential { BearerToken = "token-123" }));

        adapter.Requests.Should().BeEmpty();
        ingressPort.Requests.Should().BeEmpty();
        output.ResultJson.Should().Contain("unsupported_resource_route");
    }

    private sealed class RecordingWorkflowFileIngressPort : IWorkflowFileIngressPort
    {
        public List<WorkflowFileIngressRequest> Requests { get; } = [];

        public ValueTask<WorkflowFileIngressResult> IngestAsync(
            WorkflowFileIngressRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(new WorkflowFileIngressResult(new AppWorkflowFileRef
            {
                FileId = "wf-file-1",
                ArtifactId = "workflow-file://wf-file-1",
                SourceKind = request.SourceKind,
                SourceMessageId = request.SourceMessageId,
                SourceResourceKey = request.SourceResourceKey,
                FileName = request.FileName,
                MediaType = request.MediaType,
                SizeBytes = request.Content.Length,
                Sha256 = "sha256",
                CreatedAtUnixMs = 1,
                ExpiresAtUnixMs = 2,
                OwnerRunId = request.OwnerRunId,
                OwnerScopeId = request.OwnerScopeId,
            }));
        }
    }

    private sealed class RecordingConnectedServiceResourceFetchAdapter(
        WorkflowConnectedServiceResourceFetchRoute route) : IWorkflowConnectedServiceResourceFetchAdapter
    {
        public IReadOnlyList<WorkflowConnectedServiceResourceFetchRoute> Routes { get; } = [route];

        public List<WorkflowConnectedServiceResourceFetchRequest> Requests { get; } = [];

        public WorkflowConnectedServiceResourceFetchResult Result { get; init; } =
            new(true, Encoding.UTF8.GetBytes("resource"));

        public ValueTask<WorkflowConnectedServiceResourceFetchResult> FetchAsync(
            WorkflowConnectedServiceResourceFetchRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(Result);
        }
    }
}
