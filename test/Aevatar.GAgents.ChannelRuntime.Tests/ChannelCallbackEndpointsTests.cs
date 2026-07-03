using System.Reflection;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Bootstrap.Hosting;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using Aevatar.Authentication.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelCallbackEndpointsTests
{
    private const string RawToken = "eyJhbGciOiJVTklUIn0.eyJzdWIiOiJ1c2VyLTEyMyJ9.c2lnbmF0dXJlLXZhbHVl";

    [Fact]
    public void MapChannelCallbackEndpoints_ShouldRequireAuthorization_ForDiagnosticErrors()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
        });

        var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;
        app.MapChannelCallbackEndpoints();

        var endpoint = routeBuilder.DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(route => string.Equals(route.RoutePattern.RawText, "/api/channels/diagnostics/errors", StringComparison.Ordinal));

        endpoint.Metadata.OfType<IAuthorizeData>().Should().NotBeEmpty();
    }

    [Fact]
    public void MapChannelCallbackEndpoints_ShouldNotRegisterRetiredDirectPlatformCallback()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
        });

        var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;
        app.MapChannelCallbackEndpoints();

        var routePatterns = routeBuilder.DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(route => route.RoutePattern.RawText)
            .ToArray();

        routePatterns.Any(pattern => pattern?.Contains("/callback/", StringComparison.Ordinal) == true)
            .Should().BeFalse();
        routePatterns.Should().Contain("/api/channels/registrations");
        routePatterns.Should().Contain("/api/channels/diagnostics/errors");
        routePatterns.Should().NotContain("/api/channels/registrations/rebuild");
    }

    [Fact]
    public async Task ChannelRegistrationRoute_ShouldAppendEndpointAuditRecords()
    {
        var appender = new RecordingAuditTrailAppender();
        await using var app = await CreateRouteAuditAppAsync(
            appender,
            new ChannelRelayRegistrationFacade([new AcceptedProvisioningService()]));
        using var client = CreateClient(app);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/channels/registrations?access_token={RawToken}&email=alice@example.com")
        {
            Content = new StringContent("""
            {
              "platform": "lark",
              "app_id": "cli_123",
              "app_secret": "secret-value",
              "verification_token": "verify-value",
              "webhook_base_url": "https://aevatar.example.com"
            }
            """, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RawToken);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Accepted);
        appender.Records.Should().HaveCount(2);
        appender.Records[0].OperationName.Should().Be("channel.registration.create.attempted");
        appender.Records[0].Outcome.Should().Be(AuditOutcome.Accepted);
        appender.Records[0].ResultSummary.Should().BeEmpty();
        appender.Records[1].OperationName.Should().Be("channel.registration.create");
        appender.Records[1].Outcome.Should().Be(AuditOutcome.Accepted);
        appender.Records.Should().OnlyContain(record =>
            record.Target.Kind == "channel-registration" &&
            record.Target.Id == "new" &&
            record.RequestSummary == "POST /api/channels/registrations" &&
            record.CapturePlane == AuditCapturePlane.BoundaryEndpoint);
        appender.Records.SelectMany(RecordStrings).Should().NotContain(value =>
            value.Contains(RawToken, StringComparison.Ordinal) ||
            value.Contains("alice@example.com", StringComparison.Ordinal) ||
            value.Contains("secret-value", StringComparison.Ordinal) ||
            value.Contains("verify-value", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleRegisterAsync_RejectsUnsupportedPlatform()
    {
        var registrationFacade = new ChannelRelayRegistrationFacade([]);
        var http = CreateJsonHttpContext(
            """{"platform":"telegram","webhook_base_url":"https://aevatar.example.com"}""",
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleRegisterAsync",
            http,
            registrationFacade,
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        response.Body.Should().Contain("supported production contract");
        response.Body.Should().Contain("unsupported_platform");
    }

    [Fact]
    public async Task HandleRegisterAsync_ProvisionsLarkViaNyx()
    {
        var provisioningService = Substitute.For<INyxChannelBotProvisioningService>();
        provisioningService.Platform.Returns("lark");
        provisioningService.ProvisionAsync(Arg.Any<NyxChannelBotProvisioningRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotProvisioningResult(
                Succeeded: true,
                Status: "accepted",
                Platform: "lark",
                RegistrationId: "reg-1",
                NyxChannelBotId: "bot-1",
                NyxAgentApiKeyId: "key-1",
                NyxConversationRouteId: "route-1",
                RelayCallbackUrl: "https://aevatar.example.com/api/webhooks/nyxid-relay",
                WebhookUrl: "https://nyx.example.com/api/v1/webhooks/channel/lark/bot-1")));

        var http = CreateJsonHttpContext(
            """{"platform":"lark","app_id":"cli_123","app_secret":"secret","verification_token":"verify-123","webhook_base_url":"https://aevatar.example.com"}""",
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync("HandleRegisterAsync", http, CreateRegistrationFacade(provisioningService), NullLoggerFactory.Instance, CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        response.Body.Should().Contain("\"registration_id\":\"reg-1\"");
        response.Body.Should().Contain("\"relay_callback_url\":\"https://aevatar.example.com/api/webhooks/nyxid-relay\"");
        await provisioningService.Received(1).ProvisionAsync(
            Arg.Is<NyxChannelBotProvisioningRequest>(request =>
                request.Platform == "lark" &&
                request.AccessToken == "test-token" &&
                request.WebhookBaseUrl == "https://aevatar.example.com" &&
                request.ScopeId == "scope-1" &&
                request.Lark != null &&
                request.Lark.AppId == "cli_123" &&
                request.Lark.AppSecret == "secret" &&
                request.Lark.VerificationToken == "verify-123"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleRegisterAsync_RejectsLarkProvisioningWithoutScope()
    {
        var provisioningService = Substitute.For<INyxChannelBotProvisioningService>();
        provisioningService.Platform.Returns("lark");

        var http = CreateJsonHttpContext(
            """{"platform":"lark","app_id":"cli_123","app_secret":"secret","webhook_base_url":"https://aevatar.example.com"}""");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync("HandleRegisterAsync", http, CreateRegistrationFacade(provisioningService), NullLoggerFactory.Instance, CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        response.Body.Should().Contain("scope_id is required");
        await provisioningService.DidNotReceive().ProvisionAsync(Arg.Any<NyxChannelBotProvisioningRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleRegisterAsync_ReturnsBadGateway_WhenNyxProvisioningFails()
    {
        var provisioningService = Substitute.For<INyxChannelBotProvisioningService>();
        provisioningService.Platform.Returns("lark");
        provisioningService.ProvisionAsync(Arg.Any<NyxChannelBotProvisioningRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotProvisioningResult(
                Succeeded: false,
                Status: "error",
                Platform: "lark",
                Error: "channel_bot_id_request_failed nyx_status=401 body=invalid app secret")));

        var http = CreateJsonHttpContext(
            """{"platform":"lark","app_id":"cli_123","app_secret":"bad-secret","webhook_base_url":"https://aevatar.example.com"}""",
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync("HandleRegisterAsync", http, CreateRegistrationFacade(provisioningService), NullLoggerFactory.Instance, CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        response.Body.Should().Contain("\"status\":\"error\"");
        response.Body.Should().Contain("invalid app secret");
    }

    [Fact]
    public async Task HandleRegisterAsync_ReturnsBadRequest_WhenTelegramBotTokenMissing()
    {
        var provisioningService = Substitute.For<INyxChannelBotProvisioningService>();
        provisioningService.Platform.Returns("telegram");
        provisioningService.ProvisionAsync(Arg.Any<NyxChannelBotProvisioningRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotProvisioningResult(
                Succeeded: false,
                Status: "error",
                Platform: "telegram",
                Error: "missing_bot_token")));

        var http = CreateJsonHttpContext(
            """{"platform":"telegram","webhook_base_url":"https://aevatar.example.com"}""",
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync("HandleRegisterAsync", http, CreateRegistrationFacade(provisioningService), NullLoggerFactory.Instance, CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        response.Body.Should().Contain("missing_bot_token");
    }

    private static IChannelBotRegistrationQueryPort QueryPortWith(params ChannelBotRegistrationEntry[] entries)
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>(entries));
        return queryPort;
    }

    private static IPlatformAdminAuthorizer AdminAuthorizer(bool elevated)
    {
        var authorizer = Substitute.For<IPlatformAdminAuthorizer>();
        authorizer.ResolveCallerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PlatformCaller(elevated, elevated ? "admin" : string.Empty, "e@x", "u")));
        return authorizer;
    }

    [Fact]
    public async Task HandleListRegistrationsAsync_ReturnsRelayModeOnly_AndScopesToCaller()
    {
        var queryPort = QueryPortWith(new ChannelBotRegistrationEntry
        {
            Id = "reg-1",
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            ScopeId = "scope-1",
            NyxChannelBotId = "bot-1",
        });
        var http = CreateHttpContext("scope-1");

        var result = await InvokeAsync("HandleListRegistrationsAsync", http, queryPort, AdminAuthorizer(false), (string?)null, CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Body.Should().Contain("\"registration_mode\":\"nyx_relay_webhook\"");
        response.Body.Should().Contain("\"callback_url\":\"\"");
        response.Body.Should().Contain("\"owned\":true");
    }

    [Fact]
    public async Task HandleListRegistrationsAsync_ExcludesOtherAccountsByDefault()
    {
        var queryPort = QueryPortWith(
            new ChannelBotRegistrationEntry { Id = "mine", Platform = "lark", ScopeId = "scope-1", NyxChannelBotId = "bot-mine" },
            new ChannelBotRegistrationEntry { Id = "theirs", Platform = "lark", ScopeId = "scope-2", NyxChannelBotId = "bot-theirs" });
        var http = CreateHttpContext("scope-1");

        var result = await InvokeAsync("HandleListRegistrationsAsync", http, queryPort, AdminAuthorizer(false), (string?)null, CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Body.Should().Contain("bot-mine");
        response.Body.Should().NotContain("bot-theirs");
    }

    [Fact]
    public async Task HandleListRegistrationsAsync_ScopeAll_Forbidden_WhenNotAdmin()
    {
        var queryPort = QueryPortWith(new ChannelBotRegistrationEntry { Id = "x", Platform = "lark", ScopeId = "scope-2" });
        var http = CreateHttpContext("scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync("HandleListRegistrationsAsync", http, queryPort, AdminAuthorizer(false), "all", CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        response.Body.Should().Contain("scope_admin_required");
    }

    [Fact]
    public async Task HandleListRegistrationsAsync_ScopeAll_ReturnsAllAccounts_WhenAdmin()
    {
        var queryPort = QueryPortWith(
            new ChannelBotRegistrationEntry { Id = "mine", Platform = "lark", ScopeId = "scope-1", NyxChannelBotId = "bot-mine" },
            new ChannelBotRegistrationEntry { Id = "theirs", Platform = "lark", ScopeId = "scope-2", NyxChannelBotId = "bot-theirs" });
        var http = CreateHttpContext("scope-1");
        http.Request.Headers.Authorization = "Bearer admin-token";

        var result = await InvokeAsync("HandleListRegistrationsAsync", http, queryPort, AdminAuthorizer(true), "all", CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Body.Should().Contain("bot-mine");
        response.Body.Should().Contain("bot-theirs");
        response.Body.Should().Contain("\"owned\":false");
    }

    [Fact]
    public async Task HandleGetStatusAsync_CrossAccount_NonAdmin_ReturnsNotFound()
    {
        // L1: a non-admin caller querying a registration owned by another scope
        // must get 404 (existence-hiding), NOT a populated degraded response —
        // otherwise the foreign bot's platform/activity leaks to anyone who can
        // guess a registration id.
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("reg-foreign", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
            {
                Id = "reg-foreign",
                Platform = "lark",
                ScopeId = "scope-2",
                NyxChannelBotId = "bot-foreign",
                LastInboundAtUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            }));
        var http = CreateHttpContext("scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        // nyxClient (arg 4) null: the not-found short-circuit returns before any NyxID call.
        var result = await InvokeAsync("HandleGetStatusAsync", "reg-foreign", http, queryPort, null, AdminAuthorizer(false), NullLoggerFactory.Instance, CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        response.Body.Should().NotContain("bot-foreign", "the foreign bot id must not leak");
        response.Body.Should().NotContain("active");
    }

    [Fact]
    public async Task HandleGetStatusAsync_CrossAccount_Admin_ReturnsDegradedObservation()
    {
        // L1: a platform admin is allowed the cross-account view (mirrors the
        // list all-view) and still only gets aevatar's own relay-activity
        // observation, never the foreign owner's NyxID live status.
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("reg-foreign", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
            {
                Id = "reg-foreign",
                Platform = "lark",
                ScopeId = "scope-2",
                NyxChannelBotId = "bot-foreign",
                LastInboundAtUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            }));
        var http = CreateHttpContext("scope-1");
        http.Request.Headers.Authorization = "Bearer admin-token";

        var result = await InvokeAsync("HandleGetStatusAsync", "reg-foreign", http, queryPort, null, AdminAuthorizer(true), NullLoggerFactory.Instance, CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Body.Should().Contain("\"status\":\"active\"");
        response.Body.Should().Contain("\"owned\":false");
    }

    [Fact]
    public async Task HandleGetCallerInfoAsync_ReturnsAdminFlag()
    {
        var http = CreateHttpContext("scope-1");
        http.Request.Headers.Authorization = "Bearer admin-token";

        var result = await InvokeAsync("HandleGetCallerInfoAsync", http, AdminAuthorizer(true), CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Body.Should().Contain("\"is_admin\":true");
        response.Body.Should().Contain("\"scope_id\":\"scope-1\"");
    }

    [Fact]
    public void MapChannelCallbackEndpoints_ShouldNotRegisterRepairLarkMirrorEndpoint()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
        });

        var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;
        app.MapChannelCallbackEndpoints();

        var routePatterns = routeBuilder.DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(route => route.RoutePattern.RawText)
            .ToArray();

        routePatterns.Should().NotContain("/api/channels/registrations/repair-lark-mirror");
    }

    [Fact]
    public async Task HandleDeleteRegistrationAsync_DeprovisionsNyxThenDispatchesUnregisterCommand()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("reg-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
            {
                Id = "reg-1",
                Platform = "lark",
                NyxConversationRouteId = "route-1",
                NyxChannelBotId = "bot-1",
                NyxAgentApiKeyId = "key-1",
            }));

        EventEnvelope? capturedEnvelope = null;
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        ((IActorDispatchPort)actorRuntime).DispatchAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(envelope => capturedEnvelope = envelope),
                Arg.Any<CancellationToken>())
            .Returns(ActorDispatchPortTestSupport.AcceptAsync);

        var deprovision = Substitute.For<INyxChannelBotDeprovisioningService>();
        deprovision.DeprovisionAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotDeprovisioningResult(true, true, Array.Empty<string>())));

        var http = CreateHttpContext("scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleDeleteRegistrationAsync",
            "reg-1",
            http,
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime),
            queryPort,
            deprovision,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        await deprovision.Received(1).DeprovisionAsync(
            "test-token", "route-1", "bot-1", "key-1", Arg.Any<CancellationToken>());
        capturedEnvelope.Should().NotBeNull();
        capturedEnvelope!.Payload.Is(ChannelBotUnregisterCommand.Descriptor).Should().BeTrue();
        capturedEnvelope.Payload.Unpack<ChannelBotUnregisterCommand>().RegistrationId.Should().Be("reg-1");
    }

    [Fact]
    public async Task HandleDeleteRegistrationAsync_HardChannelBotFailure_ReturnsBadGateway_AndDoesNotTombstone()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("reg-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
            {
                Id = "reg-1",
                Platform = "lark",
                NyxChannelBotId = "bot-1",
            }));

        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));

        var deprovision = Substitute.For<INyxChannelBotDeprovisioningService>();
        deprovision.DeprovisionAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotDeprovisioningResult(false, false, Array.Empty<string>())));

        var http = CreateHttpContext("scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleDeleteRegistrationAsync",
            "reg-1",
            http,
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime),
            queryPort,
            deprovision,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        response.Body.Should().Contain("nyx_channel_bot_delete_failed");
        // Local mirror must NOT be tombstoned → no unregister command dispatched.
        await ((IActorDispatchPort)actorRuntime).DidNotReceiveWithAnyArgs()
            .DispatchAsync(default!, default!, default);
    }

    [Fact]
    public async Task HandleDeleteRegistrationAsync_ResidualWarnings_StillTombstones_AndSurfacesWarnings()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("reg-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
            {
                Id = "reg-1",
                Platform = "lark",
                NyxConversationRouteId = "route-1",
                NyxChannelBotId = "bot-1",
                NyxAgentApiKeyId = "key-1",
            }));

        EventEnvelope? capturedEnvelope = null;
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        ((IActorDispatchPort)actorRuntime).DispatchAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(envelope => capturedEnvelope = envelope),
                Arg.Any<CancellationToken>())
            .Returns(ActorDispatchPortTestSupport.AcceptAsync);

        var deprovision = Substitute.For<INyxChannelBotDeprovisioningService>();
        deprovision.DeprovisionAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotDeprovisioningResult(
                true, true, new[] { "relay_api_key_delete_failed id=key-1" })));

        var http = CreateHttpContext("scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleDeleteRegistrationAsync",
            "reg-1",
            http,
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime),
            queryPort,
            deprovision,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Body.Should().Contain("relay_api_key_delete_failed id=key-1");
        capturedEnvelope.Should().NotBeNull();
        capturedEnvelope!.Payload.Unpack<ChannelBotUnregisterCommand>().RegistrationId.Should().Be("reg-1");
    }

    [Fact]
    public async Task HandleDeleteRegistrationAsync_Telegram_RoutesThroughSamePlatformNeutralPath()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("reg-tg", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
            {
                Id = "reg-tg",
                Platform = "telegram",
                NyxConversationRouteId = "route-tg",
                NyxChannelBotId = "bot-tg",
                NyxAgentApiKeyId = "key-tg",
            }));

        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        ((IActorDispatchPort)actorRuntime).DispatchAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Any<EventEnvelope>(),
                Arg.Any<CancellationToken>())
            .Returns(ActorDispatchPortTestSupport.AcceptAsync);

        var deprovision = Substitute.For<INyxChannelBotDeprovisioningService>();
        deprovision.DeprovisionAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotDeprovisioningResult(true, true, Array.Empty<string>())));

        var http = CreateHttpContext("scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleDeleteRegistrationAsync",
            "reg-tg",
            http,
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime),
            queryPort,
            deprovision,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        await deprovision.Received(1).DeprovisionAsync(
            "test-token", "route-tg", "bot-tg", "key-tg", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleDeleteRegistrationAsync_ReturnsUnauthorized_WhenBearerMissing()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("reg-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
            {
                Id = "reg-1",
                Platform = "lark",
                NyxChannelBotId = "bot-1",
            }));

        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        var deprovision = Substitute.For<INyxChannelBotDeprovisioningService>();

        var http = CreateHttpContext("scope-1"); // no Authorization header

        var result = await InvokeAsync(
            "HandleDeleteRegistrationAsync",
            "reg-1",
            http,
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime),
            queryPort,
            deprovision,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        await deprovision.DidNotReceiveWithAnyArgs().DeprovisionAsync(
            default!, default, default, default, default);
    }

    [Fact]
    public async Task HandleDeleteRegistrationAsync_ReturnsNotFound_WhenMissing()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("missing", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(null));

        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        var deprovision = Substitute.For<INyxChannelBotDeprovisioningService>();
        var http = CreateHttpContext("scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleDeleteRegistrationAsync",
            "missing",
            http,
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime),
            queryPort,
            deprovision,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        await actorRuntime.DidNotReceiveWithAnyArgs().GetAsync(default!);
        await deprovision.DidNotReceiveWithAnyArgs().DeprovisionAsync(
            default!, default, default, default, default);
    }

    [Fact]
    public async Task HandleTestReplyAsync_ReturnsGoneDiagnostic()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("reg-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
            {
                Id = "reg-1",
                Platform = "lark",
                NyxProviderSlug = "api-lark-bot",
            }));

        var result = await InvokeAsync("HandleTestReplyAsync", "reg-1", queryPort, CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status410Gone);
        response.Body.Should().Contain("Direct platform reply diagnostics are retired");
        response.Body.Should().Contain("\"registration_id\":\"reg-1\"");
        response.Body.Should().Contain("\"platform\":\"lark\"");
    }

    [Fact]
    public async Task HandleTestReplyAsync_ReturnsNotFound_WhenMissing()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("reg-404", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(null));

        var result = await InvokeAsync("HandleTestReplyAsync", "reg-404", queryPort, CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task HandleGetDiagnosticErrorsAsync_ReturnsRetiredMessage()
    {
        var result = await InvokeAsync("HandleGetDiagnosticErrorsAsync");
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status410Gone);
        response.Body.Should().Contain("process-local diagnostic history is retired");
        response.Body.Should().NotContain("entry_count");
        response.Body.Should().NotContain("entries");
    }

    [Fact]
    public void MapChannelCallbackEndpoints_ShouldRegisterStatusRoute_RequiringAuthorization()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
        });

        var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;
        app.MapChannelCallbackEndpoints();

        var endpoint = routeBuilder.DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(route => string.Equals(route.RoutePattern.RawText, "/api/channels/registrations/{registrationId}/status", StringComparison.Ordinal));

        endpoint.Metadata.OfType<IAuthorizeData>().Should().NotBeEmpty();
        endpoint.Metadata.OfType<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()
            .Single().HttpMethods.Should().Contain("GET");
    }

    [Fact]
    public async Task HandleGetStatusAsync_ReturnsNotFound_WhenMissing()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("missing", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(null));
        var http = CreateHttpContext("scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        // nyxClient (arg 4) is null on purpose: the not-found path returns before it is used.
        var result = await InvokeAsync("HandleGetStatusAsync", "missing", http, queryPort, null, AdminAuthorizer(false), NullLoggerFactory.Instance, CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task HandleGetStatusAsync_ReturnsUnknown_WhenNoChannelBotId()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("reg-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
            {
                Id = "reg-1",
                Platform = "lark",
                ScopeId = "scope-1",   // owned by the caller, so it passes the foreign-scope check
                NyxChannelBotId = string.Empty,
            }));
        var http = CreateHttpContext("scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        // No channel bot id → returns "unknown" before touching the Nyx client (arg 4 null).
        // Owned by the caller, so the admin authorizer is never consulted.
        var result = await InvokeAsync("HandleGetStatusAsync", "reg-1", http, queryPort, null, AdminAuthorizer(false), NullLoggerFactory.Instance, CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Body.Should().Contain("\"status\":\"unknown\"");
    }

    [Fact]
    public void ParseChannelBotStatus_MapsActiveWithLastEvent()
    {
        var (status, lastEventAt) = InvokeParseStatus(
            """{"id":"bot-1","status":"active","last_event_at":"2026-06-24T00:00:00Z"}""");

        status.Should().Be("active");
        lastEventAt.Should().Be("2026-06-24T00:00:00Z");
    }

    [Fact]
    public void ParseChannelBotStatus_HandlesDataWrapperAndPending()
    {
        var (status, lastEventAt) = InvokeParseStatus(
            """{"data":{"status":"pending_webhook","last_event_at":null}}""");

        status.Should().Be("pending_webhook");
        lastEventAt.Should().BeNull();
    }

    [Fact]
    public void ParseChannelBotStatus_DegradesToUnknown_OnErrorEnvelopeOrMalformed()
    {
        InvokeParseStatus("""{"error":true,"status":404}""").Status.Should().Be("unknown");
        InvokeParseStatus("not-json").Status.Should().Be("unknown");
        InvokeParseStatus("""{"id":"bot-1"}""").Status.Should().Be("unknown");
    }

    private static (string Status, string? LastEventAt) InvokeParseStatus(string response)
    {
        var method = typeof(ChannelCallbackEndpoints)
            .GetMethod("ParseChannelBotStatus", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ParseChannelBotStatus not found.");
        var boxed = method.Invoke(null, [response]) ?? throw new InvalidOperationException("null result");
        var tuple = (ValueTuple<string, string?>)boxed;
        return (tuple.Item1, tuple.Item2);
    }

    private static HttpContext CreateHttpContext(string? scopeId = null)
    {
        var builder = WebApplication.CreateBuilder();
        var context = new DefaultHttpContext();
        context.RequestServices = builder.Services.BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        if (!string.IsNullOrWhiteSpace(scopeId))
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("scope_id", scopeId),
            ], "test"));
        }

        return context;
    }

    private static async Task<WebApplication> CreateRouteAuditAppAsync(
        RecordingAuditTrailAppender appender,
        ChannelRelayRegistrationFacade registrationFacade)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services
            .AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, RouteAuditAuthenticationHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IAuditTrailAppender>(appender);
        builder.Services.AddSingleton<IAuditActorIdentityHasher>(new StableAuditActorIdentityHasher());
        builder.Services.AddSingleton(registrationFacade);
        builder.Services.AddSingleton(Substitute.For<IChannelBotRegistrationQueryPort>());
        builder.Services.AddSingleton(Substitute.For<IPlatformAdminAuthorizer>());
        builder.Services.AddSingleton(Substitute.For<INyxChannelBotDeprovisioningService>());

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseMiddleware<EndpointAuditCaptureMiddleware>();
        app.UseAuthorization();
        app.MapChannelCallbackEndpoints();
        await app.StartAsync();
        return app;
    }

    private static HttpClient CreateClient(WebApplication app)
    {
        var address = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .Single();

        return new HttpClient
        {
            BaseAddress = new Uri(address),
        };
    }

    private static HttpContext CreateJsonHttpContext(string json, string? scopeId = null)
    {
        var context = CreateHttpContext(scopeId);
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return context;
    }

    private static ChannelRelayRegistrationFacade CreateRegistrationFacade(
        params INyxChannelBotProvisioningService[] provisioningServices) =>
        new(provisioningServices);

    private static async Task<IResult> InvokeAsync(string methodName, params object?[] args)
    {
        var method = typeof(ChannelCallbackEndpoints)
            .GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method '{methodName}' not found.");

        var invocationResult = method.Invoke(null, args);
        if (invocationResult is Task<IResult> resultTask)
            return await resultTask;

        throw new InvalidOperationException($"Method '{methodName}' did not return Task<IResult>.");
    }

    private static IEnumerable<string> RecordStrings(AuditRecord record)
    {
        yield return record.AuditId;
        yield return record.ScopeId;
        yield return record.AuditActorId;
        yield return record.IdentityKeyId;
        yield return record.OperationName;
        yield return record.Target.Kind;
        yield return record.Target.Id;
        yield return record.Target.DisplayName;
        yield return record.Correlation.TraceId;
        yield return record.Correlation.RequestId;
        yield return record.Correlation.CommandId;
        yield return record.Correlation.CallId;
        yield return record.Correlation.SessionId;
        yield return record.Correlation.WorkflowRunId;
        yield return record.Correlation.ApprovalId;
        yield return record.RequestSummary;
        yield return record.ResultSummary;
        yield return record.ErrorCode;
        yield return record.ErrorSummary;
        foreach (var annotation in record.Annotations)
        {
            yield return annotation.Key;
            yield return annotation.Value;
        }
    }

    private static async Task<(int StatusCode, string Body)> ExecuteResultAsync(IResult result)
    {
        var context = CreateHttpContext();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        return (context.Response.StatusCode, body);
    }

    private sealed class AcceptedProvisioningService : INyxChannelBotProvisioningService
    {
        public string Platform => "lark";

        public Task<NyxChannelBotProvisioningResult> ProvisionAsync(
            NyxChannelBotProvisioningRequest request,
            CancellationToken ct)
        {
            request.AccessToken.Should().Be(RawToken);
            request.ScopeId.Should().Be("scope-1");
            return Task.FromResult(new NyxChannelBotProvisioningResult(
                Succeeded: true,
                Status: "accepted",
                Platform: request.Platform,
                RegistrationId: "reg-1"));
        }
    }

    private sealed class RecordingAuditTrailAppender : IAuditTrailAppender
    {
        public List<AuditRecord> Records { get; } = [];

        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.FromResult(AuditTrailAppendResult.Appended(
                record.AuditId,
                record.AuditActorId,
                record.OccurredAt.ToDateTimeOffset()));
        }
    }

    private sealed class StableAuditActorIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) =>
            new($"hashed:{canonicalActorKey}", "kid-test");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) =>
            auditActorId == $"hashed:{canonicalActorKey}" &&
            identityKeyId == "kid-test";
    }

    private sealed class RouteAuditAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Authorization", out var authorization) ||
                !authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
            [
                new Claim("sub", "user-123"),
                new Claim("scope_id", "scope-1"),
            ], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
