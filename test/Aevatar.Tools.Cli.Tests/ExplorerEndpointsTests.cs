using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Hosting.Endpoints;
using Aevatar.Studio.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aevatar.Tools.Cli.Tests;

public sealed class ExplorerEndpointsTests
{
    [Theory]
    [InlineData("/api/explorer/files/workflows%2Fdraft.yaml")]
    [InlineData("/api/explorer/files/workflows/draft.yaml")]
    public async Task GetFileAsync_WhenWorkflowKeyUsesPathSeparators_ShouldLoadFile(string requestPath)
    {
        await using var host = await ExplorerTestHost.StartAsync("scope-a");
        host.StorageServer.StoreText("aevatar-studio", "scope-a/workflows/draft.yaml", "name: draft");

        var response = await host.Client.GetAsync(requestPath);
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/yaml");
        content.Should().Be("name: draft");
    }

    [Theory]
    [InlineData("/api/explorer/files/chat-histories/NyxIdChat:scope-a.jsonl")]
    [InlineData("/api/explorer/files/chat-histories/NyxIdChat%3Ascope-a.jsonl")]
    public async Task GetFileAsync_WhenChatHistoryLivesUnderUserConfigPrefix_ShouldLoadFile(string requestPath)
    {
        await using var host = await ExplorerTestHost.StartAsync(
            "scope-a",
            new ConnectorCatalogStorageOptions
            {
                Enabled = true,
                UseNyxProxy = false,
                BaseUrl = "http://chrono-storage.test",
                Bucket = "aevatar-studio",
                UserConfigPrefix = "user-prefix",
            });
        host.StorageServer.StoreText("aevatar-studio", "user-prefix/scope-a/chat-histories/NyxIdChat:scope-a.jsonl", "{\"id\":\"m1\"}\n");

        var response = await host.Client.GetAsync(requestPath);
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        content.Should().Contain("\"id\":\"m1\"");
    }

    [Fact]
    public async Task GetFileAsync_WhenWorkflowLivesUnderConfiguredPrefixOutsideTypeHeuristic_ShouldStillLoadFile()
    {
        // Legacy deployments that configured Cli:App:Connectors:ChronoStorage:Prefix
        // may have uploaded workflow / script / media blobs under that prefix. The
        // explorer must still resolve them via the prefix fallback even after the
        // actor-backed catalog migration — role/connector catalogs are out of
        // chrono-storage, but this generic blob prefix is not.
        await using var host = await ExplorerTestHost.StartAsync(
            "scope-a",
            new ConnectorCatalogStorageOptions
            {
                Enabled = true,
                UseNyxProxy = false,
                BaseUrl = "http://chrono-storage.test",
                Bucket = "aevatar-studio",
                Prefix = "shared-prefix",
                UserConfigPrefix = "user-prefix",
            });
        host.StorageServer.StoreText("aevatar-studio", "shared-prefix/scope-a/workflows/draft.yaml", "name: draft");

        var response = await host.Client.GetAsync("/api/explorer/files/workflows/draft.yaml");
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().Be("name: draft");
    }

    [Fact]
    public async Task GetFileAsync_WhenDirectDownloadReturnsNotFound_ShouldFallbackToPresignedUrl()
    {
        await using var host = await ExplorerTestHost.StartAsync(
            "scope-a",
            failDirectDownload: true);
        host.StorageServer.StoreText("aevatar-studio", "scope-a/workflows/draft.yaml", "name: draft");

        var response = await host.Client.GetAsync("/api/explorer/files/workflows/draft.yaml");
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().Be("name: draft");
    }

    [Fact]
    public async Task GetManifestAsync_WhenChronoStorageProxyReturnsForbidden_ShouldReturnExplicitStorageError()
    {
        await using var host = await ExplorerTestHost.StartAsync(
            "scope-a",
            new ConnectorCatalogStorageOptions
            {
                Enabled = true,
                UseNyxProxy = true,
                NyxProxyBaseUrl = "http://chrono-storage.test",
                NyxProxyServiceSlug = "chrono-storage-service",
                Bucket = "aevatar-studio",
                UserConfigPrefix = string.Empty,
            },
            failListWithForbidden: true);

        var response = await host.Client.GetAsync("/api/explorer/manifest");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        document.RootElement.GetProperty("code").GetString().Should().Be(ChronoStorageServiceException.DefaultCode);
        document.RootElement.GetProperty("dependency").GetString().Should().Be(ChronoStorageServiceException.DependencyName);
        document.RootElement.GetProperty("upstreamStatusCode").GetInt32().Should().Be((int)HttpStatusCode.Forbidden);
        document.RootElement.GetProperty("message").GetString().Should().Contain("chrono-storage-service");
        document.RootElement.GetProperty("message").GetString().Should().Contain("approval");
    }

    private sealed class ExplorerTestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private ExplorerTestHost(WebApplication app, HttpClient client, InMemoryChronoStorageServer storageServer)
        {
            _app = app;
            Client = client;
            StorageServer = storageServer;
        }

        public HttpClient Client { get; }

        public InMemoryChronoStorageServer StorageServer { get; }

