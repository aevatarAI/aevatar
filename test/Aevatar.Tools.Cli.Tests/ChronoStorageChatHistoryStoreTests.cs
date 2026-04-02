using System.Net;
using System.Net.Http.Json;
using System.Text;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using Aevatar.Studio.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Tools.Cli.Tests;

public sealed class ChronoStorageChatHistoryStoreTests
{
    [Fact]
    public async Task GetIndexAsync_ShouldBuildConversationListFromJsonlFiles()
    {
        var storageServer = new InMemoryChronoStorageServer();
        storageServer.StoreText(
            "aevatar-studio",
            "user-prefix/scope-a/chat-histories/index.json",
            "{\"conversations\":[{\"id\":\"stale\",\"title\":\"stale\"}]}");
        storageServer.StoreText(
            "aevatar-studio",
            "user-prefix/scope-a/chat-histories/NyxIdChat:scope-a.jsonl",
            """
            {"id":"u1","role":"user","content":"你好","timestamp":1711968000000,"status":"complete"}
            {"id":"a1","role":"assistant","content":"hi","timestamp":1711968060000,"status":"complete"}
            """);
        storageServer.StoreText(
            "aevatar-studio",
            "user-prefix/scope-a/chat-histories/workflowtest:run-1.jsonl",
            """
            {"id":"u2","role":"user","content":"workflow question","timestamp":1711968120000,"status":"complete"}
            {"id":"a2","role":"assistant","content":"workflow answer","timestamp":1711968180000,"status":"complete"}
            """);

        var store = CreateStore(storageServer);

        var index = await store.GetIndexAsync("scope-a");

        index.Conversations.Select(static conversation => conversation.Id).Should()
            .Equal("workflowtest:run-1", "NyxIdChat:scope-a");
        index.Conversations[0].Title.Should().Be("workflow question");
        index.Conversations[0].ServiceId.Should().Be("workflowtest");
        index.Conversations[0].ServiceKind.Should().Be("service");
        index.Conversations[0].MessageCount.Should().Be(2);
        index.Conversations[1].Title.Should().Be("你好");
        index.Conversations[1].ServiceId.Should().Be("nyxid-chat");
        index.Conversations[1].ServiceKind.Should().Be("nyxid-chat");
    }

