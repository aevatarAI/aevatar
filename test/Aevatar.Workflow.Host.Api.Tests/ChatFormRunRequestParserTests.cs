using System.Text;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ApplicationWorkflowFileRef = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowFileRef;
using ApplicationWorkflowFileSourceKind = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowFileSourceKind;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class ChatFormRunRequestParserTests
{
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
                ["scopeId"] = "scope-1",
            },
            [CreateFormFile("file", "cat.png", "image/png", "hello")]);

        var result = await parser.ParseAsync(http, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        ingressPort.Requests.Should().ContainSingle();
        var ingressRequest = ingressPort.Requests[0];
        ingressRequest.Content.ToArray().Should().Equal(Encoding.UTF8.GetBytes("hello"));
        ingressRequest.SourceKind.Should().Be(ApplicationWorkflowFileSourceKind.FormUpload);
        ingressRequest.FileName.Should().Be("cat.png");
        ingressRequest.MediaType.Should().Be("image/png");
        ingressRequest.OwnerScopeId.Should().Be("scope-1");
        result.Input.Should().NotBeNull();
        result.Input!.Prompt.Should().Be("describe this");
        result.Input.Workflow.Should().Be("direct");
        result.Input.ScopeId.Should().Be("scope-1");
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
    public async Task ParseAsync_ShouldRejectMultipleFiles()
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

        result.Succeeded.Should().BeFalse();
        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        result.Code.Should().Be("INVALID_FILE_INPUT");
        ingressPort.Requests.Should().BeEmpty();
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
        IWorkflowFileIngressPort ingressPort,
        WorkflowMultipartFileIngressOptions? options = null) =>
        new(
            ingressPort,
            new ChatFormRunRequestParser(),
            Options.Create(options ?? new WorkflowMultipartFileIngressOptions()));

    private static DefaultHttpContext CreateMultipartHttpContext(
        IDictionary<string, string> fields,
        IReadOnlyList<IFormFile> files)
    {
        var http = new DefaultHttpContext();
        http.Request.ContentType = "multipart/form-data; boundary=test";
        var formFiles = new FormFileCollection();
        foreach (var file in files)
            formFiles.Add(file);
        http.Features.Set<IFormFeature>(new FormFeature(new FormCollection(ToFormFields(fields), formFiles)));
        return http;
    }

    private static Dictionary<string, StringValues> ToFormFields(IDictionary<string, string> fields) =>
        fields.ToDictionary(
            static pair => pair.Key,
            static pair => new StringValues(pair.Value),
            StringComparer.Ordinal);

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

    private sealed class RecordingWorkflowFileIngressPort : IWorkflowFileIngressPort
    {
        public List<WorkflowFileIngressRequest> Requests { get; } = [];

        public ValueTask<WorkflowFileIngressResult> IngestAsync(
            WorkflowFileIngressRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(new WorkflowFileIngressResult(new ApplicationWorkflowFileRef
            {
                FileId = "file-1",
                ArtifactId = "workflow-file://file-1",
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