        public static async Task<ExplorerTestHost> StartAsync(
            string scopeId,
            ConnectorCatalogStorageOptions? storageOptions = null,
            bool failDirectDownload = false,
            bool failListWithForbidden = false)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var storageServer = new InMemoryChronoStorageServer(failDirectDownload, failListWithForbidden);
            builder.Services.AddSingleton<IAppScopeResolver>(new StubAppScopeResolver(scopeId));
            builder.Services.AddSingleton<IHttpClientFactory>(_ => storageServer.CreateHttpClientFactory());
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton<IOptions<ConnectorCatalogStorageOptions>>(Options.Create(storageOptions ?? new ConnectorCatalogStorageOptions
            {
                Enabled = true,
                UseNyxProxy = false,
                BaseUrl = "http://chrono-storage.test",
                Bucket = "aevatar-studio",
                UserConfigPrefix = string.Empty,
            }));
            builder.Services.AddSingleton<ChronoStorageCatalogBlobClient>();

            var app = builder.Build();
            app.MapExplorerEndpoints();
            await app.StartAsync();

            var addressFeature = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Server addresses are unavailable.");
            var client = new HttpClient
            {
                BaseAddress = new Uri(addressFeature.Addresses.Single()),
            };

            return new ExplorerTestHost(app, client, storageServer);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    private sealed class StubAppScopeResolver : IAppScopeResolver
    {
        private readonly AppScopeContext _context;

        public StubAppScopeResolver(string scopeId)
        {
            _context = new AppScopeContext(scopeId, "test");
        }

        public AppScopeContext? Resolve(Microsoft.AspNetCore.Http.HttpContext? httpContext = null) => _context;

        public bool HasAuthenticatedRequestWithoutScope(Microsoft.AspNetCore.Http.HttpContext? httpContext = null) => false;
    }

    private sealed class InMemoryChronoStorageServer
    {
        private readonly bool _failDirectDownload;
        private readonly bool _failListWithForbidden;
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

        public InMemoryChronoStorageServer(bool failDirectDownload = false, bool failListWithForbidden = false)
        {
            _failDirectDownload = failDirectDownload;
            _failListWithForbidden = failListWithForbidden;
        }

        public void StoreText(string bucket, string key, string content) =>
            _objects[$"{bucket}:{key}"] = Encoding.UTF8.GetBytes(content);

        public IHttpClientFactory CreateHttpClientFactory() =>
            new StubHttpClientFactory(new HttpClient(new Handler(_objects, _failDirectDownload, _failListWithForbidden))
            {
                BaseAddress = new Uri("http://chrono-storage.test/"),
            });

        private sealed class Handler : HttpMessageHandler
        {
            private readonly IReadOnlyDictionary<string, byte[]> _objects;
            private readonly bool _failDirectDownload;
            private readonly bool _failListWithForbidden;

            public Handler(IReadOnlyDictionary<string, byte[]> objects, bool failDirectDownload, bool failListWithForbidden)
            {
                _objects = objects;
                _failDirectDownload = failDirectDownload;
                _failListWithForbidden = failListWithForbidden;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is required.");
                var path = uri.AbsolutePath.Trim('/');
                if (string.Equals(uri.Host, "download.local", StringComparison.OrdinalIgnoreCase))
                {
                    var segments = uri.AbsolutePath.Trim('/').Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (segments.Length < 2)
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                    }

                    var bucket = segments[0];
                    var key = Uri.UnescapeDataString(segments[1]);
                    if (!_objects.TryGetValue($"{bucket}:{key}", out var presignedPayload))
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                    }

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(presignedPayload),
                    });
                }

                if (request.Method == HttpMethod.Get &&
                    path.EndsWith("api/buckets/aevatar-studio/objects/download", StringComparison.Ordinal))
                {
                    if (_failDirectDownload)
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                    }

                    var key = GetRequiredQueryValue(uri, "key");
                    if (!_objects.TryGetValue($"aevatar-studio:{key}", out var payload))
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                    }

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(payload),
                    });
                }

                if (request.Method == HttpMethod.Get &&
                    path.EndsWith("api/buckets/aevatar-studio/presigned-url", StringComparison.Ordinal))
                {
                    var key = GetRequiredQueryValue(uri, "key");
                    if (!_objects.ContainsKey($"aevatar-studio:{key}"))
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                    }

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new
                        {
                            data = new
                            {
                                url = $"http://download.local/aevatar-studio/{Uri.EscapeDataString(key)}",
                            },
                        }),
                    });
                }

                if (request.Method == HttpMethod.Get &&
                    path.EndsWith("api/buckets/aevatar-studio/objects", StringComparison.Ordinal))
                {
                    if (_failListWithForbidden)
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                        {
                            Content = JsonContent.Create(new
                            {
                                message = "approval required",
                            }),
                        });
                    }

                    var prefix = GetRequiredQueryValue(uri, "prefix");
                    var objects = _objects
                        .Where(item => item.Key.StartsWith($"aevatar-studio:{prefix}", StringComparison.Ordinal))
                        .Select(item => new
                        {
                            key = item.Key["aevatar-studio:".Length..],
                            lastModified = "2026-04-02T00:00:00Z",
                            size = (long)item.Value.Length,
                        })
                        .ToList();

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new
                        {
                            data = new
                            {
                                objects,
                            },
                        }),
                    });
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
