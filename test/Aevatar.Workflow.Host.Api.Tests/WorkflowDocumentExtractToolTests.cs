using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Aevatar.Workflow.Infrastructure.Runs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using ApplicationWorkflowFileRef = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowFileRef;
using ApplicationWorkflowFileSourceKind = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowFileSourceKind;
using ProtoWorkflowCallerCredential = Aevatar.Workflow.Abstractions.WorkflowCallerCredential;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowDocumentExtractToolTests
{
    [Fact]
    public async Task AddWorkflowInfrastructure_ShouldWireDocumentExtractImageProviderFromLlmFactory()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-di-image-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var imageProvider = new RecordingImageLlmProvider(["factory image text"]);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ILLMProviderFactory>(imageProvider);
            services.Configure<FileSystemWorkflowFileIngressOptions>(options =>
                options.RootDirectory = root);

            services.AddWorkflowInfrastructure();

            using var provider = services.BuildServiceProvider();
            var filePort = provider.GetRequiredService<FileSystemWorkflowFileIngressPort>();
            var result = await filePort.IngestAsync(new WorkflowFileIngressRequest(
                new byte[] { 1, 2, 3 },
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "receipt.png",
                MediaType: "image/png"));
            var tools = new List<IWorkflowTool>();
            foreach (var toolSource in provider.GetServices<IWorkflowToolSource>())
            {
                tools.AddRange(await toolSource.GetToolsAsync());
            }

            var output = await tools.Single(x => x.Name == "document_extract")
                .ExecuteAsync(new WorkflowToolExecutionRequest(
                    BuildDocumentExtractArguments(result.FileRef),
                    "run-1",
                    "extract",
                    "exec-1",
                    "call-1",
                    "scope-1",
                    new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("text").GetString().Should().Be("factory image text");
            imageProvider.Requests.Should().ContainSingle();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    public async Task WorkflowDocumentExtractTool_ShouldExtractImageTextThroughStreamingProvider(string mediaType)
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-image-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var imageBytes = new byte[] { 137, 80, 78, 71, 1, 2, 3, 4 };
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new WorkflowFileIngressRequest(
                imageBytes,
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: mediaType == "image/png" ? "receipt.png" : "receipt.jpg",
                MediaType: mediaType));
            var llmProvider = new RecordingImageLlmProvider(["receipt ", "total 42"]);
            var tool = await GetDocumentExtractToolAsync(port, llmProvider);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildDocumentExtractArguments(result.FileRef),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            var rootElement = document.RootElement;
            rootElement.GetProperty("extraction_kind").GetString().Should().Be("image_text");
            rootElement.GetProperty("media_type").GetString().Should().Be(mediaType);
            rootElement.GetProperty("text").GetString().Should().Be("receipt total 42");
            rootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();
            rootElement.GetProperty("extracted_chars").GetInt32().Should().Be(16);
            rootElement.GetProperty("file").GetProperty("file_id").GetString().Should().Be(result.FileRef.FileId);
            output.ResultJson.Contains("base64", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
            output.ResultJson.Contains(Convert.ToBase64String(imageBytes), StringComparison.Ordinal).Should().BeFalse();

            llmProvider.Requests.Should().ContainSingle();
            var request = llmProvider.Requests.Single();
            request.Messages.Should().HaveCount(2);
            request.Messages[0].Role.Should().Be("system");
            request.Messages[1].Role.Should().Be("user");
            request.Messages[1].ContentParts.Should().ContainSingle(part =>
                part.Kind == ContentPartKind.Image &&
                part.MediaType == mediaType &&
                part.Name == result.FileRef.FileName &&
                part.DataBase64 == Convert.ToBase64String(imageBytes));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldTruncateImageTextToRequestedMaxChars()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-image-truncation-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var imageBytes = new byte[] { 137, 80, 78, 71, 1, 2, 3, 4 };
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new WorkflowFileIngressRequest(
                imageBytes,
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "receipt.png",
                MediaType: "image/png"));
            var llmProvider = new RecordingImageLlmProvider(["receipt total 42"]);
            var tool = await GetDocumentExtractToolAsync(port, llmProvider);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildDocumentExtractArguments(result.FileRef, maxChars: 7),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            var rootElement = document.RootElement;
            rootElement.GetProperty("text").GetString().Should().Be("receipt");
            rootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
            rootElement.GetProperty("extracted_chars").GetInt32().Should().Be(7);
            llmProvider.Requests.Should().ContainSingle();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldReturnUnavailableWhenImageProviderMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-image-unavailable-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new WorkflowFileIngressRequest(
                new byte[] { 1, 2, 3 },
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "receipt.png",
                MediaType: "image/png"));
            var tool = await GetDocumentExtractToolAsync(port);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildDocumentExtractArguments(result.FileRef),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("error").GetString().Should().Be("image_provider_unavailable");
            document.RootElement.GetProperty("detail").GetString()
                .Should().NotContain("base64");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldReturnUnavailableWhenProviderDoesNotSupportImageInput()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-image-text-provider-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new WorkflowFileIngressRequest(
                new byte[] { 1, 2, 3 },
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "receipt.jpeg",
                MediaType: "image/jpeg"));
            var tool = await GetDocumentExtractToolAsync(port, new TextOnlyLlmProvider());

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildDocumentExtractArguments(result.FileRef),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("error").GetString().Should().Be("image_provider_unavailable");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldRejectImagesOverFiveMiB()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-image-large-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new WorkflowFileIngressRequest(
                new byte[(5 * 1024 * 1024) + 1],
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "large.png",
                MediaType: "image/png"));
            var llmProvider = new RecordingImageLlmProvider(["unused"]);
            var tool = await GetDocumentExtractToolAsync(port, llmProvider);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildDocumentExtractArguments(result.FileRef),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("error").GetString().Should().Be("image_too_large");
            document.RootElement.GetProperty("detail").GetString().Should().Contain("5242880");
            llmProvider.Requests.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldRejectImagesWhenStreamExceedsFiveMiB()
    {
        var fileRef = new ApplicationWorkflowFileRef
        {
            FileId = "underreported-image",
            SourceKind = ApplicationWorkflowFileSourceKind.ChatInput,
            FileName = "underreported.png",
            MediaType = "image/png",
            SizeBytes = 0,
        };
        var port = new StaticWorkflowFileArtifactReadPort(
            fileRef,
            new MemoryStream(new byte[(5 * 1024 * 1024) + 1]));
        var llmProvider = new RecordingImageLlmProvider(["unused"]);
        var tool = await GetDocumentExtractToolAsync(port, llmProvider);

        var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
            BuildDocumentExtractArguments(fileRef),
            "run-1",
            "extract",
            "exec-1",
            "call-1",
            "scope-1",
            new ProtoWorkflowCallerCredential()));

        using var document = JsonDocument.Parse(output.ResultJson);
        document.RootElement.GetProperty("error").GetString().Should().Be("image_too_large");
        document.RootElement.GetProperty("detail").GetString().Should().Contain("5242880");
        llmProvider.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldSanitizeProviderFailures()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-image-failure-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var imageBytes = new byte[] { 1, 2, 3, 4 };
            var result = await port.IngestAsync(new WorkflowFileIngressRequest(
                imageBytes,
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "receipt.png",
                MediaType: "image/png"));
            var tool = await GetDocumentExtractToolAsync(
                port,
                new ThrowingImageLlmProvider("provider failed with c3RhY2s= raw payload"));

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildDocumentExtractArguments(result.FileRef),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("error").GetString().Should().Be("image_extraction_failed");
            var detail = document.RootElement.GetProperty("detail").GetString();
            detail.Should().Be("Image extraction provider failed.");
            output.ResultJson.Contains(Convert.ToBase64String(imageBytes), StringComparison.Ordinal).Should().BeFalse();
            output.ResultJson.Contains("c3RhY2s=", StringComparison.Ordinal).Should().BeFalse();
            output.ResultJson.Contains("raw payload", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldSanitizeProviderFactoryFailures()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-image-factory-failure-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var imageBytes = new byte[] { 1, 2, 3, 4 };
            var result = await port.IngestAsync(new WorkflowFileIngressRequest(
                imageBytes,
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "receipt.png",
                MediaType: "image/png"));
            var tool = await GetDocumentExtractToolAsync(
                port,
                llmProviderFactory: new ThrowingLlmProviderFactory("factory failed with c3RhY2s= raw payload"));

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildDocumentExtractArguments(result.FileRef),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("error").GetString().Should().Be("image_extraction_failed");
            var detail = document.RootElement.GetProperty("detail").GetString();
            detail.Should().Be("Image extraction provider failed.");
            output.ResultJson.Contains(Convert.ToBase64String(imageBytes), StringComparison.Ordinal).Should().BeFalse();
            output.ResultJson.Contains("c3RhY2s=", StringComparison.Ordinal).Should().BeFalse();
            output.ResultJson.Contains("raw payload", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static FileSystemWorkflowFileIngressPort CreateFileArtifactPort(string root) =>
        new(Microsoft.Extensions.Options.Options.Create(new FileSystemWorkflowFileIngressOptions
        {
            RootDirectory = root,
            TimeToLive = TimeSpan.FromMinutes(30),
        }));

    private static async Task<IWorkflowTool> GetDocumentExtractToolAsync(
        IWorkflowFileArtifactReadPort readPort,
        ILLMProvider? llmProvider = null,
        ILLMProviderFactory? llmProviderFactory = null)
    {
        var source = new WorkflowDocumentExtractToolSource(readPort, llmProvider, llmProviderFactory);
        var tools = await source.GetToolsAsync();
        return tools.Should().ContainSingle(x => x.Name == "document_extract").Subject;
    }

    private static string BuildDocumentExtractArguments(ApplicationWorkflowFileRef fileRef, int? maxChars = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["file_ref"] = new Dictionary<string, object?>
            {
                ["file_id"] = fileRef.FileId,
                ["artifact_id"] = fileRef.ArtifactId,
                ["source_kind"] = fileRef.SourceKind.ToString(),
                ["file_name"] = fileRef.FileName,
                ["media_type"] = fileRef.MediaType,
                ["size_bytes"] = fileRef.SizeBytes,
                ["sha256"] = fileRef.Sha256,
                ["created_at_unix_ms"] = fileRef.CreatedAtUnixMs,
                ["expires_at_unix_ms"] = fileRef.ExpiresAtUnixMs,
            },
        };
        if (maxChars != null)
            payload["max_chars"] = maxChars.Value;

        return JsonSerializer.Serialize(payload);
    }

    private sealed class StaticWorkflowFileArtifactReadPort(
        ApplicationWorkflowFileRef fileRef,
        Stream content) : IWorkflowFileArtifactReadPort
    {
        public ValueTask<ApplicationWorkflowFileRef> DescribeAsync(
            ApplicationWorkflowFileRef requestedFileRef,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(fileRef);

        public ValueTask<WorkflowFileArtifactContent> OpenReadAsync(
            ApplicationWorkflowFileRef requestedFileRef,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new WorkflowFileArtifactContent(fileRef, content));
    }

    private sealed class RecordingImageLlmProvider(IReadOnlyList<string> chunks) : ILLMProvider, ILLMProviderFactory
    {
        public string Name => "recording-image";

        public LLMProviderCapabilities Capabilities { get; } = new()
        {
            SupportedInputModalities = new HashSet<ContentPartKind>
            {
                ContentPartKind.Text,
                ContentPartKind.Image,
            },
        };

        public List<LLMRequest> Requests { get; } = [];

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                yield return new LLMStreamChunk { DeltaContent = chunk };
                await Task.Yield();
            }
        }
    }

    private sealed class TextOnlyLlmProvider : ILLMProvider
    {
        public string Name => "text-only";

        public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(LLMRequest request, CancellationToken ct = default) =>
            AsyncEnumerable.Empty<LLMStreamChunk>();
    }

    private sealed class ThrowingImageLlmProvider(string message) : ILLMProvider
    {
        public string Name => "throwing-image";

        public LLMProviderCapabilities Capabilities { get; } = new()
        {
            SupportedInputModalities = new HashSet<ContentPartKind>
            {
                ContentPartKind.Text,
                ContentPartKind.Image,
            },
        };

        public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            CancellationToken ct = default)
        {
            return ThrowAsync();

            async IAsyncEnumerable<LLMStreamChunk> ThrowAsync()
            {
                if (ct.IsCancellationRequested)
                {
                    yield return new LLMStreamChunk();
                    yield break;
                }

                await Task.Yield();
                throw new InvalidOperationException(message);
            }
        }
    }

    private sealed class ThrowingLlmProviderFactory(string message) : ILLMProviderFactory
    {
        public ILLMProvider GetProvider(string name) => throw new InvalidOperationException(message);

        public ILLMProvider GetDefault() => throw new InvalidOperationException(message);

        public IReadOnlyList<string> GetAvailableProviders() => [];
    }
}
