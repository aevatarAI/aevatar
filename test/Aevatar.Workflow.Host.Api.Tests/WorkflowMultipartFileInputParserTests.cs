using System.Text;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowMultipartFileInputParserTests
{
    [Fact]
    public async Task ParseAsync_ShouldReturnRawPayloadAndPendingFilesWithoutIngress()
    {
        var parser = CreateParser();
        var http = CreateMultipartHttpContext(
            new Dictionary<string, string>
            {
                ["payload"] = """{"prompt":"payload prompt"}""",
                ["prompt"] = "form prompt",
            },
            [CreateFormFile("file", "cat.png", "image/png", "hello")]);

        var result = await parser.ParseAsync(http, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.HasFiles.Should().BeTrue();
        result.RawPayloadJson.Should().Be("""{"prompt":"payload prompt"}""");
        result.Form.Should().NotBeNull();
        result.Form!.Fields["prompt"].ToString().Should().Be("form prompt");
        result.Form.PendingFiles.Should().ContainSingle();
        var file = result.Form.PendingFiles[0];
        file.FileName.Should().Be("cat.png");
        file.MediaType.Should().Be("image/png");
        file.InputPartType.Should().Be("image");
        Encoding.UTF8.GetString(file.Content.ToArray()).Should().Be("hello");
    }

    [Fact]
    public async Task ParseAsync_ShouldAllowNoFileMultipartForHostDtoParsing()
    {
        var parser = CreateParser();
        var http = CreateMultipartHttpContext(
            new Dictionary<string, string>
            {
                ["payload"] = """{"prompt":"payload prompt"}""",
            },
            []);

        var result = await parser.ParseAsync(http, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.HasFiles.Should().BeFalse();
        result.RawPayloadJson.Should().Be("""{"prompt":"payload prompt"}""");
        result.Form.Should().NotBeNull();
        result.Form!.PendingFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseAsync_ShouldRejectMismatchedFileFieldName()
    {
        var parser = CreateParser();
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
    }

    [Fact]
    public async Task ParseAsync_ShouldRejectWholeRequest_WhenAnyFileIsInvalid()
    {
        var parser = CreateParser(new WorkflowMultipartFileIngressOptions
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
    }

    [Fact]
    public async Task ParseAsync_ShouldRejectUnsupportedMediaType()
    {
        var parser = CreateParser();
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
    }

    [Fact]
    public async Task ParseAsync_ShouldAcceptConfiguredFileFieldName()
    {
        var parser = CreateParser(formOptions: new WorkflowFormFileIngressOptions
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
        result.Form.Should().NotBeNull();
        result.Form!.PendingFiles.Should().ContainSingle()
            .Which.FileName.Should().Be("cat.png");
    }

    private static WorkflowMultipartFileInputParser CreateParser(
        WorkflowMultipartFileIngressOptions? options = null,
        WorkflowFormFileIngressOptions? formOptions = null) =>
        new(
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
}
