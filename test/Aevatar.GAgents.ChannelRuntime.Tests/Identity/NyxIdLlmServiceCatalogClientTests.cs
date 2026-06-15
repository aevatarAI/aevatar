using System.Net;
using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.NyxidChat.LlmSelection;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

public sealed class NyxIdLlmServiceCatalogClientTests
{
    [Fact]
    public async Task GetServicesAsync_CachesProxyServicesPerAccessToken()
    {
        var handler = new RecordingHandler();
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            new HttpClient(handler),
            NullLogger<NyxIdApiClient>.Instance);
        var memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        var client = new NyxIdLlmServiceCatalogClient(
            nyxClient,
            memoryCache,
            NullLogger<NyxIdLlmServiceCatalogClient>.Instance);
        var query = new UserLlmOptionsQuery(
            new BindingId { Value = "bnd-1" },
            new ExternalSubjectRef
            {
                Platform = "lark",
                Tenant = "tenant",
                ExternalUserId = "user",
            },
            RegistrationScopeId: "scope-1");

        await client.GetServicesAsync(query, "token-a", CancellationToken.None);
        await client.GetServicesAsync(query, "token-a", CancellationToken.None);
        await client.GetServicesAsync(query, "token-b", CancellationToken.None);

        handler.Paths.Count(path => path == "/api/v1/llm/services").Should().Be(3);
        handler.Paths.Count(path => path == NyxIdLlmCatalogRoutes.ProxyServicesPath)
            .Should()
            .Be(2, "same-token calls should reuse the short-lived proxy-services cache");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            Paths.Add(path);
            var body = path switch
            {
                "/api/v1/llm/services" => """{"services":[]}""",
                _ when path == NyxIdLlmCatalogRoutes.ProxyServicesPath => """
                    {
                      "services": [
                        {
                          "id": "svc-chrono",
                          "slug": "chrono-llm",
                          "name": "Chrono LLM",
                          "description": "Shared LLM route",
                          "proxy_url_slug": "https://nyx.test/api/v1/proxy/s/chrono-llm/{path}"
                        }
                      ]
                    }
                    """,
                _ => """{"error":true,"status":404}""",
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
