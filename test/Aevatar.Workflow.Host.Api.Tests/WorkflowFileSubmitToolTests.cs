using System.Text;
using System.Text.Json;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Aevatar.Workflow.Infrastructure.Runs;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
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
    public void AddWorkflowCapabilityServices_ShouldBindWorkflowFileSubmitTargetsFromStableSection()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(BuildWorkflowFileSubmitTargetConfiguration())
            .Build();

        services.AddWorkflowCapability(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<WorkflowConnectedServiceFileSubmitOptions>>().Value;

        var target = options.Targets.Should().ContainSingle().Subject;
        target.Target.Should().Be("submit_invoice");
        target.Provider.Should().Be("nyxid_connected_service");
        target.OutputField.Should().Be("document_id");
        target.MaxFileBytes.Should().Be(1024);
        target.AllowedMediaTypes.Should().Equal("text/plain");
        target.Arguments.Should().ContainKey("folder");
        target.Arguments["folder"].Required.Should().BeTrue();
        target.Arguments["folder"].AllowedValues.Should().Equal("reports");
        target.Endpoint.Should().NotBeNull();
        target.Endpoint!.ServiceSlug.Should().Be("storage");
        target.Endpoint.Path.Should().Be("files/upload");
        target.Endpoint.Method.Should().Be("POST");
        target.Endpoint.FileFieldName.Should().Be("upload");
        target.Endpoint.Headers.Should().ContainKey("X-Trace").WhoseValue.Should().Be("trace-1");
        target.Endpoint.Body.Should().ContainKey("bucket").WhoseValue.Should().Be("reports");
    }

    [Theory]
    [InlineData("Endpoint:ServiceSlug", "")]
    [InlineData("Endpoint:Path", "")]
    [InlineData("Endpoint:Path", "https://storage.example.test/files/upload")]
    [InlineData("Endpoint:Method", "GET")]
    [InlineData("Endpoint:FileFieldName", "")]
    public void AddWorkflowCapabilityServices_ShouldValidateConfiguredWorkflowFileSubmitEndpointPolicy(
        string setting,
        string value)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configurationValues = BuildWorkflowFileSubmitTargetConfiguration();
        configurationValues[$"WorkflowConnectedServiceFileSubmit:Targets:0:{setting}"] = value;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();

        services.AddWorkflowCapability(configuration);

        using var provider = services.BuildServiceProvider();
        var act = () => provider.GetRequiredService<IOptions<WorkflowConnectedServiceFileSubmitOptions>>().Value;

        act.Should()
            .Throw<OptionsValidationException>()
            .WithMessage("*WorkflowConnectedServiceFileSubmit:Targets[0].Endpoint*");
    }

    [Fact]
    public async Task GetToolsAsync_ShouldReturnNoToolWhenNoFileSubmitTargetsExist()
    {
        var descriptor = BuildFileRef(sizeBytes: 12);
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"));
        var source = new WorkflowFileSubmitToolSource(
            [],
            artifactPort,
            Options.Create(new WorkflowConnectedServiceFileSubmitOptions()));

        var tools = await source.GetToolsAsync();

        tools.Should().BeEmpty();
        artifactPort.OpenCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSubmitHostConfiguredTargetThroughMatchingProvider()
    {
        var descriptor = BuildFileRef(sizeBytes: 12);
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"));
        var adapter = new RecordingFileSubmitAdapter("nyxid_connected_service")
        {
            Result = new WorkflowConnectedServiceFileSubmitResult(
                Succeeded: true,
                OutputCode: "doc_123",
                Code: 0),
        };
        var tool = await GetSubmitToolAsync(
            [adapter],
            artifactPort,
            BuildConfiguredOptions(ConfiguredNyxIdTarget));

        var result = await tool.ExecuteAsync(NewSubmitRequest(NewConfiguredSubmitArguments(
            folder: "reports",
            mediaType: "text/plain",
            sizeBytes: 12)));

        adapter.Requests.Should().ContainSingle();
        var submitRequest = adapter.Requests[0];
        submitRequest.Target.Should().BeEquivalentTo(ConfiguredNyxIdTarget);
        submitRequest.Target.Endpoint.Should().NotBeNull();
        submitRequest.Target.Endpoint!.ServiceSlug.Should().Be("storage");
        submitRequest.Target.Endpoint.Path.Should().Be("files/upload");
        submitRequest.Target.Endpoint.Method.Should().Be("POST");
        submitRequest.Target.Endpoint.FileFieldName.Should().Be("upload");
        submitRequest.Target.Endpoint.Headers.Should().ContainKey("X-Trace").WhoseValue.Should().Be("trace-1");
        submitRequest.Target.Endpoint.Body.Should().ContainKey("bucket").WhoseValue.Should().Be("reports");
        submitRequest.CallerCredential.BearerToken.Should().Be("token-123");
        submitRequest.FileName.Should().Be("report.txt");
        submitRequest.MediaType.Should().Be("text/plain");
        submitRequest.SizeBytes.Should().Be(12);
        submitRequest.Arguments.Should().ContainKey("folder").WhoseValue.Should().Be("reports");
        submitRequest.UploadedBytes.Should().Equal(Encoding.UTF8.GetBytes("upload bytes"));

        using var document = JsonDocument.Parse(result.ResultJson);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("provider").GetString().Should().Be("nyxid_connected_service");
        root.GetProperty("target").GetString().Should().Be("submit_invoice");
        root.GetProperty("output_field").GetString().Should().Be("document_id");
        root.GetProperty("output_code").GetString().Should().Be("doc_123");
        root.TryGetProperty("file_token", out _).Should().BeFalse();
        root.TryGetProperty("file_code", out _).Should().BeFalse();
        root.ToString().Should().NotContain("upload bytes");
        root.ToString().Contains("base64", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Theory]
    [InlineData("sales", "text/plain", 12, "unsupported_argument_value")]
    [InlineData("reports", "application/pdf", 12, "unsupported_media_type")]
    [InlineData("reports", "text/plain", 101, "file_too_large")]
    public async Task ExecuteAsync_ShouldEnforceHostConfiguredTargetPolicyBeforeAdapterDispatch(
        string folder,
        string mediaType,
        long sizeBytes,
        string expectedError)
    {
        var descriptor = BuildFileRef(sizeBytes, mediaType);
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"));
        var adapter = new RecordingFileSubmitAdapter("nyxid_connected_service");
        var tool = await GetSubmitToolAsync(
            [adapter],
            artifactPort,
            BuildConfiguredOptions(ConfiguredNyxIdTarget));

        var result = await tool.ExecuteAsync(NewSubmitRequest(NewConfiguredSubmitArguments(
            folder,
            mediaType,
            sizeBytes)));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString().Should().Be(expectedError);
        artifactPort.OpenCount.Should().Be(0);
        adapter.Requests.Should().BeEmpty();
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
    public async Task ExecuteAsync_ShouldRejectHostConfiguredEndpointOverrideArgumentsBeforeArtifactOpen(
        string reservedPropertyName)
    {
        var descriptor = BuildFileRef(sizeBytes: 12);
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"));
        var adapter = new RecordingFileSubmitAdapter("nyxid_connected_service");
        var tool = await GetSubmitToolAsync(
            [adapter],
            artifactPort,
            BuildConfiguredOptions(ConfiguredNyxIdTarget));

        var result = await tool.ExecuteAsync(NewSubmitRequest($$"""
        {
          "target": "submit_invoice",
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

    private static readonly WorkflowConnectedServiceFileSubmitTarget ConfiguredNyxIdTarget = new(
        Target: "submit_invoice",
        Provider: "nyxid_connected_service",
        OutputField: "document_id",
        MaxFileBytes: 100,
        AllowedMediaTypes: new HashSet<string>(StringComparer.Ordinal) { "text/plain" },
        Arguments: new Dictionary<string, WorkflowConnectedServiceFileSubmitArgumentPolicy>(StringComparer.Ordinal)
        {
            ["folder"] = new(
                Name: "folder",
                Required: true,
                AllowedValues: new HashSet<string>(StringComparer.Ordinal) { "reports" }),
        },
        Endpoint: new WorkflowConnectedServiceFileSubmitEndpoint(
            ServiceSlug: "storage",
            Path: "files/upload",
            Method: "POST",
            FileFieldName: "upload",
            Headers: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["X-Trace"] = "trace-1",
            },
            Body: new Dictionary<string, string>(StringComparer.Ordinal)
            {
            ["bucket"] = "reports",
        }));

    private static Dictionary<string, string?> BuildWorkflowFileSubmitTargetConfiguration() =>
        new()
        {
            ["WorkflowConnectedServiceFileSubmit:Targets:0:Target"] = "submit_invoice",
            ["WorkflowConnectedServiceFileSubmit:Targets:0:Provider"] = "nyxid_connected_service",
            ["WorkflowConnectedServiceFileSubmit:Targets:0:OutputField"] = "document_id",
            ["WorkflowConnectedServiceFileSubmit:Targets:0:MaxFileBytes"] = "1024",
            ["WorkflowConnectedServiceFileSubmit:Targets:0:AllowedMediaTypes:0"] = "text/plain",
            ["WorkflowConnectedServiceFileSubmit:Targets:0:Arguments:folder:Name"] = "folder",
            ["WorkflowConnectedServiceFileSubmit:Targets:0:Arguments:folder:Required"] = "true",
            ["WorkflowConnectedServiceFileSubmit:Targets:0:Arguments:folder:AllowedValues:0"] = "reports",
            ["WorkflowConnectedServiceFileSubmit:Targets:0:Endpoint:ServiceSlug"] = "storage",
            ["WorkflowConnectedServiceFileSubmit:Targets:0:Endpoint:Path"] = "files/upload",
            ["WorkflowConnectedServiceFileSubmit:Targets:0:Endpoint:Method"] = "POST",
            ["WorkflowConnectedServiceFileSubmit:Targets:0:Endpoint:FileFieldName"] = "upload",
            ["WorkflowConnectedServiceFileSubmit:Targets:0:Endpoint:Headers:X-Trace"] = "trace-1",
            ["WorkflowConnectedServiceFileSubmit:Targets:0:Endpoint:Body:bucket"] = "reports",
        };

    private static async Task<IWorkflowTool> GetSubmitToolAsync(
        IReadOnlyList<IWorkflowConnectedServiceFileSubmitAdapter> adapters,
        IWorkflowFileArtifactReadPort artifactPort,
        WorkflowConnectedServiceFileSubmitOptions? options = null)
    {
        var source = new WorkflowFileSubmitToolSource(
            adapters,
            artifactPort,
            Options.Create(options ?? new WorkflowConnectedServiceFileSubmitOptions()));
        var tools = await source.GetToolsAsync();
        return tools.Should().ContainSingle(x => x.Name == "workflow_file_submit").Subject;
    }

    private static WorkflowConnectedServiceFileSubmitOptions BuildConfiguredOptions(
        WorkflowConnectedServiceFileSubmitTarget target)
    {
        var options = new WorkflowConnectedServiceFileSubmitOptions();
        options.Targets.Add(target);
        return options;
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

    private static string NewConfiguredSubmitArguments(
        string folder,
        string mediaType,
        long sizeBytes) =>
        $$"""
        {
          "target": "submit_invoice",
          "folder": "{{folder}}",
          "file_ref": {
            "file_id": "file-1",
            "artifact_id": "artifact-1",
            "file_name": "report.txt",
            "media_type": "{{mediaType}}",
            "size_bytes": {{sizeBytes}},
            "sha256": "sha256-value",
            "owner_run_id": "run-1",
            "owner_scope_id": "scope-1"
          }
        }
        """;

    private static AppWorkflowFileRef BuildFileRef(long sizeBytes, string mediaType = "text/plain") =>
        new()
        {
            FileId = "file-1",
            ArtifactId = "artifact-1",
            FileName = "report.txt",
            MediaType = mediaType,
            SizeBytes = sizeBytes,
            Sha256 = "sha256-value",
            OwnerRunId = "run-1",
            OwnerScopeId = "scope-1",
        };

    private sealed class RecordingFileSubmitAdapter : IWorkflowConnectedServiceFileSubmitAdapter
    {
        public RecordingFileSubmitAdapter(WorkflowConnectedServiceFileSubmitTarget target)
            : this(target.Provider, [target])
        {
        }

        public RecordingFileSubmitAdapter(
            string provider,
            IReadOnlyList<WorkflowConnectedServiceFileSubmitTarget>? targets = null)
        {
            Provider = provider;
            Targets = targets ?? [];
        }

        public string Provider { get; }

        public IReadOnlyList<WorkflowConnectedServiceFileSubmitTarget> Targets { get; }

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
