using System.Net;
using System.Reflection;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Bootstrap.Extensions.AI;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.VoicePresence.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Bootstrap.Tests;

public sealed class NyxIdRealtimeProviderCredentialResolverTests
{
    private static readonly VoiceProviderSessionKey SessionKey =
        new("session-1", "owner-1", "transport-1", 1);

    private static VoiceProviderConfig Config() =>
        new() { ProviderName = "openai", Model = "gpt-realtime" };

    [Fact]
    public async Task ResolveApiKey_with_caller_token_mints_ga_shape_ephemeral_on_caller_identity()
    {
        var resolver = CreateResolver("""{"value":"ek_ga_123","expires_at":1781534157}""", out var handler);

        string? result;
        using (AgentToolContextScope.Push(ContextWithToken("caller-jwt")))
        {
            result = await resolver.ResolveApiKeyAsync(SessionKey, Config(), CancellationToken.None);
        }

        result.Should().Be("ek_ga_123");
        handler.RequestCount.Should().Be(1);
        handler.LastRequestPath.Should().EndWith("/api/v1/proxy/s/openai-realtime/v1/realtime/client_secrets");
        handler.LastAuthorization.Should().Be("Bearer caller-jwt");
    }

    [Fact]
    public async Task ResolveApiKey_supports_legacy_client_secret_shape()
    {
        var resolver = CreateResolver("""{"client_secret":{"value":"ek_beta_456","expires_at":1}}""", out _);

        using var scope = AgentToolContextScope.Push(ContextWithToken("caller-jwt"));
        var result = await resolver.ResolveApiKeyAsync(SessionKey, Config(), CancellationToken.None);

        result.Should().Be("ek_beta_456");
    }

    [Fact]
    public async Task ResolveApiKey_without_caller_token_returns_null_and_does_not_call_nyxid()
    {
        var resolver = CreateResolver("""{"value":"ek_unused"}""", out var handler);

        // No AgentToolContextScope pushed → no caller NyxID token in context.
        var result = await resolver.ResolveApiKeyAsync(SessionKey, Config(), CancellationToken.None);

        result.Should().BeNull();
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task ResolveApiKey_with_actor_side_credential_ref_mints_ephemeral_on_caller_identity()
    {
        var credentials = new StubCredentialProvider(("voice-tool:ref-1", "caller-jwt-from-ref"));
        var resolver = CreateResolver("""{"value":"ek_ga_from_ref"}""", out var handler, credentials);
        var sessionKey = SessionKey with
        {
            ToolContext = new VoiceToolExecutionContext
            {
                CredentialRef = "voice-tool:ref-1",
                ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5)),
            },
        };

        var result = await resolver.ResolveApiKeyAsync(sessionKey, Config(), CancellationToken.None);

