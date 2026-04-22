using System.Net;
using System.Net.Http;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.ChannelRuntime.Adapters;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelPlatformReplyServiceTests
{
    [Fact]
    public async Task DeliverAsync_returns_manual_reauth_when_lark_token_is_expired()
    {
        var store = new FakeChannelBotRegistrationStore(MakeRegistration("old-access", "old-refresh"));
        var handler = new ScriptedNyxHandler(
            proxyFactory: request =>
            {
                var auth = request.Headers.Authorization?.Parameter;
                return Task.FromResult(auth switch
                {
                    "old-access" => CreateJsonResponse(
                        HttpStatusCode.Unauthorized,
                        """{"error":"token_expired","error_code":2001,"message":"Token expired"}"""),
                    _ => CreateJsonResponse(HttpStatusCode.BadRequest, """{"message":"unexpected auth"}"""),
                });
            },
            refreshFactory: _ => throw new InvalidOperationException("refresh should not be called"));

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });
        var replyService = new ChannelPlatformReplyService(
            store,
            nyxClient,
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
        result.Detail.Should().Contain("reply_path_token_refresh_disabled");
        store.Current.NyxUserToken.Should().Be("old-access");
        store.Current.NyxRefreshToken.Should().Be("old-refresh");
        handler.ProxyCalls.Should().Be(1);
        handler.RefreshCalls.Should().Be(0);
    }

    [Fact]
    public async Task DeliverAsync_returns_manual_reauth_when_refresh_token_is_missing()
    {
        var store = new FakeChannelBotRegistrationStore(MakeRegistration("old-access", string.Empty));
        var handler = new ScriptedNyxHandler(
            proxyFactory: _ => Task.FromResult(CreateJsonResponse(
                HttpStatusCode.Unauthorized,
                """{"error":"token_expired","error_code":2001,"message":"Token expired"}""")),
            refreshFactory: _ => throw new InvalidOperationException("refresh should not be called"));

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });
        var replyService = new ChannelPlatformReplyService(
            store,
            nyxClient,
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
        result.Detail.Should().Contain("missing_nyx_refresh_token");
        store.Current.NyxUserToken.Should().Be("old-access");
        store.Current.NyxRefreshToken.Should().BeEmpty();
        handler.ProxyCalls.Should().Be(1);
        handler.RefreshCalls.Should().Be(0);
    }

    [Fact]
    public async Task DeliverAsync_returns_upstream_failure_for_non_refreshable_auth_error()
    {
        var store = new FakeChannelBotRegistrationStore(MakeRegistration("old-access", "old-refresh"));
        var handler = new ScriptedNyxHandler(
            proxyFactory: _ => Task.FromResult(CreateJsonResponse(
                HttpStatusCode.Forbidden,
                """{"error":"forbidden","message":"bot muted"}""")),
            refreshFactory: _ => throw new InvalidOperationException("refresh should not be called"));

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });
        var replyService = new ChannelPlatformReplyService(
            store,
            nyxClient,
            NullLogger<ChannelPlatformReplyService>.Instance);
        var adapter = new LarkPlatformAdapter(NullLogger<LarkPlatformAdapter>.Instance);

        var result = await replyService.DeliverAsync(
            adapter,
            "hello",
            MakeInbound(),
            store.Current,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Detail.Should().NotContain("manual_reauth_required");
        result.Detail.Should().Contain("forbidden");
        handler.ProxyCalls.Should().Be(1);
        handler.RefreshCalls.Should().Be(0);
    }

    [Fact]
    public async Task DeliverAsync_concurrent_expired_lark_replies_fail_without_mutating_registration()
    {
        var store = new FakeChannelBotRegistrationStore(MakeRegistration("old-access", "old-refresh"));
        var handler = new ScriptedNyxHandler(
            proxyFactory: request =>
            {
                var auth = request.Headers.Authorization?.Parameter;
                return Task.FromResult(auth switch
                {
                    "old-access" => CreateJsonResponse(
                        HttpStatusCode.Unauthorized,
                        """{"error":"token_expired","error_code":2001,"message":"Token expired"}"""),
                    _ => CreateJsonResponse(HttpStatusCode.BadRequest, """{"message":"unexpected auth"}"""),
                });
            },
            refreshFactory: _ => throw new InvalidOperationException("refresh should not be called"));

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });
        var replyService = new ChannelPlatformReplyService(
            store,
            nyxClient,
            NullLogger<ChannelPlatformReplyService>.Instance);
        var adapter = new LarkPlatformAdapter(NullLogger<LarkPlatformAdapter>.Instance);

        var registration = store.Current;
        var first = replyService.DeliverAsync(adapter, "hello-1", MakeInbound(), registration, CancellationToken.None);
        var second = replyService.DeliverAsync(adapter, "hello-2", MakeInbound(), registration, CancellationToken.None);

        var results = await Task.WhenAll(first, second);

        results.Should().OnlyContain(static result =>
            !result.Succeeded &&
            result.FailureKind == PlatformReplyFailureKind.Permanent &&
            !string.IsNullOrWhiteSpace(result.Detail) &&
            result.Detail.Contains("manual_reauth_required", StringComparison.Ordinal));
        handler.ProxyCalls.Should().Be(2);
        handler.RefreshCalls.Should().Be(0);
        store.Current.NyxUserToken.Should().Be("old-access");
        store.Current.NyxRefreshToken.Should().Be("old-refresh");
    }

    [Fact]
    public async Task DeliverAsync_should_use_current_registration_and_return_success_when_adapter_succeeds()
    {
        var store = new FakeChannelBotRegistrationStore(MakeRegistration("new-access", "new-refresh"));
        var staleRegistration = MakeRegistration("old-access", "old-refresh");
        var adapter = new StubPlatformAdapter(
            platform: "lark",
            result: new PlatformReplyDeliveryResult(true, "ok"));
        var replyService = new ChannelPlatformReplyService(
            store,
            new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }),
            NullLogger<ChannelPlatformReplyService>.Instance);

        var result = await replyService.DeliverAsync(
            adapter,
            "hello",
            MakeInbound(),
            staleRegistration,
            CancellationToken.None);

        result.Should().Be(new PlatformReplyDeliveryResult(true, "ok"));
        adapter.Registrations.Should().ContainSingle();
        adapter.Registrations[0].NyxUserToken.Should().Be("new-access");
        adapter.Registrations[0].NyxRefreshToken.Should().Be("new-refresh");
    }

    [Fact]
    public async Task DeliverAsync_should_not_translate_non_lark_failures_to_manual_reauth()
    {
        var store = new FakeChannelBotRegistrationStore(MakeRegistration("old-access", "old-refresh"));
        var adapter = new StubPlatformAdapter(
            platform: "telegram",
            result: new PlatformReplyDeliveryResult(
                false,
                "lark_error=token_expired platform rejected",
                PlatformReplyFailureKind.Transient));
        var replyService = new ChannelPlatformReplyService(
            store,
            new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }),
            NullLogger<ChannelPlatformReplyService>.Instance);

        var result = await replyService.DeliverAsync(
            adapter,
            "hello",
            MakeInbound(),
            store.Current,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Detail.Should().Be("lark_error=token_expired platform rejected");
        result.Detail.Should().NotContain("manual_reauth_required");
        result.FailureKind.Should().Be(PlatformReplyFailureKind.Transient);
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

    private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, string body) =>
        new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class StubPlatformAdapter(
        string platform,
        PlatformReplyDeliveryResult result) : IPlatformAdapter
    {
        public string Platform { get; } = platform;

        public List<ChannelBotRegistrationEntry> Registrations { get; } = [];

        public Task<IResult?> TryHandleVerificationAsync(HttpContext http, ChannelBotRegistrationEntry registration) =>
            Task.FromResult<IResult?>(null);

        public Task<InboundMessage?> ParseInboundAsync(HttpContext http, ChannelBotRegistrationEntry registration) =>
            Task.FromResult<InboundMessage?>(null);

        public Task<PlatformReplyDeliveryResult> SendReplyAsync(
            string replyText,
            InboundMessage inbound,
            ChannelBotRegistrationEntry registration,
            NyxIdApiClient nyxClient,
            CancellationToken ct)
        {
            Registrations.Add(registration.Clone());
            return Task.FromResult(result);
        }
    }

    private sealed class FakeChannelBotRegistrationStore(ChannelBotRegistrationEntry registration)
        : IChannelBotRegistrationQueryPort, IChannelBotRegistrationRuntimeQueryPort
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
