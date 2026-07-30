using System.Text;
using System.Text.Json;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Aevatar.Workflow.Infrastructure.Runs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ProtoWorkflowCallerCredential = Aevatar.Workflow.Abstractions.WorkflowCallerCredential;
using AppWorkflowCallerCredential = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential;
using AppFileArtifactRef = Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactRef;

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
        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(IWorkflowFileMultipartUploadPolicyResolver));
        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(IWorkflowFileMultipartUploadPort));
    }

    [Fact]
    public async Task GetToolsAsync_ShouldExposeWorkflowFileSubmitWithoutHostTargets()
    {
        var descriptor = BuildFileRef(sizeBytes: 12);
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"));
        var source = new WorkflowFileSubmitToolSource(
            artifactPort,
            new RecordingMultipartUploadPolicyResolver
            {
                Resolution = WorkflowFileMultipartUploadPolicyResolution.Denied(
                    "destination_not_allowed",
                    "not allowed"),
            },
            new RecordingMultipartUploadPort());

        var tools = await source.GetToolsAsync();

        tools.Should().ContainSingle(tool => tool.Name == "workflow_file_submit");
        artifactPort.OpenCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFailClosedWhenPolicyResolverDeniesDestination()
    {
        var descriptor = BuildFileRef(sizeBytes: 12);
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"));
        var resolver = new RecordingMultipartUploadPolicyResolver
        {
            Resolution = WorkflowFileMultipartUploadPolicyResolution.Denied(
                "destination_not_allowed",
                "workflow_file_submit destination is not allowed by the multipart upload policy."),
        };
        var uploadPort = new RecordingMultipartUploadPort();
        var tool = await GetSubmitToolAsync(artifactPort, resolver, uploadPort);

        var result = await tool.ExecuteAsync(NewSubmitRequest(NewSubmitArguments()));

        using var document = JsonDocument.Parse(result.ResultJson);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("error").GetString().Should().Be("destination_not_allowed");
        result.Failure.Should().NotBeNull();
        result.Failure!.ErrorCode.Should().Be("destination_not_allowed");
        result.Failure.ErrorMessage.Should().Contain("not allowed");
        root.GetProperty("destination").GetProperty("slug").GetString().Should().Be("api-storage");
        artifactPort.DescribeCount.Should().Be(1);
        artifactPort.OpenCount.Should().Be(0);
        resolver.Candidates.Should().ContainSingle();
        uploadPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFailClosedWhenPolicyResolverIsUnavailable()
    {
        var descriptor = BuildFileRef(sizeBytes: 12);
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"));
        var resolver = new RecordingMultipartUploadPolicyResolver
        {
            Exception = new InvalidOperationException("discovery unavailable"),
        };
        var uploadPort = new RecordingMultipartUploadPort();
        var tool = await GetSubmitToolAsync(artifactPort, resolver, uploadPort);

        var result = await tool.ExecuteAsync(NewSubmitRequest(NewSubmitArguments()));

        using var document = JsonDocument.Parse(result.ResultJson);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("error").GetString().Should().Be("policy_unavailable");
        root.GetProperty("detail").GetString().Should().Be("workflow_file_submit multipart upload policy is unavailable.");
        artifactPort.OpenCount.Should().Be(0);
        uploadPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldUploadCanonicalPolicyRequestAndReturnFixedSanitizedSchema()
    {
        var descriptor = BuildFileRef(
            sizeBytes: 12,
            mediaType: "text/plain",
            fileName: "descriptor.txt",
            sha256: "descriptor-sha");
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"));
        var resolver = new RecordingMultipartUploadPolicyResolver
        {
            Resolution = WorkflowFileMultipartUploadPolicyResolution.Allowed(new WorkflowFileMultipartUploadPolicy(
                ServiceSlug: "canonical-storage",
                Path: "/canonical/upload",
                Method: "PUT",
                FileFieldName: "document",
                FormFields: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["folder"] = "reports",
                },
                OutputKind: "external_resource_id",
                OutputSelector: "data.document_id",
                MaxFileBytes: 100)),
        };
        var uploadPort = new RecordingMultipartUploadPort
        {
            Result = WorkflowFileMultipartUploadResult.Success(
                "doc_123",
                providerCode: 0,
                httpStatus: 200),
        };
        var tool = await GetSubmitToolAsync(artifactPort, resolver, uploadPort);

        var result = await tool.ExecuteAsync(NewSubmitRequest(NewSubmitArguments()));

        resolver.Candidates.Should().ContainSingle();
        resolver.Candidates[0].ServiceSlug.Should().Be("api-storage");
        resolver.Candidates[0].Path.Should().Be("/files/upload");
        resolver.Candidates[0].Method.Should().Be("POST");
        resolver.Candidates[0].FileFieldName.Should().Be("file");
        resolver.Candidates[0].FormFields.Should().ContainKey("folder").WhoseValue.Should().Be("reports");
        resolver.Candidates[0].OutputKind.Should().Be("ignored_kind");
        resolver.Candidates[0].OutputSelector.Should().Be("data.ignored");

        uploadPort.Requests.Should().ContainSingle();
        var uploadRequest = uploadPort.Requests[0];
        uploadRequest.CallerCredential.BearerToken.Should().Be("token-123");
        uploadRequest.ServiceSlug.Should().Be("canonical-storage");
        uploadRequest.Path.Should().Be("/canonical/upload");
        uploadRequest.Method.Should().Be("PUT");
        uploadRequest.FileFieldName.Should().Be("document");
        uploadRequest.FormFields.Should().ContainKey("folder").WhoseValue.Should().Be("reports");
        uploadRequest.OutputSelector.Should().Be("data.document_id");
        uploadRequest.FileName.Should().Be("descriptor.txt");
        uploadRequest.MediaType.Should().Be("text/plain");
        uploadRequest.SizeBytes.Should().Be(12);
        uploadRequest.Sha256.Should().Be("descriptor-sha");
        uploadRequest.UploadedBytes.Should().Equal(Encoding.UTF8.GetBytes("upload bytes"));

        using var document = JsonDocument.Parse(result.ResultJson);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("detail").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("output_code").GetString().Should().Be("doc_123");
        root.GetProperty("output_kind").GetString().Should().Be("external_resource_id");
        root.GetProperty("http_status").GetInt32().Should().Be(200);
        root.GetProperty("provider_code").GetInt32().Should().Be(0);
        root.GetProperty("destination").GetProperty("slug").GetString().Should().Be("canonical-storage");
        root.GetProperty("destination").GetProperty("path").GetString().Should().Be("/canonical/upload");
        root.GetProperty("destination").GetProperty("method").GetString().Should().Be("PUT");
        root.GetProperty("file").GetProperty("file_name").GetString().Should().Be("descriptor.txt");
        root.GetProperty("file").GetProperty("media_type").GetString().Should().Be("text/plain");
        root.GetProperty("file").GetProperty("size_bytes").GetInt64().Should().Be(12);
        root.GetProperty("file").GetProperty("sha256").GetString().Should().Be("descriptor-sha");
        root.TryGetProperty("file_token", out _).Should().BeFalse();
        root.TryGetProperty("file_code", out _).Should().BeFalse();
        root.TryGetProperty("body", out _).Should().BeFalse();
        root.TryGetProperty("data_base64", out _).Should().BeFalse();
        result.ResultJson.Should().NotContain("upload bytes");
        result.ResultJson.Should().NotContain("raw");
        result.ResultJson.Contains("base64", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Theory]
    [InlineData("file_token", "provider_file_token", "data.file_token", "tok_123")]
    [InlineData("file_code", "provider_file_code", "data.file_code", "code_123")]
    public async Task ExecuteAsync_ShouldNotReturnDynamicProviderAliases(
        string providerField,
        string outputKind,
        string outputSelector,
        string outputCode)
    {
        var descriptor = BuildFileRef(sizeBytes: 12);
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"));
        var resolver = new RecordingMultipartUploadPolicyResolver
        {
            Resolution = WorkflowFileMultipartUploadPolicyResolution.Allowed(new WorkflowFileMultipartUploadPolicy(
                ServiceSlug: "api-lark-bot",
                Path: "/open-apis/example/upload",
                Method: "POST",
                FileFieldName: "file",
                FormFields: new Dictionary<string, string>(StringComparer.Ordinal),
                OutputKind: outputKind,
                OutputSelector: outputSelector,
                MaxFileBytes: 100)),
        };
        var uploadPort = new RecordingMultipartUploadPort
        {
            Result = WorkflowFileMultipartUploadResult.Success(
                outputCode,
                providerCode: 0),
        };
        var tool = await GetSubmitToolAsync(artifactPort, resolver, uploadPort);

        var result = await tool.ExecuteAsync(NewSubmitRequest(NewSubmitArguments()));

        using var document = JsonDocument.Parse(result.ResultJson);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("output_code").GetString().Should().Be(outputCode);
        root.GetProperty("output_kind").GetString().Should().Be(outputKind);
        root.TryGetProperty(providerField, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("\"path\": \"https://storage.example.test/files/upload\",", "invalid_destination")]
    [InlineData("\"path\": \"//storage.example.test/files/upload\",", "invalid_destination")]
    [InlineData("\"path\": \"data:text/plain;base64,AAAA\",", "invalid_destination")]
    [InlineData("\"path\": \"javascript:alert(1)\",", "invalid_destination")]
    [InlineData("\"path\": \".\",", "invalid_destination")]
    [InlineData("\"path\": \"..\",", "invalid_destination")]
    [InlineData("\"path\": \"./files/upload\",", "invalid_destination")]
    [InlineData("\"path\": \"files/../upload\",", "invalid_destination")]
    [InlineData("\"path\": \"files/./upload\",", "invalid_destination")]
    [InlineData("\"method\": \"GET\",", "unsupported_method")]
    [InlineData("\"headers\": { \"X-Test\": \"value\" },", "invalid_arguments")]
    [InlineData("\"body\": \"raw\",", "invalid_arguments")]
    [InlineData("\"bytes\": \"AAAA\",", "invalid_arguments")]
    [InlineData("\"base64\": \"AAAA\",", "invalid_arguments")]
    [InlineData("\"data_uri\": \"data:text/plain;base64,AAAA\",", "invalid_arguments")]
    public async Task ExecuteAsync_ShouldRejectForbiddenCandidateArgumentsBeforePolicy(
        string overrideJson,
        string expectedError)
    {
        var descriptor = BuildFileRef(sizeBytes: 12);
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"));
        var resolver = new RecordingMultipartUploadPolicyResolver();
        var uploadPort = new RecordingMultipartUploadPort();
        var tool = await GetSubmitToolAsync(artifactPort, resolver, uploadPort);

        var result = await tool.ExecuteAsync(NewSubmitRequest(NewSubmitArguments(extraTopLevelJson: overrideJson)));

        using var document = JsonDocument.Parse(result.ResultJson);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("error").GetString().Should().Be(expectedError);
        root.GetProperty("destination").ValueKind.Should().Be(JsonValueKind.Null);
        result.ResultJson.Contains("data:text/plain", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        result.ResultJson.Contains("javascript:", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        artifactPort.DescribeCount.Should().Be(0);
        artifactPort.OpenCount.Should().Be(0);
        resolver.Candidates.Should().BeEmpty();
        uploadPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectInvalidFileRefSourceKindBeforeArtifactRead()
    {
        var descriptor = BuildFileRef(sizeBytes: 12);
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"));
        var resolver = new RecordingMultipartUploadPolicyResolver();
        var uploadPort = new RecordingMultipartUploadPort();
        var tool = await GetSubmitToolAsync(artifactPort, resolver, uploadPort);

        var result = await tool.ExecuteAsync(NewSubmitRequest(NewSubmitArguments("""
          "source_kind": "not-a-source-kind",
        """)));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString().Should().Be("invalid_file_ref");
        artifactPort.DescribeCount.Should().Be(0);
        artifactPort.OpenCount.Should().Be(0);
        resolver.Candidates.Should().BeEmpty();
        uploadPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectOutputSelectorWithEmptySegmentBeforeArtifactRead()
    {
        var descriptor = BuildFileRef(sizeBytes: 12);
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"));
        var resolver = new RecordingMultipartUploadPolicyResolver();
        var uploadPort = new RecordingMultipartUploadPort();
        var tool = await GetSubmitToolAsync(artifactPort, resolver, uploadPort);

        var result = await tool.ExecuteAsync(NewSubmitRequest(NewSubmitArguments(
            outputSelector: "data..ignored")));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString().Should().Be("invalid_destination");
        artifactPort.DescribeCount.Should().Be(0);
        artifactPort.OpenCount.Should().Be(0);
        resolver.Candidates.Should().BeEmpty();
        uploadPort.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("body")]
    [InlineData("bytes")]
    [InlineData("raw_body")]
    [InlineData("base64")]
    [InlineData("data_base64")]
    [InlineData("data_uri")]
    [InlineData("raw")]
    [InlineData("rawBody")]
    [InlineData("dataUri")]
    [InlineData("dataBase64")]
    [InlineData("data.bytes")]
    [InlineData("data.raw_body")]
    [InlineData("data.raw")]
    [InlineData("data.rawBody")]
    [InlineData("data.dataUri")]
    [InlineData("data.dataBase64")]
    public async Task ExecuteAsync_ShouldRejectUnsafeCandidateOutputSelectorBeforeArtifactRead(string outputSelector)
    {
        var descriptor = BuildFileRef(sizeBytes: 12);
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"));
        var resolver = new RecordingMultipartUploadPolicyResolver();
        var uploadPort = new RecordingMultipartUploadPort();
        var tool = await GetSubmitToolAsync(artifactPort, resolver, uploadPort);

        var result = await tool.ExecuteAsync(NewSubmitRequest(NewSubmitArguments(
            outputSelector: outputSelector)));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString().Should().Be("invalid_destination");
        artifactPort.DescribeCount.Should().Be(0);
        artifactPort.OpenCount.Should().Be(0);
        resolver.Candidates.Should().BeEmpty();
        uploadPort.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("body")]
    [InlineData("bytes")]
    [InlineData("raw_body")]
    [InlineData("base64")]
    [InlineData("data_base64")]
    [InlineData("data_uri")]
    [InlineData("raw")]
    [InlineData("rawBody")]
    [InlineData("dataUri")]
    [InlineData("dataBase64")]
    [InlineData("data.bytes")]
    [InlineData("data.raw_body")]
    [InlineData("data.raw")]
    [InlineData("data.rawBody")]
    [InlineData("data.dataUri")]
    [InlineData("data.dataBase64")]
    public async Task ExecuteAsync_ShouldRejectUnsafeResolvedPolicyOutputSelectorBeforeUpload(string outputSelector)
    {
        var descriptor = BuildFileRef(sizeBytes: 12);
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"));
        var resolver = new RecordingMultipartUploadPolicyResolver
        {
            Resolution = WorkflowFileMultipartUploadPolicyResolution.Allowed(new WorkflowFileMultipartUploadPolicy(
                ServiceSlug: "canonical-storage",
                Path: "/canonical/upload",
                Method: "POST",
                FileFieldName: "file",
                FormFields: new Dictionary<string, string>(StringComparer.Ordinal),
                OutputKind: "external_resource_id",
                OutputSelector: outputSelector,
                MaxFileBytes: 100)),
        };
        var uploadPort = new RecordingMultipartUploadPort();
        var tool = await GetSubmitToolAsync(artifactPort, resolver, uploadPort);

        var result = await tool.ExecuteAsync(NewSubmitRequest(NewSubmitArguments()));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString().Should().Be("destination_not_allowed");
        artifactPort.DescribeCount.Should().Be(1);
        artifactPort.OpenCount.Should().Be(0);
        resolver.Candidates.Should().ContainSingle();
        uploadPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectPublicFileRefFactsBeforeArtifactRead()
    {
        var descriptor = BuildFileRef(sizeBytes: 12);
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"));
        var resolver = new RecordingMultipartUploadPolicyResolver();
        var uploadPort = new RecordingMultipartUploadPort();
        var tool = await GetSubmitToolAsync(artifactPort, resolver, uploadPort);

        var result = await tool.ExecuteAsync(NewSubmitRequest(NewSubmitArguments("""
          "file_name": "argument.txt",
          "media_type": "text/plain",
          "size_bytes": 12,
          "sha256": "argument-sha",
        """)));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString().Should().Be("invalid_file_ref");
        artifactPort.DescribeCount.Should().Be(0);
        artifactPort.OpenCount.Should().Be(0);
        resolver.Candidates.Should().BeEmpty();
        uploadPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldEnforceRequestedMaxFileBytesAfterPolicyResolution()
    {
        var descriptor = BuildFileRef(sizeBytes: 80, mediaType: "text/plain");
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"));
        var resolver = new RecordingMultipartUploadPolicyResolver
        {
            Resolution = WorkflowFileMultipartUploadPolicyResolution.Allowed(new WorkflowFileMultipartUploadPolicy(
                ServiceSlug: "canonical-storage",
                Path: "/canonical/upload",
                Method: "POST",
                FileFieldName: "file",
                FormFields: new Dictionary<string, string>(StringComparer.Ordinal),
                OutputKind: "external_resource_id",
                OutputSelector: "data.document_id",
                MaxFileBytes: 100)),
        };
        var uploadPort = new RecordingMultipartUploadPort();
        var tool = await GetSubmitToolAsync(artifactPort, resolver, uploadPort);

        var result = await tool.ExecuteAsync(NewSubmitRequest(NewSubmitArguments(
            extraTopLevelJson: """
          "max_file_bytes": 64,
""")));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString().Should().Be("file_too_large");
        artifactPort.DescribeCount.Should().Be(1);
        artifactPort.OpenCount.Should().Be(0);
        resolver.Candidates.Should().ContainSingle()
            .Which.MaxFileBytes.Should().Be(64);
        uploadPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldEnforceResolvedPolicyBeforeOpeningArtifact()
    {
        var descriptor = BuildFileRef(101, "text/plain");
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"));
        var resolver = new RecordingMultipartUploadPolicyResolver
        {
            Resolution = WorkflowFileMultipartUploadPolicyResolution.Allowed(new WorkflowFileMultipartUploadPolicy(
                ServiceSlug: "canonical-storage",
                Path: "/canonical/upload",
                Method: "POST",
                FileFieldName: "file",
                FormFields: new Dictionary<string, string>(StringComparer.Ordinal),
                OutputKind: "external_resource_id",
                OutputSelector: "data.document_id",
                MaxFileBytes: 100)),
        };
        var uploadPort = new RecordingMultipartUploadPort();
        var tool = await GetSubmitToolAsync(artifactPort, resolver, uploadPort);

        var result = await tool.ExecuteAsync(NewSubmitRequest(NewSubmitArguments()));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString().Should().Be("file_too_large");
        artifactPort.OpenCount.Should().Be(0);
        uploadPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotApplyDestinationMediaTypePolicy()
    {
        var descriptor = BuildFileRef(12, "application/pdf");
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"));
        var resolver = new RecordingMultipartUploadPolicyResolver
        {
            Resolution = WorkflowFileMultipartUploadPolicyResolution.Allowed(new WorkflowFileMultipartUploadPolicy(
                ServiceSlug: "canonical-storage",
                Path: "/canonical/upload",
                Method: "POST",
                FileFieldName: "file",
                FormFields: new Dictionary<string, string>(StringComparer.Ordinal),
                OutputKind: "external_resource_id",
                OutputSelector: "data.document_id",
                MaxFileBytes: 100)),
        };
        var uploadPort = new RecordingMultipartUploadPort
        {
            Result = WorkflowFileMultipartUploadResult.Success("doc_pdf"),
        };
        var tool = await GetSubmitToolAsync(artifactPort, resolver, uploadPort);

        var result = await tool.ExecuteAsync(NewSubmitRequest(NewSubmitArguments()));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        uploadPort.Requests.Should().ContainSingle()
            .Which.MediaType.Should().Be("application/pdf");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnArtifactUnavailableWhenDescriptorCannotBeRead()
    {
        var descriptor = BuildFileRef(sizeBytes: 12);
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"))
        {
            DescribeException = new FileNotFoundException("missing descriptor"),
        };
        var resolver = new RecordingMultipartUploadPolicyResolver();
        var uploadPort = new RecordingMultipartUploadPort();
        var tool = await GetSubmitToolAsync(artifactPort, resolver, uploadPort);

        var result = await tool.ExecuteAsync(NewSubmitRequest(NewSubmitArguments()));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString().Should().Be("artifact_unavailable");
        artifactPort.DescribeCount.Should().Be(1);
        artifactPort.OpenCount.Should().Be(0);
        resolver.Candidates.Should().BeEmpty();
        uploadPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectDescriptorOwnerMismatchBeforePolicyResolution()
    {
        var descriptor = BuildFileRef(sizeBytes: 12, ownerRunId: "other-run");
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"));
        var resolver = new RecordingMultipartUploadPolicyResolver();
        var uploadPort = new RecordingMultipartUploadPort();
        var tool = await GetSubmitToolAsync(artifactPort, resolver, uploadPort);

        var result = await tool.ExecuteAsync(NewSubmitRequest(NewSubmitArguments()));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString().Should().Be("invalid_file_scope");
        artifactPort.DescribeCount.Should().Be(1);
        artifactPort.OpenCount.Should().Be(0);
        resolver.Candidates.Should().BeEmpty();
        uploadPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnArtifactUnavailableWhenContentCannotBeOpened()
    {
        var descriptor = BuildFileRef(sizeBytes: 12);
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"))
        {
            OpenException = new IOException("content unavailable"),
        };
        var resolver = new RecordingMultipartUploadPolicyResolver
        {
            Resolution = AllowedPolicy(),
        };
        var uploadPort = new RecordingMultipartUploadPort();
        var tool = await GetSubmitToolAsync(artifactPort, resolver, uploadPort);

        var result = await tool.ExecuteAsync(NewSubmitRequest(NewSubmitArguments()));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString().Should().Be("artifact_unavailable");
        artifactPort.DescribeCount.Should().Be(1);
        artifactPort.OpenCount.Should().Be(1);
        uploadPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectOpenedArtifactOwnerMismatchBeforeUpload()
    {
        var descriptor = BuildFileRef(sizeBytes: 12);
        var openedDescriptor = BuildFileRef(sizeBytes: 12, ownerRunId: "other-run");
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(
            descriptor,
            Encoding.UTF8.GetBytes("upload bytes"))
        {
            OpenDescriptor = openedDescriptor,
        };
        var resolver = new RecordingMultipartUploadPolicyResolver
        {
            Resolution = AllowedPolicy(),
        };
        var uploadPort = new RecordingMultipartUploadPort();
        var tool = await GetSubmitToolAsync(artifactPort, resolver, uploadPort);

        var result = await tool.ExecuteAsync(NewSubmitRequest(NewSubmitArguments()));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString().Should().Be("invalid_file_scope");
        artifactPort.OpenCount.Should().Be(1);
        uploadPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnProviderCallFailedWhenUploadThrows()
    {
        var descriptor = BuildFileRef(sizeBytes: 12);
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"));
        var resolver = new RecordingMultipartUploadPolicyResolver
        {
            Resolution = AllowedPolicy(),
        };
        var uploadPort = new RecordingMultipartUploadPort
        {
            Exception = new HttpRequestException("network failed"),
        };
        var tool = await GetSubmitToolAsync(artifactPort, resolver, uploadPort);

        var result = await tool.ExecuteAsync(NewSubmitRequest(NewSubmitArguments()));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString().Should().Be("provider_call_failed");
        uploadPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnUploadFailureWithoutOpeningProviderBody()
    {
        var descriptor = BuildFileRef(sizeBytes: 12);
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"));
        var resolver = new RecordingMultipartUploadPolicyResolver
        {
            Resolution = AllowedPolicy(),
        };
        var uploadPort = new RecordingMultipartUploadPort
        {
            Result = WorkflowFileMultipartUploadResult.Failure(
                "provider_error",
                "provider_code=403",
                providerCode: 403,
                httpStatus: 200),
        };
        var tool = await GetSubmitToolAsync(artifactPort, resolver, uploadPort);

        var result = await tool.ExecuteAsync(NewSubmitRequest(NewSubmitArguments()));

        using var document = JsonDocument.Parse(result.ResultJson);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("error").GetString().Should().Be("provider_error");
        root.GetProperty("provider_code").GetInt32().Should().Be(403);
        root.GetProperty("http_status").GetInt32().Should().Be(200);
        uploadPort.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFailClosedWhenSanitizedUploadSuccessOmitsOutputCode()
    {
        var descriptor = BuildFileRef(sizeBytes: 12);
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"));
        var resolver = new RecordingMultipartUploadPolicyResolver
        {
            Resolution = AllowedPolicy(),
        };
        var uploadPort = new RecordingMultipartUploadPort
        {
            Result = new WorkflowFileMultipartUploadResult(
                Succeeded: true,
                OutputCode: null,
                Error: null,
                Detail: null,
                ProviderCode: 0,
                HttpStatus: 200),
        };
        var tool = await GetSubmitToolAsync(artifactPort, resolver, uploadPort);

        var result = await tool.ExecuteAsync(NewSubmitRequest(NewSubmitArguments()));

        using var document = JsonDocument.Parse(result.ResultJson);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("error").GetString().Should().Be("missing_output_code");
        root.GetProperty("provider_code").GetInt32().Should().Be(0);
        root.GetProperty("http_status").GetInt32().Should().Be(200);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectForeignFileRefBeforePolicy()
    {
        var descriptor = BuildFileRef(sizeBytes: 12);
        var artifactPort = new RecordingWorkflowFileArtifactReadPort(descriptor, Encoding.UTF8.GetBytes("upload bytes"));
        var resolver = new RecordingMultipartUploadPolicyResolver();
        var uploadPort = new RecordingMultipartUploadPort();
        var tool = await GetSubmitToolAsync(artifactPort, resolver, uploadPort);

        var result = await tool.ExecuteAsync(NewSubmitRequest(NewSubmitArguments(ownerRunId: "other-run")));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString().Should().Be("invalid_file_scope");
        artifactPort.DescribeCount.Should().Be(0);
        artifactPort.OpenCount.Should().Be(0);
        resolver.Candidates.Should().BeEmpty();
        uploadPort.Requests.Should().BeEmpty();
    }

    private static async Task<IWorkflowTool> GetSubmitToolAsync(
        IFileArtifactReadPort artifactPort,
        IWorkflowFileMultipartUploadPolicyResolver policyResolver,
        IWorkflowFileMultipartUploadPort uploadPort)
    {
        var source = new WorkflowFileSubmitToolSource(artifactPort, policyResolver, uploadPort);
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

    private static string NewSubmitArguments(
        string fileRefFactsJson = "",
        string extraTopLevelJson = "",
        string ownerRunId = "run-1",
        string ownerScopeId = "scope-1",
        string outputSelector = "data.ignored") =>
        $$"""
        {
          "slug": "api-storage",
          "path": "/files/upload",
          "method": "POST",
          "file_field_name": "file",
          {{extraTopLevelJson}}
          "form": {
            "folder": "reports"
          },
          "output": {
            "kind": "ignored_kind",
            "selector": "{{outputSelector}}"
          },
          "file_ref": {
            "file_id": "file-1",
            "artifact_id": "artifact-1",
            {{fileRefFactsJson}}
            "owner_run_id": "{{ownerRunId}}",
            "owner_scope_id": "{{ownerScopeId}}"
          }
        }
        """;

    private static AppFileArtifactRef BuildFileRef(
        long sizeBytes,
        string mediaType = "text/plain",
        string fileName = "report.txt",
        string sha256 = "sha256-value",
        string ownerRunId = "run-1",
        string ownerScopeId = "scope-1") =>
        new()
        {
            FileId = "file-1",
            ArtifactId = "artifact-1",
            SourceKind = Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactSourceKind.ExternalResource,
            FileName = fileName,
            MediaType = mediaType,
            SizeBytes = sizeBytes,
            Sha256 = sha256,
            OwnerRunId = ownerRunId,
            OwnerScopeId = ownerScopeId,
        };

    private static WorkflowFileMultipartUploadPolicyResolution AllowedPolicy() =>
        WorkflowFileMultipartUploadPolicyResolution.Allowed(new WorkflowFileMultipartUploadPolicy(
            ServiceSlug: "canonical-storage",
            Path: "/canonical/upload",
            Method: "POST",
            FileFieldName: "file",
            FormFields: new Dictionary<string, string>(StringComparer.Ordinal),
            OutputKind: "external_resource_id",
            OutputSelector: "data.document_id",
            MaxFileBytes: 100));

    private sealed class RecordingMultipartUploadPolicyResolver : IWorkflowFileMultipartUploadPolicyResolver
    {
        public List<WorkflowFileMultipartUploadCandidate> Candidates { get; } = [];

        public WorkflowFileMultipartUploadPolicyResolution Resolution { get; init; } =
            WorkflowFileMultipartUploadPolicyResolution.Denied("destination_not_allowed", "not allowed");

        public Exception? Exception { get; init; }

        public ValueTask<WorkflowFileMultipartUploadPolicyResolution> ResolveAsync(
            WorkflowFileMultipartUploadCandidate candidate,
            AppFileArtifactRef descriptor,
            WorkflowFileMultipartUploadExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            if (Exception != null)
                throw Exception;

            Candidates.Add(candidate);
            return ValueTask.FromResult(Resolution);
        }
    }

    private sealed class RecordingMultipartUploadPort : IWorkflowFileMultipartUploadPort
    {
        public List<RecordedMultipartUploadRequest> Requests { get; } = [];

        public WorkflowFileMultipartUploadResult Result { get; init; } =
            WorkflowFileMultipartUploadResult.Success("doc_default");

        public Exception? Exception { get; init; }

        public async ValueTask<WorkflowFileMultipartUploadResult> UploadAsync(
            WorkflowFileMultipartUploadRequest request,
            CancellationToken cancellationToken = default)
        {
            if (Exception != null)
                throw Exception;

            using var buffer = new MemoryStream();
            await request.Content.CopyToAsync(buffer, cancellationToken);
            Requests.Add(new RecordedMultipartUploadRequest(
                request.CallerCredential,
                request.ServiceSlug,
                request.Path,
                request.Method,
                request.FileFieldName,
                request.FormFields,
                request.OutputSelector,
                request.FileName,
                request.MediaType,
                request.SizeBytes,
                request.Sha256,
                buffer.ToArray()));
            return Result;
        }
    }

    private sealed record RecordedMultipartUploadRequest(
        AppWorkflowCallerCredential CallerCredential,
        string ServiceSlug,
        string Path,
        string Method,
        string FileFieldName,
        IReadOnlyDictionary<string, string> FormFields,
        string OutputSelector,
        string FileName,
        string MediaType,
        long SizeBytes,
        string? Sha256,
        byte[] UploadedBytes);

    private sealed class RecordingWorkflowFileArtifactReadPort(
        AppFileArtifactRef descriptor,
        byte[] content) : IFileArtifactReadPort
    {
        public int DescribeCount { get; private set; }
        public int OpenCount { get; private set; }
        public Exception? DescribeException { get; init; }
        public Exception? OpenException { get; init; }
        public AppFileArtifactRef? OpenDescriptor { get; init; }

        public ValueTask<AppFileArtifactRef> DescribeAsync(
            AppFileArtifactRef fileRef,
            CancellationToken cancellationToken = default)
        {
            DescribeCount++;
            if (DescribeException != null)
                throw DescribeException;

            return ValueTask.FromResult(descriptor);
        }

        public ValueTask<FileArtifactContent> OpenReadAsync(
            AppFileArtifactRef fileRef,
            CancellationToken cancellationToken = default)
        {
            OpenCount++;
            if (OpenException != null)
                throw OpenException;

            return ValueTask.FromResult(new FileArtifactContent(
                OpenDescriptor ?? descriptor,
                new MemoryStream(content, writable: false)));
        }
    }
}
