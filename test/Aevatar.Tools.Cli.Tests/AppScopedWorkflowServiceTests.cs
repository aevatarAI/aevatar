using System.Net;
using System.Text;
using Aevatar.Tools.Cli.Hosting;
using Aevatar.Tools.Cli.Studio.Application.Abstractions;
using Aevatar.Tools.Cli.Studio.Domain.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Tools.Cli.Tests;

public sealed class AppScopedWorkflowServiceTests
{
    [Fact]
    public async Task ListAsync_WhenBackendRedirectsToLogin_ShouldThrowAuthRequiredException()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers =
            {
                Location = new Uri("https://login.example/sign-in", UriKind.Absolute),
            },
        });

        var act = () => service.ListAsync("scope-1");

        var exception = await Assert.ThrowsAsync<AppApiException>(act);
        exception.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        exception.Code.Should().Be(AppApiErrors.BackendAuthRequiredCode);
        exception.LoginUrl.Should().Be("https://login.example/sign-in");
    }

    [Fact]
    public async Task ListAsync_WhenBackendReturnsHtml_ShouldThrowInvalidResponseException()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "<!DOCTYPE html><html><body>sign in</body></html>",
                Encoding.UTF8,
                "text/html"),
        });

        var act = () => service.ListAsync("scope-1");

        var exception = await Assert.ThrowsAsync<AppApiException>(act);
        exception.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        exception.Code.Should().Be(AppApiErrors.BackendInvalidResponseCode);
        exception.Message.Should().Be("Workflow backend returned a non-JSON response.");
    }

    private static AppScopedWorkflowService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var handler = new StubHttpMessageHandler(responseFactory);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://backend.example"),
        };

        return new AppScopedWorkflowService(
            new StubHttpClientFactory(httpClient),
            new StubWorkflowYamlDocumentService());
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _httpClient;

        public StubHttpClientFactory(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public HttpClient CreateClient(string name) => _httpClient;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = _responseFactory(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class StubWorkflowYamlDocumentService : IWorkflowYamlDocumentService
    {
        public WorkflowParseResult Parse(string yaml) => new(null, []);

        public string Serialize(WorkflowDocument document) => string.Empty;
    }
}