        result.Should().Be("ek_ga_from_ref");
        handler.RequestCount.Should().Be(1);
        handler.LastAuthorization.Should().Be("Bearer caller-jwt-from-ref");
        credentials.RequestedRefs.Should().ContainSingle().Which.Should().Be("voice-tool:ref-1");
    }

    [Fact]
    public async Task ResolveApiKey_with_expired_actor_side_credential_ref_returns_null()
    {
        var credentials = new StubCredentialProvider(("voice-tool:expired", "caller-jwt-from-ref"));
        var resolver = CreateResolver("""{"value":"ek_unused"}""", out var handler, credentials);
        var sessionKey = SessionKey with
        {
            ToolContext = new VoiceToolExecutionContext
            {
                CredentialRef = "voice-tool:expired",
                ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(-1)),
            },
        };

        var result = await resolver.ResolveApiKeyAsync(sessionKey, Config(), CancellationToken.None);

        result.Should().BeNull();
        handler.RequestCount.Should().Be(0);
        credentials.RequestedRefs.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveApiKey_when_response_has_no_ephemeral_throws()
    {
        var resolver = CreateResolver("""{"unexpected":true}""", out _);

        using var scope = AgentToolContextScope.Push(ContextWithToken("caller-jwt"));
        var act = () => resolver.ResolveApiKeyAsync(SessionKey, Config(), CancellationToken.None);

        await act.Should().ThrowAsync<RealtimeProviderCredentialException>();
    }

    [Fact]
    public void Broker_is_enabled_by_default_with_conventional_slug()
    {
        // Voice broker is ON by default even when the deployment config/secret is absent or empty,
        // so a wiped config cannot disable voice. A configured slug still overrides the default.
        IsBrokerEnabled(BuildConfig(slug: null)).Should().BeTrue();
        IsBrokerEnabled(BuildConfig(slug: "")).Should().BeTrue();
        BuildBrokerOptions(BuildConfig(slug: null)).ServiceSlug.Should().Be("openai-realtime");
        BuildBrokerOptions(BuildConfig(slug: "custom-openai")).ServiceSlug.Should().Be("custom-openai");
    }

    [Fact]
    public void Broker_options_bind_slug_and_defaults_from_configuration()
    {
        var options = BuildBrokerOptions(BuildConfig(slug: "openai-realtime"));

        options.ServiceSlug.Should().Be("openai-realtime");
        options.MintPath.Should().Be("v1/realtime/client_secrets");
        options.Enabled.Should().BeTrue();
    }

    private static AgentToolExecutionContext ContextWithToken(string token) =>
        AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(token, null, null),
        };

    private static NyxIdRealtimeProviderCredentialResolver CreateResolver(
        string responseJson,
        out StubHttpMessageHandler handler,
        ICredentialProvider? credentialProvider = null)
    {
        var capturedHandler = new StubHttpMessageHandler(responseJson);
        handler = capturedHandler;
        var factory = new StubClientFactory(() => new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(capturedHandler),
            logger: null));
        var options = new NyxIdRealtimeProviderCredentialOptions { ServiceSlug = "openai-realtime" };
        return new NyxIdRealtimeProviderCredentialResolver(
            factory,
            options,
            NullLogger<NyxIdRealtimeProviderCredentialResolver>.Instance,
            credentialProvider);
    }

    private static IConfiguration BuildConfig(string? slug)
    {
        var values = new Dictionary<string, string?>();
        if (slug != null)
            values["Aevatar:VoicePresence:OpenAI:Nyxid:ServiceSlug"] = slug;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static bool IsBrokerEnabled(IConfiguration configuration)
    {
        var method = typeof(Aevatar.Bootstrap.Extensions.AI.ServiceCollectionExtensions).GetMethod(
            "IsNyxIdRealtimeBrokerEnabled",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)method.Invoke(null, [configuration])!;
    }

    private static NyxIdRealtimeProviderCredentialOptions BuildBrokerOptions(IConfiguration configuration)
    {
        var method = typeof(Aevatar.Bootstrap.Extensions.AI.ServiceCollectionExtensions).GetMethod(
            "BuildNyxIdRealtimeCredentialOptions",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (NyxIdRealtimeProviderCredentialOptions)method.Invoke(null, [configuration])!;
    }

    private sealed class StubClientFactory(Func<NyxIdApiClient> create) : INyxIdApiClientFactory
    {
        public NyxIdApiClient CreateClient() => create();
    }

    private sealed class StubHttpMessageHandler(string responseJson) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? LastRequestPath { get; private set; }
        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestPath = request.RequestUri?.AbsolutePath;
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson),
            });
        }
    }

    private sealed class StubCredentialProvider(params (string Ref, string Token)[] credentials) : ICredentialProvider
    {
        private readonly Dictionary<string, string> _credentials = credentials.ToDictionary(
            static credential => credential.Ref,
            static credential => credential.Token,
            StringComparer.Ordinal);

        public List<string> RequestedRefs { get; } = [];

        public Task<string?> ResolveAsync(string credentialRef, CancellationToken ct = default)
        {
            _ = ct;
            RequestedRefs.Add(credentialRef);
            return Task.FromResult(_credentials.GetValueOrDefault(credentialRef));
        }
    }
}
