using System.Collections.Concurrent;
using System.Text.Json;

namespace Aevatar.Tools.MockNyxId;

/// <summary>
/// In-memory state for the mock NyxID server.
/// All data lives in ConcurrentDictionaries, seeded with defaults on construction.
/// </summary>
public sealed class MockStore
{
    public ConcurrentDictionary<string, JsonElement> Users { get; } = new();
    public ConcurrentDictionary<string, JsonElement> ChannelBots { get; } = new();
    public ConcurrentDictionary<string, JsonElement> ConversationRoutes { get; } = new();
    public ConcurrentDictionary<string, JsonElement> ApiKeys { get; } = new();
    public ConcurrentDictionary<string, JsonElement> Services { get; } = new();

    /// <summary>Proxy request log: slug → list of captured requests.</summary>
    public ConcurrentDictionary<string, ConcurrentBag<ProxyLogEntry>> ProxyLog { get; } = new();

    public MockStore(MockNyxIdOptions options)
    {
        // Seed default user
        Users["user-123"] = JsonSerializer.SerializeToElement(new
        {
            id = options.DefaultUserId,
            email = options.DefaultUserEmail,
            name = options.DefaultUserName,
            created_at = "2026-01-01T00:00:00Z",
        });

        // Seed default services for proxy/services discovery
        Services["api-lark-bot"] = JsonSerializer.SerializeToElement(new
        {
            slug = "api-lark-bot",
            name = "Lark Bot API",
            provider = "lark",
            proxy_url = "https://open.larksuite.com",
            base_url = "https://open.larksuite.com",
            connected = true,
        });

        Services["api-github"] = JsonSerializer.SerializeToElement(new
        {
            slug = "api-github",
            name = "GitHub API",
            provider = "github",
            proxy_url = "https://api.github.com",
            base_url = "https://api.github.com",
            connected = true,
        });

        // Seed default API key
        ApiKeys["key-1"] = JsonSerializer.SerializeToElement(new
        {
            id = "key-1",
            name = "test-key",
            key = "nyx_test_key_abc123",
            scopes = "proxy read write",
            created_at = "2026-01-01T00:00:00Z",
        });
    }
}

public sealed record ProxyLogEntry(
    string Slug,
    string Path,
    string Method,
    string? Body,
    DateTimeOffset Timestamp);
