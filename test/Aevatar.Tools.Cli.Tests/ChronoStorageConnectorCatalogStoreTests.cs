using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using Aevatar.Studio.Infrastructure.Storage;
using Aevatar.Tools.Cli.Hosting;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Aevatar.Tools.Cli.Tests;

public sealed class ChronoStorageConnectorCatalogStoreTests
{
    [Fact]
    public async Task SaveAndGetCatalogAsync_WhenRemoteEnabled_ShouldRoundTripEncryptedCatalog()
    {
        using var workspaceRoot = new TemporaryDirectory();
        var localStore = new InMemoryStudioWorkspaceStore();
        var storageServer = new InMemoryChronoStorageServer();
        var store = CreateStore(
            localStore,
            new StubAppScopeResolver("scope-alpha"),
            storageServer.CreateHttpClientFactory(),
            workspaceRoot.Path);
        var catalog = new StoredConnectorCatalog(
            HomeDirectory: string.Empty,
            FilePath: string.Empty,
            FileExists: false,
            Connectors:
            [
                CreateConnector("scope_web", "https://example.com/api"),
            ]);

        var saved = await store.SaveConnectorCatalogAsync(catalog);
        var loaded = await store.GetConnectorCatalogAsync();

        saved.FileExists.Should().BeTrue();
        saved.FilePath.Should().StartWith("chrono-storage://studio-connectors/");
        loaded.Connectors.Should().BeEquivalentTo(catalog.Connectors);

        storageServer.Objects.Should().ContainSingle();
        var persistedPayload = storageServer.Objects.Values.Single();
        Encoding.UTF8.GetString(persistedPayload).Should().NotContain("scope_web");
        Encoding.UTF8.GetString(persistedPayload).Should().NotContain("https://example.com/api");
    }

    [Fact]
    public async Task GetConnectorCatalogAsync_WhenLegacyOwnerKeyExists_ShouldLoadLegacyObject()
    {
        using var workspaceRoot = new TemporaryDirectory();
        var scopeResolver = new StubAppScopeResolver("scope-legacy");
        var storageServer = new InMemoryChronoStorageServer();
        var httpClientFactory = storageServer.CreateHttpClientFactory();
        var blobClient = CreateBlobClient(scopeResolver, httpClientFactory, workspaceRoot.Path);
        var remoteContext = blobClient.TryResolveContext("aevatar/connectors/v1", "catalog.json.enc")
                           ?? throw new InvalidOperationException("Expected remote context.");
        remoteContext.LegacyObjectKeys.Should().ContainSingle();

        await using var stream = new MemoryStream();
        await ConnectorCatalogJsonSerializer.WriteCatalogAsync(
            stream,
            [CreateConnector("legacy_connector", "https://legacy.example.com")],
            CancellationToken.None);
        var encryptedPayload = blobClient.EncryptPayload(remoteContext, stream.ToArray(), remoteContext.LegacyObjectKeys[0]);
        storageServer.Objects[$"{remoteContext.Bucket}:{remoteContext.LegacyObjectKeys[0]}"] = encryptedPayload;

        var store = new ChronoStorageConnectorCatalogStore(
            new InMemoryStudioWorkspaceStore(),
            blobClient,
            CreateOptions(),
            Options.Create(new StudioStorageOptions
            {
                RootDirectory = workspaceRoot.Path,
            }));

        var catalog = await store.GetConnectorCatalogAsync();

        catalog.FileExists.Should().BeTrue();
        catalog.Connectors.Should().ContainSingle(x => x.Name == "legacy_connector");
        catalog.FilePath.Should().Be($"chrono-storage://{remoteContext.Bucket}/{remoteContext.ObjectKey}");
    }

    [Fact]
    public async Task ImportLocalCatalogAsync_WhenRemoteCatalogMissing_ShouldUploadLocalCatalog()
    {
        using var workspaceRoot = new TemporaryDirectory();
        var localStore = new InMemoryStudioWorkspaceStore
        {
            ConnectorCatalog = new StoredConnectorCatalog(
                HomeDirectory: "/tmp/.aevatar",
                FilePath: "/tmp/.aevatar/connectors.json",
                FileExists: true,
                Connectors:
                [
                    CreateConnector("imported_http", "https://import.example.com"),
                ]),
        };
        var storageServer = new InMemoryChronoStorageServer();
        var store = CreateStore(
            localStore,
            new StubAppScopeResolver("scope-import"),
            storageServer.CreateHttpClientFactory(),
            workspaceRoot.Path);

        var imported = await store.ImportLocalCatalogAsync();

        imported.SourceFilePath.Should().Be("/tmp/.aevatar/connectors.json");
        imported.Catalog.FileExists.Should().BeTrue();
        imported.Catalog.Connectors.Should().BeEquivalentTo(localStore.ConnectorCatalog.Connectors);
        storageServer.Objects.Should().ContainSingle();
    }

