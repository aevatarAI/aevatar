using System.Text;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ApplicationFileArtifactRef = Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactRef;
using ApplicationFileArtifactSourceKind = Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactSourceKind;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class ChatFormRunRequestParserTests
{
    [Fact]
    public void ServiceProvider_ShouldResolveParserWithSharedMultipartFileParser()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton<IFileArtifactIngressPort>(new RecordingWorkflowFileIngressPort());
        services.AddSingleton<WorkflowMultipartFileInputParser>();
        services.AddSingleton<WorkflowMultipartChatRequestParser>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        provider.GetRequiredService<WorkflowMultipartChatRequestParser>()
            .Should()
            .NotBeNull();
    }

    [Fact]
    public async Task ParseAsync_ShouldIngestSingleFileAsFormUploadAndBuildFileRefInputPart()
    {
        var ingressPort = new RecordingWorkflowFileIngressPort();
        var parser = CreateParser(ingressPort);
        var http = CreateMultipartHttpContext(
            new Dictionary<string, string>
            {
                ["prompt"] = "describe this",
                ["workflow"] = "direct",
            },
            [CreateFormFile("file", "cat.png", "image/png", "hello")]);

        var result = await parser.ParseAsync(http, "scope-1", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        ingressPort.Requests.Should().ContainSingle();
        var ingressRequest = ingressPort.Requests[0];
        ingressRequest.Content.ToArray().Should().Equal(Encoding.UTF8.GetBytes("hello"));
        ingressRequest.SourceKind.Should().Be(ApplicationFileArtifactSourceKind.FormUpload);
        ingressRequest.FileName.Should().Be("cat.png");
        ingressRequest.MediaType.Should().Be("image/png");
        ingressRequest.OwnerScopeId.Should().Be("scope-1");
        result.Input.Should().NotBeNull();
        result.Input!.Prompt.Should().Be("describe this");
        result.Input.Workflow.Should().Be("direct");
        var part = result.Input.InputParts.Should().ContainSingle().Which;
        part.Type.Should().Be("image");
        part.DataBase64.Should().BeNull();
        part.InlineFile.Should().BeNull();
        part.FileRef.Should().NotBeNull();
        part.FileRef!.SourceKind.Should().Be("form_upload");
        part.FileRef.ArtifactId.Should().Be("workflow-file://file-1");
        part.FileRef.MediaType.Should().Be("image/png");
    }

    [Fact]
    public async Task ParseAsync_ShouldTrimOwnerScopeIdBeforeIngestingFile()
    {
        var ingressPort = new RecordingWorkflowFileIngressPort();
        var parser = CreateParser(ingressPort);
        var http = CreateMultipartHttpContext(
            new Dictionary<string, string>
            {
                ["prompt"] = "describe this",
            },
            [CreateFormFile("file", "cat.png", "image/png", "hello")]);

        var result = await parser.ParseAsync(http, "  scope-1  ", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        ingressPort.Requests.Should().ContainSingle()
            .Which.OwnerScopeId.Should().Be("scope-1");
    }

    [Fact]
    public async Task ParseAsync_ShouldLeaveOwnerScopeIdNull_WhenOwnerScopeIdIsBlank()
    {
        var ingressPort = new RecordingWorkflowFileIngressPort();
        var parser = CreateParser(ingressPort);
        var http = CreateMultipartHttpContext(
            new Dictionary<string, string>
            {
                ["prompt"] = "describe this",
            },
            [CreateFormFile("file", "cat.png", "image/png", "hello")]);

        var result = await parser.ParseAsync(http, "   ", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        ingressPort.Requests.Should().ContainSingle()
            .Which.OwnerScopeId.Should().BeNull();
    }

    [Fact]
    public async Task ParseAsync_ShouldMergePayloadAndLetScalarFieldsOverridePayloadValues()
    {
        var ingressPort = new RecordingWorkflowFileIngressPort();
        var parser = CreateParser(ingressPort);
        var http = CreateMultipartHttpContext(
            new Dictionary<string, StringValues>
            {
                ["payload"] = """
                {
                  "prompt": "payload prompt",
                  "workflow": "payload-workflow",
                  "sessionId": "payload-session",
                  "workflowYaml": "payload-yaml",
                  "workflowYamls": ["payload-root-yaml"],
                  "inputParts": [
                    {
                      "type": "text",
                      "text": "payload text"
                    }
                  ]
                }
                """,
                ["prompt"] = "form prompt",
                ["workflow"] = "form-workflow",
                ["sessionId"] = "form-session",
                ["workflowYaml"] = "form-yaml",
                ["workflowYamls"] = new StringValues(["form-root-yaml", "form-helper-yaml"]),
            },
            [CreateFormFile("file", "cat.png", "image/png", "hello")]);

        var result = await parser.ParseAsync(http, "form-scope", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Input.Should().NotBeNull();
        result.Input!.Prompt.Should().Be("form prompt");
        result.Input.Workflow.Should().Be("form-workflow");
        result.Input.SessionId.Should().Be("form-session");
        result.Input.WorkflowYaml.Should().Be("form-yaml");
        result.Input.WorkflowYamls.Should().Equal("form-root-yaml", "form-helper-yaml");
        result.Input.InputParts.Should().HaveCount(2);
        result.Input.InputParts![0].Type.Should().Be("text");
        result.Input.InputParts[0].Text.Should().Be("payload text");
        var uploadedPart = result.Input.InputParts[1];
        uploadedPart.Type.Should().Be("image");
        uploadedPart.FileRef.Should().NotBeNull();
        uploadedPart.FileRef!.ArtifactId.Should().Be("workflow-file://file-1");
        ingressPort.Requests.Should().ContainSingle();
        ingressPort.Requests[0].OwnerScopeId.Should().Be("form-scope");
    }

    [Fact]
    public async Task ParseAsync_ShouldPreserveConversationPayload()
    {
        var ingressPort = new RecordingWorkflowFileIngressPort();
        var parser = CreateParser(ingressPort);
        var http = CreateMultipartHttpContext(
            new Dictionary<string, string>
            {
                ["payload"] = """
                {
                  "prompt": "continue",
                  "conversation": {
                    "conversationId": "conversation-existing"
                  }
                }
                """,
            },
            [CreateFormFile("file", "cat.png", "image/png", "hello")]);

        var result = await parser.ParseAsync(http, "scope-1", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Input.Should().NotBeNull();
        result.Input!.Conversation.Should().NotBeNull();
        result.Input.Conversation!.ConversationId.Should().Be("conversation-existing");
        ingressPort.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ParseAsync_ShouldRejectFormScopeIdBeforeIngestingFile()
    {
        var ingressPort = new RecordingWorkflowFileIngressPort();
        var parser = CreateParser(ingressPort);
        var http = CreateMultipartHttpContext(
            new Dictionary<string, string>
            {
                ["prompt"] = "describe this",
                ["scopeId"] = "scope-from-form",
            },
            [CreateFormFile("file", "cat.png", "image/png", "hello")]);

        var result = await parser.ParseAsync(http, "trusted-scope", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        result.Code.Should().Be("INVALID_CHAT_INPUT");
        ingressPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseAsync_ShouldRejectRequestWithoutFile()
    {
        var ingressPort = new RecordingWorkflowFileIngressPort();
        var parser = CreateParser(ingressPort);
        var http = CreateMultipartHttpContext(
            new Dictionary<string, string>
            {
                ["prompt"] = "hello",
            },
            []);

        var result = await parser.ParseAsync(http, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        result.Code.Should().Be("INVALID_FILE_INPUT");
        ingressPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseAsync_ShouldIngestMultipleSameFieldFilesInFormOrder()
    {
        var ingressPort = new RecordingWorkflowFileIngressPort();
        var parser = CreateParser(ingressPort);
        var http = CreateMultipartHttpContext(
            new Dictionary<string, string>
            {
                ["prompt"] = "hello",
            },
            [
                CreateFormFile("file", "cat.png", "image/png", "hello"),
                CreateFormFile("file", "dog.png", "image/png", "world"),
            ]);

        var result = await parser.ParseAsync(http, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        ingressPort.Requests.Should().HaveCount(2);
        ingressPort.Requests.Select(request => request.FileName).Should().Equal("cat.png", "dog.png");
        ingressPort.Requests.Select(request => Encoding.UTF8.GetString(request.Content.ToArray()))
            .Should().Equal("hello", "world");
        result.Input.Should().NotBeNull();
        result.Input!.InputParts.Should().HaveCount(2);
        result.Input.InputParts!.Select(part => part.FileRef!.ArtifactId)
            .Should().Equal("workflow-file://file-1", "workflow-file://file-2");
        result.Input.InputParts!.Select(part => part.FileRef!.FileName)
            .Should().Equal("cat.png", "dog.png");
        result.Input.InputParts!.All(part =>
            part.DataBase64 == null &&
            part.InlineFile == null &&
            part.FileRef != null).Should().BeTrue();
    }

    [Fact]
    public async Task ParseAsync_ShouldRejectMismatchedFileFieldName()
    {
        var ingressPort = new RecordingWorkflowFileIngressPort();
        var parser = CreateParser(ingressPort);
        var http = CreateMultipartHttpContext(
            new Dictionary<string, string>
            {
                ["prompt"] = "hello",
            },
            [CreateFormFile("upload", "cat.png", "image/png", "hello")]);

        var result = await parser.ParseAsync(http, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        result.Code.Should().Be("INVALID_FILE_INPUT");
        ingressPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseAsync_ShouldRejectWholeRequestWithoutIngesting_WhenAnyFileFieldNameDiffers()
    {
        var ingressPort = new RecordingWorkflowFileIngressPort();
        var parser = CreateParser(ingressPort);
        var http = CreateMultipartHttpContext(
            new Dictionary<string, string>
            {
                ["prompt"] = "hello",
            },
            [
                CreateFormFile("file", "cat.png", "image/png", "hello"),
                CreateFormFile("upload", "dog.png", "image/png", "world"),
            ]);

        var result = await parser.ParseAsync(http, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        result.Code.Should().Be("INVALID_FILE_INPUT");
        ingressPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseAsync_ShouldAcceptConfiguredFileFieldName()
    {
        var ingressPort = new RecordingWorkflowFileIngressPort();
        var parser = CreateParser(
            ingressPort,
            formOptions: new WorkflowFormFileIngressOptions
            {
                FileFieldName = "upload",
            });
        var http = CreateMultipartHttpContext(
            new Dictionary<string, string>
            {
                ["prompt"] = "hello",
            },
            [CreateFormFile("upload", "cat.png", "image/png", "hello")]);

        var result = await parser.ParseAsync(http, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        ingressPort.Requests.Should().ContainSingle();
        ingressPort.Requests[0].FileName.Should().Be("cat.png");
        result.Input.Should().NotBeNull();
        result.Input!.InputParts.Should().ContainSingle()
            .Which.FileRef.Should().NotBeNull();
    }

    [Fact]
    public async Task ParseAsync_ShouldRejectUnsupportedMediaType()
    {
        var ingressPort = new RecordingWorkflowFileIngressPort();
        var parser = CreateParser(ingressPort);
        var http = CreateMultipartHttpContext(
            new Dictionary<string, string>
            {
                ["prompt"] = "hello",
            },
            [CreateFormFile("file", "cat.gif", "image/gif", "hello")]);

        var result = await parser.ParseAsync(http, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        result.Code.Should().Be("INVALID_FILE_INPUT");
        ingressPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseAsync_ShouldIngestAudioUploadAsAudioInputPart()
    {
        var ingressPort = new RecordingWorkflowFileIngressPort();
        var parser = CreateParser(ingressPort);
        var http = CreateMultipartHttpContext(
            new Dictionary<string, string>
            {
                ["prompt"] = "transcribe this",
            },
            [CreateFormFile("file", "voice.mp3", "audio/mpeg", "hello")]);

        var result = await parser.ParseAsync(http, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        ingressPort.Requests.Should().ContainSingle();
        ingressPort.Requests[0].MediaType.Should().Be("audio/mpeg");
        result.Input.Should().NotBeNull();
        var part = result.Input!.InputParts.Should().ContainSingle().Which;
        part.Type.Should().Be("audio");
        part.FileRef.Should().NotBeNull();
        part.FileRef!.MediaType.Should().Be("audio/mpeg");
    }

    [Theory]
    [InlineData("invoice.pdf", "application/pdf")]
    [InlineData("notes.txt", "text/plain")]
    [InlineData("readme.md", "text/markdown")]
    [InlineData("table.csv", "text/csv")]
    [InlineData("report.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("sheet.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    public async Task ParseAsync_ShouldIngestAllowedDocumentUploadAsFileInputPart(
        string fileName,
        string mediaType)
    {
        var ingressPort = new RecordingWorkflowFileIngressPort();
        var parser = CreateParser(ingressPort);
        var http = CreateMultipartHttpContext(
            new Dictionary<string, string>
            {
                ["prompt"] = "summarize this",
                ["workflow"] = "direct",
            },
            [CreateFormFile("file", fileName, mediaType, "hello")]);

        var result = await parser.ParseAsync(http, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        ingressPort.Requests.Should().ContainSingle();
        ingressPort.Requests[0].FileName.Should().Be(fileName);
        ingressPort.Requests[0].MediaType.Should().Be(mediaType);
        result.Input.Should().NotBeNull();
        var part = result.Input!.InputParts.Should().ContainSingle().Which;
        part.Type.Should().Be("file");
        part.MediaType.Should().Be(mediaType);
        part.DataBase64.Should().BeNull();
        part.InlineFile.Should().BeNull();
        part.FileRef.Should().NotBeNull();
        part.FileRef!.FileName.Should().Be(fileName);
        part.FileRef.MediaType.Should().Be(mediaType);
    }

    [Fact]
    public async Task ParseAsync_ShouldRejectFileLargerThanConfiguredLimit()
    {
        var ingressPort = new RecordingWorkflowFileIngressPort();
        var parser = CreateParser(ingressPort, new WorkflowMultipartFileIngressOptions
        {
            MaxFileBytes = 4,
        });
        var http = CreateMultipartHttpContext(
            new Dictionary<string, string>
            {
                ["prompt"] = "hello",
            },
            [CreateFormFile("file", "cat.png", "image/png", "hello")]);

        var result = await parser.ParseAsync(http, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        result.Code.Should().Be("INVALID_FILE_INPUT");
        ingressPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseAsync_ShouldRejectWholeRequestWithoutIngesting_WhenAnyFileIsInvalid()
    {
        var ingressPort = new RecordingWorkflowFileIngressPort();
        var parser = CreateParser(ingressPort, new WorkflowMultipartFileIngressOptions
        {
            MaxFileBytes = 8,
        });
        var http = CreateMultipartHttpContext(
            new Dictionary<string, string>
            {
                ["prompt"] = "hello",
            },
            [
                CreateFormFile("file", "cat.png", "image/png", "hello"),
                CreateFormFile("file", "large.png", "image/png", "too-large"),
            ]);

        var result = await parser.ParseAsync(http, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        result.Code.Should().Be("INVALID_FILE_INPUT");
        ingressPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseAsync_ShouldRejectWholeRequest_WhenAnyFileIngressFails()
    {
        var ingressPort = new RecordingWorkflowFileIngressPort
        {
            FailOnRequestNumber = 2,
        };
        var parser = CreateParser(ingressPort);
        var http = CreateMultipartHttpContext(
            new Dictionary<string, string>
            {
                ["prompt"] = "hello",
            },
            [
                CreateFormFile("file", "cat.png", "image/png", "hello"),
                CreateFormFile("file", "dog.png", "image/png", "world"),
            ]);

        var result = await parser.ParseAsync(http, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        result.Code.Should().Be("INVALID_FILE_INPUT");
        result.Input.Should().BeNull();
        ingressPort.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task ParseAsync_ShouldRejectMalformedPayloadWithoutIngestingFile()
    {
        var ingressPort = new RecordingWorkflowFileIngressPort();
        var parser = CreateParser(ingressPort);
        var http = CreateMultipartHttpContext(
            new Dictionary<string, string>
            {
                ["payload"] = """{ "prompt": """,
            },
            [CreateFormFile("file", "cat.png", "image/png", "hello")]);

        var result = await parser.ParseAsync(http, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        result.Code.Should().Be("INVALID_CHAT_INPUT");
        ingressPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseAsync_ShouldRejectPayloadInlineFile()
    {
        var ingressPort = new RecordingWorkflowFileIngressPort();
        var parser = CreateParser(ingressPort);
        var http = CreateMultipartHttpContext(
            new Dictionary<string, string>
            {
                ["payload"] = """
                {
                  "prompt": "hello",
                  "inputParts": [
                    {
                      "type": "image",
                      "inlineFile": {
                        "dataBase64": "aGVsbG8=",
                        "mediaType": "image/png"
                      }
                    }
                  ]
                }
                """,
            },
            [CreateFormFile("file", "cat.png", "image/png", "hello")]);

        var result = await parser.ParseAsync(http, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        result.Code.Should().Be("INVALID_FILE_INPUT");
        ingressPort.Requests.Should().BeEmpty();
    }

    private static WorkflowMultipartChatRequestParser CreateParser(
        IFileArtifactIngressPort ingressPort,
        WorkflowMultipartFileIngressOptions? options = null,
        WorkflowFormFileIngressOptions? formOptions = null) =>
        new(
            ingressPort,
            Options.Create(options ?? new WorkflowMultipartFileIngressOptions()),
            Options.Create(formOptions ?? new WorkflowFormFileIngressOptions()));

    private static DefaultHttpContext CreateMultipartHttpContext(
        IDictionary<string, string> fields,
        IReadOnlyList<IFormFile> files)
    {
        var stringValues = fields.ToDictionary(
            static pair => pair.Key,
            static pair => new StringValues(pair.Value),
            StringComparer.Ordinal);

        return CreateMultipartHttpContext(stringValues, files);
    }

    private static DefaultHttpContext CreateMultipartHttpContext(
        IDictionary<string, StringValues> fields,
        IReadOnlyList<IFormFile> files)
    {
        var http = new DefaultHttpContext();
        http.Request.ContentType = "multipart/form-data; boundary=test";
        var formFiles = new FormFileCollection();
        foreach (var file in files)
            formFiles.Add(file);
        http.Features.Set<IFormFeature>(new FormFeature(new FormCollection(
            new Dictionary<string, StringValues>(fields, StringComparer.Ordinal),
            formFiles)));
        return http;
    }

    private static IFormFile CreateFormFile(
        string fieldName,
        string fileName,
        string contentType,
        string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, fieldName, fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
    }

    private sealed class RecordingWorkflowFileIngressPort : IFileArtifactIngressPort
    {
        public List<FileArtifactIngressRequest> Requests { get; } = [];

        public int? FailOnRequestNumber { get; init; }

        public ValueTask<FileArtifactIngressResult> IngestAsync(
            FileArtifactIngressRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var index = Requests.Count;
            if (FailOnRequestNumber == index)
                throw new IOException("file ingress failed");

            return ValueTask.FromResult(new FileArtifactIngressResult(new ApplicationFileArtifactRef
            {
                FileId = $"file-{index}",
                ArtifactId = $"workflow-file://file-{index}",
                SourceKind = request.SourceKind,
                FileName = request.FileName,
                MediaType = request.MediaType,
                SizeBytes = request.Content.Length,
                Sha256 = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
                CreatedAtUnixMs = 1710000000000,
                ExpiresAtUnixMs = 1710003600000,
                OwnerScopeId = request.OwnerScopeId,
            }));
        }
    }
}
