using System.Reflection;
using System.Net;
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
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
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
using static Aevatar.GAgents.Channel.NyxIdRelay.VerifiedChannelRegistrationServiceSelection.VerifiedChannelRegistrationAuthorizationPlan;

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
        routePatterns.Should().Contain("/api/channels/services");
        routePatterns.Should().Contain("/api/channels/registrations/{registrationId}/runtime-config");
        routePatterns.Should().Contain("/api/channels/diagnostics/errors");
        routePatterns.Should().NotContain("/api/channels/registrations/rebuild");
    }

    [Fact]
    public void MapChannelCallbackEndpoints_ShouldRegisterAuditedRuntimeConfigUpdateRoute()
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
            .Single(route => string.Equals(
                route.RoutePattern.RawText,
                "/api/channels/registrations/{registrationId}/runtime-config",
                StringComparison.Ordinal) &&
                route.Metadata.OfType<HttpMethodMetadata>()
                    .Single().HttpMethods.Contains("POST"));

        endpoint.Metadata.OfType<IAuthorizeData>().Should().NotBeEmpty();
    }

    [Fact]
    public void MapChannelCallbackEndpoints_ShouldRegisterRuntimeConfigReadRoute()
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
            .Single(route => string.Equals(
                route.RoutePattern.RawText,
                "/api/channels/registrations/{registrationId}/runtime-config",
                StringComparison.Ordinal) &&
                route.Metadata.OfType<HttpMethodMetadata>()
                    .Single().HttpMethods.Contains("GET"));

        endpoint.Metadata.OfType<IAuthorizeData>().Should().NotBeEmpty();
    }

    [Fact]
    public void MapChannelCallbackEndpoints_ShouldRegisterAuditedWorkflowResultDeliveryRepairRoute()
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
            .Single(route => string.Equals(
                route.RoutePattern.RawText,
                "/api/channels/registrations/{registrationId}/workflow-result-delivery/repair",
                StringComparison.Ordinal));

        endpoint.Metadata.OfType<IAuthorizeData>().Should().NotBeEmpty();
        endpoint.Metadata.OfType<HttpMethodMetadata>()
            .Single().HttpMethods.Should().Contain("POST");
    }

    [Fact]
    public async Task ChannelRegistrationRoute_ShouldAppendEndpointAuditRecords()
    {
        var appender = new RecordingAuditTrailAppender();
        await using var app = await CreateRouteAuditAppAsync(appender);
        using var client = CreateClient(app);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/channels/registrations?access_token={RawToken}&email=alice@example.com")
        {
            Content = new StringContent("""
            {
              "registration_id": "new",
              "nyx_channel_bot_id": "bot-audit",
              "webhook_base_url": "https://aevatar.example.com"
            }
            """, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RawToken);

        var response = await client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Accepted, responseBody);
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
    public async Task WorkflowResultDeliveryRepairRoute_ShouldAppendEndpointAuditRecords()
    {
        var appender = new RecordingAuditTrailAppender();
        var repairService = Substitute.For<IChannelWorkflowResultDeliveryRepairService>();
        repairService.RepairAsync(
                "reg-alpha",
                "scope-1",
                "user-123",
                RawToken,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChannelWorkflowResultDeliveryRepairResult(
                ChannelWorkflowResultDeliveryRepairResultStatus.Repaired,
                "repair-alpha",
                "reg-alpha",
                "key-new-alpha")));
        await using var app = await CreateRouteAuditAppAsync(
            appender,
            repairService);
        using var client = CreateClient(app);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/channels/registrations/reg-alpha/workflow-result-delivery/repair?access_token={RawToken}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RawToken);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        appender.Records.Should().HaveCount(2);
        appender.Records[0].OperationName.Should().Be(
            "channel.registration.workflow-result-delivery.repair.attempted");
        appender.Records[1].OperationName.Should().Be(
            "channel.registration.workflow-result-delivery.repair");
        appender.Records.Should().OnlyContain(record =>
            record.Target.Kind == "channel-registration" &&
            record.Target.Id == "reg-alpha" &&
            record.RequestSummary ==
                "POST /api/channels/registrations/{registrationId}/workflow-result-delivery/repair registrationId=reg-alpha" &&
            record.CapturePlane == AuditCapturePlane.BoundaryEndpoint);
        appender.Records.SelectMany(RecordStrings).Should().NotContain(value =>
            value.Contains(RawToken, StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleRepairWorkflowResultDeliveryAsync_ReturnsUnauthorized_WhenBearerMissing()
    {
        var repairService = Substitute.For<IChannelWorkflowResultDeliveryRepairService>();
        var http = CreateAuthenticatedHttpContext(
            "scope-alpha",
            new Claim("sub", "user-alpha"));

        var result = await InvokeAsync(
            "HandleRepairWorkflowResultDeliveryAsync",
            "reg-alpha",
            http,
            repairService,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        await repairService.DidNotReceiveWithAnyArgs().RepairAsync(
            default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task HandleRepairWorkflowResultDeliveryAsync_HidesRegistration_WhenScopeClaimMissing()
    {
        var repairService = Substitute.For<IChannelWorkflowResultDeliveryRepairService>();
        var http = CreateAuthenticatedHttpContext(
            scopeId: null,
            new Claim("sub", "user-alpha"));
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleRepairWorkflowResultDeliveryAsync",
            "reg-alpha",
            http,
            repairService,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        response.Body.Contains("scope", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        await repairService.DidNotReceiveWithAnyArgs().RepairAsync(
            default!, default!, default!, default!, default);
    }

    public static TheoryData<string, string> RepairSubjectClaimCases => new()
    {
        { "uid", "uid-alpha" },
        { "sub", "sub-alpha" },
        { ClaimTypes.NameIdentifier, "name-alpha" },
        { "user_id", "user-alpha" },
    };

    [Theory]
    [MemberData(nameof(RepairSubjectClaimCases))]
    public async Task HandleRepairWorkflowResultDeliveryAsync_UsesFirstSupportedSubjectClaim(
        string highestPriorityClaim,
        string expectedSubjectId)
    {
        var claims = new List<Claim>();
        if (highestPriorityClaim == "uid")
            claims.Add(new Claim("uid", "uid-alpha"));
        if (highestPriorityClaim is "uid" or "sub")
            claims.Add(new Claim("sub", "sub-alpha"));
        if (highestPriorityClaim is "uid" or "sub" ||
            highestPriorityClaim == ClaimTypes.NameIdentifier)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, "name-alpha"));
        }

        claims.Add(new Claim("user_id", "user-alpha"));
        var repairService = Substitute.For<IChannelWorkflowResultDeliveryRepairService>();
        repairService.RepairAsync(
                "reg-alpha",
                "scope-alpha",
                expectedSubjectId,
                "test-token",
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChannelWorkflowResultDeliveryRepairResult(
                ChannelWorkflowResultDeliveryRepairResultStatus.AlreadyEnabled,
                string.Empty,
                "reg-alpha",
                "key-alpha")));
        var http = CreateAuthenticatedHttpContext("scope-alpha", claims.ToArray());
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleRepairWorkflowResultDeliveryAsync",
            "reg-alpha",
            http,
            repairService,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        await repairService.Received(1).RepairAsync(
            "reg-alpha",
            "scope-alpha",
            expectedSubjectId,
            "test-token",
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ChannelWorkflowResultDeliveryRepairResultStatus.Repaired, 200, "repaired", "enabled")]
    [InlineData(ChannelWorkflowResultDeliveryRepairResultStatus.AlreadyEnabled, 200, "already_enabled", "enabled")]
    [InlineData(ChannelWorkflowResultDeliveryRepairResultStatus.Repairing, 202, "repairing", "repairing")]
    [InlineData(ChannelWorkflowResultDeliveryRepairResultStatus.NotFound, 404, "not_found", "repair_required")]
    [InlineData(ChannelWorkflowResultDeliveryRepairResultStatus.UnsupportedPlatform, 409, "unsupported_platform", "repair_required")]
    [InlineData(ChannelWorkflowResultDeliveryRepairResultStatus.RepairFailed, 502, "repair_failed", "repair_failed")]
    public async Task HandleRepairWorkflowResultDeliveryAsync_MapsSafeStableHttpContract(
        ChannelWorkflowResultDeliveryRepairResultStatus repairStatus,
        int expectedStatusCode,
        string expectedStatus,
        string expectedCapabilityStatus)
    {
        var repairService = Substitute.For<IChannelWorkflowResultDeliveryRepairService>();
        repairService.RepairAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChannelWorkflowResultDeliveryRepairResult(
                repairStatus,
                "repair-alpha",
                "reg-alpha",
                "key-new-alpha",
                ChannelWorkflowResultDeliveryRepairPhase.VaultStorage,
                ChannelWorkflowResultDeliveryRepairFailureReason.AmbiguousRotatedKeyRecovery)));
        var http = CreateAuthenticatedHttpContext(
            "scope-alpha",
            new Claim("sub", "user-alpha"));
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleRepairWorkflowResultDeliveryAsync",
            "reg-alpha",
            http,
            repairService,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(expectedStatusCode);
        using var document = JsonDocument.Parse(response.Body);
        var root = document.RootElement;
        root.GetProperty("status").GetString().Should().Be(expectedStatus);
        root.GetProperty("workflow_result_delivery_status").GetString()
            .Should().Be(expectedCapabilityStatus);
        root.EnumerateObject().Select(property => property.Name).Should().BeSubsetOf(
        [
            "status",
            "repair_request_id",
            "registration_id",
            "nyx_agent_api_key_id",
            "workflow_result_delivery_status",
            "failure_phase",
            "failure_reason",
            "note",
        ]);

        if (repairStatus == ChannelWorkflowResultDeliveryRepairResultStatus.RepairFailed)
        {
            root.GetProperty("failure_phase").GetString().Should().Be("vault_storage");
            root.GetProperty("failure_reason").GetString().Should()
                .Be("ambiguous_rotated_key_recovery");
        }
        else
        {
            root.TryGetProperty("failure_phase", out _).Should().BeFalse();
            root.TryGetProperty("failure_reason", out _).Should().BeFalse();
        }

        response.Body.Contains("full_key", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        response.Body.Contains("secret_reference", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        response.Body.Contains("owner_scope_key", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        response.Body.Should().NotContain("sec-repair-alpha");
    }

    [Fact]
    public async Task HandleRegisterAsync_RequiresNyxChannelBotId()
    {
        var http = CreateJsonHttpContext(
            """{"webhook_base_url":"https://aevatar.example.com"}""",
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";
        var actorRuntime = AcceptedRegistrationRuntime();

        var result = await InvokeAsync(
            "HandleRegisterAsync",
            http,
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime),
            QueryPortWithSnapshots(),
            OwnerResolver("scope-1"),
            AuthorizationPlanner(),
            CreateAgentKeyProvisioningService(CreateNyxClient()),
            CreateNyxClient(),
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        response.Body.Should().Contain("nyx_channel_bot_id is required");
    }

    [Fact]
    public async Task HandleRegisterAsync_ReturnsStableMissingAccessTokenError()
    {
        var http = CreateJsonHttpContext(
            """{"nyx_channel_bot_id":"bot-1","webhook_base_url":"https://aevatar.example.com"}""",
            "scope-1");
        var actorRuntime = AcceptedRegistrationRuntime();

        var result = await InvokeAsync(
            "HandleRegisterAsync",
            http,
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime),
            QueryPortWithSnapshots(),
            OwnerResolver("scope-1"),
            AuthorizationPlanner(),
            CreateAgentKeyProvisioningService(CreateNyxClient()),
            CreateNyxClient(),
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        response.Body.Should().Contain("\"error\":\"missing_access_token\"");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"svc-alpha\"")]
    [InlineData("[null]")]
    [InlineData("[42]")]
    [InlineData("[\"\"]")]
    [InlineData("[\"   \"]")]
    public async Task HandleRegisterAsync_RejectsInvalidServiceIdsBeforeNyxWrites(
        string serviceIdsJson)
    {
        var http = CreateJsonHttpContext(
            $$"""{"nyx_channel_bot_id":"bot-1","webhook_base_url":"https://aevatar.example.com","authorization_mode":"explicit_service_allowlist","service_ids":{{serviceIdsJson}}}""",
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";
        var nyxClient = CreateNyxClient();
        var actorRuntime = AcceptedRegistrationRuntime();

        var result = await InvokeAsync(
            "HandleRegisterAsync",
            http,
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime),
            QueryPortWithSnapshots(),
            OwnerResolver("scope-1"),
            AuthorizationPlanner(),
            CreateAgentKeyProvisioningService(nyxClient),
            nyxClient,
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        response.Body.Should().Contain("invalid_service_ids");
    }

    [Fact]
    public async Task HandleRegisterAsync_BindsExistingNyxChannelBotAndDispatchesLocalMirror()
    {
        EventEnvelope? capturedEnvelope = null;
        var actorRuntime = AcceptedRegistrationRuntime(envelope => capturedEnvelope = envelope);
        var nyxHandler = new RecordingNyxHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v1/channel-bots/bot-1")
            {
                return JsonResponse("""
                {
                  "id": "bot-1",
                  "platform": "whatsapp",
                  "name": "Dinner Bot",
                  "status": "active",
                  "active": true,
                  "webhook_url": "https://nyx.example.com/api/v1/webhooks/channel/whatsapp/bot-1"
                }
                """);
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/v1/api-keys")
            {
                return JsonResponse("""
                {
                  "id": "key-new",
                  "full_key": "nyx_full_key_secret",
                  "scopes": "read write proxy",
                  "platform": "generic",
                  "purpose": "general",
                  "scheduled_write_enabled": false,
                  "durable_grants": [],
                  "allow_all_services": true,
                  "allow_all_nodes": true,
                  "allowed_service_ids": [],
                  "allowed_node_ids": []
                }
                """);
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v1/channel-conversations")
            {
                request.RequestUri.Query.Should().Contain("bot_id=bot-1");
                return JsonResponse("""{"items":[{"id":"route-1","channel_bot_id":"bot-1","agent_api_key_id":"key-old","default_agent":true}]}""");
            }

            if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath == "/api/v1/channel-conversations/route-1")
            {
                return JsonResponse("""{"id":"route-1","channel_bot_id":"bot-1","agent_api_key_id":"key-new","default_agent":true}""");
            }

            return NotFoundResponse(request);
        });
        var nyxClient = CreateNyxClient(nyxHandler);
        var http = CreateJsonHttpContext(
            """
            {
              "registration_id": "reg-adopt",
              "nyx_channel_bot_id": "bot-1",
              "webhook_base_url": "https://aevatar.example.com",
              "default_skill_name": " /Dinner-Booking ",
              "runtime_config": {
                "instructions": "Book dinner only after confirmation.",
                "default_skill": { "name": "dinner-booking" },
                "credential_source_mode": "registration_agent_key"
              }
            }
            """,
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleRegisterAsync",
            http,
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime),
            QueryPortWithSnapshots(),
            OwnerResolver("scope-1"),
            AuthorizationPlanner(),
            CreateAgentKeyProvisioningService(nyxClient),
            nyxClient,
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status202Accepted, $"{response.Body}; requests={string.Join(", ", nyxHandler.Requests.Select(request => $"{request.Method.Method} {request.RequestUri!.AbsolutePath}"))}");
        response.Body.Should().Contain("\"registration_id\":\"reg-adopt\"");
        response.Body.Should().Contain("\"nyx_channel_bot_id\":\"bot-1\"");
        response.Body.Should().Contain("\"nyx_agent_api_key_id\":\"key-new\"");
        capturedEnvelope.Should().NotBeNull();
        var command = capturedEnvelope!.Payload.Unpack<ChannelBotRegisterCommand>();
        command.Platform.Should().Be("whatsapp");
        command.NyxChannelBotId.Should().Be("bot-1");
        command.NyxConversationRouteId.Should().Be("route-1");
        command.NyxAgentApiKeyId.Should().Be("key-new");
        command.DefaultSkillName.Should().Be("dinner-booking");
        command.RuntimeConfig.DefaultSkill.Name.Should().Be("dinner-booking");
        command.ChannelAgentKey.Should().NotBeNull();
        command.ChannelAgentKey.SecretReference.Ref.Should().NotBeNullOrWhiteSpace();
        nyxHandler.Requests.Select(request => $"{request.Method.Method} {request.RequestUri!.AbsolutePath}")
            .Should().ContainInOrder(
                "GET /api/v1/channel-bots/bot-1",
                "POST /api/v1/api-keys",
                "GET /api/v1/channel-conversations",
                "PUT /api/v1/channel-conversations/route-1");
        response.Body.Should().NotContain("nyx_full_key_secret");
        response.Body.Contains("secret_reference", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Fact]
    public async Task HandleRegisterAsync_ReturnsConflict_WhenNyxChannelBotAlreadyBound()
    {
        var existing = NewModelRegistration("reg-existing", "scope-1", "key-existing");
        existing.NyxChannelBotId = "bot-1";
        var http = CreateJsonHttpContext(
            """{"nyx_channel_bot_id":"bot-1","webhook_base_url":"https://aevatar.example.com"}""",
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";
        var nyxClient = CreateNyxClient();
        var actorRuntime = AcceptedRegistrationRuntime();

        var result = await InvokeAsync(
            "HandleRegisterAsync",
            http,
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime),
            QueryPortWithSnapshots(new ChannelBotRegistrationSnapshot(existing, 12)),
            OwnerResolver("scope-1"),
            AuthorizationPlanner(),
            CreateAgentKeyProvisioningService(nyxClient),
            nyxClient,
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        response.Body.Should().Contain("channel_bot_already_bound");
        response.Body.Should().Contain("reg-existing");
    }

    [Fact]
    public async Task HandleRegisterAsync_ReturnsConflict_WhenChannelBotRouteIsAmbiguous()
    {
        var nyxHandler = new RecordingNyxHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v1/channel-bots/bot-1")
            {
                return JsonResponse("""{"id":"bot-1","platform":"discord","name":"Dinner Bot","status":"active","active":true,"webhook_url":"https://nyx.example.com/api/v1/webhooks/channel/discord/bot-1"}""");
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/v1/api-keys")
            {
                return JsonResponse("""{"id":"key-new","full_key":"secret-value","scopes":"read write proxy","platform":"generic","purpose":"general","scheduled_write_enabled":false,"durable_grants":[],"allow_all_services":true,"allow_all_nodes":true,"allowed_service_ids":[],"allowed_node_ids":[]}""");
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v1/channel-conversations")
            {
                return JsonResponse("""{"items":[{"id":"route-a","channel_bot_id":"bot-1","agent_api_key_id":"key-a","default_agent":false},{"id":"route-b","channel_bot_id":"bot-1","agent_api_key_id":"key-b","default_agent":false}]}""");
            }

            if (request.Method == HttpMethod.Delete && request.RequestUri!.AbsolutePath == "/api/v1/api-keys/key-new")
                return JsonResponse("{}");

            return NotFoundResponse(request);
        });
        var nyxClient = CreateNyxClient(nyxHandler);
        var actorRuntime = AcceptedRegistrationRuntime();
        var http = CreateJsonHttpContext(
            """{"nyx_channel_bot_id":"bot-1","webhook_base_url":"https://aevatar.example.com"}""",
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleRegisterAsync",
            http,
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime),
            QueryPortWithSnapshots(),
            OwnerResolver("scope-1"),
            AuthorizationPlanner(),
            CreateAgentKeyProvisioningService(nyxClient),
            nyxClient,
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status409Conflict, $"{response.Body}; requests={string.Join(", ", nyxHandler.Requests.Select(request => $"{request.Method.Method} {request.RequestUri!.AbsolutePath}"))}");
        response.Body.Should().Contain("ambiguous_channel_bot_route");
        await ((IActorDispatchPort)actorRuntime).DidNotReceiveWithAnyArgs()
            .DispatchAsync(default!, default!, default);
    }

    [Fact]
    public async Task HandleRegisterAsync_ReturnsBadRequest_WhenWebhookBaseUrlInsecure()
    {
        var http = CreateJsonHttpContext(
            """{"nyx_channel_bot_id":"bot-1","webhook_base_url":"http://aevatar.example.com"}""",
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";
        var actorRuntime = AcceptedRegistrationRuntime();

        var result = await InvokeAsync(
            "HandleRegisterAsync",
            http,
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime),
            QueryPortWithSnapshots(),
            OwnerResolver("scope-1"),
            AuthorizationPlanner(),
            CreateAgentKeyProvisioningService(CreateNyxClient()),
            CreateNyxClient(),
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        response.Body.Should().Contain("insecure_webhook_base_url");
    }

    [Fact]
    public async Task HandleListServicesAsync_ReturnsVerifiedNonSensitiveServiceChoices()
    {
        var authorizationPort = Substitute.For<IChannelRegistrationNyxIdAuthorizationPort>();
        authorizationPort.ReadUserServicesAsync("test-token", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxIdApiAccessResult<NyxIdUserServices>(
                new NyxIdUserServices([
                    new NyxIdUserService(
                        "svc-calendar",
                        "api-calendar",
                        "Calendar",
                        "Calendar API",
                        true,
                        new NyxIdUserServiceCredentialSource(
                            NyxIdUserServiceCredentialSourceKind.Organization,
                            OrganizationId: "scope-1",
                            OrganizationRole: NyxIdOrganizationRole.Admin,
                            Allowed: true)),
                ]),
                null)));
        var http = CreateHttpContext("scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleListServicesAsync",
            http,
            authorizationPort,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Body.Should().Contain("\"id\":\"svc-calendar\"");
        response.Body.Should().Contain("\"slug\":\"api-calendar\"");
        response.Body.Should().Contain("\"credential_source\":");
        var lowerBody = response.Body.ToLowerInvariant();
        lowerBody.Should().NotContain("token");
        lowerBody.Should().NotContain("secret");
        lowerBody.Should().NotContain("api_key");
    }

    [Fact]
    public async Task HandleGetRuntimeConfigAsync_ReturnsReadModelRuntimeConfigWithoutSecrets()
    {
        var registration = ExplicitModelRegistration("reg-runtime", "scope-1", "key-runtime", "svc-calendar");
        registration.RuntimeConfig = new ChannelBotRuntimeConfig
        {
            Instructions = "Use channel-safe replies.",
            DefaultSkill = new ChannelBotRuntimeDefaultSkillConfig
            {
                Name = "calendar-booking",
                Version = "1.2.3",
            },
            CredentialSourceMode = ChannelBotRuntimeCredentialSourceMode.RegistrationAgentKey,
            ToolSetRefs = { "channel.reply.default" },
            ExtraToolNames = { "calendar_lookup" },
            NyxidServiceSelectors =
            {
                new ChannelBotRuntimeNyxIdServiceSelector
                {
                    ServiceSlug = "api-calendar",
                    EndpointNames = { "events.create" },
                },
            },
        };
        registration.WorkflowResultDeliveryCredential = new SecretReference
        {
            Ref = "sec-hidden-runtime",
            Purpose = CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey,
            OwnerScopeKey = "scope-1",
            Version = 1,
        };
        var queryPort = QueryPortWithSnapshots(new ChannelBotRegistrationSnapshot(registration, 74));
        var http = CreateHttpContext("scope-1");

        var result = await InvokeAsync(
            "HandleGetRuntimeConfigAsync",
            "reg-runtime",
            http,
            queryPort,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Body.Should().Contain("\"registration_id\":\"reg-runtime\"");
        response.Body.Should().Contain("\"state_version\":74");
        response.Body.Should().Contain("\"instructions\":\"Use channel-safe replies.\"");
        response.Body.Should().Contain("\"name\":\"calendar-booking\"");
        response.Body.Should().Contain("\"tool_set_refs\":[\"channel.reply.default\"]");
        response.Body.Should().Contain("\"extra_tool_names\":[\"calendar_lookup\"]");
        response.Body.Should().Contain("\"service_slug\":\"api-calendar\"");
        response.Body.Should().Contain("\"api_key_id\":\"key-runtime\"");
        response.Body.Should().NotContain("sec-hidden-runtime");
        response.Body.Contains("secret_reference", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        response.Body.Contains("owner_scope_key", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        response.Body.Contains("allowed_service_ids", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Fact]
    public async Task HandleGetRuntimeConfigAsync_ReturnsNotFound_WhenCallerDoesNotOwnRegistration()
    {
        var queryPort = QueryPortWithSnapshots(new ChannelBotRegistrationSnapshot(
            NewModelRegistration("reg-foreign-runtime", "scope-2", "key-foreign"),
            11));
        var http = CreateHttpContext("scope-1");

        var result = await InvokeAsync(
            "HandleGetRuntimeConfigAsync",
            "reg-foreign-runtime",
            http,
            queryPort,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        response.Body.Should().NotContain("key-foreign");
    }

    [Fact]
    public async Task HandleUpdateRuntimeConfigAsync_ReturnsAcceptedReceiptWithCommandId()
    {
        var queryPort = QueryPortWith(ExplicitModelRegistration("reg-update", "scope-1", "key-update", "svc-calendar"));
        EventEnvelope? capturedEnvelope = null;
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        ((IActorDispatchPort)actorRuntime).DispatchAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(envelope => capturedEnvelope = envelope),
                Arg.Any<CancellationToken>())
            .Returns(ActorDispatchPortTestSupport.AcceptAsync);
        var http = CreateJsonHttpContext(
            """
            {
              "runtime_config": {
                "instructions": "  Trimmed instructions.  ",
                "default_skill": { "name": "booking-capacity", "version": "1.0" },
                "tool_set_refs": ["channel.reply.default"],
                "extra_tool_names": ["calendar_lookup"],
                "nyxid_service_selectors": [
                  { "service_slug": "api-calendar", "endpoint_names": ["events.create"] }
                ]
              }
            }
            """,
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleUpdateRuntimeConfigAsync",
            "reg-update",
            http,
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime),
            queryPort,
            OwnerResolver("scope-1"),
            AuthorizationPlanner(("svc-calendar", "api-calendar")),
            CreateNyxClient(),
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        response.Body.Should().Contain("\"status\":\"accepted\"");
        response.Body.Should().Contain("\"registration_id\":\"reg-update\"");
        response.Body.Should().Contain("\"command_id\":");
        capturedEnvelope.Should().NotBeNull();
        var command = capturedEnvelope!.Payload.Unpack<ChannelBotUpdateRuntimeConfigCommand>();
        command.RegistrationId.Should().Be("reg-update");
        command.DefaultSkillName.Should().Be("booking-capacity");
        command.RuntimeConfig.Instructions.Should().Be("Trimmed instructions.");
        command.RuntimeConfig.NyxidServiceSelectors.Single().ServiceSlug.Should().Be("api-calendar");
    }

    [Fact]
    public async Task HandleUpdateRuntimeConfigAsync_ReturnsFieldError_WhenSelectorIsNotAuthorized()
    {
        var registration = ExplicitModelRegistration("reg-explicit-update", "scope-1", "key-explicit", "svc-calendar");
        registration.RuntimeConfig = new ChannelBotRuntimeConfig
        {
            NyxidServiceSelectors =
            {
                new ChannelBotRuntimeNyxIdServiceSelector { ServiceSlug = "api-calendar" },
            },
        };
        var queryPort = QueryPortWith(registration);
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        var http = CreateJsonHttpContext(
            """
            {
              "runtime_config": {
                "nyxid_service_selectors": [
                  { "service_slug": "api-github", "endpoint_names": ["issues.create"] }
                ]
              }
            }
            """,
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleUpdateRuntimeConfigAsync",
            "reg-explicit-update",
            http,
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime),
            queryPort,
            OwnerResolver("scope-1"),
            AuthorizationPlanner(("svc-calendar", "api-calendar")),
            CreateNyxClient(),
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        response.Body.Should().Contain("\"error\":\"invalid_runtime_config\"");
        response.Body.Should().Contain("service_not_authorized");
        response.Body.Should().Contain("runtime_config.nyxid_service_selectors[0].service_slug");
        await ((IActorDispatchPort)actorRuntime).DidNotReceiveWithAnyArgs()
            .DispatchAsync(default!, default!, default);
    }

    [Fact]
    public async Task HandleUpdateRuntimeConfigAsync_ReturnsFieldError_WhenDefaultSkillVersionHasNoName()
    {
        var queryPort = QueryPortWith(NewModelRegistration("reg-invalid-skill", "scope-1", "key-invalid-skill"));
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        var http = CreateJsonHttpContext(
            """
            {
              "runtime_config": {
                "default_skill": { "version": "1.0" }
              }
            }
            """,
            "scope-1");

        var result = await InvokeAsync(
            "HandleUpdateRuntimeConfigAsync",
            "reg-invalid-skill",
            http,
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime),
            queryPort,
            OwnerResolver("scope-1"),
            AuthorizationPlanner(),
            CreateNyxClient(),
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        response.Body.Should().Contain("default_skill_name_required");
        await ((IActorDispatchPort)actorRuntime).DidNotReceiveWithAnyArgs()
            .DispatchAsync(default!, default!, default);
    }

    private static IChannelRegistrationOwnerResolver OwnerResolver(string scopeId)
    {
        var resolver = Substitute.For<IChannelRegistrationOwnerResolver>();
        resolver.ResolveAsync(Arg.Any<string>(), scopeId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChannelRegistrationOwnerResolution(
                new VerifiedChannelRegistrationOwner(
                    "user-alpha",
                    new ChannelRegistrationKeyOwner(ChannelRegistrationKeyOwnerKind.Organization, scopeId),
                    scopeId),
                string.Empty)));
        return resolver;
    }

    private static ChannelRegistrationAuthorizationPlanner AuthorizationPlanner(
        params (string ServiceId, string Slug)[] services)
    {
        var serviceArray = services.Select(static service => new NyxIdUserService(
                service.ServiceId,
                service.Slug,
                Label: service.Slug,
                CatalogServiceName: service.Slug,
                IsActive: true,
                CredentialSource: new NyxIdUserServiceCredentialSource(
                    NyxIdUserServiceCredentialSourceKind.Organization,
                    OrganizationId: "scope-1",
                    OrganizationRole: NyxIdOrganizationRole.Admin,
                    Allowed: true)))
            .ToArray();
        var authorizationPort = Substitute.For<IChannelRegistrationNyxIdAuthorizationPort>();
        authorizationPort.ReadUserServicesAsync("test-token", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxIdApiAccessResult<NyxIdUserServices>(
                new NyxIdUserServices(serviceArray),
                null)));
        authorizationPort.PlanApiKeyScopeAsync(
                "test-token",
                Arg.Any<IReadOnlyList<string>>(),
                "scope-1",
                Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new NyxIdApiAccessResult<NyxIdApiKeyScopePlan>(
                ScopePlan(call.ArgAt<IReadOnlyList<string>>(1)),
                null)));
        return new ChannelRegistrationAuthorizationPlanner(authorizationPort);
    }

    private static NyxIdApiKeyScopePlan ScopePlan(IReadOnlyList<string> selectedServiceIds) =>
        new(
            NyxIdApiAccessResponseParser.ScopePlanAuthority,
            NyxIdApiAccessResponseParser.ScopePlanContractVersion,
            NyxIdApiAccessResponseParser.ScopePlanPolicyVersion,
            new NyxIdScopePlanPrincipal("user-alpha", NyxIdScopePlanPrincipalKind.Personal),
            new NyxIdScopePlanPrincipal("scope-1", NyxIdScopePlanPrincipalKind.Organization),
            selectedServiceIds.Select(static serviceId => new NyxIdScopePlanServiceGrant(
                    serviceId,
                    new NyxIdScopePlanPrincipal("scope-1", NyxIdScopePlanPrincipalKind.Organization),
                    new NyxIdScopePlanNodeGrant(NyxIdScopePlanNodeGrantKind.NotRequired, [])))
                .ToArray(),
            selectedServiceIds.ToArray(),
            [],
            new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero),
            "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            new NyxIdScopePlanFreshness(
                NyxIdScopePlanFreshnessMode.MutationRevalidatedSnapshot,
                "scope_plan_digest",
                NyxIdScopePlanPostCreationDrift.FailClosed),
            new NyxIdScopePlanCompleteness(
                true,
                true,
                NyxIdScopePlanRouteCandidateBasis.ActiveConfiguredRoutes,
                true));

    private static IActorRuntime AcceptedRegistrationRuntime(Action<EventEnvelope>? capture = null)
    {
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        ((IActorDispatchPort)actorRuntime).DispatchAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(envelope => capture?.Invoke(envelope)),
                Arg.Any<CancellationToken>())
            .Returns(ActorDispatchPortTestSupport.AcceptAsync);
        return actorRuntime;
    }

    private static NyxIdApiClient CreateNyxClient(RecordingNyxHttpMessageHandler? handler = null)
    {
        var httpClient = new HttpClient(handler ?? new RecordingNyxHttpMessageHandler(NotFoundResponse))
        {
            BaseAddress = new Uri("https://nyx.test"),
        };
        return new NyxIdApiClient(
            new NyxIdToolOptions
            {
                BaseUrl = "https://nyx.test",
                ApiBaseUrl = "https://nyx.test",
            },
            httpClient,
            NullLogger<NyxIdApiClient>.Instance);
    }

    private static NyxIdApiClient NyxClientWithChannelBots(params string[] botIds)
    {
        var items = string.Join(",", botIds.Select(botId => $$"""
            {"id":"{{botId}}","platform":"lark","name":"{{botId}} label","status":"active","active":true,"webhook_url":"https://nyx.example.com/api/v1/webhooks/channel/lark/{{botId}}"}
            """));
        return CreateNyxClient(new RecordingNyxHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v1/channel-bots")
                return JsonResponse($$"""{"items":[{{items}}]}""");

            return NotFoundResponse(request);
        }));
    }

    private static ChannelAgentKeyProvisioningService CreateAgentKeyProvisioningService(NyxIdApiClient nyxClient) =>
        new(
            nyxClient,
            new InMemorySecretVault(),
            NullLogger<ChannelAgentKeyProvisioningService>.Instance,
            ChannelAgentKeyWriteMode.NyxIdDefault);

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage NotFoundResponse(HttpRequestMessage request) =>
        new(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                $$"""{"error":true,"status":404,"body":"not found: {{request.Method}} {{request.RequestUri!.AbsolutePath}}"}""",
                Encoding.UTF8,
                "application/json"),
        };

    private sealed class RecordingNyxHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(CloneRequest(request));
            return Task.FromResult(handler(request));
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return clone;
        }
    }

    private static IChannelBotRegistrationQueryPort QueryPortWith(params ChannelBotRegistrationEntry[] entries)
    {
        var snapshots = entries
            .Select(static entry => new ChannelBotRegistrationSnapshot(entry, 0))
            .ToArray();
        return QueryPortWithSnapshots(snapshots);
    }

    private static IChannelBotRegistrationQueryPort QueryPortWithSnapshots(
        params ChannelBotRegistrationSnapshot[] snapshots)
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>(
                snapshots.Select(static snapshot => snapshot.Registration).ToArray()));
        queryPort.QueryAllSnapshotsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationSnapshot>>(snapshots));
        foreach (var snapshot in snapshots)
        {
            queryPort.GetAsync(snapshot.Registration.Id, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(snapshot.Registration));
            queryPort.GetSnapshotAsync(snapshot.Registration.Id, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<ChannelBotRegistrationSnapshot?>(snapshot));
        }
        return queryPort;
    }

    private static ChannelBotRegistrationEntry NewModelRegistration(
        string registrationId,
        string scopeId,
        string apiKeyId)
    {
        var reference = new SecretReference
        {
            Ref = $"sec-{registrationId}",
            Purpose = CredentialSecretPurposes.ChannelNyxIdAgentKey,
            OwnerScopeKey = scopeId,
            Version = 1,
            Fingerprint = $"sha256:{registrationId}",
            CreatedAtUnixMs = 1788825600000,
        };
        return new ChannelBotRegistrationEntry
        {
            Id = registrationId,
            Platform = "lark",
            ScopeId = scopeId,
            NyxAgentApiKeyId = apiKeyId,
            NyxChannelBotId = registrationId.Replace("reg-", "bot-", StringComparison.Ordinal),
            WebhookUrl = $"https://nyx.example.com/api/v1/webhooks/channel/lark/{registrationId.Replace("reg-", "bot-", StringComparison.Ordinal)}",
            WorkflowResultDeliveryCredential = reference.Clone(),
            AuthorizationMode = ChannelRegistrationAuthorizationMode.NyxidDefault,
            ChannelAgentKey = new ChannelAgentKeyCredential
            {
                ApiKeyId = apiKeyId,
                SecretReference = reference,
                Grant = new ChannelAgentKeyGrantSnapshot
                {
                    AllowedServiceIds = { "svc-alpha" },
                    AllowedNodeIds = { "node-alpha" },
                    AllowAllServices = false,
                    AllowAllNodes = false,
                },
            },
        };
    }

    private static ChannelBotRegistrationEntry ExplicitModelRegistration(
        string registrationId,
        string scopeId,
        string apiKeyId,
        params string[] serviceIds)
    {
        var registration = NewModelRegistration(registrationId, scopeId, apiKeyId);
        registration.AuthorizationMode = ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist;
        registration.RegistrationServiceAllowlist = new ChannelRegistrationServiceAllowlist
        {
            ServiceIds = { serviceIds },
        };
        registration.ChannelAgentKey.Grant.ScopePlanDigest =
            "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        return registration;
    }

    private static IPlatformAdminAuthorizer AdminAuthorizer(bool elevated)
    {
        var authorizer = Substitute.For<IPlatformAdminAuthorizer>();
        authorizer.ResolveCallerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PlatformCaller(
                elevated,
                elevated ? "admin" : string.Empty,
                "e@x",
                "u",
                elevated ? PlatformAdminGrantSources.NyxIdPlatformRole : string.Empty)));
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
            WebhookUrl = "https://nyx.example/api/v1/webhooks/channel/lark/bot-alpha",
        });
        var http = CreateHttpContext("scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync("HandleListRegistrationsAsync", http, queryPort, AdminAuthorizer(false), NyxClientWithChannelBots("bot-1"), (string?)null, CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Body.Should().Contain("\"registration_mode\":\"nyx_relay_webhook\"");
        response.Body.Should().Contain("\"callback_url\":\"\"");
        response.Body.Should().Contain("\"webhook_url\":\"https://nyx.example/api/v1/webhooks/channel/lark/bot-alpha\"");
        response.Body.Should().Contain("\"owned\":true");
        response.Body.Should().Contain("\"workflow_result_delivery_status\":\"repair_required\"");
    }

    [Fact]
    public async Task HandleListRegistrationsAsync_ExposesTypedCapabilityWithoutSecretReferences()
    {
        var queryPort = QueryPortWithSnapshots(
            new ChannelBotRegistrationSnapshot(
                NewModelRegistration("reg-new", "scope-1", "key-new"),
                43),
            new ChannelBotRegistrationSnapshot(
                ExplicitModelRegistration(
                    "reg-explicit-list",
                    "scope-1",
                    "key-explicit-list",
                    "svc-alpha",
                    "svc-beta"),
                46),
            new ChannelBotRegistrationSnapshot(
                ExplicitModelRegistration(
                    "reg-explicit-empty",
                    "scope-1",
                    "key-explicit-empty"),
                45),
            new ChannelBotRegistrationSnapshot(
                new ChannelBotRegistrationEntry
                {
                    Id = "reg-invalid-new",
                    Platform = "telegram",
                    ScopeId = "scope-1",
                    NyxChannelBotId = "bot-invalid-new",
                    NyxAgentApiKeyId = "key-invalid-new",
                    AuthorizationMode = ChannelRegistrationAuthorizationMode.NyxidDefault,
                },
                44),
            new ChannelBotRegistrationSnapshot(
                new ChannelBotRegistrationEntry
                {
                    Id = "reg-enabled",
                    Platform = "lark",
                    ScopeId = "scope-1",
                    NyxChannelBotId = "bot-enabled",
                    NyxAgentApiKeyId = "key-enabled",
                    WorkflowResultDeliveryCredential = new SecretReference
                    {
                        Ref = "sec-hidden-alpha",
                        Purpose = CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey,
                        OwnerScopeKey = "scope-1",
                        Version = 1,
                    },
                },
                41),
            new ChannelBotRegistrationSnapshot(
                new ChannelBotRegistrationEntry
                {
                    Id = "reg-failed",
                    Platform = "lark",
                    ScopeId = "scope-1",
                    NyxChannelBotId = "bot-failed",
                    NyxAgentApiKeyId = "key-old-alpha",
                    WorkflowResultDeliveryRepair = new ChannelWorkflowResultDeliveryRepairState
                    {
                        RequestId = "repair-alpha",
                        Status = ChannelWorkflowResultDeliveryRepairStatus.Failed,
                        FailurePhase = ChannelWorkflowResultDeliveryRepairPhase.RouteRebinding,
                        FailureReason = ChannelWorkflowResultDeliveryRepairFailureReason.RouteUpdateFailed,
                    },
                },
                42));
        var http = CreateHttpContext("scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleListRegistrationsAsync",
            http,
            queryPort,
            AdminAuthorizer(false),
            NyxClientWithChannelBots("bot-new", "bot-explicit-list", "bot-explicit-empty", "bot-invalid-new", "bot-enabled", "bot-failed"),
            (string?)null,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        using var document = JsonDocument.Parse(response.Body);
        var newRegistration = document.RootElement.EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "reg-new");
        newRegistration.GetProperty("authorization_mode").GetString()
            .Should().Be("nyxid_default");
        newRegistration.GetProperty("state_version").GetInt64().Should().Be(43);
        newRegistration.TryGetProperty("service_ids", out _).Should().BeFalse();
        var explicitRegistration = document.RootElement.EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "reg-explicit-list");
        explicitRegistration.GetProperty("authorization_mode").GetString()
            .Should().Be("explicit_service_allowlist");
        explicitRegistration.GetProperty("service_ids").EnumerateArray()
            .Select(static item => item.GetString())
            .Should().Equal("svc-alpha", "svc-beta");
        var explicitEmptyRegistration = document.RootElement.EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "reg-explicit-empty");
        explicitEmptyRegistration.GetProperty("authorization_mode").GetString()
            .Should().Be("explicit_service_allowlist");
        explicitEmptyRegistration.GetProperty("service_ids").EnumerateArray().Should().BeEmpty();
        var invalidNew = document.RootElement.EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "reg-invalid-new");
        invalidNew.GetProperty("authorization_mode").GetString()
            .Should().Be("nyxid_default");
        invalidNew.GetProperty("workflow_result_delivery_status").GetString()
            .Should().Be("contract_invalid");
        invalidNew.GetProperty("state_version").GetInt64().Should().Be(44);
        var enabled = document.RootElement.EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "reg-enabled");
        enabled.GetProperty("workflow_result_delivery_status").GetString()
            .Should().Be("enabled");
        enabled.TryGetProperty("workflow_result_delivery_failure_phase", out _)
            .Should().BeFalse();
        enabled.TryGetProperty("authorization_mode", out _).Should().BeFalse();
        enabled.TryGetProperty("service_ids", out _).Should().BeFalse();
        enabled.GetProperty("state_version").GetInt64().Should().Be(41);
        var failed = document.RootElement.EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "reg-failed");
        failed.GetProperty("workflow_result_delivery_status").GetString()
            .Should().Be("repair_failed");
        failed.GetProperty("workflow_result_delivery_failure_phase").GetString()
            .Should().Be("route_rebinding");
        failed.GetProperty("workflow_result_delivery_failure_reason").GetString()
            .Should().Be("route_update_failed");
        response.Body.Should().NotContain("sec-hidden-alpha");
        response.Body.Should().NotContain("sec-reg-new");
        response.Body.Contains("secret_reference", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        response.Body.Contains("owner_scope_key", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        response.Body.Contains("channel_agent_key", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        response.Body.Contains("allowed_service_ids", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        response.Body.Contains("allowed_node_ids", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        response.Body.Contains("allow_all_services", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        response.Body.Contains("scope_plan_digest", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        response.Body.Contains("fingerprint", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        await queryPort.DidNotReceive().GetStateVersionAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleListRegistrationsAsync_ExcludesOtherAccountsByDefault()
    {
        var queryPort = QueryPortWith(
            new ChannelBotRegistrationEntry { Id = "mine", Platform = "lark", ScopeId = "scope-1", NyxChannelBotId = "bot-mine" },
            new ChannelBotRegistrationEntry { Id = "theirs", Platform = "lark", ScopeId = "scope-2", NyxChannelBotId = "bot-theirs" });
        var http = CreateHttpContext("scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync("HandleListRegistrationsAsync", http, queryPort, AdminAuthorizer(false), NyxClientWithChannelBots("bot-mine", "bot-theirs"), (string?)null, CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Body.Should().Contain("bot-mine");
        response.Body.Should().Contain("bot-theirs");
        response.Body.Should().NotContain("\"id\":\"theirs\"");
    }

    [Fact]
    public async Task HandleListRegistrationsAsync_ScopeAll_Forbidden_WhenNotAdmin()
    {
        var queryPort = QueryPortWith(new ChannelBotRegistrationEntry { Id = "x", Platform = "lark", ScopeId = "scope-2" });
        var http = CreateHttpContext("scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync("HandleListRegistrationsAsync", http, queryPort, AdminAuthorizer(false), NyxClientWithChannelBots("bot-mine", "bot-theirs"), "all", CancellationToken.None);
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

        var result = await InvokeAsync("HandleListRegistrationsAsync", http, queryPort, AdminAuthorizer(true), NyxClientWithChannelBots("bot-mine", "bot-theirs"), "all", CancellationToken.None);
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
        // L1: an admin is allowed the cross-account view (mirrors the
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
        var registration = NewModelRegistration("reg-1", "scope-1", "key-1");
        registration.NyxConversationRouteId = "route-1";
        registration.NyxChannelBotId = "bot-1";
        queryPort.GetAsync("reg-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(registration));

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
                Arg.Any<string>(), Arg.Any<NyxChannelBotDeprovisioningRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotDeprovisioningResult(true, true, true, Array.Empty<string>())));

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
            "test-token",
            Arg.Is<NyxChannelBotDeprovisioningRequest>(request =>
                request.RegistrationId == "reg-1" &&
                request.ConversationRouteId == "route-1" &&
                request.ChannelBotId == "bot-1" &&
                request.AgentKeyId == "key-1" &&
                request.SecretReference != null &&
                request.SecretReference.Ref == "sec-reg-1"),
            Arg.Any<CancellationToken>());
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
                Arg.Any<string>(), Arg.Any<NyxChannelBotDeprovisioningRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotDeprovisioningResult(false, false, false, Array.Empty<string>())));

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
    public async Task HandleDeleteRegistrationAsync_HardAgentKeyFailure_ReturnsBadGateway_AndDoesNotTombstone()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("reg-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
            {
                Id = "reg-1",
                Platform = "lark",
                NyxChannelBotId = "bot-1",
                NyxAgentApiKeyId = "key-1",
            }));
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        var deprovision = Substitute.For<INyxChannelBotDeprovisioningService>();
        deprovision.DeprovisionAsync(
                Arg.Any<string>(), Arg.Any<NyxChannelBotDeprovisioningRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotDeprovisioningResult(true, false, false, Array.Empty<string>())));
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
        response.Body.Should().Contain("nyx_agent_key_delete_failed");
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
                Arg.Any<string>(), Arg.Any<NyxChannelBotDeprovisioningRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotDeprovisioningResult(
                true, true, true, new[] { "vault_revoke_failed" })));

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
        response.Body.Should().Contain("vault_revoke_failed");
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
                WorkflowResultDeliveryCredential = new SecretReference
                {
                    Ref = "vault://channel/legacy-tg",
                    Purpose = CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey,
                    OwnerScopeKey = "scope-1",
                    Version = 1,
                    Fingerprint = "sha256:legacy-tg",
                    CreatedAtUnixMs = 1788825600000,
                },
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
                Arg.Any<string>(), Arg.Any<NyxChannelBotDeprovisioningRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotDeprovisioningResult(true, true, true, Array.Empty<string>())));

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
            "test-token",
            Arg.Is<NyxChannelBotDeprovisioningRequest>(request =>
                request.RegistrationId == "reg-tg" &&
                request.ConversationRouteId == "route-tg" &&
                request.ChannelBotId == "bot-tg" &&
                request.AgentKeyId == "key-tg" &&
                request.SecretReference != null &&
                request.SecretReference.Ref == "vault://channel/legacy-tg"),
            Arg.Any<CancellationToken>());
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
            default!, default!, default);
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
            default!, default!, default);
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
        response.Body.Should().Contain("\"workflow_result_delivery_status\":\"repair_required\"");
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

    private static HttpContext CreateAuthenticatedHttpContext(
        string? scopeId,
        params Claim[] subjectClaims)
    {
        var context = CreateHttpContext();
        var claims = new List<Claim>(subjectClaims);
        if (!string.IsNullOrWhiteSpace(scopeId))
            claims.Add(new Claim("scope_id", scopeId));
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        return context;
    }

    private static async Task<WebApplication> CreateRouteAuditAppAsync(
        RecordingAuditTrailAppender appender,
        IChannelWorkflowResultDeliveryRepairService? repairService = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = Environments.Development,
        });
        var nyxClient = CreateRouteAuditNyxClient();
        var actorRuntime = AcceptedRegistrationRuntime();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services
            .AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, RouteAuditAuthenticationHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IAuditTrailAppender>(appender);
        builder.Services.AddSingleton<IAuditActorIdentityHasher>(new StableAuditActorIdentityHasher());
        builder.Services.AddSingleton(ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime));
        builder.Services.AddSingleton(QueryPortWithSnapshots());
        builder.Services.AddSingleton(OwnerResolver("scope-1"));
        builder.Services.AddSingleton(AuthorizationPlanner());
        builder.Services.AddSingleton(nyxClient);
        builder.Services.AddSingleton(CreateAgentKeyProvisioningService(nyxClient));
        builder.Services.AddSingleton(Substitute.For<IPlatformAdminAuthorizer>());
        builder.Services.AddSingleton(Substitute.For<INyxChannelBotDeprovisioningService>());
        builder.Services.AddSingleton(
            repairService ?? Substitute.For<IChannelWorkflowResultDeliveryRepairService>());

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseMiddleware<EndpointAuditCaptureMiddleware>();
        app.UseAuthorization();
        app.MapChannelCallbackEndpoints();
        await app.StartAsync();
        return app;
    }

    private static NyxIdApiClient CreateRouteAuditNyxClient() =>
        CreateNyxClient(new RecordingNyxHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v1/channel-bots/bot-audit")
            {
                return JsonResponse("""{"id":"bot-audit","platform":"lark","name":"Audit Bot","status":"active","active":true,"webhook_url":"https://nyx.example.com/api/v1/webhooks/channel/lark/bot-audit"}""");
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/v1/api-keys")
            {
                return JsonResponse("""{"id":"key-audit","full_key":"audit-secret-value","scopes":"read write proxy","platform":"generic","purpose":"general","scheduled_write_enabled":false,"durable_grants":[],"allow_all_services":true,"allow_all_nodes":true,"allowed_service_ids":[],"allowed_node_ids":[]}""");
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v1/channel-conversations")
            {
                return JsonResponse("""{"items":[{"id":"route-audit","channel_bot_id":"bot-audit","agent_api_key_id":"key-old","default_agent":true}]}""");
            }

            if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath == "/api/v1/channel-conversations/route-audit")
            {
                return JsonResponse("""{"id":"route-audit","channel_bot_id":"bot-audit","agent_api_key_id":"key-audit","default_agent":true}""");
            }

            return NotFoundResponse(request);
        }));

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
