using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Aevatar.GAgents.Channel.Lark;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using FoundationCredentialProvider = Aevatar.Foundation.Abstractions.Credentials.ICredentialProvider;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class LarkConversationAdapterRegistryTests
{
    [Fact]
    public async Task GetAsync_WithCredentialRef_ResolvesSecretForWebhookVerification()
    {
        const string credentialRef = "vault://channels/lark/reg-1";
        const string encryptKey = "encrypt-key-1";
        var credentialProvider = new RecordingCredentialProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [credentialRef] = encryptKey,
        });

        var services = new ServiceCollection();
        services.AddSingleton<FoundationCredentialProvider>(credentialProvider);
        using var serviceProvider = services.BuildServiceProvider();

        await using var registry = new LarkConversationAdapterRegistry(
            new LarkMessageComposer(),
            new LarkPayloadRedactor(),
            new StubHttpClientFactory(),
            NullLoggerFactory.Instance,
            serviceProvider);

        var adapter = await registry.GetAsync(new ChannelBotRegistrationEntry
        {
            Id = "reg-1",
            Platform = "lark",
            ScopeId = "scope-1",
            CredentialRef = credentialRef,
        }, CancellationToken.None);

        var payload = JsonSerializer.Serialize(new
        {
            schema = "2.0",
            header = new
            {
                event_type = "im.message.receive_v1",
            },
            @event = new
            {
                sender = new
                {
                    sender_id = new
                    {
                        open_id = "user-open-1",
                    },
                    sender_type = "user",
                },
                message = new
                {
                    chat_id = "group-1",
                    message_id = "msg-1",
                    message_type = "text",
                    chat_type = "group",
                    content = JsonSerializer.Serialize(new { text = "hello" }),
                },
            },
        });

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        const string nonce = "nonce";
        var response = await adapter.HandleWebhookAsync(new LarkWebhookRequest(
            Encoding.UTF8.GetBytes(payload),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Lark-Request-Timestamp"] = timestamp,
                ["X-Lark-Request-Nonce"] = nonce,
                ["X-Lark-Signature"] = ComputeLarkSignature(timestamp, nonce, encryptKey, payload),
            }));

        credentialProvider.ResolvedRefs.Should().ContainSingle(credentialRef);
        response.StatusCode.Should().Be(200);
        response.Activity.Should().NotBeNull();
        response.Activity!.Content.Text.Should().Be("hello");
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new()
            {
                BaseAddress = LarkConversationHostDefaults.BaseAddress,
            };
    }

    private static string ComputeLarkSignature(string timestamp, string nonce, string encryptKey, string body)
    {
        var raw = string.Concat(timestamp, nonce, encryptKey, body);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed class RecordingCredentialProvider(IReadOnlyDictionary<string, string> values) : FoundationCredentialProvider
    {
        public List<string> ResolvedRefs { get; } = [];

        public Task<string?> ResolveAsync(string credentialRef, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ResolvedRefs.Add(credentialRef);
            return Task.FromResult(values.TryGetValue(credentialRef, out var secret) ? secret : null);
        }
    }
}
