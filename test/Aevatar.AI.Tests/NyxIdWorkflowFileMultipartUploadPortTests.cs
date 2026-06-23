using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public sealed class NyxIdWorkflowFileMultipartUploadPortTests
{
    [Fact]
    public void AddNyxIdTools_ShouldRegisterWorkflowFileMultipartUploadPort()
    {
        var services = new ServiceCollection();

        services.AddNyxIdTools(options =>
        {
            options.BaseUrl = "https://nyx.example.com";
        });

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IWorkflowFileMultipartUploadPort) &&
            descriptor.ImplementationType == typeof(NyxIdWorkflowFileMultipartUploadPort));

        using var provider = services.BuildServiceProvider();
        provider.GetServices<IWorkflowFileMultipartUploadPort>()
            .Should()
            .ContainSingle()
            .Which
            .Should()
            .BeOfType<NyxIdWorkflowFileMultipartUploadPort>();
    }

    [Fact]
    public async Task UploadAsync_ShouldUseResolvedPolicyRequestAndReturnSanitizedProviderOutput()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"code":0,"data":{"document_id":"doc_123","body":"raw","data_base64":"AAAA"}}""",
                Encoding.UTF8,
                "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            httpClient);
        var port = new NyxIdWorkflowFileMultipartUploadPort(client);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("upload bytes"));

        var result = await port.UploadAsync(new WorkflowFileMultipartUploadRequest(
            CallerCredential: new WorkflowCallerCredential("token-123"),
            ServiceSlug: "storage",
            Path: "/files/upload",
            Method: "POST",
            FileFieldName: "upload",
            FormFields: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["bucket"] = "invoices",
            },
            FileName: "invoice.txt",
            MediaType: "text/plain",
            SizeBytes: 12,
            Sha256: "sha256-value",
            OutputSelector: "data.document_id",
            Content: content));

        result.Succeeded.Should().BeTrue();
        result.OutputCode.Should().Be("doc_123");
        result.Error.Should().BeNull();
        result.ToString().Should().NotContain("raw");
        result.ToString()!.Contains("base64", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/storage/files/upload");
        handler.LastRequest.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.Headers.Authorization!.Parameter.Should().Be("token-123");
        handler.LastRequest.Headers.Should().NotContain(header =>
            string.Equals(header.Key, "X-Trace", StringComparison.OrdinalIgnoreCase));
        handler.LastBody.Should().Contain("""name=bucket""");
        handler.LastBody.Should().Contain("invoices");
        handler.LastBody.Should().Contain("""name=upload; filename=invoice.txt""");
        handler.LastBody.Should().Contain("upload bytes");
    }

    [Fact]
    public async Task UploadAsync_ShouldFailClosedWithoutEchoingProviderBody()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"error":true,"status":502,"body":"raw upstream","data_base64":"AAAA"}""",
                Encoding.UTF8,
                "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            httpClient);
        var port = new NyxIdWorkflowFileMultipartUploadPort(client);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("upload bytes"));

        var result = await port.UploadAsync(new WorkflowFileMultipartUploadRequest(
            CallerCredential: new WorkflowCallerCredential("token-123"),
            ServiceSlug: "storage",
            Path: "/files/upload",
            Method: "POST",
            FileFieldName: "upload",
            FormFields: new Dictionary<string, string>(StringComparer.Ordinal),
            FileName: "invoice.txt",
            MediaType: "text/plain",
            SizeBytes: 12,
            Sha256: null,
            OutputSelector: "data.document_id",
            Content: content));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("provider_error");
        result.Detail.Should().Be("workflow_file_submit provider returned an error envelope.");
        result.OutputCode.Should().BeNull();
        result.ProviderCode.Should().BeNull();
        result.HttpStatus.Should().Be(502);
        result.ToString().Should().NotContain("raw upstream");
        result.ToString()!.Contains("base64", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"code":403,"msg":"forbidden"}""", "provider_error", "provider_code=403", 403)]
    [InlineData("""{"code":0,"data":{}}""", "missing_output_code", "workflow_file_submit response did not include the required output_code.", 0)]
    [InlineData("""{not-json}""", "invalid_provider_response", "workflow_file_submit provider response was not valid JSON.", null)]
    public async Task UploadAsync_ShouldFailClosedForProviderSchemaFailures(
        string responseJson,
        string expectedError,
        string expectedDetail,
        int? expectedProviderCode)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            httpClient);
        var port = new NyxIdWorkflowFileMultipartUploadPort(client);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("upload bytes"));

        var result = await port.UploadAsync(new WorkflowFileMultipartUploadRequest(
            CallerCredential: new WorkflowCallerCredential("token-123"),
            ServiceSlug: "storage",
            Path: "/files/upload",
            Method: "POST",
            FileFieldName: "upload",
            FormFields: new Dictionary<string, string>(StringComparer.Ordinal),
            FileName: "invoice.txt",
            MediaType: "text/plain",
            SizeBytes: 12,
            Sha256: null,
            OutputSelector: "data.document_id",
            Content: content));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(expectedError);
        result.Detail.Should().Be(expectedDetail);
        result.ProviderCode.Should().Be(expectedProviderCode);
        result.OutputCode.Should().BeNull();
    }

    [Theory]
    [InlineData("data:text/plain;base64,AAAA")]
    [InlineData("dGVzdA")]
    [InlineData("cmF3")]
    [InlineData("c2VjcmV0")]
    [InlineData("JVBERi0x")]
    [InlineData("iVBORw0KGgo")]
    [InlineData("AAECAwQ")]
    [InlineData("__8")]
    [InlineData("QUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFB")]
    [InlineData("QUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUE")]
    [InlineData("__________________________________________________________________8")]
    public async Task UploadAsync_ShouldFailClosedWhenSelectedOutputCodeLooksLikePayload(string unsafeOutputCode)
    {
        var responseJson = $"{{\"code\":0,\"data\":{{\"document_id\":{JsonSerializer.Serialize(unsafeOutputCode)}}}}}";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            httpClient);
        var port = new NyxIdWorkflowFileMultipartUploadPort(client);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("upload bytes"));

        var result = await port.UploadAsync(new WorkflowFileMultipartUploadRequest(
            CallerCredential: new WorkflowCallerCredential("token-123"),
            ServiceSlug: "storage",
            Path: "/files/upload",
            Method: "POST",
            FileFieldName: "upload",
            FormFields: new Dictionary<string, string>(StringComparer.Ordinal),
            FileName: "invoice.txt",
            MediaType: "text/plain",
            SizeBytes: 12,
            Sha256: null,
            OutputSelector: "data.document_id",
            Content: content));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("invalid_provider_response");
        result.Detail.Should().Be("workflow_file_submit response selected output_code was not a safe resource identifier.");
        result.OutputCode.Should().BeNull();
        result.ToString().Should().NotContain(unsafeOutputCode);
        result.ToString()!.Contains("base64", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Theory]
    [InlineData("doc_123")]
    [InlineData("tok_123")]
    [InlineData("code_123")]
    public async Task UploadAsync_ShouldAllowNormalShortResourceIds(string outputCode)
    {
        var responseJson = $"{{\"code\":0,\"data\":{{\"document_id\":{JsonSerializer.Serialize(outputCode)}}}}}";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            httpClient);
        var port = new NyxIdWorkflowFileMultipartUploadPort(client);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("upload bytes"));

        var result = await port.UploadAsync(new WorkflowFileMultipartUploadRequest(
            CallerCredential: new WorkflowCallerCredential("token-123"),
            ServiceSlug: "storage",
            Path: "/files/upload",
            Method: "POST",
            FileFieldName: "upload",
            FormFields: new Dictionary<string, string>(StringComparer.Ordinal),
            FileName: "invoice.txt",
            MediaType: "text/plain",
            SizeBytes: 12,
            Sha256: null,
            OutputSelector: "data.document_id",
            Content: content));

        result.Succeeded.Should().BeTrue();
        result.OutputCode.Should().Be(outputCode);
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task UploadAsync_ShouldAllowOpaqueLarkDriveFileTokenFromFileTokenSelector()
    {
        const string outputCode = "FKR1bvabcdefghijklmnodtFgUc";
        var responseJson = $"{{\"code\":0,\"data\":{{\"file_token\":{JsonSerializer.Serialize(outputCode)}}}}}";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            httpClient);
        var port = new NyxIdWorkflowFileMultipartUploadPort(client);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("upload bytes"));

        var result = await port.UploadAsync(new WorkflowFileMultipartUploadRequest(
            CallerCredential: new WorkflowCallerCredential("token-123"),
            ServiceSlug: "storage",
            Path: "/files/upload",
            Method: "POST",
            FileFieldName: "upload",
            FormFields: new Dictionary<string, string>(StringComparer.Ordinal),
            FileName: "invoice.txt",
            MediaType: "text/plain",
            SizeBytes: 12,
            Sha256: null,
            OutputSelector: "data.file_token",
            Content: content));

        result.Succeeded.Should().BeTrue();
        result.OutputCode.Should().Be(outputCode);
        result.Error.Should().BeNull();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    public async Task UploadAsync_ShouldFailClosedForNonObjectProviderJsonRoot(string responseJson)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            httpClient);
        var port = new NyxIdWorkflowFileMultipartUploadPort(client);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("upload bytes"));

        Func<Task<WorkflowFileMultipartUploadResult>> act = async () => await port.UploadAsync(
            new WorkflowFileMultipartUploadRequest(
                CallerCredential: new WorkflowCallerCredential("token-123"),
                ServiceSlug: "storage",
                Path: "/files/upload",
                Method: "POST",
                FileFieldName: "upload",
                FormFields: new Dictionary<string, string>(StringComparer.Ordinal),
                FileName: "invoice.txt",
                MediaType: "text/plain",
                SizeBytes: 12,
                Sha256: null,
                OutputSelector: "data.document_id",
                Content: content));

        var result = await act.Should().NotThrowAsync();
        result.Which.Succeeded.Should().BeFalse();
        result.Which.Error.Should().Be("invalid_provider_response");
        result.Which.Detail.Should().Be("workflow_file_submit provider response root was not a JSON object.");
        result.Which.OutputCode.Should().BeNull();
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }
}