    [Fact]
    public async Task GetIndexAsync_ShouldUseSidecarWhenFresh()
    {
        var storageServer = new InMemoryChronoStorageServer();
        // .jsonl written at T0
        storageServer.StoreText(
            "aevatar-studio",
            "user-prefix/scope-a/chat-histories/NyxIdChat:scope-a.jsonl",
            """
            {"id":"u1","role":"user","content":"original question","timestamp":1711968000000,"status":"complete"}
            """,
            "2026-04-01T00:00:00Z");
        // sidecar written at T1 (after .jsonl) — fresh
        storageServer.StoreText(
            "aevatar-studio",
            "user-prefix/scope-a/chat-histories/_meta/NyxIdChat:scope-a.json",
            """{"title":"original question","serviceId":"nyxid-chat","serviceKind":"nyxid-chat","createdAtMs":1711968000000,"updatedAtMs":1711968000000,"messageCount":1}""",
            "2026-04-01T00:01:00Z");

        var store = CreateStore(storageServer);
        var index = await store.GetIndexAsync("scope-a");

        index.Conversations.Should().HaveCount(1);
        index.Conversations[0].Title.Should().Be("original question");
        index.Conversations[0].MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task GetIndexAsync_ShouldFallbackWhenSidecarMissing()
    {
        var storageServer = new InMemoryChronoStorageServer();
        storageServer.StoreText(
            "aevatar-studio",
            "user-prefix/scope-a/chat-histories/NyxIdChat:scope-a.jsonl",
            """
            {"id":"u1","role":"user","content":"legacy conversation","timestamp":1711968000000,"status":"complete"}
            {"id":"a1","role":"assistant","content":"reply","timestamp":1711968060000,"status":"complete"}
            """);
        // No sidecar file at all

        var store = CreateStore(storageServer);
        var index = await store.GetIndexAsync("scope-a");

        index.Conversations.Should().HaveCount(1);
        index.Conversations[0].Title.Should().Be("legacy conversation");
        index.Conversations[0].MessageCount.Should().Be(2);

        // Backfill should have written a sidecar (best-effort, give it a moment)
        await Task.Delay(200);
        storageServer.Objects.Should().ContainKey("aevatar-studio:user-prefix/scope-a/chat-histories/_meta/NyxIdChat:scope-a.json");
    }

    [Fact]
    public async Task GetIndexAsync_ShouldFallbackWhenSidecarStale()
    {
        var storageServer = new InMemoryChronoStorageServer();
        // sidecar written at T0
        storageServer.StoreText(
            "aevatar-studio",
            "user-prefix/scope-a/chat-histories/_meta/conv-1.json",
            """{"title":"old title","serviceId":"nyxid-chat","serviceKind":"nyxid-chat","createdAtMs":1711968000000,"updatedAtMs":1711968000000,"messageCount":1}""",
            "2026-04-01T00:00:00Z");
        // .jsonl updated at T1 (after sidecar) — sidecar is stale
        storageServer.StoreText(
            "aevatar-studio",
            "user-prefix/scope-a/chat-histories/conv-1.jsonl",
            """
            {"id":"u1","role":"user","content":"new question","timestamp":1711968000000,"status":"complete"}
            {"id":"a1","role":"assistant","content":"new answer","timestamp":1711968060000,"status":"complete"}
            {"id":"u2","role":"user","content":"follow up","timestamp":1711968120000,"status":"complete"}
            """,
            "2026-04-01T01:00:00Z");

        var store = CreateStore(storageServer);
        var index = await store.GetIndexAsync("scope-a");

        index.Conversations.Should().HaveCount(1);
        index.Conversations[0].Title.Should().Be("new question");
        index.Conversations[0].MessageCount.Should().Be(3);
    }

    [Fact]
    public async Task SaveMessagesAsync_ShouldWriteSidecar()
    {
        var storageServer = new InMemoryChronoStorageServer();
        var store = CreateStore(storageServer);
        var messages = new[]
        {
            new StoredChatMessage("u1", "user", "hello sidecar", 1711968000000, "complete"),
            new StoredChatMessage("a1", "assistant", "hi there", 1711968060000, "complete"),
        };

        await store.SaveMessagesAsync("scope-a", "test-conv", messages);

        storageServer.Objects.Should().ContainKey("aevatar-studio:user-prefix/scope-a/chat-histories/test-conv.jsonl");
        storageServer.Objects.Should().ContainKey("aevatar-studio:user-prefix/scope-a/chat-histories/_meta/test-conv.json");
    }

    [Fact]
    public async Task DeleteConversationAsync_ShouldDeleteSidecarAndJsonl()
    {
        var storageServer = new InMemoryChronoStorageServer();
        storageServer.StoreText(
            "aevatar-studio",
            "user-prefix/scope-a/chat-histories/conv-del.jsonl",
            """{"id":"u1","role":"user","content":"bye","timestamp":1711968000000,"status":"complete"}""");
        storageServer.StoreText(
            "aevatar-studio",
            "user-prefix/scope-a/chat-histories/_meta/conv-del.json",
            """{"title":"bye","serviceId":"nyxid-chat","serviceKind":"nyxid-chat","createdAtMs":1711968000000,"updatedAtMs":1711968000000,"messageCount":1}""");

        var store = CreateStore(storageServer);
        await store.DeleteConversationAsync("scope-a", "conv-del");

        storageServer.Objects.Should().NotContainKey("aevatar-studio:user-prefix/scope-a/chat-histories/conv-del.jsonl");
        storageServer.Objects.Should().NotContainKey("aevatar-studio:user-prefix/scope-a/chat-histories/_meta/conv-del.json");
    }

    [Fact]
    public async Task SaveMessagesAsync_ShouldDeleteLegacyIndexFile()
    {
        var storageServer = new InMemoryChronoStorageServer();
        storageServer.StoreText(
            "aevatar-studio",
            "user-prefix/scope-a/chat-histories/index.json",
            "{\"conversations\":[]}");

        var store = CreateStore(storageServer);
        var messages = new[]
        {
            new StoredChatMessage("u1", "user", "hello", 1711968000000, "complete"),
        };

        await store.SaveMessagesAsync("scope-a", "NyxIdChat:scope-a", messages);

        storageServer.Objects.Should().ContainKey("aevatar-studio:user-prefix/scope-a/chat-histories/NyxIdChat:scope-a.jsonl");
        storageServer.Objects.Should().NotContainKey("aevatar-studio:user-prefix/scope-a/chat-histories/index.json");
    }

    private static ChronoStorageChatHistoryStore CreateStore(InMemoryChronoStorageServer storageServer)
    {
        var blobClient = new ChronoStorageCatalogBlobClient(
            new StubAppScopeResolver("scope-a"),
            storageServer.CreateHttpClientFactory(),
            Options.Create(new ConnectorCatalogStorageOptions
            {
                Enabled = true,
                UseNyxProxy = false,
                BaseUrl = "http://chrono-storage.test",
                Bucket = "aevatar-studio",
                Prefix = "connectors-prefix",
                RolesPrefix = "roles-prefix",
                UserConfigPrefix = "user-prefix",
            }));

        return new ChronoStorageChatHistoryStore(
            blobClient,
            Options.Create(new ConnectorCatalogStorageOptions
            {
                Enabled = true,
                UseNyxProxy = false,
                BaseUrl = "http://chrono-storage.test",
                Bucket = "aevatar-studio",
                Prefix = "connectors-prefix",
                RolesPrefix = "roles-prefix",
                UserConfigPrefix = "user-prefix",
            }),
            NullLogger<ChronoStorageChatHistoryStore>.Instance);
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
        public Dictionary<string, string> Timestamps { get; } = new(StringComparer.Ordinal);

        public void StoreText(string bucket, string key, string content, string? lastModified = null)
        {
            Objects[$"{bucket}:{key}"] = Encoding.UTF8.GetBytes(content);
            Timestamps[$"{bucket}:{key}"] = lastModified ?? "2026-04-01T00:00:00Z";
        }

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

                if (request.Method == HttpMethod.Get && string.Equals(path, "api/buckets/aevatar-studio/objects", StringComparison.Ordinal))
                {
                    var prefix = GetRequiredQueryValue(uri, "prefix");
                    var objects = _server.Objects
                        .Where(static entry => entry.Key.StartsWith("aevatar-studio:", StringComparison.Ordinal))
                        .Select(static entry => entry.Key["aevatar-studio:".Length..])
                        .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                        .OrderBy(static key => key, StringComparer.Ordinal)
                        .Select(key => new
                        {
                            key,
                            lastModified = _server.Timestamps.GetValueOrDefault($"aevatar-studio:{key}", "2026-04-01T00:00:00Z"),
                            size = _server.Objects[$"aevatar-studio:{key}"].LongLength,
                        })
                        .ToList();
                    return CreateJsonResponse(HttpStatusCode.OK, new { data = new { objects }, error = (object?)null });
                }

                if (request.Method == HttpMethod.Post && string.Equals(path, "api/buckets/aevatar-studio/objects", StringComparison.Ordinal))
                {
                    var key = GetRequiredQueryValue(uri, "key");
                    _server.Objects[$"aevatar-studio:{key}"] = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                    _server.Timestamps[$"aevatar-studio:{key}"] = DateTimeOffset.UtcNow.ToString("O");
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

                if (request.Method == HttpMethod.Delete && string.Equals(path, "api/buckets/aevatar-studio/objects", StringComparison.Ordinal))
                {
                    var key = GetRequiredQueryValue(uri, "key");
                    _server.Objects.Remove($"aevatar-studio:{key}");
                    return CreateJsonResponse(HttpStatusCode.OK, new { data = new { deleted = true }, error = (object?)null });
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
