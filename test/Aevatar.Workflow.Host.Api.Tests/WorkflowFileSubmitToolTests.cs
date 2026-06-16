using System.Text;
using System.Text.Json;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Aevatar.Workflow.Infrastructure.Runs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProtoWorkflowCallerCredential = Aevatar.Workflow.Abstractions.WorkflowCallerCredential;
using AppWorkflowCallerCredential = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential;
using AppWorkflowFileRef = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowFileRef;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowFileSubmitToolTests
{
    [Fact]
    public void AddWorkflowInfrastructure_ShouldRegisterCanonicalFileSubmitToolSource()
    {
        var services = new ServiceCollection();

        services.AddWorkflowInfrastructure();

        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(IWorkflowToolSource) &&
            x.ImplementationType == typeof(WorkflowFileSubmitToolSource));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldValidateArtifactAndReturnTypedAdapterOutputOnly()
    {
        var descriptor = BuildFileRef(sizeBytes: 12);
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"));
        var adapter = new RecordingFileSubmitAdapter(SubmitTarget)
        {
            Result = new WorkflowConnectedServiceFileSubmitResult(
                Succeeded: true,
                OutputCode: "tok_123",
                Code: 0),
        };
        var tool = await GetSubmitToolAsync([adapter], artifactPort);

        var result = await tool.ExecuteAsync(NewSubmitRequest("""
        {
          "target": "acme_file_token",
          "folder": "reports",
          "file_ref": {
            "file_id": "file-1",
            "artifact_id": "artifact-1",
            "file_name": "report.txt",
            "media_type": "text/plain",
            "size_bytes": 12,
            "sha256": "sha256-value",
            "owner_run_id": "run-1",
            "owner_scope_id": "scope-1"
          }
        }
        """));

        adapter.Requests.Should().ContainSingle();
        var submitRequest = adapter.Requests[0];
        submitRequest.Target.Target.Should().Be("acme_file_token");
        submitRequest.CallerCredential.BearerToken.Should().Be("token-123");
        submitRequest.FileName.Should().Be("report.txt");
        submitRequest.MediaType.Should().Be("text/plain");
        submitRequest.SizeBytes.Should().Be(12);
        submitRequest.Arguments.Should().ContainKey("folder").WhoseValue.Should().Be("reports");
        submitRequest.UploadedBytes.Should().Equal(Encoding.UTF8.GetBytes("upload bytes"));

        using var document = JsonDocument.Parse(result.ResultJson);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("provider").GetString().Should().Be("acme");
        root.GetProperty("target").GetString().Should().Be("acme_file_token");
        root.GetProperty("output_field").GetString().Should().Be("file_token");
        root.GetProperty("file_token").GetString().Should().Be("tok_123");
        root.ToString().Should().NotContain("upload bytes");
        root.ToString().Contains("base64", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Theory]
    [InlineData("service_slug")]
    [InlineData("path")]
    [InlineData("method")]
    [InlineData("file_field_name")]
    [InlineData("headers")]
    [InlineData("body")]
    public async Task ExecuteAsync_ShouldRejectEndpointOverrideArgumentsBeforeArtifactOpen(string reservedPropertyName)
    {
        var descriptor = BuildFileRef(sizeBytes: 12);
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"));
        var adapter = new RecordingFileSubmitAdapter(SubmitTarget);
        var tool = await GetSubmitToolAsync([adapter], artifactPort);

        var result = await tool.ExecuteAsync(NewSubmitRequest($$"""
        {
          "target": "acme_file_token",
          "folder": "reports",
          "{{reservedPropertyName}}": "attempted override",
          "file_ref": {
            "file_id": "file-1",
            "artifact_id": "artifact-1",
            "file_name": "report.txt",
            "media_type": "text/plain",
            "size_bytes": 12,
            "owner_run_id": "run-1",
            "owner_scope_id": "scope-1"
          }
        }
        """));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString().Should().Be("reserved_argument");
        artifactPort.OpenCount.Should().Be(0);
        adapter.Requests.Should().BeEmpty();
    }

    private static readonly WorkflowConnectedServiceFileSubmitTarget SubmitTarget = new(
        Target: "acme_file_token",
        Provider: "acme",
        OutputField: "file_token",
        MaxFileBytes: 100,
        AllowedMediaTypes: new HashSet<string>(StringComparer.Ordinal) { "text/plain" },
        Arguments: new Dictionary<string, WorkflowConnectedServiceFileSubmitArgumentPolicy>(StringComparer.Ordinal)
        {
            ["folder"] = new(
                Name: "folder",
                Required: true,
                AllowedValues: new HashSet<string>(StringComparer.Ordinal) { "reports" }),
        });

    private static async Task<IWorkflowTool> GetSubmitToolAsync(
        IReadOnlyList<IWorkflowConnectedServiceFileSubmitAdapter> adapters,
        IWorkflowFileArtifactReadPort artifactPort)
    {
        var source = new WorkflowFileSubmitToolSource(
            adapters,
            artifactPort,
            Options.Create(new WorkflowConnectedServiceFileSubmitOptions()));
        var tools = await source.GetToolsAsync();
        return tools.Should().ContainSingle(x => x.Name == "workflow_file_submit").Subject;
    }

    private static WorkflowToolExecutionRequest NewSubmitRequest(string argumentsJson, string? bearerToken = "token-123") =>
        new(
            ArgumentsJson: argumentsJson,
            RunId: "run-1",
            StepId: "step-1",
            ExecutionId: "exec-1",
            CallId: "call-1",
            ScopeId: "scope-1",
            CallerCredential: new ProtoWorkflowCallerCredential
            {
                BearerToken = bearerToken ?? string.Empty,
            });

    private static AppWorkflowFileRef BuildFileRef(long sizeBytes) =>
        new()
        {
            FileId = "file-1",
            ArtifactId = "artifact-1",
            FileName = "report.txt",
            MediaType = "text/plain",
            SizeBytes = sizeBytes,
            Sha256 = "sha256-value",
            OwnerRunId = "run-1",
            OwnerScopeId = "scope-1",
        };

    private sealed class RecordingFileSubmitAdapter(
        WorkflowConnectedServiceFileSubmitTarget target) : IWorkflowConnectedServiceFileSubmitAdapter
    {
        public string Provider => target.Provider;

        public IReadOnlyList<WorkflowConnectedServiceFileSubmitTarget> Targets { get; } = [target];

        public List<RecordedSubmitRequest> Requests { get; } = [];

        public WorkflowConnectedServiceFileSubmitResult Result { get; init; } =
            new(Succeeded: true, OutputCode: "tok_default");

        public async ValueTask<WorkflowConnectedServiceFileSubmitResult> SubmitAsync(
            WorkflowConnectedServiceFileSubmitRequest request,
            CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await request.Content.CopyToAsync(buffer, cancellationToken);
            Requests.Add(new RecordedSubmitRequest(
                request.Target,
                request.FileRef,
                request.FileName,
                request.MediaType,
                request.SizeBytes,
                request.CallerCredential,
                request.Arguments,
                buffer.ToArray()));
            return Result;
        }
    }

    private sealed record RecordedSubmitRequest(
        WorkflowConnectedServiceFileSubmitTarget Target,
        AppWorkflowFileRef FileRef,
        string FileName,
        string MediaType,
        long SizeBytes,
        AppWorkflowCallerCredential CallerCredential,
        IReadOnlyDictionary<string, string> Arguments,
        byte[] UploadedBytes);

    private sealed class RecordingWorkflowFileArtifactReadPort(
        AppWorkflowFileRef descriptor,
        byte[] content) : IWorkflowFileArtifactReadPort
    {
        public int OpenCount { get; private set; }

        public ValueTask<AppWorkflowFileRef> DescribeAsync(
            AppWorkflowFileRef fileRef,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(descriptor);

        public ValueTask<WorkflowFileArtifactContent> OpenReadAsync(
            AppWorkflowFileRef fileRef,
            CancellationToken cancellationToken = default)
        {
            OpenCount++;
            return ValueTask.FromResult(new WorkflowFileArtifactContent(
                descriptor,
                new MemoryStream(content, writable: false)));
        }
    }
}
