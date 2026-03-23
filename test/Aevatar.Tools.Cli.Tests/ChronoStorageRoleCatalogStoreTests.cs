using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aevatar.Tools.Cli.Hosting;
using Aevatar.Tools.Cli.Studio.Application.Abstractions;
using Aevatar.Tools.Cli.Studio.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Aevatar.Tools.Cli.Tests;

public sealed class ChronoStorageRoleCatalogStoreTests
{
    [Fact]
    public async Task SaveAndGetCatalogAsync_WhenRemoteEnabled_ShouldRoundTripEncryptedCatalog()
    {
        using var workspaceRoot = new TemporaryDirectory();
        var localStore = new InMemoryStudioWorkspaceStore();
        var storageServer = new InMemoryChronoStorageServer();
        var store = CreateStore(localStore, new StubAppScopeResolver("role-scope"), storageServer.CreateHttpClientFactory(), workspaceRoot.Path);
        var catalog = new StoredRoleCatalog(
            HomeDirectory: string.Empty,
            FilePath: string.Empty,
            FileExists: false,
            Roles:
            [
                CreateRole("assistant", "Main Assistant"),
            ]);

        var saved = await store.SaveRoleCatalogAsync(catalog);
        var loaded = await store.GetRoleCatalogAsync();

        saved.FileExists.Should().BeTrue();
        saved.FilePath.Should().StartWith("chrono-storage://studio-catalogs/");
        loaded.Roles.Should().BeEquivalentTo(catalog.Roles);

        storageServer.Objects.Should().ContainSingle();
        var persistedPayload = storageServer.Objects.Values.Single();
        Encoding.UTF8.GetString(persistedPayload).Should().NotContain("Main Assistant");
        Encoding.UTF8.GetString(persistedPayload).Should().NotContain("assistant");
    }

    [Fact]
    public async Task ImportLocalCatalogAsync_WhenRemoteEnabled_ShouldUploadLocalRoles()
    {
        using var workspaceRoot = new TemporaryDirectory();
        var localStore = new InMemoryStudioWorkspaceStore
        {
            RoleCatalog = new StoredRoleCatalog(
                HomeDirectory: "/tmp/.aevatar",
                FilePath: "/tmp/.aevatar/roles.json",
                FileExists: true,
                Roles:
                [
                    CreateRole("reviewer", "Reviewer"),
                ]),
        };
        var storageServer = new InMemoryChronoStorageServer();
        var store = CreateStore(localStore, new StubAppScopeResolver("role-import"), storageServer.CreateHttpClientFactory(), workspaceRoot.Path);

        var imported = await store.ImportLocalCatalogAsync();

        imported.SourceFilePath.Should().Be("/tmp/.aevatar/roles.json");
        imported.Catalog.FileExists.Should().BeTrue();
        imported.Catalog.Roles.Should().BeEquivalentTo(localStore.RoleCatalog.Roles);
        storageServer.Objects.Should().ContainSingle();
    }

    [Fact]
    public async Task DraftOperations_WhenRemoteEnabled_ShouldUseScopeScopedLocalDraftFiles()
    {
        using var workspaceRoot = new TemporaryDirectory();
        var storageServer = new InMemoryChronoStorageServer();
        var draft = new StoredRoleDraft(
            HomeDirectory: string.Empty,
            FilePath: string.Empty,
            FileExists: false,
            UpdatedAtUtc: DateTimeOffset.Parse("2026-03-18T09:30:00Z"),
            Draft: CreateRole("catalog_admin", "Catalog Admin"));
        var scopeAStore = CreateStore(new InMemoryStudioWorkspaceStore(), new StubAppScopeResolver("scope-a"), storageServer.CreateHttpClientFactory(), workspaceRoot.Path);
        var scopeBStore = CreateStore(new InMemoryStudioWorkspaceStore(), new StubAppScopeResolver("scope-b"), storageServer.CreateHttpClientFactory(), workspaceRoot.Path);

        var savedDraft = await scopeAStore.SaveRoleDraftAsync(draft);
        var loadedDraft = await scopeAStore.GetRoleDraftAsync();
        var otherScopeDraft = await scopeBStore.GetRoleDraftAsync();

        savedDraft.FileExists.Should().BeTrue();
        File.Exists(savedDraft.FilePath).Should().BeTrue();
        loadedDraft.Draft.Should().BeEquivalentTo(draft.Draft);
        otherScopeDraft.FileExists.Should().BeFalse();
        otherScopeDraft.Draft.Should().BeNull();
    }

    private static ChronoStorageRoleCatalogStore CreateStore(
        IStudioWorkspaceStore localStore,
        IAppScopeResolver scopeResolver,
        IHttpClientFactory httpClientFactory,
        string workspaceRoot)
    {
        var options = Options.Create(new ConnectorCatalogStorageOptions
        {
            Enabled = true,
            NyxProxyBaseUrl = "https://nyx.test",
            NyxProxyServiceSlug = "chrono-storage-service",
            Bucket = "studio-catalogs",
            Prefix = "aevatar/connectors/v1",
            RolesPrefix = "aevatar/roles/v1",
            MasterKey = "unit-test-master-key",
            CreateBucketIfMissing = true,
        });
        var masterKeyResolver = new ChronoStorageMasterKeyResolver(workspaceRoot, allowKeychain: false);
        var blobClient = new ChronoStorageCatalogBlobClient(scopeResolver, httpClientFactory, options, masterKeyResolver);
        return new ChronoStorageRoleCatalogStore(
            localStore,
            blobClient,
            options,
            Options.Create(new StudioStorageOptions
            {
                RootDirectory = workspaceRoot,
            }));
    }

    private static StoredRoleDefinition CreateRole(string id, string name) =>
        new(
            Id: id,
            Name: name,
            SystemPrompt: "You are helpful.",
            Provider: "openai-main",
            Model: "gpt-test",
            Connectors: ["scope_web"]);

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
        public StoredRoleCatalog RoleCatalog { get; set; } = new(
            HomeDirectory: string.Empty,
            FilePath: string.Empty,
            FileExists: false,
            Roles: []);

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
            throw new NotSupportedException();

        public Task<StoredConnectorCatalog> SaveConnectorCatalogAsync(StoredConnectorCatalog catalog, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredConnectorDraft> GetConnectorDraftAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredConnectorDraft> SaveConnectorDraftAsync(StoredConnectorDraft draft, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteConnectorDraftAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredRoleCatalog> GetRoleCatalogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(RoleCatalog);

        public Task<StoredRoleCatalog> SaveRoleCatalogAsync(StoredRoleCatalog catalog, CancellationToken cancellationToken = default)
        {
            RoleCatalog = catalog with { FileExists = true };
            return Task.FromResult(RoleCatalog);
        }

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

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
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

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aevatar-role-store-tests",
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
