using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public sealed class NyxIdWorkflowConnectedServiceFileSubmitAdapterTests
{
    [Fact]
    public void AddNyxIdTools_ShouldRegisterWorkflowFileSubmitAdapter()
    {
        var services = new ServiceCollection();

        services.AddNyxIdTools(options =>
        {
            options.BaseUrl = "https://nyx.example.com";
        });

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IWorkflowConnectedServiceFileSubmitAdapter) &&
            descriptor.ImplementationType == typeof(NyxIdWorkflowConnectedServiceFileSubmitAdapter));

        using var provider = services.BuildServiceProvider();
        provider.GetServices<IWorkflowConnectedServiceFileSubmitAdapter>()
            .Should()
            .ContainSingle()
            .Which
            .Should()
            .BeOfType<NyxIdWorkflowConnectedServiceFileSubmitAdapter>();
    }

    [Fact]
    public async Task SubmitAsync_ShouldUseConfiguredEndpointAndReturnSanitizedOutputCode()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"code":0,"data":{"file_token":"tok_123","body":"raw","data_base64":"AAAA"}}""",
                Encoding.UTF8,
                "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            httpClient);
        var adapter = new NyxIdWorkflowConnectedServiceFileSubmitAdapter(client);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("upload bytes"));

        var result = await adapter.SubmitAsync(new WorkflowConnectedServiceFileSubmitRequest(
            Target: new WorkflowConnectedServiceFileSubmitTarget(
                Target: "submit_invoice",
                Provider: NyxIdWorkflowConnectedServiceFileSubmitAdapter.ProviderName,
                OutputField: "file_token",
                MaxFileBytes: 1024,
                AllowedMediaTypes: new HashSet<string>(StringComparer.Ordinal) { "text/plain" },
                Arguments: new Dictionary<string, WorkflowConnectedServiceFileSubmitArgumentPolicy>(),
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
                        ["bucket"] = "invoices",
                    })),
            FileRef: new WorkflowFileRef { FileId = "file-1", ArtifactId = "artifact-1" },
            FileName: "invoice.txt",
            MediaType: "text/plain",
            SizeBytes: 12,
            Content: content,
            CallerCredential: new WorkflowCallerCredential("token-123"),
            Arguments: new Dictionary<string, string>()));

        result.Succeeded.Should().BeTrue();
        result.OutputCode.Should().Be("tok_123");
        result.Detail.Should().BeNull();
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/storage/files/upload");
        handler.LastRequest.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.Headers.Authorization!.Parameter.Should().Be("token-123");
        handler.LastRequest.Headers.GetValues("X-Trace").Should().ContainSingle().Which.Should().Be("trace-1");
        handler.LastBody.Should().Contain("""name=bucket""");
        handler.LastBody.Should().Contain("invoices");
        handler.LastBody.Should().Contain("""name=upload; filename=invoice.txt""");
        handler.LastBody.Should().Contain("upload bytes");
    }

    [Fact]
    public async Task SubmitAsync_ShouldFailClosedWithoutEchoingProviderBody()
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
        var adapter = new NyxIdWorkflowConnectedServiceFileSubmitAdapter(client);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("upload bytes"));

        var result = await adapter.SubmitAsync(new WorkflowConnectedServiceFileSubmitRequest(
            Target: new WorkflowConnectedServiceFileSubmitTarget(
                Target: "submit_invoice",
                Provider: NyxIdWorkflowConnectedServiceFileSubmitAdapter.ProviderName,
                OutputField: "file_token",
                MaxFileBytes: 1024,
                AllowedMediaTypes: new HashSet<string>(StringComparer.Ordinal) { "text/plain" },
                Arguments: new Dictionary<string, WorkflowConnectedServiceFileSubmitArgumentPolicy>(),
                Endpoint: new WorkflowConnectedServiceFileSubmitEndpoint(
                    ServiceSlug: "storage",
                    Path: "files/upload",
                    Method: "POST",
                    FileFieldName: "upload")),
            FileRef: new WorkflowFileRef { FileId = "file-1", ArtifactId = "artifact-1" },
            FileName: "invoice.txt",
            MediaType: "text/plain",
            SizeBytes: 12,
            Content: content,
            CallerCredential: new WorkflowCallerCredential("token-123"),
            Arguments: new Dictionary<string, string>()));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("nyx_proxy_error");
        result.Detail.Should().Be("nyx_proxy_error status=502");
        JsonSerializer.Serialize(result).Should().NotContain("raw upstream");
        JsonSerializer.Serialize(result).Contains("base64", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
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
