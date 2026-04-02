using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using Aevatar.Studio.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Tools.Cli.Tests;

public sealed class ChronoStorageUserConfigStoreTests
{
    [Fact]
    public async Task GetAsync_WhenConfigIsMissing_ShouldReturnLocalDefaults()
    {
        var store = CreateStore(
            new InMemoryChronoStorageServer(),
            scopeId: "scope-missing",
            defaultLocalRuntimeBaseUrl: "http://127.0.0.1:6001",
            defaultRemoteRuntimeBaseUrl: "https://remote-default.example");

        var config = await store.GetAsync();

        config.DefaultModel.Should().BeEmpty();
        config.PreferredLlmRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
        config.RuntimeMode.Should().Be(UserConfigRuntimeDefaults.LocalMode);
        config.LocalRuntimeBaseUrl.Should().Be("http://127.0.0.1:6001");
        config.RemoteRuntimeBaseUrl.Should().Be("https://remote-default.example");
    }

    [Fact]
    public async Task GetAsync_WhenLegacyRuntimeBaseUrlIsRemote_ShouldMigrateToExplicitRemoteMode()
    {
        var storageServer = new InMemoryChronoStorageServer();
        storageServer.Objects["aevatar-studio:profiles/scope-remote/config.json"] = Encoding.UTF8.GetBytes(
            """
            {
              "defaultModel": "gpt-4.1",
              "runtimeBaseUrl": "https://runtime.example"
            }
            """);
        var store = CreateStore(storageServer, scopeId: "scope-remote");

        var config = await store.GetAsync();

        config.DefaultModel.Should().Be("gpt-4.1");
        config.PreferredLlmRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
        config.RuntimeMode.Should().Be(UserConfigRuntimeDefaults.RemoteMode);
        config.LocalRuntimeBaseUrl.Should().Be(UserConfigRuntimeDefaults.LocalRuntimeBaseUrl);
        config.RemoteRuntimeBaseUrl.Should().Be("https://runtime.example");
    }

    [Fact]
    public async Task GetAsync_WhenUserConfigProvidesRuntimeUrl_ShouldOverrideAppSettingsDefaults()
    {
        var storageServer = new InMemoryChronoStorageServer();
        storageServer.Objects["aevatar-studio:profiles/scope-override/config.json"] = Encoding.UTF8.GetBytes(
            """
            {
              "defaultModel": "gpt-4.1",
              "preferredLlmRoute": "chrono-llm",
              "runtimeMode": "remote",
              "remoteRuntimeBaseUrl": "https://user-remote.example"
            }
            """);
        var store = CreateStore(
            storageServer,
            scopeId: "scope-override",
            defaultLocalRuntimeBaseUrl: "http://127.0.0.1:6001",
            defaultRemoteRuntimeBaseUrl: "https://remote-default.example");

        var config = await store.GetAsync();

        config.DefaultModel.Should().Be("gpt-4.1");
        config.PreferredLlmRoute.Should().Be("chrono-llm");
        config.RuntimeMode.Should().Be(UserConfigRuntimeDefaults.RemoteMode);
        config.LocalRuntimeBaseUrl.Should().Be("http://127.0.0.1:6001");
        config.RemoteRuntimeBaseUrl.Should().Be("https://user-remote.example");
    }

    [Fact]
    public async Task SaveAsync_ShouldPersistExplicitRuntimeModeAndUrls()
    {
        var storageServer = new InMemoryChronoStorageServer();
        var store = CreateStore(storageServer, scopeId: "scope-save");
        var config = new UserConfig(
            DefaultModel: "claude-sonnet-4-5-20250929",
            PreferredLlmRoute: "chrono-llm",
            RuntimeMode: UserConfigRuntimeDefaults.RemoteMode,
            LocalRuntimeBaseUrl: "http://127.0.0.1:5080",
            RemoteRuntimeBaseUrl: "https://runtime-save.example");

        await store.SaveAsync(config);

        storageServer.Objects.Should().ContainKey("aevatar-studio:profiles/scope-save/config.json");
        using var json = JsonDocument.Parse(storageServer.Objects["aevatar-studio:profiles/scope-save/config.json"]);
        json.RootElement.GetProperty("defaultModel").GetString().Should().Be("claude-sonnet-4-5-20250929");
        json.RootElement.GetProperty("preferredLlmRoute").GetString().Should().Be("chrono-llm");
        json.RootElement.GetProperty("runtimeMode").GetString().Should().Be(UserConfigRuntimeDefaults.RemoteMode);
        json.RootElement.GetProperty("localRuntimeBaseUrl").GetString().Should().Be("http://127.0.0.1:5080");
        json.RootElement.GetProperty("remoteRuntimeBaseUrl").GetString().Should().Be("https://runtime-save.example");
        json.RootElement.TryGetProperty("runtimeBaseUrl", out _).Should().BeFalse();
    }

