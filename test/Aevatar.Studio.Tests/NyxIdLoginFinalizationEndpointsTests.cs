using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using Aevatar.GAgentService.Abstractions;
using Aevatar.Studio.Hosting.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Studio.Tests;

public sealed class NyxIdLoginFinalizationEndpointsTests
{
    [Fact]
    public async Task Config_ShouldReturnBrokerOAuthClientUsedByFinalizeExchange()
    {
        var result = await NyxIdLoginFinalizationEndpoints.HandleConfigAsync(
            new StubAevatarOAuthClientProvider(new AevatarOAuthClientSnapshot(
                ClientId: "broker-client-1",
                ClientIdIssuedAt: DateTimeOffset.UnixEpoch,
                HmacKid: "kid",
                HmacKey: [1, 2, 3],
                HmacKeyRotatedAt: DateTimeOffset.UnixEpoch,
                NyxIdAuthority: "https://nyx.example/",
                BrokerCapabilityObserved: true,
                BrokerCapabilityObservedAt: DateTimeOffset.UnixEpoch,
                OauthScope: "openid broker proxy")));

        var (statusCode, payload) = await ExecuteJsonAsync<NyxIdLoginConfigurationResponse>(result);

        statusCode.Should().Be(StatusCodes.Status200OK);
        payload.Should().BeEquivalentTo(new NyxIdLoginConfigurationResponse(
            "https://nyx.example",
            "broker-client-1",
            "openid broker proxy"));
    }

    [Fact]
    public async Task Config_ShouldUseAuthorizationScope_WhenSnapshotScopeIsMissing()
    {
        var result = await NyxIdLoginFinalizationEndpoints.HandleConfigAsync(
            new StubAevatarOAuthClientProvider(new AevatarOAuthClientSnapshot(
                ClientId: "broker-client-1",
                ClientIdIssuedAt: DateTimeOffset.UnixEpoch,
                HmacKid: "kid",
                HmacKey: [1, 2, 3],
                HmacKeyRotatedAt: DateTimeOffset.UnixEpoch,
                NyxIdAuthority: "https://nyx.example/",
                BrokerCapabilityObserved: true,
                BrokerCapabilityObservedAt: DateTimeOffset.UnixEpoch)));

        var (statusCode, payload) = await ExecuteJsonAsync<NyxIdLoginConfigurationResponse>(result);

        statusCode.Should().Be(StatusCodes.Status200OK);
        payload!.Scope.Should().Be(AevatarOAuthClientScopes.AuthorizationScope);
    }

    [Fact]
    public async Task Config_ShouldReturnUnavailable_WhenBrokerOAuthClientIsNotProvisioned()
    {
        var result = await NyxIdLoginFinalizationEndpoints.HandleConfigAsync(
            new NotProvisionedAevatarOAuthClientProvider());

        var context = NewHttpContext();
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Finalize_ShouldCommitOwnerBindingFromAuthorizationCodeExchange()
    {
        var broker = new RecordingBrokerCallback(new BrokerAuthorizationCodeResult(
            BindingId: "bnd-owner-1",
            IdToken: CreateIdToken(new { uid = "owner-user-1", email = "owner@example.com", name = "Owner" }),
            AccessToken: "access-token")
        {
            TokenType = "Bearer",
            ExpiresIn = 1800,
            Scope = "openid profile proxy",
        });
        var queryPort = new FakeExternalIdentityBindingQueryPort();
        var dispatch = new RecordingBindingDispatch();

        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest
            {
                Code = "auth-code",
                CodeVerifier = "pkce-verifier",
                RedirectUri = "http://localhost/auth/callback",
            },
            broker,
            queryPort,
            dispatch,
            NullLoggerFactory.Instance);

        var (statusCode, payload) = await ExecuteJsonAsync<NyxIdLoginFinalizationResponse>(result);

        broker.Exchanges.Should().ContainSingle().Which.Should().Be(("auth-code", "pkce-verifier", "http://localhost/auth/callback"));
        statusCode.Should().Be(StatusCodes.Status200OK);
        payload.Should().NotBeNull();
        payload!.BindingCommitted.Should().BeTrue();
        payload.Tokens.AccessToken.Should().Be("access-token");
        payload.Tokens.ExpiresIn.Should().Be(1800);
        payload.User.Sub.Should().Be("owner-user-1");
        payload.User.Email.Should().Be("owner@example.com");
        dispatch.Commands.Should().ContainSingle().Which.Should().BeEquivalentTo(new CommitBindingCommand
        {
            ExternalSubject = new ExternalSubjectRef
            {
                Platform = OwnerScope.NyxIdPlatform,
                Tenant = string.Empty,
                ExternalUserId = "owner-user-1",
            },
            BindingId = "bnd-owner-1",
        });
    }