    [Fact]
    public async Task GetConnectorCatalogAsync_WhenNyxProxyReturnsSameOriginDownloadUrl_ShouldReuseAuthorizedClient()
    {
        using var workspaceRoot = new TemporaryDirectory();
        var scopeResolver = new StubAppScopeResolver("scope-proxy-download");
        var storageServer = new SameOriginDownloadChronoStorageServer();
        var blobClient = CreateBlobClient(scopeResolver, storageServer.CreateHttpClientFactory(), workspaceRoot.Path);
        var remoteContext = blobClient.TryResolveContext("aevatar/connectors/v1", "catalog.json.enc")
                           ?? throw new InvalidOperationException("Expected remote context.");

        await using var stream = new MemoryStream();
        await ConnectorCatalogJsonSerializer.WriteCatalogAsync(
            stream,
            [CreateConnector("proxy_connector", "https://proxy.example.com")],
            CancellationToken.None);
        storageServer.Store(
            remoteContext.Bucket,
            remoteContext.ObjectKey,
            blobClient.EncryptPayload(remoteContext, stream.ToArray()));

        var store = new ChronoStorageConnectorCatalogStore(
            new InMemoryStudioWorkspaceStore(),
            blobClient,
            CreateOptions(),
            Options.Create(new StudioStorageOptions
            {
                RootDirectory = workspaceRoot.Path,
            }));

        var catalog = await store.GetConnectorCatalogAsync();

        catalog.FileExists.Should().BeTrue();
        catalog.Connectors.Should().ContainSingle(x => x.Name == "proxy_connector");
    }

    [Fact]
    public async Task GetConnectorCatalogAsync_WhenDownloadUrlReturnsNotFound_ShouldNotSilentlyReturnMissingCatalog()
    {
        using var workspaceRoot = new TemporaryDirectory();
        var scopeResolver = new StubAppScopeResolver("scope-missing-download");
        var storageServer = new BrokenDownloadChronoStorageServer();
        var blobClient = CreateBlobClient(scopeResolver, storageServer.CreateHttpClientFactory(), workspaceRoot.Path);
        var remoteContext = blobClient.TryResolveContext("aevatar/connectors/v1", "catalog.json.enc")
                           ?? throw new InvalidOperationException("Expected remote context.");

        storageServer.MarkObjectPresent(remoteContext.Bucket, remoteContext.ObjectKey);

        var store = new ChronoStorageConnectorCatalogStore(
            new InMemoryStudioWorkspaceStore(),
            blobClient,
            CreateOptions(),
            Options.Create(new StudioStorageOptions
            {
                RootDirectory = workspaceRoot.Path,
            }));

        var act = () => store.GetConnectorCatalogAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*could not be downloaded*");
    }

    [Fact]
    public async Task GetConnectorCatalogAsync_WhenCurrentDownloadFails_ShouldFallbackToLegacyObject()
    {
        using var workspaceRoot = new TemporaryDirectory();
        var scopeResolver = new StubAppScopeResolver("scope-fallback-download");
        var storageServer = new MixedDownloadChronoStorageServer();
        var blobClient = CreateBlobClient(scopeResolver, storageServer.CreateHttpClientFactory(), workspaceRoot.Path);
        var remoteContext = blobClient.TryResolveContext("aevatar/connectors/v1", "catalog.json.enc")
                           ?? throw new InvalidOperationException("Expected remote context.");
        remoteContext.LegacyObjectKeys.Should().ContainSingle();

        storageServer.MarkBrokenDownload(remoteContext.Bucket, remoteContext.ObjectKey);

        await using var stream = new MemoryStream();
        await ConnectorCatalogJsonSerializer.WriteCatalogAsync(
            stream,
            [CreateConnector("legacy_connector", "https://legacy.example.com")],
            CancellationToken.None);
        storageServer.Store(
            remoteContext.Bucket,
            remoteContext.LegacyObjectKeys[0],
            blobClient.EncryptPayload(remoteContext, stream.ToArray(), remoteContext.LegacyObjectKeys[0]));

        var store = new ChronoStorageConnectorCatalogStore(
            new InMemoryStudioWorkspaceStore(),
            blobClient,
            CreateOptions(),
            Options.Create(new StudioStorageOptions
            {
                RootDirectory = workspaceRoot.Path,
            }));

        var catalog = await store.GetConnectorCatalogAsync();

        catalog.FileExists.Should().BeTrue();
        catalog.Connectors.Should().ContainSingle(x => x.Name == "legacy_connector");
    }