    private static ChronoStorageUserConfigStore CreateStore(
        InMemoryChronoStorageServer storageServer,
        string scopeId,
        string? defaultLocalRuntimeBaseUrl = null,
        string? defaultRemoteRuntimeBaseUrl = null)
    {
        var options = Options.Create(new ConnectorCatalogStorageOptions
        {
            Enabled = true,
            UseNyxProxy = false,
            BaseUrl = "http://chrono-storage.test",
            Bucket = "aevatar-studio",
            UserConfigPrefix = "profiles",
        });
        var studioStorageOptions = Options.Create(new StudioStorageOptions
        {
            DefaultLocalRuntimeBaseUrl = defaultLocalRuntimeBaseUrl ?? UserConfigRuntimeDefaults.LocalRuntimeBaseUrl,
            DefaultRemoteRuntimeBaseUrl = defaultRemoteRuntimeBaseUrl ?? UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl,
        });
        var blobClient = new ChronoStorageCatalogBlobClient(
            new StubAppScopeResolver(scopeId),
            storageServer.CreateHttpClientFactory(),
            options);
        return new ChronoStorageUserConfigStore(
            blobClient,
            options,
            studioStorageOptions,
            NullLogger<ChronoStorageUserConfigStore>.Instance);
    }

    private sealed class StubAppScopeResolver : IAppScopeResolver
    {
        private readonly AppScopeContext _context;

        public StubAppScopeResolver(string scopeId)
        {
            _context = new AppScopeContext(scopeId, "test");
        }

        public AppScopeContext? Resolve(Microsoft.AspNetCore.Http.HttpContext? httpContext = null) => _context;
    }

    private sealed class InMemoryChronoStorageServer
    {
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

        public IHttpClientFactory CreateHttpClientFactory() =>
            new StubHttpClientFactory(new HttpClient(new Handler(this))
            {
                BaseAddress = new Uri("http://chrono-storage.test/"),
            });

        private sealed class Handler : HttpMessageHandler
        {
            private readonly InMemoryChronoStorageServer _server;

            public Handler(InMemoryChronoStorageServer server)
            {
                _server = server;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is required.");
                var path = uri.AbsolutePath.Trim('/');

                if (request.Method == HttpMethod.Post && string.Equals(path, "api/buckets/aevatar-studio/objects", StringComparison.Ordinal))
                {
                    var key = GetRequiredQueryValue(uri, "key");
                    _server.Objects[$"aevatar-studio:{key}"] = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                    return CreateJsonResponse(HttpStatusCode.OK, new { data = new { stored = true }, error = (object?)null });
                }

                if (request.Method == HttpMethod.Get && string.Equals(path, "api/buckets/aevatar-studio/objects/download", StringComparison.Ordinal))
                {
                    var key = GetRequiredQueryValue(uri, "key");
                    if (!_server.Objects.TryGetValue($"aevatar-studio:{key}", out var payload))
                    {
                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                    }

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(payload),
                    };
                }

                throw new InvalidOperationException($"Unhandled request {request.Method} {uri}.");
            }

            private static string GetRequiredQueryValue(Uri uri, string key)
            {
                var query = uri.Query.TrimStart('?')
                    .Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(pair => pair.Split('=', 2))
                    .ToDictionary(
                        pair => Uri.UnescapeDataString(pair[0]),
                        pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty,
                        StringComparer.Ordinal);
                return query.TryGetValue(key, out var value)
                    ? value
                    : throw new InvalidOperationException($"Missing query key '{key}'.");
            }

            private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, object payload) =>
                new(statusCode)
                {
                    Content = JsonContent.Create(payload),
                };
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public StubHttpClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name) => _client;
    }
}
