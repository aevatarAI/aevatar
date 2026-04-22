using System.Text;
using System.Text.Json;
using Aevatar.GAgents.Channel.Lark;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using FoundationCredentialProvider = Aevatar.Foundation.Abstractions.Credentials.ICredentialProvider;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class LarkConversationAdapterFactoryTests
{
    [Fact]
    public async Task CreateAsync_WithCredentialRef_ResolvesSecretForWebhookVerification()
    {
        const string credentialRef = "vault://channels/lark/reg-1";
        const string encryptKey = "encrypt-key-1";
        var credentialProvider = new RecordingCredentialProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [credentialRef] = encryptKey,
        });

        var factory = new LarkConversationAdapterFactory(
            new LarkMessageComposer(),
            new LarkPayloadRedactor(),
            new StubHttpClientFactory(),
            NullLoggerFactory.Instance,
            credentialProvider);

        var adapter = await factory.CreateAsync(new ChannelBotRegistrationEntry
        {
            Id = "reg-1",
            Platform = "lark",
            ScopeId = "scope-1",
            CredentialRef = credentialRef,
        }, CancellationToken.None);

        try
        {
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
                    ["X-Lark-Signature"] = LarkChannelAdapter.ComputeLarkSignature(timestamp, nonce, encryptKey, payload),
                }));

            credentialProvider.ResolvedRefs.Should().ContainSingle(credentialRef);
            response.StatusCode.Should().Be(200);
            response.Activity.Should().NotBeNull();
            response.Activity!.Content.Text.Should().Be("hello");
        }
        finally
        {
            await adapter.StopReceivingAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task CreateAsync_WithoutCredentialRef_FallsBackToLegacyEncryptKey()
    {
        const string encryptKey = "legacy-encrypt-key";

        var factory = new LarkConversationAdapterFactory(
            new LarkMessageComposer(),
            new LarkPayloadRedactor(),
            new StubHttpClientFactory(),
            NullLoggerFactory.Instance);

        var adapter = await factory.CreateAsync(new ChannelBotRegistrationEntry
        {
            Id = "reg-legacy",
            Platform = "lark",
            ScopeId = "scope-1",
            EncryptKey = encryptKey,
        }, CancellationToken.None);

        try
        {
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
                    ["X-Lark-Signature"] = LarkChannelAdapter.ComputeLarkSignature(timestamp, nonce, encryptKey, payload),
                }));

            response.StatusCode.Should().Be(200);
            response.Activity.Should().NotBeNull();
            response.Activity!.Content.Text.Should().Be("hello");
        }
        finally
        {
            await adapter.StopReceivingAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task CreateAsync_WithCredentialRefAndNoCredentialProvider_ShouldThrow()
    {
        var factory = new LarkConversationAdapterFactory(
            new LarkMessageComposer(),
            new LarkPayloadRedactor(),
            new StubHttpClientFactory(),
            NullLoggerFactory.Instance);

        var act = async () => await factory.CreateAsync(new ChannelBotRegistrationEntry
        {
            Id = "reg-1",
            Platform = "lark",
            ScopeId = "scope-1",
            CredentialRef = "vault://channels/lark/reg-1",
        }, CancellationToken.None);

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.Which.Message.Should().Contain("No ICredentialProvider is registered");
    }

    [Fact]
    public async Task CreateAsync_WithCredentialRefThatResolvesToWhitespace_ShouldThrow()
    {
        var factory = new LarkConversationAdapterFactory(
            new LarkMessageComposer(),
            new LarkPayloadRedactor(),
            new StubHttpClientFactory(),
            NullLoggerFactory.Instance,
            new RecordingCredentialProvider(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["vault://channels/lark/reg-1"] = "   ",
            }));

        var act = async () => await factory.CreateAsync(new ChannelBotRegistrationEntry
        {
            Id = "reg-1",
            Platform = "lark",
            ScopeId = "scope-1",
            CredentialRef = "vault://channels/lark/reg-1",
        }, CancellationToken.None);

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.Which.Message.Should().Contain("did not resolve to a usable Lark secret");
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new()
            {
                BaseAddress = LarkConversationHostDefaults.BaseAddress,
            };
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