    [Fact]
    public async Task DraftOperations_WhenRemoteEnabled_ShouldUseScopeScopedLocalDraftFiles()
    {
        using var workspaceRoot = new TemporaryDirectory();
        var storageServer = new InMemoryChronoStorageServer();
        var draft = new StoredConnectorDraft(
            HomeDirectory: string.Empty,
            FilePath: string.Empty,
            FileExists: false,
            UpdatedAtUtc: DateTimeOffset.Parse("2026-03-18T09:30:00Z"),
            Draft: CreateConnector("draft_connector", "https://draft.example.com"));
        var scopeAStore = CreateStore(
            new InMemoryStudioWorkspaceStore(),
            new StubAppScopeResolver("scope-a"),
            storageServer.CreateHttpClientFactory(),
            workspaceRoot.Path);
        var scopeBStore = CreateStore(
            new InMemoryStudioWorkspaceStore(),
            new StubAppScopeResolver("scope-b"),
            storageServer.CreateHttpClientFactory(),
            workspaceRoot.Path);

        var savedDraft = await scopeAStore.SaveConnectorDraftAsync(draft);
        var loadedDraft = await scopeAStore.GetConnectorDraftAsync();
        var otherScopeDraft = await scopeBStore.GetConnectorDraftAsync();

        savedDraft.FileExists.Should().BeTrue();
        File.Exists(savedDraft.FilePath).Should().BeTrue();
        loadedDraft.Draft.Should().BeEquivalentTo(draft.Draft);
        otherScopeDraft.FileExists.Should().BeFalse();
        otherScopeDraft.Draft.Should().BeNull();
    }

    private static ChronoStorageConnectorCatalogStore CreateStore(
        IStudioWorkspaceStore localStore,
        IAppScopeResolver scopeResolver,
        IHttpClientFactory httpClientFactory,
        string workspaceRoot)
    {
        var options = CreateOptions();
        var blobClient = CreateBlobClient(scopeResolver, httpClientFactory, workspaceRoot);
        return new ChronoStorageConnectorCatalogStore(
            localStore,
            blobClient,
            options,
            Options.Create(new StudioStorageOptions
            {
                RootDirectory = workspaceRoot,
            }));
    }

    private static IOptions<ConnectorCatalogStorageOptions> CreateOptions() =>
        Options.Create(new ConnectorCatalogStorageOptions
        {
            Enabled = true,
            UseNyxProxy = true,
            NyxProxyBaseUrl = "https://nyx.test",
            NyxProxyServiceSlug = "chrono-storage-service",
            Bucket = "studio-connectors",
            Prefix = "aevatar/connectors/v1",
            RolesPrefix = "aevatar/roles/v1",
            MasterKey = "unit-test-master-key",
            CreateBucketIfMissing = true,
        });

    private static ChronoStorageCatalogBlobClient CreateBlobClient(
        IAppScopeResolver scopeResolver,
        IHttpClientFactory httpClientFactory,
        string workspaceRoot)
    {
        var options = CreateOptions();
        var masterKeyResolver = new ChronoStorageMasterKeyResolver(workspaceRoot, allowKeychain: false);
        return new ChronoStorageCatalogBlobClient(scopeResolver, httpClientFactory, options, masterKeyResolver);
    }

    private static StoredConnectorDefinition CreateConnector(string name, string baseUrl) =>
        new(
            Name: name,
            Type: "http",
            Enabled: true,
            TimeoutMs: 30_000,
            Retry: 1,
            Http: new StoredHttpConnectorConfig(
                BaseUrl: baseUrl,
                AllowedMethods: ["POST"],
                AllowedPaths: ["/"],
                AllowedInputKeys: ["input"],
                DefaultHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Authorization"] = "Bearer demo",
                }),
            Cli: new StoredCliConnectorConfig(
                Command: string.Empty,
                FixedArguments: [],
                AllowedOperations: [],
                AllowedInputKeys: [],
                WorkingDirectory: string.Empty,
                Environment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            Mcp: new StoredMcpConnectorConfig(
                ServerName: string.Empty,
                Command: string.Empty,
                Arguments: [],
                Environment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                DefaultTool: string.Empty,
                AllowedTools: [],
                AllowedInputKeys: []));

