using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using Aevatar.Studio.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Tools.Cli.Tests;

public sealed class ChronoStorageUserMemoryStoreTests
{
    // ─── GetAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_WhenFileMissing_ShouldReturnEmptyDocument()
    {
        var store = CreateStore(new InMemoryChronoStorageServer(), scopeId: "scope-empty");

        var doc = await store.GetAsync();

        doc.Should().Be(UserMemoryDocument.Empty);
        doc.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_WhenFileExists_ShouldDeserializeEntries()
    {
        var server = new InMemoryChronoStorageServer();
        server.Objects["aevatar-studio:profiles/scope-read/user-memory.json"] = Encoding.UTF8.GetBytes(
            """
            {
              "version": 1,
              "entries": [
                {
                  "id": "aabbcc112233",
                  "category": "preference",
                  "content": "Prefers concise replies",
                  "source": "explicit",
                  "createdAt": 1000,
                  "updatedAt": 2000
                }
              ]
            }
            """);

        var store = CreateStore(server, scopeId: "scope-read");

        var doc = await store.GetAsync();

        doc.Version.Should().Be(1);
        doc.Entries.Should().HaveCount(1);
        var entry = doc.Entries[0];
        entry.Id.Should().Be("aabbcc112233");
        entry.Category.Should().Be(UserMemoryCategories.Preference);
        entry.Content.Should().Be("Prefers concise replies");
        entry.Source.Should().Be(UserMemorySources.Explicit);
        entry.CreatedAt.Should().Be(1000);
        entry.UpdatedAt.Should().Be(2000);
    }

    // ─── SaveAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_ShouldPersistDocument()
    {
        var server = new InMemoryChronoStorageServer();
        var store = CreateStore(server, scopeId: "scope-save");
        var entry = new UserMemoryEntry("id1", UserMemoryCategories.Instruction, "Always use code examples", UserMemorySources.Explicit, 100, 200);
        var doc = new UserMemoryDocument(1, [entry]);

        await store.SaveAsync(doc);

        server.Objects.Should().ContainKey("aevatar-studio:profiles/scope-save/user-memory.json");
        using var json = JsonDocument.Parse(server.Objects["aevatar-studio:profiles/scope-save/user-memory.json"]);
        json.RootElement.GetProperty("version").GetInt32().Should().Be(1);
        var entries = json.RootElement.GetProperty("entries").EnumerateArray().ToList();
        entries.Should().HaveCount(1);
        entries[0].GetProperty("id").GetString().Should().Be("id1");
        entries[0].GetProperty("category").GetString().Should().Be("instruction");
        entries[0].GetProperty("content").GetString().Should().Be("Always use code examples");
        entries[0].GetProperty("source").GetString().Should().Be("explicit");
    }

    // ─── AddEntryAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task AddEntryAsync_ShouldPersistAndReturnNewEntry()
    {
        var server = new InMemoryChronoStorageServer();
        var store = CreateStore(server, scopeId: "scope-add");

        var entry = await store.AddEntryAsync(
            UserMemoryCategories.Preference, "Uses Chinese for communication", UserMemorySources.Explicit);

        entry.Category.Should().Be(UserMemoryCategories.Preference);
        entry.Content.Should().Be("Uses Chinese for communication");
        entry.Source.Should().Be(UserMemorySources.Explicit);
        entry.Id.Should().HaveLength(12); // 6 bytes hex
        entry.CreatedAt.Should().BeGreaterThan(0);

        // Round-trip
        var doc = await store.GetAsync();
        doc.Entries.Should().HaveCount(1);
        doc.Entries[0].Id.Should().Be(entry.Id);
    }

    [Fact]
    public async Task AddEntryAsync_WhenAtCapacity_ShouldEvictOldestSameCategoryFirst()
    {
        const int max = 50;
        var server = new InMemoryChronoStorageServer();
        var store = CreateStore(server, scopeId: "scope-cap");

        // Fill with 50 preference entries (oldest first).
        for (var i = 0; i < max; i++)
        {
            await store.AddEntryAsync(UserMemoryCategories.Preference, $"pref-{i}", UserMemorySources.Inferred);
            await Task.Delay(1); // Ensure distinct timestamps
        }

        // The 51st entry should evict the oldest preference entry.
        var newEntry = await store.AddEntryAsync(UserMemoryCategories.Preference, "newest-pref", UserMemorySources.Explicit);

        var doc = await store.GetAsync();
        doc.Entries.Should().HaveCount(max);
        doc.Entries.Should().Contain(e => e.Id == newEntry.Id);
        doc.Entries.Should().NotContain(e => e.Content == "pref-0");
    }

    [Fact]
    public async Task AddEntryAsync_WhenNoSameCategoryToEvict_ShouldEvictGloballyOldest()
    {
        const int max = 50;
        var server = new InMemoryChronoStorageServer();
        var store = CreateStore(server, scopeId: "scope-global-evict");

        // Fill with 50 preference entries.
        for (var i = 0; i < max; i++)
        {
            await store.AddEntryAsync(UserMemoryCategories.Preference, $"pref-{i}", UserMemorySources.Inferred);
            await Task.Delay(1);
        }

        // Add an instruction entry — should evict the globally oldest (pref-0).
        var newEntry = await store.AddEntryAsync(UserMemoryCategories.Instruction, "always show code", UserMemorySources.Explicit);

        var doc = await store.GetAsync();
        doc.Entries.Should().HaveCount(max);
        doc.Entries.Should().Contain(e => e.Id == newEntry.Id);
        doc.Entries.Should().NotContain(e => e.Content == "pref-0");
    }

    // ─── RemoveEntryAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task RemoveEntryAsync_WhenEntryExists_ShouldRemoveAndReturnTrue()
    {
        var server = new InMemoryChronoStorageServer();
        var store = CreateStore(server, scopeId: "scope-remove");
        var entry = await store.AddEntryAsync(UserMemoryCategories.Context, "Working on Aevatar", UserMemorySources.Inferred);

        var removed = await store.RemoveEntryAsync(entry.Id);

        removed.Should().BeTrue();
        var doc = await store.GetAsync();
        doc.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveEntryAsync_WhenEntryMissing_ShouldReturnFalse()
    {
        var store = CreateStore(new InMemoryChronoStorageServer(), scopeId: "scope-missing-remove");

        var removed = await store.RemoveEntryAsync("nonexistent");

        removed.Should().BeFalse();
    }

    // ─── BuildPromptSectionAsync ───────────────────────────────────────────

    [Fact]
    public async Task BuildPromptSectionAsync_WhenEmpty_ShouldReturnEmptyString()
    {
        var store = CreateStore(new InMemoryChronoStorageServer(), scopeId: "scope-no-memory");

        var section = await store.BuildPromptSectionAsync();

        section.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildPromptSectionAsync_ShouldGroupByCategoryWithXmlTags()
    {
        var server = new InMemoryChronoStorageServer();
        var store = CreateStore(server, scopeId: "scope-prompt");
        await store.AddEntryAsync(UserMemoryCategories.Preference, "Concise replies", UserMemorySources.Explicit);
        await store.AddEntryAsync(UserMemoryCategories.Instruction, "Always show code", UserMemorySources.Explicit);
        await store.AddEntryAsync(UserMemoryCategories.Context, "Working on Aevatar", UserMemorySources.Inferred);

        var section = await store.BuildPromptSectionAsync();

        section.Should().StartWith("<user-memory>");
        section.Should().EndWith("</user-memory>");
        section.Should().Contain("## Preferences");
        section.Should().Contain("## Instructions");
        section.Should().Contain("## Context");
        section.Should().Contain("Concise replies");
        section.Should().Contain("Always show code");
        section.Should().Contain("Working on Aevatar");
    }

    [Fact]
    public async Task BuildPromptSectionAsync_WhenExceedsMaxChars_ShouldTruncate()
    {
        var server = new InMemoryChronoStorageServer();
        var store = CreateStore(server, scopeId: "scope-truncate");
        var longContent = new string('x', 300);
        await store.AddEntryAsync(UserMemoryCategories.Preference, longContent, UserMemorySources.Inferred);

        var section = await store.BuildPromptSectionAsync(maxChars: 100);

        section.Length.Should().BeLessThanOrEqualTo(100 + "</user-memory>".Length);
    }

    // ─── Scope isolation ───────────────────────────────────────────────────

    [Fact]
    public async Task StoreIsIsolatedByScope()
    {
        var server = new InMemoryChronoStorageServer();
        var storeA = CreateStore(server, scopeId: "user-a");
        var storeB = CreateStore(server, scopeId: "user-b");

        await storeA.AddEntryAsync(UserMemoryCategories.Preference, "A's pref", UserMemorySources.Explicit);

        var docB = await storeB.GetAsync();
        docB.Entries.Should().BeEmpty();
    }

    // ─── Factory / helpers ─────────────────────────────────────────────────

    private static ChronoStorageUserMemoryStore CreateStore(
        InMemoryChronoStorageServer server,
        string scopeId)
    {
        var options = Options.Create(new ConnectorCatalogStorageOptions
        {
            Enabled = true,
            UseNyxProxy = false,
            BaseUrl = "http://chrono-storage.test",
            Bucket = "aevatar-studio",
            UserConfigPrefix = "profiles",
        });
        var blobClient = new ChronoStorageCatalogBlobClient(
            new StubAppScopeResolver(scopeId),
            server.CreateHttpClientFactory(),
            options);
        return new ChronoStorageUserMemoryStore(
            blobClient,
            options,
            NullLogger<ChronoStorageUserMemoryStore>.Instance);
    }

    private sealed class StubAppScopeResolver : IAppScopeResolver
    {
        private readonly AppScopeContext _context;

        public StubAppScopeResolver(string scopeId)
        {
            _context = new AppScopeContext(scopeId, "test");
        }

        public AppScopeContext? Resolve(HttpContext? httpContext = null) => _context;
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

            public Handler(InMemoryChronoStorageServer server) => _server = server;

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is required.");
                var path = uri.AbsolutePath.Trim('/');

                if (request.Method == HttpMethod.Post &&
                    string.Equals(path, "api/buckets/aevatar-studio/objects", StringComparison.Ordinal))
                {
                    var key = GetQueryValue(uri, "key");
                    _server.Objects[$"aevatar-studio:{key}"] = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                    return CreateJsonResponse(HttpStatusCode.OK, new { data = new { stored = true }, error = (object?)null });
                }

                if (request.Method == HttpMethod.Get &&
                    string.Equals(path, "api/buckets/aevatar-studio/objects/download", StringComparison.Ordinal))
                {
                    var key = GetQueryValue(uri, "key");
                    if (!_server.Objects.TryGetValue($"aevatar-studio:{key}", out var payload))
                        return new HttpResponseMessage(HttpStatusCode.NotFound);

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(payload),
                    };
                }

                throw new InvalidOperationException($"Unhandled request {request.Method} {uri}.");
            }

            private static string GetQueryValue(Uri uri, string key) =>
                uri.Query.TrimStart('?')
                    .Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Split('=', 2))
                    .ToDictionary(
                        p => Uri.UnescapeDataString(p[0]),
                        p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : string.Empty,
                        StringComparer.Ordinal)
                    .GetValueOrDefault(key)
                ?? throw new InvalidOperationException($"Missing query key '{key}'.");

            private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, object payload) =>
                new(statusCode) { Content = JsonContent.Create(payload) };
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public StubHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