    [Fact]
    public async Task Finalize_ShouldBeIdempotent_WhenOwnerBindingAlreadyExists()
    {
        var broker = new RecordingBrokerCallback(new BrokerAuthorizationCodeResult(
            BindingId: "bnd-new",
            IdToken: CreateIdToken(new { uid = "owner-user-1" }),
            AccessToken: "access-token"));
        var queryPort = new FakeExternalIdentityBindingQueryPort();
        queryPort.Bindings[SubjectKey(OwnerSubject("owner-user-1"))] = "bnd-existing";
        var dispatch = new RecordingBindingDispatch();

        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest { Code = "auth-code", CodeVerifier = "pkce-verifier", RedirectUri = "http://localhost/auth/callback" },
            broker,
            queryPort,
            dispatch,
            NullLoggerFactory.Instance);

        var (statusCode, payload) = await ExecuteJsonAsync<NyxIdLoginFinalizationResponse>(result);

        statusCode.Should().Be(StatusCodes.Status200OK);
        payload!.BindingCommitted.Should().BeFalse();
        dispatch.Commands.Should().BeEmpty();
        broker.RevokedBindingIds.Should().ContainSingle().Which.Should().Be("bnd-new");
    }

    [Fact]
    public async Task Finalize_ShouldRejectMissingCode()
    {
        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest { CodeVerifier = "pkce-verifier", RedirectUri = "http://localhost/auth/callback" },
            new RecordingBrokerCallback(new BrokerAuthorizationCodeResult("bnd", CreateIdToken(new { uid = "owner" }), "access")),
            new FakeExternalIdentityBindingQueryPort(),
            new RecordingBindingDispatch(),
            NullLoggerFactory.Instance);

        var context = NewHttpContext();
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Finalize_ShouldRejectMissingCodeVerifier()
    {
        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest { Code = "auth-code" },
            new RecordingBrokerCallback(new BrokerAuthorizationCodeResult("bnd", CreateIdToken(new { uid = "owner" }), "access")),
            new FakeExternalIdentityBindingQueryPort(),
            new RecordingBindingDispatch(),
            NullLoggerFactory.Instance);

        var context = NewHttpContext();
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Finalize_ShouldRejectMissingRedirectUri()
    {
        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest { Code = "auth-code", CodeVerifier = "pkce-verifier" },
            new RecordingBrokerCallback(new BrokerAuthorizationCodeResult("bnd", CreateIdToken(new { uid = "owner" }), "access")),
            new FakeExternalIdentityBindingQueryPort(),
            new RecordingBindingDispatch(),
            NullLoggerFactory.Instance);

        var context = NewHttpContext();
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Finalize_ShouldReturnConflict_WhenExchangeDoesNotReturnBindingId()
    {
        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest { Code = "auth-code", CodeVerifier = "pkce-verifier", RedirectUri = "http://localhost/auth/callback" },
            new RecordingBrokerCallback(new BrokerAuthorizationCodeResult(null, CreateIdToken(new { uid = "owner" }), "access")),
            new FakeExternalIdentityBindingQueryPort(),
            new RecordingBindingDispatch(),
            NullLoggerFactory.Instance);

        var context = NewHttpContext();
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Finalize_ShouldReturnBadGateway_WhenExchangeDoesNotReturnAccessToken()
    {
        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest { Code = "auth-code", CodeVerifier = "pkce-verifier", RedirectUri = "http://localhost/auth/callback" },
            new RecordingBrokerCallback(new BrokerAuthorizationCodeResult("bnd", CreateIdToken(new { uid = "owner" }), null)),
            new FakeExternalIdentityBindingQueryPort(),
            new RecordingBindingDispatch(),
            NullLoggerFactory.Instance);

        var context = NewHttpContext();
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
    }

    [Fact]
    public async Task Finalize_ShouldReturnBadGatewayAndRevokeBinding_WhenSubjectIsMissing()
    {
        var broker = new RecordingBrokerCallback(new BrokerAuthorizationCodeResult("bnd", CreateIdToken(new { email = "owner@example.com" }), "access"));

        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest { Code = "auth-code", CodeVerifier = "pkce-verifier", RedirectUri = "http://localhost/auth/callback" },
            broker,
            new FakeExternalIdentityBindingQueryPort(),
            new RecordingBindingDispatch(),
            NullLoggerFactory.Instance);

        var context = NewHttpContext();
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        broker.RevokedBindingIds.Should().ContainSingle().Which.Should().Be("bnd");
    }

    [Fact]
    public async Task Finalize_ShouldReturnUnavailable_WhenBindingDispatchFails()
    {
        var broker = new RecordingBrokerCallback(new BrokerAuthorizationCodeResult(
            BindingId: "bnd-owner-1",
            IdToken: CreateIdToken(new { uid = "owner-user-1" }),
            AccessToken: "access-token"));

        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest { Code = "auth-code", CodeVerifier = "pkce-verifier", RedirectUri = "http://localhost/auth/callback" },
            broker,
            new FakeExternalIdentityBindingQueryPort(),
            new RejectingBindingDispatch(),
            NullLoggerFactory.Instance);

        var context = NewHttpContext();
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        broker.RevokedBindingIds.Should().ContainSingle().Which.Should().Be("bnd-owner-1");
    }

    private static string CreateIdToken(object payload)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "none" }));
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        return $"{header}.{body}.";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static HttpContext NewHttpContext()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
        };
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<(int StatusCode, T? Payload)> ExecuteJsonAsync<T>(IResult result)
    {
        var context = NewHttpContext();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        return (context.Response.StatusCode, JsonSerializer.Deserialize<T>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static ExternalSubjectRef OwnerSubject(string externalUserId) =>
        new()
        {
            Platform = OwnerScope.NyxIdPlatform,
            Tenant = string.Empty,
            ExternalUserId = externalUserId,
        };

    private static string SubjectKey(ExternalSubjectRef subject) =>
        $"{subject.Platform}:{subject.Tenant}:{subject.ExternalUserId}";

    private sealed class StubAevatarOAuthClientProvider(AevatarOAuthClientSnapshot snapshot) : IAevatarOAuthClientProvider
    {
        public Task<AevatarOAuthClientSnapshot> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class NotProvisionedAevatarOAuthClientProvider : IAevatarOAuthClientProvider
    {
        public Task<AevatarOAuthClientSnapshot> GetAsync(CancellationToken ct = default) =>
            throw new AevatarOAuthClientNotProvisionedException();
    }

    private sealed class RecordingBrokerCallback(BrokerAuthorizationCodeResult result) : INyxIdBrokerCallbackClient
    {
        public List<string> RevokedBindingIds { get; } = [];
        public List<(string Code, string CodeVerifier, string RedirectUri)> Exchanges { get; } = [];

        public Task<CallbackStateDecode> TryDecodeStateTokenAsync(string stateToken, CancellationToken ct = default) =>
            Task.FromResult(CallbackStateDecode.Failed("not_supported"));

        public Task<BrokerAuthorizationCodeResult> ExchangeAuthorizationCodeAsync(
            string authorizationCode,
            string codeVerifier,
            CancellationToken ct = default) =>
            Task.FromResult(result);

        public Task<BrokerAuthorizationCodeResult> ExchangeAuthorizationCodeAsync(
            string authorizationCode,
            string codeVerifier,
            string redirectUri,
            CancellationToken ct = default)
        {
            Exchanges.Add((authorizationCode, codeVerifier, redirectUri));
            return Task.FromResult(result);
        }

        public Task RevokeBindingByIdAsync(string bindingId, CancellationToken ct = default)
        {
            RevokedBindingIds.Add(bindingId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeExternalIdentityBindingQueryPort : IExternalIdentityBindingQueryPort
    {
        public Dictionary<string, string> Bindings { get; } = new(StringComparer.Ordinal);

        public Task<BindingId?> ResolveAsync(ExternalSubjectRef externalSubject, CancellationToken ct = default)
        {
            return Task.FromResult(Bindings.TryGetValue(SubjectKey(externalSubject), out var bindingId)
                ? new BindingId { Value = bindingId }
                : null);
        }
    }

    private sealed class RecordingBindingDispatch
        : ICommandDispatchService<CommitBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>
    {
        public List<CommitBindingCommand> Commands { get; } = [];

        public Task<CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>> DispatchAsync(
            CommitBindingCommand command,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>.Success(
                new ChannelIdentityOAuthAcceptedReceipt("actor", "command", "command")));
        }
    }

    private sealed class RejectingBindingDispatch
        : ICommandDispatchService<CommitBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>
    {
        public Task<CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>> DispatchAsync(
            CommitBindingCommand command,
            CancellationToken ct = default) =>
            Task.FromResult(CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>.Failure(
                ChannelIdentityOAuthDispatchError.InvalidTarget));
    }
}
