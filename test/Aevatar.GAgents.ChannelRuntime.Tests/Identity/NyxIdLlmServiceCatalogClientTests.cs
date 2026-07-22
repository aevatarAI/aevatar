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
        handler.Paths.Count(path => path == NyxIdLlmCatalogRoutes.UserKeysPath)
            .Should()
            .Be(2, "same-token calls should reuse the short-lived user-keys cache");
        handler.Paths.Count(path => path == "/api/v1/user-services")
            .Should()
            .Be(3, "exact identity inventory must be fetched for every catalog read");
    }

    [Fact]
    public async Task GetServicesAsync_MintsIdentityOnlyFromUserServicesInventory()
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

        var result = await client.GetServicesAsync(query, "token-a", CancellationToken.None);

        var chrono = result.Services.Should()
            .ContainSingle(service => service.ServiceSlug == "chrono-llm")
            .Subject;
        chrono.Allowed.Should().BeTrue(
            "the active personal inventory record is eligible even when proxy/services " +
            "still reports the legacy connections store as not connected");
        chrono.Status.Should().Be("ready");
        chrono.RouteValue.Should().Be("/api/v1/proxy/s/chrono-llm");
        chrono.CatalogEntryId.Should().NotBe("us-chrono");
        chrono.Identity.Should().Be(new UserLlmServiceIdentity(
            UserLlmIdentityAuthority.NyxIdUserServicesInventory,
            "us-chrono"));
        chrono.Identity!.NyxIdUserServiceId.Should().NotBe("key-chrono");
        chrono.Identity.NyxIdUserServiceId.Should().NotBe("svc-chrono");
    }

    [Fact]
    public async Task GetServicesAsync_WhenUserServicesResponseIsMalformed_ShouldRejectCatalog()
    {
        var handler = new RecordingHandler("""{"services":[{"id":"us-chrono","slug":"chrono-llm"}]}""");
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            new HttpClient(handler),
            NullLogger<NyxIdApiClient>.Instance);
        var memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        var client = new NyxIdLlmServiceCatalogClient(
            nyxClient,
            memoryCache,
            NullLogger<NyxIdLlmServiceCatalogClient>.Instance);

        var act = () => client.GetServicesAsync(Query(), "token-a", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Paths.Should().Contain("/api/v1/user-services");
    }

    private static UserLlmOptionsQuery Query() => new(
        new BindingId { Value = "bnd-1" },
        new ExternalSubjectRef
        {
            Platform = "lark",
            Tenant = "tenant",
            ExternalUserId = "user",
        },
        RegistrationScopeId: "scope-1");

    private sealed class RecordingHandler(string? userServicesResponse = null) : HttpMessageHandler
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
                          "connected": false,
                          "requires_connection": true,
                          "proxy_url_slug": "https://nyx.test/api/v1/proxy/s/chrono-llm/{path}"
                        }
                      ]
                    }
                    """,
                _ when path == NyxIdLlmCatalogRoutes.UserKeysPath => """
                    {
                      "keys": [
                        {
                          "id": "key-chrono",
                          "label": "Chrono LLM",
                          "slug": "chrono-llm",
                          "endpoint_url": "https://llm.test/v1",
                          "credential_type": "api_key",
                          "status": "active",
                          "catalog_service_id": "svc-chrono",
                          "catalog_service_slug": "chrono-llm",
                          "catalog_service_name": "Chrono LLM",
                          "service_type": "http",
                          "is_active": true
                        }
                      ]
                    }
                    """,
                "/api/v1/user-services" => userServicesResponse ?? """
                    {
                      "services": [
                        {
                          "id": "us-chrono",
                          "slug": "chrono-llm",
                          "label": "Chrono LLM",
                          "catalog_service_name": "Chrono LLM",
                          "is_active": true,
                          "credential_source": {
                            "type": "personal"
                          }
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
