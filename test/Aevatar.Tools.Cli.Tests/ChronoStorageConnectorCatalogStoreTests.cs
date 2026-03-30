using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using Aevatar.Studio.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Aevatar.Tools.Cli.Tests;

public sealed class ChronoStorageConnectorCatalogStoreTests
{
    [Fact]
    public async Task SaveAndGetCatalogAsync_WhenRemoteEnabled_ShouldRoundTripScopeCatalog()
    {
        using var workspaceRoot = new TemporaryDirectory();
        var storageServer = new InMemoryChronoStorageServer();
        var store = CreateStore(
            new InMemoryStudioWorkspaceStore(),
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
        saved.FilePath.Should().Be("chrono-storage://aevatar-studio/scope-alpha/connectors.json");
        loaded.Connectors.Should().BeEquivalentTo(catalog.Connectors);
        storageServer.Objects.Should().ContainKey("aevatar-studio:scope-alpha/connectors.json");
        Encoding.UTF8.GetString(storageServer.Objects["aevatar-studio:scope-alpha/connectors.json"])
            .Should().Contain("scope_web");
    }

    [Fact]
    public async Task ImportLocalCatalogAsync_WhenRemoteEnabled_ShouldUploadLocalCatalog()
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
        imported.Catalog.FilePath.Should().Be("chrono-storage://aevatar-studio/scope-import/connectors.json");
        imported.Catalog.Connectors.Should().BeEquivalentTo(localStore.ConnectorCatalog.Connectors);
        storageServer.Objects.Should().ContainKey("aevatar-studio:scope-import/connectors.json");
    }

    [Fact]
    public async Task DraftOperations_WhenRemoteEnabled_ShouldUseScopeScopedRemoteDraftFiles()
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
        savedDraft.FilePath.Should().Be("chrono-storage://aevatar-studio/scope-a/connectors.draft.json");
        loadedDraft.Draft.Should().BeEquivalentTo(draft.Draft);
        otherScopeDraft.FileExists.Should().BeFalse();
        otherScopeDraft.Draft.Should().BeNull();
        storageServer.Objects.Should().ContainKey("aevatar-studio:scope-a/connectors.draft.json");
    }

    [Fact]
    public async Task DeleteConnectorDraftAsync_WhenRemoteEnabled_ShouldDeleteRemoteDraftObject()
    {
        using var workspaceRoot = new TemporaryDirectory();
        var storageServer = new InMemoryChronoStorageServer();
        var store = CreateStore(
            new InMemoryStudioWorkspaceStore(),
            new StubAppScopeResolver("scope-delete"),
            storageServer.CreateHttpClientFactory(),
            workspaceRoot.Path);

        await store.SaveConnectorDraftAsync(
            new StoredConnectorDraft(
                HomeDirectory: string.Empty,
                FilePath: string.Empty,
                FileExists: false,
                UpdatedAtUtc: DateTimeOffset.Parse("2026-03-18T09:30:00Z"),
                Draft: CreateConnector("draft_connector", "https://draft.example.com")));

        await store.DeleteConnectorDraftAsync();

        storageServer.Objects.Should().NotContainKey("aevatar-studio:scope-delete/connectors.draft.json");
        var loadedDraft = await store.GetConnectorDraftAsync();
        loadedDraft.FileExists.Should().BeFalse();
        loadedDraft.Draft.Should().BeNull();
    }

    [Fact]
    public async Task GetConnectorCatalogAsync_WhenDownloadUrlReturnsNotFound_ShouldTreatCatalogAsMissing()
    {
        using var workspaceRoot = new TemporaryDirectory();
        var storageServer = new BrokenDownloadChronoStorageServer();
        storageServer.MarkObjectPresent("aevatar-studio", "scope-missing/connectors.json");
        var store = CreateStore(
            new InMemoryStudioWorkspaceStore(),
            new StubAppScopeResolver("scope-missing"),
            storageServer.CreateHttpClientFactory(),
            workspaceRoot.Path);

        var catalog = await store.GetConnectorCatalogAsync();

        catalog.FileExists.Should().BeFalse();
        catalog.Connectors.Should().BeEmpty();
        catalog.FilePath.Should().Be("chrono-storage://aevatar-studio/scope-missing/connectors.json");
    }

    private static ChronoStorageConnectorCatalogStore CreateStore(
        IStudioWorkspaceStore localStore,
        IAppScopeResolver scopeResolver,
        IHttpClientFactory httpClientFactory,
        string workspaceRoot)
    {
        var options = CreateOptions();
        var blobClient = new ChronoStorageCatalogBlobClient(scopeResolver, httpClientFactory, options);
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
            UseNyxProxy = false,
            BaseUrl = "http://chrono-storage.test",
            Bucket = "aevatar-studio",
            Prefix = string.Empty,
            RolesPrefix = string.Empty,
        });

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

        public IHttpClientFactory CreateHttpClientFactory() => new StubHttpClientFactory(new HttpClient(new Handler(this))
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
                if (string.Equals(uri.Host, "download.local", StringComparison.OrdinalIgnoreCase))
                {
                    return _server.HandleDownload(uri);
                }

                var path = uri.AbsolutePath.Trim('/');
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

                if (request.Method == HttpMethod.Post && string.Equals(path, "api/buckets/aevatar-studio/objects", StringComparison.Ordinal))
                {
                    var key = GetRequiredQueryValue(uri, "key");
                    _server._buckets.Add("aevatar-studio");
                    _server.Objects[$"aevatar-studio:{key}"] = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                    return CreateJsonResponse(HttpStatusCode.OK, new { data = new { stored = true }, error = (object?)null });
                }

                if (request.Method == HttpMethod.Delete && string.Equals(path, "api/buckets/aevatar-studio/objects", StringComparison.Ordinal))
                {
                    var key = GetRequiredQueryValue(uri, "key");
                    _server.Objects.Remove($"aevatar-studio:{key}");
                    return CreateJsonResponse(HttpStatusCode.OK, new { data = new { deleted = true }, error = (object?)null });
                }

                if (request.Method == HttpMethod.Get && string.Equals(path, "api/buckets/aevatar-studio/presigned-url", StringComparison.Ordinal))
                {
                    var key = GetRequiredQueryValue(uri, "key");
                    if (!_server.Objects.ContainsKey($"aevatar-studio:{key}"))
                    {
                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                    }

                    return CreateJsonResponse(
                        HttpStatusCode.OK,
                        new
                        {
                            data = new
                            {
                                url = $"http://download.local/aevatar-studio/{Uri.EscapeDataString(key)}",
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

    private sealed class BrokenDownloadChronoStorageServer
    {
        private readonly HashSet<string> _presentObjects = [];

        public void MarkObjectPresent(string bucket, string objectKey) =>
            _presentObjects.Add($"{bucket}:{objectKey}");

        public IHttpClientFactory CreateHttpClientFactory()
        {
            var client = new HttpClient(new Handler(_presentObjects))
            {
                BaseAddress = new Uri("http://chrono-storage.test/"),
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
                    string.Equals(uri.AbsolutePath.Trim('/'), "api/buckets/aevatar-studio/presigned-url", StringComparison.Ordinal))
                {
                    var key = GetRequiredQueryValue(uri, "key");
                    if (!_presentObjects.Contains($"aevatar-studio:{key}"))
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                    }

                    return Task.FromResult(CreateJsonResponse(
                        HttpStatusCode.OK,
                        new
                        {
                            data = new
                            {
                                presignedUrl = $"http://download.local/missing/{Uri.EscapeDataString(key)}",
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

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public StubHttpClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name) => _client;
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