    private sealed class StubAppScopeResolver : IAppScopeResolver
    {
        private readonly AppScopeContext _context;

        public StubAppScopeResolver(string scopeId)
        {
            _context = new AppScopeContext(scopeId, "test");
        }

        public AppScopeContext? Resolve(Microsoft.AspNetCore.Http.HttpContext? httpContext = null) => _context;
    }

    private sealed class InMemoryStudioWorkspaceStore : IStudioWorkspaceStore
    {
        public StoredConnectorCatalog ConnectorCatalog { get; set; } = new(
            HomeDirectory: string.Empty,
            FilePath: string.Empty,
            FileExists: false,
            Connectors: []);

        public Task<StudioWorkspaceSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StudioWorkspaceSettings("http://127.0.0.1:5100", [], "blue", "light"));

        public Task SaveSettingsAsync(StudioWorkspaceSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<StoredWorkflowFile>> ListWorkflowFilesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredWorkflowFile>>([]);

        public Task<StoredWorkflowFile?> GetWorkflowFileAsync(string workflowId, CancellationToken cancellationToken = default) =>
            Task.FromResult<StoredWorkflowFile?>(null);

        public Task<StoredWorkflowFile> SaveWorkflowFileAsync(StoredWorkflowFile workflowFile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StoredExecutionRecord>> ListExecutionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredExecutionRecord>>([]);

        public Task<StoredExecutionRecord?> GetExecutionAsync(string executionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<StoredExecutionRecord?>(null);

        public Task<StoredExecutionRecord> SaveExecutionAsync(StoredExecutionRecord execution, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredConnectorCatalog> GetConnectorCatalogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ConnectorCatalog);

        public Task<StoredConnectorCatalog> SaveConnectorCatalogAsync(StoredConnectorCatalog catalog, CancellationToken cancellationToken = default)
        {
            ConnectorCatalog = catalog with { FileExists = true };
            return Task.FromResult(ConnectorCatalog);
        }

        public Task<StoredConnectorDraft> GetConnectorDraftAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredConnectorDraft> SaveConnectorDraftAsync(StoredConnectorDraft draft, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteConnectorDraftAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredRoleCatalog> GetRoleCatalogAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredRoleCatalog> SaveRoleCatalogAsync(StoredRoleCatalog catalog, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredRoleDraft> GetRoleDraftAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredRoleDraft> SaveRoleDraftAsync(StoredRoleDraft draft, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteRoleDraftAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class InMemoryChronoStorageServer
    {
        private readonly HashSet<string> _buckets = [];

        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

        public IHttpClientFactory CreateHttpClientFactory() => new StubHttpClientFactory(CreateHttpClient());

        private HttpClient CreateHttpClient() => new(new Handler(this))
        {
            BaseAddress = new Uri("https://chrono-storage.test/"),
        };

        private sealed class Handler : HttpMessageHandler
        {
            private readonly InMemoryChronoStorageServer _server;

            public Handler(InMemoryChronoStorageServer server)
            {
                _server = server;
            }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is required.");
                if (string.Equals(uri.Host, "download.local", StringComparison.OrdinalIgnoreCase))
                {
                    return _server.HandleDownload(uri);
                }

                var path = StripProxyPrefix(uri.AbsolutePath.Trim('/'));
                if (request.Method == HttpMethod.Head && path.StartsWith("api/buckets/", StringComparison.Ordinal))
                {
                    var bucket = path["api/buckets/".Length..];
                    return new HttpResponseMessage(_server._buckets.Contains(bucket) ? HttpStatusCode.OK : HttpStatusCode.NotFound);
                }

                if (request.Method == HttpMethod.Post && string.Equals(path, "api/buckets", StringComparison.Ordinal))
                {
                    var payload = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                    var name = payload.GetProperty("name").GetString() ?? string.Empty;
                    _server._buckets.Add(name);
                    return CreateJsonResponse(HttpStatusCode.Created, new { data = new { name, created = true }, error = (object?)null });
                }

                if (request.Method == HttpMethod.Post && path.Contains("/objects", StringComparison.Ordinal))
                {
                    var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    var bucket = segments[2];
                    _server._buckets.Add(bucket);
                    var key = GetRequiredQueryValue(uri, "key");
                    _server.Objects[$"{bucket}:{key}"] = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                    return CreateJsonResponse(HttpStatusCode.OK, new { data = new { stored = true }, error = (object?)null });
                }

                if (request.Method == HttpMethod.Get && path.Contains("/presigned-url", StringComparison.Ordinal))
                {
                    var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    var bucket = segments[2];
                    var key = GetRequiredQueryValue(uri, "key");
                    if (!_server.Objects.ContainsKey($"{bucket}:{key}"))
                    {
                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                    }

                    var escapedKey = Uri.EscapeDataString(key);
                    return CreateJsonResponse(
                        HttpStatusCode.OK,
                        new
                        {
                            data = new
                            {
                                url = $"https://download.local/{bucket}/{escapedKey}",
                            },
                            error = (object?)null,
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

            private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, object payload) =>
                new(statusCode)
                {
                    Content = JsonContent.Create(payload),
                };

            private static string StripProxyPrefix(string path)
            {
                const string proxyPrefix = "api/v1/proxy/s/chrono-storage-service/";
                return path.StartsWith(proxyPrefix, StringComparison.Ordinal)
                    ? path[proxyPrefix.Length..]
                    : path;
            }
        }

        private HttpResponseMessage HandleDownload(Uri uri)
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
            var bucket = segments[0];
            var key = Uri.UnescapeDataString(segments[1]);
            if (!Objects.TryGetValue($"{bucket}:{key}", out var payload))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
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

    private sealed class NamedHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _defaultClient;
        private readonly IReadOnlyDictionary<string, HttpClient> _namedClients;

        public NamedHttpClientFactory(HttpClient defaultClient, IReadOnlyDictionary<string, HttpClient> namedClients)
        {
            _defaultClient = defaultClient;
            _namedClients = namedClients;
        }

        public HttpClient CreateClient(string name) =>
            _namedClients.TryGetValue(name, out var client)
                ? client
                : _defaultClient;
    }

    private sealed class SameOriginDownloadChronoStorageServer
    {
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

        public void Store(string bucket, string objectKey, byte[] payload) =>
            _objects[$"{bucket}:{objectKey}"] = payload;

        public IHttpClientFactory CreateHttpClientFactory()
        {
            var anonymousClient = new HttpClient(new Handler(_objects, allowProxyDownload: false))
            {
                BaseAddress = new Uri("https://nyx.test/"),
            };
            var authorizedClient = new HttpClient(new Handler(_objects, allowProxyDownload: true))
            {
                BaseAddress = new Uri("https://nyx.test/"),
            };

            return new NamedHttpClientFactory(
                anonymousClient,
                new Dictionary<string, HttpClient>(StringComparer.Ordinal)
                {
                    [ChronoStorageCatalogBlobClient.NyxProxyHttpClientName] = authorizedClient,
                });
        }

        private sealed class Handler : HttpMessageHandler
        {
            private readonly IReadOnlyDictionary<string, byte[]> _objects;
            private readonly bool _allowProxyDownload;

            public Handler(IReadOnlyDictionary<string, byte[]> objects, bool allowProxyDownload)
            {
                _objects = objects;
                _allowProxyDownload = allowProxyDownload;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is required.");
                var path = uri.AbsolutePath.Trim('/');
                const string proxyPrefix = "api/v1/proxy/s/chrono-storage-service/";
                if (!path.StartsWith(proxyPrefix, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Unhandled request {request.Method} {uri}.");
                }

                var relativePath = path[proxyPrefix.Length..];
                if (request.Method == HttpMethod.Get && relativePath.StartsWith("api/buckets/", StringComparison.Ordinal) &&
                    relativePath.Contains("/presigned-url", StringComparison.Ordinal))
                {
                    var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    var bucket = segments[2];
                    var key = GetRequiredQueryValue(uri, "key");
                    if (!_objects.ContainsKey($"{bucket}:{key}"))
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                    }

                    return Task.FromResult(CreateJsonResponse(
                        HttpStatusCode.OK,
                        new
                        {
                            data = new
                            {
                                presignedUrl = $"https://nyx.test/{proxyPrefix}downloads/{bucket}/{Uri.EscapeDataString(key)}",
                            },
                            error = (object?)null,
                        }));
                }

                if (request.Method == HttpMethod.Get && relativePath.StartsWith("downloads/", StringComparison.Ordinal))
                {
                    if (!_allowProxyDownload)
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                    }

                    var segments = relativePath.Split('/', 3, StringSplitOptions.RemoveEmptyEntries);
                    var bucket = segments[1];
                    var key = Uri.UnescapeDataString(segments[2]);
                    return Task.FromResult(
                        _objects.TryGetValue($"{bucket}:{key}", out var payload)
                            ? new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new ByteArrayContent(payload),
                            }
                            : new HttpResponseMessage(HttpStatusCode.NotFound));
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

    private sealed class BrokenDownloadChronoStorageServer
    {
        private readonly HashSet<string> _presentObjects = [];

        public void MarkObjectPresent(string bucket, string objectKey) =>
            _presentObjects.Add($"{bucket}:{objectKey}");

        public IHttpClientFactory CreateHttpClientFactory()
        {
            var client = new HttpClient(new Handler(_presentObjects))
            {
                BaseAddress = new Uri("https://nyx.test/"),
            };
            return new StubHttpClientFactory(client);
        }

        private sealed class Handler : HttpMessageHandler
        {
            private readonly IReadOnlySet<string> _presentObjects;

            public Handler(IReadOnlySet<string> presentObjects)
            {
                _presentObjects = presentObjects;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is required.");
                if (request.Method == HttpMethod.Get &&
                    uri.AbsolutePath.Contains("/presigned-url", StringComparison.Ordinal))
                {
                    var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                    var bucket = segments[^2];
                    var key = GetRequiredQueryValue(uri, "key");
                    if (!_presentObjects.Contains($"{bucket}:{key}"))
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                    }

                    return Task.FromResult(CreateJsonResponse(
                        HttpStatusCode.OK,
                        new
                        {
                            data = new
                            {
                                presignedUrl = $"https://download.local/missing/{bucket}/{Uri.EscapeDataString(key)}",
                            },
                            error = (object?)null,
                        }));
                }

                if (string.Equals(uri.Host, "download.local", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
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

    private sealed class MixedDownloadChronoStorageServer
    {
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
        private readonly HashSet<string> _brokenDownloads = [];

        public void Store(string bucket, string objectKey, byte[] payload) =>
            _objects[$"{bucket}:{objectKey}"] = payload;

        public void MarkBrokenDownload(string bucket, string objectKey) =>
            _brokenDownloads.Add($"{bucket}:{objectKey}");

        public IHttpClientFactory CreateHttpClientFactory()
        {
            var client = new HttpClient(new Handler(_objects, _brokenDownloads))
            {
                BaseAddress = new Uri("https://nyx.test/"),
            };
            return new StubHttpClientFactory(client);
        }

        private sealed class Handler : HttpMessageHandler
        {
            private readonly IReadOnlyDictionary<string, byte[]> _objects;
            private readonly IReadOnlySet<string> _brokenDownloads;

            public Handler(
                IReadOnlyDictionary<string, byte[]> objects,
                IReadOnlySet<string> brokenDownloads)
            {
                _objects = objects;
                _brokenDownloads = brokenDownloads;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is required.");
                if (request.Method == HttpMethod.Get &&
                    uri.AbsolutePath.Contains("/presigned-url", StringComparison.Ordinal))
                {
                    var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                    var bucket = segments[^2];
                    var key = GetRequiredQueryValue(uri, "key");
                    var objectId = $"{bucket}:{key}";
                    if (_objects.ContainsKey(objectId))
                    {
                        return Task.FromResult(CreateJsonResponse(
                            HttpStatusCode.OK,
                            new
                            {
                                data = new
                                {
                                    presignedUrl = $"https://download.local/ok/{bucket}/{Uri.EscapeDataString(key)}",
                                },
                                error = (object?)null,
                            }));
                    }

                    if (_brokenDownloads.Contains(objectId))
                    {
                        return Task.FromResult(CreateJsonResponse(
                            HttpStatusCode.OK,
                            new
                            {
                                data = new
                                {
                                    presignedUrl = $"https://download.local/missing/{bucket}/{Uri.EscapeDataString(key)}",
                                },
                                error = (object?)null,
                            }));
                    }

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }

                if (string.Equals(uri.Host, "download.local", StringComparison.OrdinalIgnoreCase))
                {
                    var segments = uri.AbsolutePath.Trim('/').Split('/', 3, StringSplitOptions.RemoveEmptyEntries);
                    var mode = segments[0];
                    var bucket = segments[1];
                    var key = Uri.UnescapeDataString(segments[2]);
                    if (!string.Equals(mode, "ok", StringComparison.Ordinal))
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                    }

                    return Task.FromResult(
                        _objects.TryGetValue($"{bucket}:{key}", out var payload)
                            ? new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new ByteArrayContent(payload),
                            }
                            : new HttpResponseMessage(HttpStatusCode.NotFound));
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

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aevatar-connector-store-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
