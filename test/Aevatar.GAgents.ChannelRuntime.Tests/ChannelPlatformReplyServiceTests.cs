using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ChannelRuntime.Adapters;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelPlatformReplyServiceTests
{
    [Fact]
    public async Task DeliverAsync_refreshes_expired_lark_token_and_replays_successfully()
    {
        var store = new FakeChannelBotRegistrationStore(MakeRegistration("old-access", "old-refresh"));
        var actorRuntime = BuildActorRuntime(store, out _);
        var handler = new ScriptedNyxHandler(
            proxyFactory: request =>
            {
                var auth = request.Headers.Authorization?.Parameter;
                var response = auth switch
                {
                    "old-access" => CreateJsonResponse(
                        HttpStatusCode.Unauthorized,
                        """{"error":"token_expired","error_code":2001,"message":"Token expired"}"""),
                    "fresh-access" => CreateJsonResponse(
                        HttpStatusCode.OK,
                        """{"code":0,"msg":"success","data":{"message_id":"om_fresh_1"}}"""),
                    _ => CreateJsonResponse(HttpStatusCode.BadRequest, """{"message":"unexpected auth"}"""),
                };
                return Task.FromResult(response);
            },
            refreshFactory: _ => Task.FromResult(CreateJsonResponse(
                HttpStatusCode.OK,
                """{"access_token":"fresh-access","refresh_token":"fresh-refresh","expires_in":900}""")));

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });
        var refreshService = new ChannelBotRegistrationTokenRefreshService(
            store,
            actorRuntime,
            nyxClient,
            NullLogger<ChannelBotRegistrationTokenRefreshService>.Instance);
        var replyService = new ChannelPlatformReplyService(
            store,
            nyxClient,
            refreshService,
            NullLogger<ChannelPlatformReplyService>.Instance);
        var adapter = new LarkPlatformAdapter(NullLogger<LarkPlatformAdapter>.Instance);

        var result = await replyService.DeliverAsync(
            adapter,
            "hello",
            MakeInbound(),
            store.Current,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Detail.Should().Contain("auto_refresh_succeeded");
        store.Current.NyxUserToken.Should().Be("fresh-access");
        store.Current.NyxRefreshToken.Should().Be("fresh-refresh");
        handler.ProxyCalls.Should().Be(2);
        handler.RefreshCalls.Should().Be(1);
    }

    [Fact]
    public async Task DeliverAsync_returns_manual_reauth_when_refresh_fails()
    {
        var store = new FakeChannelBotRegistrationStore(MakeRegistration("old-access", "old-refresh"));
        var actorRuntime = BuildActorRuntime(store, out _);
        var handler = new ScriptedNyxHandler(
            proxyFactory: _ => Task.FromResult(CreateJsonResponse(
                HttpStatusCode.Unauthorized,
                """{"error":"token_expired","error_code":2001,"message":"Token expired"}""")),
            refreshFactory: _ => Task.FromResult(CreateJsonResponse(
                HttpStatusCode.Unauthorized,
                """{"error":"invalid_token","message":"Refresh token expired"}""")));

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });
        var refreshService = new ChannelBotRegistrationTokenRefreshService(
            store,
            actorRuntime,
            nyxClient,
            NullLogger<ChannelBotRegistrationTokenRefreshService>.Instance);
        var replyService = new ChannelPlatformReplyService(
            store,
            nyxClient,
            refreshService,
            NullLogger<ChannelPlatformReplyService>.Instance);
        var adapter = new LarkPlatformAdapter(NullLogger<LarkPlatformAdapter>.Instance);

        var result = await replyService.DeliverAsync(
            adapter,
            "hello",
            MakeInbound(),
            store.Current,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.FailureKind.Should().Be(PlatformReplyFailureKind.Permanent);
        result.Detail.Should().Contain("manual_reauth_required");
        result.Detail.Should().Contain("refresh_failed");
        store.Current.NyxUserToken.Should().Be("old-access");
        store.Current.NyxRefreshToken.Should().Be("old-refresh");
        handler.ProxyCalls.Should().Be(1);
        handler.RefreshCalls.Should().Be(1);
    }

    [Fact]
    public async Task DeliverAsync_returns_manual_reauth_when_refresh_token_is_missing()
    {
        var store = new FakeChannelBotRegistrationStore(MakeRegistration("old-access", string.Empty));
        var actorRuntime = BuildActorRuntime(store, out _);
        var handler = new ScriptedNyxHandler(
            proxyFactory: _ => Task.FromResult(CreateJsonResponse(
                HttpStatusCode.Unauthorized,
                """{"error":"token_expired","error_code":2001,"message":"Token expired"}""")),
            refreshFactory: _ => throw new InvalidOperationException("refresh should not be called"));

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });
        var refreshService = new ChannelBotRegistrationTokenRefreshService(
            store,
            actorRuntime,
            nyxClient,
            NullLogger<ChannelBotRegistrationTokenRefreshService>.Instance);
        var replyService = new ChannelPlatformReplyService(
            store,
            nyxClient,
            refreshService,
            NullLogger<ChannelPlatformReplyService>.Instance);
        var adapter = new LarkPlatformAdapter(NullLogger<LarkPlatformAdapter>.Instance);

        var result = await replyService.DeliverAsync(
            adapter,
            "hello",
            MakeInbound(),
            store.Current,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Detail.Should().Contain("manual_reauth_required");
        result.Detail.Should().Contain("missing_nyx_refresh_token");
        handler.ProxyCalls.Should().Be(1);
        handler.RefreshCalls.Should().Be(0);
    }

    [Fact]
    public async Task DeliverAsync_single_flights_refresh_for_same_registration()
    {
        var store = new FakeChannelBotRegistrationStore(MakeRegistration("old-access", "old-refresh"));
        var actorRuntime = BuildActorRuntime(store, out var actor);
        var bothExpiredAttemptsObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expiredAttemptCount = 0;
        var handler = new ScriptedNyxHandler(
            proxyFactory: request =>
            {
                var auth = request.Headers.Authorization?.Parameter;
                if (string.Equals(auth, "old-access", StringComparison.Ordinal) &&
                    Interlocked.Increment(ref expiredAttemptCount) == 2)
                {
                    bothExpiredAttemptsObserved.TrySetResult();
                }

                var response = auth switch
                {
                    "old-access" => CreateJsonResponse(
                        HttpStatusCode.Unauthorized,
                        """{"error":"token_expired","error_code":2001,"message":"Token expired"}"""),
                    "fresh-access" => CreateJsonResponse(
                        HttpStatusCode.OK,
                        """{"code":0,"msg":"success","data":{"message_id":"om_shared"}}"""),
                    _ => CreateJsonResponse(HttpStatusCode.BadRequest, """{"message":"unexpected auth"}"""),
                };
                return Task.FromResult(response);
            },
            refreshFactory: async _ =>
            {
                await bothExpiredAttemptsObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                return CreateJsonResponse(
                    HttpStatusCode.OK,
                    """{"access_token":"fresh-access","refresh_token":"fresh-refresh","expires_in":900}""");
            });

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });
        var refreshService = new ChannelBotRegistrationTokenRefreshService(
            store,
            actorRuntime,
            nyxClient,
            NullLogger<ChannelBotRegistrationTokenRefreshService>.Instance);
        var replyService = new ChannelPlatformReplyService(
            store,
            nyxClient,
            refreshService,
            NullLogger<ChannelPlatformReplyService>.Instance);
        var adapter = new LarkPlatformAdapter(NullLogger<LarkPlatformAdapter>.Instance);

        var registration = store.Current;
        var first = replyService.DeliverAsync(adapter, "hello-1", MakeInbound(), registration, CancellationToken.None);
        var second = replyService.DeliverAsync(adapter, "hello-2", MakeInbound(), registration, CancellationToken.None);

        var results = await Task.WhenAll(first, second);

        results.Should().OnlyContain(static result => result.Succeeded);
        handler.RefreshCalls.Should().Be(1);
        await actor.Received(1).HandleEventAsync(
            Arg.Is<EventEnvelope>(envelope =>
                envelope.Payload != null &&
                envelope.Payload.Is(ChannelBotUpdateTokenCommand.Descriptor)),
            Arg.Any<CancellationToken>());
    }

    private static ChannelBotRegistrationEntry MakeRegistration(string accessToken, string refreshToken) =>
        new()
        {
            Id = "bot-1",
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            NyxUserToken = accessToken,
            NyxRefreshToken = refreshToken,
            VerificationToken = "verify-token",
            ScopeId = "scope-1",
            CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

    private static InboundMessage MakeInbound() =>
        new()
        {
            Platform = "lark",
            ConversationId = "oc_chat_1",
            SenderId = "ou_sender_1",
            SenderName = "sender",
            Text = "hello",
            MessageId = "om_1",
            ChatType = "p2p",
        };

    private static IActorRuntime BuildActorRuntime(FakeChannelBotRegistrationStore store, out IActor actor)
    {
        actor = Substitute.For<IActor>();
        actor.Id.Returns(ChannelBotRegistrationGAgent.WellKnownId);
        actor.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var envelope = callInfo.ArgAt<EventEnvelope>(0);
                var command = envelope.Payload!.Unpack<ChannelBotUpdateTokenCommand>();
                store.ApplyUpdate(command);
                return Task.CompletedTask;
            });

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(actor));
        actorRuntime.CreateAsync<ChannelBotRegistrationGAgent>(
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(actor));
        return actorRuntime;
    }

    private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, string body) =>
        new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class FakeChannelBotRegistrationStore(ChannelBotRegistrationEntry registration)
        : IChannelBotRegistrationQueryPort
    {
        private readonly object _gate = new();
        private ChannelBotRegistrationEntry _registration = registration.Clone();
        private long _stateVersion = 1;

        public ChannelBotRegistrationEntry Current
        {
            get
            {
                lock (_gate)
                {
                    return _registration.Clone();
                }
            }
        }

        public Task<ChannelBotRegistrationEntry?> GetAsync(string registrationId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return Task.FromResult<ChannelBotRegistrationEntry?>(
                    string.Equals(_registration.Id, registrationId, StringComparison.Ordinal)
                        ? _registration.Clone()
                        : null);
            }
        }

        public Task<long?> GetStateVersionAsync(string registrationId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return Task.FromResult<long?>(
                    string.Equals(_registration.Id, registrationId, StringComparison.Ordinal)
                        ? _stateVersion
                        : null);
            }
        }

        public Task<IReadOnlyList<ChannelBotRegistrationEntry>> QueryAllAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>([_registration.Clone()]);
            }
        }

        public void ApplyUpdate(ChannelBotUpdateTokenCommand command)
        {
            lock (_gate)
            {
                _registration.NyxUserToken = command.NyxUserToken;
                _registration.NyxRefreshToken = command.NyxRefreshToken;
                _stateVersion++;
            }
        }
    }

    private sealed class ScriptedNyxHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>>? proxyFactory = null,
        Func<HttpRequestMessage, Task<HttpResponseMessage>>? refreshFactory = null)
        : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _proxyFactory =
            proxyFactory ?? throw new ArgumentNullException(nameof(proxyFactory));
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _refreshFactory =
            refreshFactory ?? throw new ArgumentNullException(nameof(refreshFactory));

        public ScriptedNyxHandler(
            Func<HttpRequestMessage, HttpResponseMessage> proxyFactory,
            Func<HttpRequestMessage, HttpResponseMessage> refreshFactory)
            : this(
                request => Task.FromResult(proxyFactory(request)),
                request => Task.FromResult(refreshFactory(request)))
        {
        }

        public int ProxyCalls => _proxyCalls;
        public int RefreshCalls => _refreshCalls;

        private int _proxyCalls;
        private int _refreshCalls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/api/v1/auth/refresh", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _refreshCalls);
                return await _refreshFactory(request);
            }

            if (path.Contains("/api/v1/proxy/s/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _proxyCalls);
                return await _proxyFactory(request);
            }

            throw new InvalidOperationException($"Unexpected Nyx request path: {path}");
        }
    }
}
