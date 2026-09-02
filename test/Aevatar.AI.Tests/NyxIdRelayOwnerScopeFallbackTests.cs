using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using RelayOptions = Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdRelayOwnerScopeFallbackTests
{
    private static readonly Type EndpointsType = typeof(NyxIdChatEndpoints);

    [Fact]
    public async Task HandleRelayWebhookAsync_WhenResolverReturnsNullAndUserTokenHasScopeId_ShouldRouteToOwnerScope()
    {
        var relay = CreateRelayInvocationDependencies(relayApiKeyId: "nyx-key-owner-fallback");
        var ownerToken = CreateUserTokenWithScope("owner-scope-1");
        var scopeResolver = new StubNyxIdRelayScopeResolver { ScopeId = null };
        var payload = """
            {
              "message_id":"msg-owner-scope-fallback",
              "correlation_id":"corr-owner-scope-fallback",
              "platform":"lark",
              "reply_token":"reply-token-owner-scope-fallback",
              "agent":{"api_key_id":"nyx-key-owner-fallback"},
              "conversation":{"platform_id":"ou_user_1","type":"private"},
              "sender":{"platform_id":"ou_user_1"},
              "content":{"text":"hello"}
            }
            """;
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .AddSingleton<INyxIdRelayScopeResolver>(scopeResolver)
                .BuildServiceProvider(),
        };
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        AttachRelayHeaders(context, relay with { UserToken = ownerToken }, payload, "msg-owner-scope-fallback", includeSubject: false);

        var runtime = new StubActorRuntime();
        var result = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            context,
            runtime,
            relay.Transport,
            relay.Validator,
            relay.Options,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        scopeResolver.LastNyxAgentApiKeyId.Should().Be("nyx-key-owner-fallback");
        var expectedActorId = BuildScopedRelayConversationActorId("owner-scope-1", "lark:dm:ou_user_1");
        runtime.CreateCalls.Should().ContainSingle(call =>
            call.Type == typeof(ConversationGAgent) &&
            call.Id == expectedActorId);
        var actor = runtime.Actors[expectedActorId];
        var relayInbound = actor.HandledEnvelopes.Single().Payload.Unpack<NyxRelayInboundActivity>();
        relayInbound.Activity.TransportExtras.NyxRegistrationScopeId.Should().Be("owner-scope-1");
        relayInbound.Activity.TransportExtras.NyxUserAccessToken.Should().Be(ownerToken);
    }

    private static async Task<IResult> InvokeResultAsync(string methodName, params object[] args)
    {
        var method = EndpointsType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!;
        var normalizedArgs = NormalizeEndpointArgs(method, args);
        var result = method.Invoke(null, normalizedArgs);
        return result switch
        {
            Task<IResult> task => await task,
            ValueTask<IResult> valueTask => await valueTask,
            _ => throw new InvalidOperationException($"Unexpected return type: {result?.GetType().FullName}"),
        };
    }

    private static object[] NormalizeEndpointArgs(MethodInfo method, object[] args)
    {
        var parameters = method.GetParameters();
        var normalized = args.ToList();
        var suppliedRuntime = normalized.OfType<IActorRuntime>().FirstOrDefault();

        if (parameters.Any(parameter => parameter.ParameterType == typeof(INyxIdRelayIngressPort)) &&
            normalized.All(arg => arg is not INyxIdRelayIngressPort))
        {
            var runtime = suppliedRuntime
                ?? throw new InvalidOperationException("Relay endpoint tests must supply an IActorRuntime.");
            normalized.Add(new NyxIdRelayIngressPort(
                runtime,
                new StubActorDispatchPort(runtime),
                NullLogger<NyxIdRelayIngressPort>.Instance));
        }

        if (parameters.Any(parameter => parameter.ParameterType == typeof(Aevatar.GAgents.Scheduled.INyxIdCurrentUserResolver)) &&
            normalized.All(arg => arg is not Aevatar.GAgents.Scheduled.INyxIdCurrentUserResolver))
        {
            normalized.Add(new StubNyxIdCurrentUserResolver());
        }

        var rebuilt = new List<object>(parameters.Length);
        var used = new bool[normalized.Count];
        foreach (var parameter in parameters)
        {
            var index = -1;
            for (var i = 0; i < normalized.Count; i++)
            {
                if (!used[i] && parameter.ParameterType.IsInstanceOfType(normalized[i]))
                {
                    index = i;
                    break;
                }
            }

            if (index >= 0)
            {
                used[index] = true;
                rebuilt.Add(normalized[index]);
                continue;
            }

            if (parameter.ParameterType == typeof(CancellationToken))
            {
                rebuilt.Add(CancellationToken.None);
                continue;
            }

            throw new InvalidOperationException($"Unable to normalize endpoint argument {parameter.Name}:{parameter.ParameterType.FullName}.");
        }

        return rebuilt.ToArray();
    }

    private static async Task<(int StatusCode, string Body)> ExecuteResultAsync(IResult result)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .BuildServiceProvider(),
        };
        await using var body = new MemoryStream();
        context.Response.Body = body;

        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    private static RelayInvocationDependencies CreateRelayInvocationDependencies(string relayApiKeyId)
    {
        const string baseUrl = "https://nyx.example.com";
        var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "kid-1" };
        var discoveryJson = $$"""
            {
              "issuer": "{{baseUrl}}",
              "jwks_uri": "{{baseUrl}}/jwks"
            }
            """;
        var jwksJson = JsonSerializer.Serialize(new
        {
            keys = new[] { JsonWebKeyConverter.ConvertFromSecurityKey(key) },
        });
        var options = new RelayOptions
        {
            OidcCacheTtlSeconds = 60,
            JwtClockSkewSeconds = 60,
            RequireMessageIdHeader = true,
            JwksKidMissRefreshCooldownSeconds = 0,
        };
        var validator = new NyxIdRelayAuthValidator(
            new NyxRelayTestHttpClientFactory(new HttpClient(new NyxRelayOidcDocumentHandler(discoveryJson, jwksJson))),
            new NyxIdToolOptions { BaseUrl = baseUrl },
            options,
            NullLogger<NyxIdRelayAuthValidator>.Instance);

        return new RelayInvocationDependencies(
            new NyxIdRelayTransport(),
            validator,
            options,
            key,
            baseUrl,
            relayApiKeyId,
            UserToken: "user-token-1");
    }

    private static string CreateRelayJwt(
        RsaSecurityKey key,
        string issuer,
        string relayApiKeyId,
        string messageId,
        string platform,
        string jti,
        string bodySha256,
        bool includeSubject)
    {
        var claims = new List<Claim>
        {
            new("api_key_id", relayApiKeyId),
            new("message_id", messageId),
            new("platform", platform),
            new("body_sha256", bodySha256),
            new(JwtRegisteredClaimNames.Jti, jti),
            new("token_type", "relay_callback"),
        };
        if (includeSubject)
            claims.Insert(0, new Claim(JwtRegisteredClaimNames.Sub, relayApiKeyId));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = "channel-relay/callback",
            Subject = new ClaimsIdentity(claims),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        };

        return new JwtSecurityTokenHandler().CreateEncodedJwt(descriptor);
    }

    private static void AttachRelayHeaders(
        DefaultHttpContext context,
        RelayInvocationDependencies relay,
        string body,
        string messageId,
        bool includeSubject)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var platform = root.GetProperty("platform").GetString() ?? string.Empty;
        var correlationId = root.GetProperty("correlation_id").GetString() ?? string.Empty;
        var callbackToken = CreateRelayJwt(
            relay.SigningKey,
            relay.Issuer,
            relay.RelayApiKeyId,
            messageId,
            platform,
            correlationId,
            ComputeBodySha256Hex(Encoding.UTF8.GetBytes(body)),
            includeSubject);
        context.Request.Headers["X-NyxID-Callback-Token"] = callbackToken;
        context.Request.Headers["X-NyxID-User-Token"] = relay.UserToken;
        context.Request.Headers["X-NyxID-Message-Id"] = messageId;
    }

    private static string CreateUserTokenWithScope(string scopeId)
    {
        var token = new JwtSecurityToken(
            claims:
            [
                new Claim("scope_id", scopeId),
                new Claim("uid", "owner-user-1"),
                new Claim(JwtRegisteredClaimNames.Sub, "owner-sub-1"),
            ]);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string ComputeBodySha256Hex(byte[] bodyBytes) =>
        Convert.ToHexString(SHA256.HashData(bodyBytes)).ToLowerInvariant();

    private static string BuildScopedRelayConversationActorId(string scopeId, string canonicalKey)
    {
        var scopeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scopeId.Trim())))
            .ToLowerInvariant();
        return $"channel-conversation:{canonicalKey}:scope:{scopeHash}";
    }

    private sealed record RelayInvocationDependencies(
        NyxIdRelayTransport Transport,
        NyxIdRelayAuthValidator Validator,
        RelayOptions Options,
        RsaSecurityKey SigningKey,
        string Issuer,
        string RelayApiKeyId,
        string UserToken);

    private sealed class NyxRelayOidcDocumentHandler(string discoveryJson, string jwksJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var body = path.EndsWith("/jwks", StringComparison.Ordinal) ? jwksJson : discoveryJson;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class NyxRelayTestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubNyxIdRelayScopeResolver : INyxIdRelayScopeResolver
    {
        public string? ScopeId { get; init; }
        public string? LastNyxAgentApiKeyId { get; private set; }

        public Task<string?> ResolveScopeIdByApiKeyAsync(string nyxAgentApiKeyId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastNyxAgentApiKeyId = nyxAgentApiKeyId;
            return Task.FromResult(ScopeId);
        }
    }

    private sealed class StubNyxIdCurrentUserResolver : Aevatar.GAgents.Scheduled.INyxIdCurrentUserResolver
    {
        public Task<string?> ResolveCurrentUserIdAsync(string nyxIdAccessToken, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class StubActorRuntime : IActorRuntime
    {
        public Dictionary<string, StubActor> Actors { get; } = [];
        public List<(Type Type, string? Id)> CreateCalls { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? Guid.NewGuid().ToString("N");
            var actor = new StubActor(actorId);
            Actors[actorId] = actor;
            CreateCalls.Add((agentType, id));
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(Actors.GetValueOrDefault(id));
        public Task<bool> ExistsAsync(string id) => Task.FromResult(Actors.ContainsKey(id));
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubActorDispatchPort(IActorRuntime runtime) : IActorDispatchPort
    {
        public async Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            var actor = await runtime.GetAsync(actorId);
            if (actor is not null)
                await actor.HandleEventAsync(envelope, ct);
            return DispatchAdmissionFactory.Create(actorId, envelope);
        }
    }

    private sealed class StubActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new StubAgent();
        public List<EventEnvelope> HandledEnvelopes { get; } = [];
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            HandledEnvelopes.Add(envelope);
            return Task.CompletedTask;
        }
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubAgent : IAgent
    {
        public string Id => "agent";
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
