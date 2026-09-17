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
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
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
    public async Task HandleRegisterAsync_IncompleteCompensationExposesOwnedHandlesAndCleanupOutcomes()
    {
        var adoption = Substitute.For<INyxChannelBotAdoptionService>();
        adoption.AdoptAsync(Arg.Any<NyxChannelBotAdoptionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new NyxChannelBotAdoptionResult(false, "error", "lark", "reg-retained", "bot-lark", "key-retained", "route-retained",
                Error: "local_mirror_dispatch_failed", Note: "Command was not accepted. Owned resource cleanup is incomplete.",
                Cleanup: new(true, false, ["conversation_route_delete_failed id=route-retained"], false),
                CleanupRequest: new("reg-retained", "lark", "route-retained", "key-retained",
                    new SecretReference { Ref = "vault://private-reference" })));
        var http = CreateJsonHttpContext(
            """{"platform":"lark","nyx_channel_bot_id":"bot-lark","webhook_base_url":"https://aevatar.example.com"}""", "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var response = await ExecuteResultAsync(await InvokeAsync("HandleRegisterAsync", http,
            CreateRegistrationFacade(adoption), NullLoggerFactory.Instance, CancellationToken.None));

        response.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        using var body = JsonDocument.Parse(response.Body);
        var root = body.RootElement;
        root.GetProperty("error").GetString().Should().Be("local_mirror_dispatch_failed");
        root.GetProperty("registration_id").GetString().Should().Be("reg-retained");
        root.GetProperty("nyx_channel_bot_id").GetString().Should().Be("bot-lark");
        root.GetProperty("nyx_agent_api_key_id").GetString().Should().Be("key-retained");
        root.GetProperty("nyx_conversation_route_id").GetString().Should().Be("route-retained");
        var cleanup = root.GetProperty("cleanup");
        cleanup.GetProperty("complete").GetBoolean().Should().BeFalse();
        cleanup.GetProperty("conversation_route_removed").GetBoolean().Should().BeFalse();
        cleanup.GetProperty("agent_key_removed").GetBoolean().Should().BeTrue();
        cleanup.GetProperty("vault_secret_revoked").GetBoolean().Should().BeTrue();
        cleanup.GetProperty("warnings")[0].GetString().Should().Contain("route-retained");
        response.Body.Should().NotContain("vault://").And.NotContain("cleanup_request").And.NotContain("acceptance_unknown");
    }

    [Fact]
    public async Task HandleRegisterAsync_UnknownRouteAcquisitionPreservesIncompleteCleanupWithoutInternalHandles()
    {
        var adoption = Substitute.For<INyxChannelBotAdoptionService>();
        adoption.AdoptAsync(Arg.Any<NyxChannelBotAdoptionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new NyxChannelBotAdoptionResult(false, "error", "matrix", "reg-retained", "bot-matrix", "key-retained", null,
                Error: "provisioning_failed", Note: "Owned resource cleanup is incomplete.",
                Cleanup: new(false, false, ["conversation_route_acquisition_unresolved"],
                    ConversationRouteRemoved: false, VaultSecretRevoked: false),
                CleanupRequest: new("reg-retained", "matrix", null, "key-retained",
                    new SecretReference { Ref = "vault://private-reference" }, AgentKeyDeletionRequired: true)));
        var http = CreateJsonHttpContext(
            """{"platform":"matrix","nyx_channel_bot_id":"bot-matrix","webhook_base_url":"https://aevatar.example.com"}""", "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var response = await ExecuteResultAsync(await InvokeAsync("HandleRegisterAsync", http,
            CreateRegistrationFacade(adoption), NullLoggerFactory.Instance, CancellationToken.None));

        response.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        using var body = JsonDocument.Parse(response.Body);
        var root = body.RootElement;
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("error").GetString().Should().Be("provisioning_failed");
        root.GetProperty("registration_id").GetString().Should().Be("reg-retained");
        root.GetProperty("nyx_channel_bot_id").GetString().Should().Be("bot-matrix");
        root.GetProperty("nyx_agent_api_key_id").GetString().Should().Be("key-retained");
        root.GetProperty("nyx_conversation_route_id").GetString().Should().BeEmpty();
        var cleanup = root.GetProperty("cleanup");
        cleanup.GetProperty("complete").GetBoolean().Should().BeFalse();
        cleanup.GetProperty("conversation_route_removed").GetBoolean().Should().BeFalse();
        cleanup.GetProperty("agent_key_removed").GetBoolean().Should().BeFalse();
        cleanup.GetProperty("vault_secret_revoked").GetBoolean().Should().BeFalse();
        cleanup.GetProperty("warnings").EnumerateArray().Select(static warning => warning.GetString())
            .Should().Equal("conversation_route_acquisition_unresolved");
        response.Body.Should().NotContain("vault://").And.NotContain("cleanup_request")
            .And.NotContain("CleanupRequest").And.NotContain("cleanupRequest");
    }

    [Theory]
    [InlineData("\tmatrix")]
    [InlineData("matrix\n")]
    [InlineData("ma trix")]
    public async Task HandleRegisterAsync_InvalidPlatformCannotBeTrimmedIntoValidity(string platform)
    {
        var adoption = Substitute.For<INyxChannelBotAdoptionService>();
        adoption.AdoptAsync(Arg.Any<NyxChannelBotAdoptionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new NyxChannelBotAdoptionResult(true, "accepted", "matrix"));
        var http = CreateJsonHttpContext(JsonSerializer.Serialize(new
        {
            platform, nyx_channel_bot_id = "bot-matrix", webhook_base_url = "https://aevatar.example.com",
        }), "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";
        var response = await ExecuteResultAsync(await InvokeAsync("HandleRegisterAsync", http,
            CreateRegistrationFacade(adoption), NullLoggerFactory.Instance, CancellationToken.None));
        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        response.Body.Should().Contain("invalid_channel_bot_detail");
        await adoption.DidNotReceiveWithAnyArgs().AdoptAsync(default!, default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" bot-lark")]
    [InlineData("bot-lark ")]
    public async Task HandleRegisterAsync_RequiresNonPaddedBotIdBeforeAdoption(string? botId)
    {
        var adoption = Substitute.For<INyxChannelBotAdoptionService>();
        var http = CreateJsonHttpContext(JsonSerializer.Serialize(new
        {
            platform = "lark", nyx_channel_bot_id = botId, webhook_base_url = "https://aevatar.example.com",
        }), "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";
        var result = await InvokeAsync("HandleRegisterAsync", http, CreateRegistrationFacade(adoption),
            NullLoggerFactory.Instance, CancellationToken.None);
        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        response.Body.Should().Contain("missing_nyx_channel_bot_id");
        await adoption.DidNotReceiveWithAnyArgs().AdoptAsync(default!, default);
    }

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
        await using var app = await CreateRouteAuditAppAsync(
            appender,
            CreateRegistrationFacade(new AcceptedProvisioningService()));
        using var client = CreateClient(app);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/channels/registrations?access_token={RawToken}&email=alice@example.com")
        {
            Content = new StringContent("""
            {
              "platform": "lark","nyx_channel_bot_id":"bot-lark",
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
            CreateRegistrationFacade(new AcceptedProvisioningService()),
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
    public async Task HandleRegisterAsync_AcceptsNewPlatformThroughNeutralAdoption()
    {
        var registrationFacade = CreateRegistrationFacade();
        var http = CreateJsonHttpContext(
            """{"platform":"matrix","nyx_channel_bot_id":"bot-matrix","webhook_base_url":"https://aevatar.example.com"}""",
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleRegisterAsync",
            http,
            registrationFacade,
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        response.Body.Should().NotContain("unsupported_platform");
    }

    [Fact]
    public async Task HandleRegisterAsync_ReturnsServiceUnavailableWithoutWrites_WhenAgentKeyWriteGateIsClosed()
    {
        new NyxIdRelayOptions().ChannelAgentKeyWriteMode
            .Should().Be(ChannelAgentKeyWriteMode.Disabled);
        var provisioningService = Substitute.For<INyxChannelBotAdoptionService>();
        var registrationFacade = ChannelAdoptionFacadeTestSupport.Create(provisioningService, ChannelAgentKeyWriteMode.Disabled);
        var http = CreateJsonHttpContext(
            """{"platform":"lark","nyx_channel_bot_id":"bot-lark","app_id":"cli_123","app_secret":"secret","verification_token":"verify","webhook_base_url":"https://aevatar.example.com"}""",
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleRegisterAsync",
            http,
            registrationFacade,
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        response.Body.Should().Contain("\"error\":\"channel_agent_key_write_gate_closed\"");
        await provisioningService.DidNotReceiveWithAnyArgs()
            .AdoptAsync(default!, default);
    }

    [Fact]
    public async Task HandleRegisterAsync_ProvisionsLarkViaNyx()
    {
        var provisioningService = Substitute.For<INyxChannelBotAdoptionService>();
        provisioningService.AdoptAsync(Arg.Any<NyxChannelBotAdoptionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotAdoptionResult(
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
            """{"platform":"lark","nyx_channel_bot_id":"bot-lark","app_id":"cli_123","app_secret":"secret","verification_token":"verify-123","webhook_base_url":"https://aevatar.example.com"}""",
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync("HandleRegisterAsync", http, CreateRegistrationFacade(provisioningService), NullLoggerFactory.Instance, CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        response.Body.Should().Contain("\"registration_id\":\"reg-1\"");
        response.Body.Should().Contain("\"relay_callback_url\":\"https://aevatar.example.com/api/webhooks/nyxid-relay\"");
        response.Body.Should().Contain("\"workflow_result_delivery_status\":\"repair_required\"");
        await provisioningService.Received(1).AdoptAsync(
            Arg.Is<NyxChannelBotAdoptionRequest>(request =>
                request.Registration.Platform == "lark" &&
                request.Registration.AccessToken == "test-token" &&
                request.Registration.WebhookBaseUrl == "https://aevatar.example.com" &&
                request.Registration.ScopeId == "scope-1" &&
                request.Registration.NyxChannelBotId == "bot-lark"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleRegisterAsync_ReturnsStableMissingAccessTokenError()
    {
        var provisioningService = Substitute.For<INyxChannelBotAdoptionService>();
        var http = CreateJsonHttpContext(
            """{"platform":"lark","nyx_channel_bot_id":"bot-lark","app_id":"cli_123","app_secret":"secret","verification_token":"verify-123","webhook_base_url":"https://aevatar.example.com"}""",
            "scope-1");

        var result = await InvokeAsync(
            "HandleRegisterAsync",
            http,
            CreateRegistrationFacade(provisioningService),
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        response.Body.Should().Contain("\"error\":\"missing_access_token\"");
        await provisioningService.DidNotReceive().AdoptAsync(
            Arg.Any<NyxChannelBotAdoptionRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"svc-alpha\"")]
    [InlineData("[null]")]
    [InlineData("[42]")]
    [InlineData("[\"\"]")]
    [InlineData("[\"   \"]")]
    public async Task HandleRegisterAsync_RejectsInvalidServiceIdsBeforeProvisioning(
        string serviceIdsJson)
    {
        var provisioningService = Substitute.For<INyxChannelBotAdoptionService>();
        provisioningService.AdoptAsync(
                Arg.Any<NyxChannelBotAdoptionRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotAdoptionResult(
                Succeeded: true,
                Status: "accepted",
                Platform: "lark",
                RegistrationId: "reg-invalid-service-ids")));
        var http = CreateJsonHttpContext(
            $$"""{"platform":"lark","nyx_channel_bot_id":"bot-lark","app_id":"cli_123","app_secret":"secret","verification_token":"verify-123","webhook_base_url":"https://aevatar.example.com","authorization_mode":"explicit_service_allowlist","service_ids":{{serviceIdsJson}}}""",
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleRegisterAsync",
            http,
            CreateRegistrationFacade(provisioningService),
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        response.Body.Should().Contain("invalid_service_ids");
        await provisioningService.DidNotReceive().AdoptAsync(
            Arg.Any<NyxChannelBotAdoptionRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleRegisterAsync_WhenServiceIdsAreMissing_SelectsNyxIdDefault()
    {
        ChannelRelayRegistrationRequest? captured = null;
        var provisioningService = Substitute.For<INyxChannelBotAdoptionService>();
        provisioningService.AdoptAsync(
                Arg.Do<NyxChannelBotAdoptionRequest>(request => captured = request.Registration),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotAdoptionResult(
                Succeeded: true,
                Status: "accepted",
                Platform: "lark",
                RegistrationId: "reg-default")));
        var http = CreateJsonHttpContext(
            """{"platform":"lark","nyx_channel_bot_id":"bot-lark","app_id":"cli_123","app_secret":"secret","verification_token":"verify-123","webhook_base_url":"https://aevatar.example.com"}""",
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleRegisterAsync",
            http,
            CreateRegistrationFacade(provisioningService),
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        captured.Should().NotBeNull();
        captured!.ServiceSelection.AuthorizationMode.Should()
            .Be(ChannelRegistrationAuthorizationMode.NyxidDefault);
        captured.ServiceSelection.ServiceIds.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleRegisterAsync_WhenServiceIdsArePresentWithoutMode_SelectsExplicitAllowlist()
    {
        ChannelRelayRegistrationRequest? captured = null;
        var provisioningService = Substitute.For<INyxChannelBotAdoptionService>();
        provisioningService.AdoptAsync(
                Arg.Do<NyxChannelBotAdoptionRequest>(request => captured = request.Registration),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotAdoptionResult(
                Succeeded: true,
                Status: "accepted",
                Platform: "lark",
                RegistrationId: "reg-legacy-default")));
        var http = CreateJsonHttpContext(
            """{"platform":"lark","nyx_channel_bot_id":"bot-lark","app_id":"cli_123","app_secret":"secret","verification_token":"verify-123","webhook_base_url":"https://aevatar.example.com","service_ids":["svc-legacy"]}""",
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleRegisterAsync",
            http,
            CreateRegistrationFacade(provisioningService),
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        captured.Should().NotBeNull();
        captured!.ServiceSelection.AuthorizationMode.Should()
            .Be(ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist);
        captured.ServiceSelection.ServiceIds.Should().Equal("svc-legacy");
    }

    [Fact]
    public async Task HandleRegisterAsync_WhenExplicitModeOmitsServiceIds_SelectsEmptyExplicitAllowlist()
    {
        ChannelRelayRegistrationRequest? captured = null;
        var provisioningService = Substitute.For<INyxChannelBotAdoptionService>();
        provisioningService.AdoptAsync(
                Arg.Do<NyxChannelBotAdoptionRequest>(request => captured = request.Registration),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotAdoptionResult(
                Succeeded: true,
                Status: "accepted",
                Platform: "lark",
                RegistrationId: "reg-explicit-empty")));
        var http = CreateJsonHttpContext(
            """{"platform":"lark","nyx_channel_bot_id":"bot-lark","app_id":"cli_123","app_secret":"secret","verification_token":"verify-123","webhook_base_url":"https://aevatar.example.com","authorization_mode":"explicit_service_allowlist"}""",
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleRegisterAsync",
            http,
            CreateRegistrationFacade(provisioningService),
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        captured.Should().NotBeNull();
        captured!.ServiceSelection.AuthorizationMode.Should()
            .Be(ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist);
        captured.ServiceSelection.ServiceIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData("[]", new string[0])]
    [InlineData("[\" svc-b \",\"svc-a\",\"svc-a\"]", new[] { "svc-a", "svc-b" })]
    public async Task HandleRegisterAsync_WhenServiceIdsAreValid_SelectsCanonicalExplicitAllowlist(
        string serviceIdsJson,
        string[] expectedServiceIds)
    {
        ChannelRelayRegistrationRequest? captured = null;
        var provisioningService = Substitute.For<INyxChannelBotAdoptionService>();
        provisioningService.AdoptAsync(
                Arg.Do<NyxChannelBotAdoptionRequest>(request => captured = request.Registration),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotAdoptionResult(
                Succeeded: true,
                Status: "accepted",
                Platform: "lark",
                RegistrationId: "reg-explicit")));
        var http = CreateJsonHttpContext(
            $$"""{"platform":"lark","nyx_channel_bot_id":"bot-lark","app_id":"cli_123","app_secret":"secret","verification_token":"verify-123","webhook_base_url":"https://aevatar.example.com","authorization_mode":"explicit_service_allowlist","service_ids":{{serviceIdsJson}}}""",
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleRegisterAsync",
            http,
            CreateRegistrationFacade(provisioningService),
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        captured.Should().NotBeNull();
        captured!.ServiceSelection.AuthorizationMode.Should()
            .Be(ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist);
        captured.ServiceSelection.ServiceIds.Should().Equal(expectedServiceIds);
    }

    [Fact]
    public async Task HandleRegisterAsync_DoesNotCarryLegacySecretsIntoApplicationRequest()
    {
        ChannelRelayRegistrationRequest? captured = null;
        var provisioningService = Substitute.For<INyxChannelBotAdoptionService>();
        provisioningService.AdoptAsync(
                Arg.Do<NyxChannelBotAdoptionRequest>(request => captured = request.Registration),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotAdoptionResult(
                Succeeded: true,
                Status: "accepted",
                Platform: "lark",
                RegistrationId: "reg-1")));

        var http = CreateJsonHttpContext(
            """{"platform":"lark","nyx_channel_bot_id":"bot-lark","app_id":"cli_123","app_secret":"secret-alpha","verification_token":"verify-alpha","encrypt_key":" encrypt-alpha ","webhook_base_url":"https://aevatar.example.com"}""",
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleRegisterAsync",
            http,
            CreateRegistrationFacade(provisioningService),
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        captured.Should().NotBeNull();
        captured!.NyxChannelBotId.Should().Be("bot-lark");
        captured.ToString().Should().NotContain("secret-alpha").And.NotContain("encrypt-alpha");
        response.Body.Should().NotContain("encrypt-alpha");
    }

    [Fact]
    public async Task HandleRegisterAsync_RejectsLarkProvisioningWithoutScope()
    {
        var provisioningService = Substitute.For<INyxChannelBotAdoptionService>();

        var http = CreateJsonHttpContext(
            """{"platform":"lark","nyx_channel_bot_id":"bot-lark","app_id":"cli_123","app_secret":"secret","webhook_base_url":"https://aevatar.example.com"}""");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync("HandleRegisterAsync", http, CreateRegistrationFacade(provisioningService), NullLoggerFactory.Instance, CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        response.Body.Should().Contain("scope_id is required");
        await provisioningService.DidNotReceive().AdoptAsync(Arg.Any<NyxChannelBotAdoptionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleRegisterAsync_RequestScopeCannotReplaceMissingAuthenticatedScope()
    {
        var provisioningService = Substitute.For<INyxChannelBotAdoptionService>();
        provisioningService.AdoptAsync(
                Arg.Any<NyxChannelBotAdoptionRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotAdoptionResult(
                Succeeded: true,
                Status: "accepted",
                Platform: "lark",
                RegistrationId: "reg-untrusted-scope")));
        var http = CreateJsonHttpContext(
            """{"platform":"lark","nyx_channel_bot_id":"bot-lark","scope_id":"owner-alpha","app_id":"cli_123","app_secret":"secret","verification_token":"verify-123","webhook_base_url":"https://aevatar.example.com"}""");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleRegisterAsync",
            http,
            CreateRegistrationFacade(provisioningService),
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        response.Body.Should().Contain("scope_id is required");
        await provisioningService.DidNotReceive().AdoptAsync(
            Arg.Any<NyxChannelBotAdoptionRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleRegisterAsync_RequestScopeMustMatchAuthenticatedOwnerScope()
    {
        var provisioningService = Substitute.For<INyxChannelBotAdoptionService>();
        var http = CreateJsonHttpContext(
            """{"platform":"lark","nyx_channel_bot_id":"bot-lark","scope_id":"owner-beta","app_id":"cli_123","app_secret":"secret","verification_token":"verify-123","webhook_base_url":"https://aevatar.example.com"}""",
            "owner-alpha");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleRegisterAsync",
            http,
            CreateRegistrationFacade(provisioningService),
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        response.Body.Should().Contain("scope_id does not match the authenticated scope");
        await provisioningService.DidNotReceive().AdoptAsync(
            Arg.Any<NyxChannelBotAdoptionRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleRegisterAsync_ReturnsBadGateway_WhenNyxProvisioningFails()
    {
        var provisioningService = Substitute.For<INyxChannelBotAdoptionService>();
        provisioningService.AdoptAsync(Arg.Any<NyxChannelBotAdoptionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotAdoptionResult(
                Succeeded: false,
                Status: "error",
                Platform: "lark",
                Error: "channel_bot_not_found_or_forbidden provider-body-secret")));

        var http = CreateJsonHttpContext(
            """{"platform":"lark","nyx_channel_bot_id":"bot-lark","app_id":"cli_123","app_secret":"bad-secret","webhook_base_url":"https://aevatar.example.com"}""",
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync("HandleRegisterAsync", http, CreateRegistrationFacade(provisioningService), NullLoggerFactory.Instance, CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        response.Body.Should().Contain("\"status\":\"error\"");
        response.Body.Should().Contain("\"error\":\"channel_bot_not_found_or_forbidden\"");
        response.Body.Should().NotContain("error_detail");
        response.Body.Should().NotContain("invalid app secret");
        response.Body.Should().NotContain("bad-secret");
    }

    [Fact]
    public async Task HandleRegisterAsync_DoesNotTrustUnsafeAdapterErrorDetail()
    {
        var provisioningService = Substitute.For<INyxChannelBotAdoptionService>();
        provisioningService.AdoptAsync(Arg.Any<NyxChannelBotAdoptionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotAdoptionResult(
                Succeeded: false,
                Status: "error",
                Platform: "telegram",
                Error: "channel_bot_not_found_or_forbidden provider-body-secret",
                ErrorDetail: "provider echoed bad-token-secret")));

        var http = CreateJsonHttpContext(
            """{"platform":"telegram","nyx_channel_bot_id":"bot-telegram","bot_token":"bad-token-secret","webhook_base_url":"https://aevatar.example.com"}""",
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync("HandleRegisterAsync", http, CreateRegistrationFacade(provisioningService), NullLoggerFactory.Instance, CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        response.Body.Should().Contain("\"error\":\"channel_bot_not_found_or_forbidden\"");
        response.Body.Should().NotContain("error_detail");
        response.Body.Should().NotContain("provider echoed");
        response.Body.Should().NotContain("bad-token-secret");
    }

    [Theory]
    [InlineData("secret_vault_unavailable", StatusCodes.Status503ServiceUnavailable)]
    [InlineData("service_owner_forbidden", StatusCodes.Status403Forbidden)]
    [InlineData("nyxid_user_service_not_accessible", StatusCodes.Status404NotFound)]
    [InlineData("scope_plan_changed", StatusCodes.Status409Conflict)]
    [InlineData("nyxid_scope_plan_unavailable", StatusCodes.Status502BadGateway)]
    [InlineData("channel_authorization_contract_invalid", StatusCodes.Status409Conflict)]
    public async Task HandleRegisterAsync_MapsUnifiedAuthorizationFailures(
        string failureCode,
        int expectedStatusCode)
    {
        var provisioningService = Substitute.For<INyxChannelBotAdoptionService>();
        provisioningService.AdoptAsync(
                Arg.Any<NyxChannelBotAdoptionRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotAdoptionResult(
                Succeeded: false,
                Status: "error",
                Platform: "lark",
                Error: failureCode)));
        var http = CreateJsonHttpContext(
            """{"platform":"lark","nyx_channel_bot_id":"bot-lark","app_id":"cli_123","app_secret":"secret","verification_token":"verify-123","webhook_base_url":"https://aevatar.example.com"}""",
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleRegisterAsync",
            http,
            CreateRegistrationFacade(provisioningService),
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(expectedStatusCode);
        response.Body.Should().Contain(failureCode);
    }

    [Fact]
    public async Task HandleRegisterAsync_ReturnsBadRequest_WhenBotIdInvalid()
    {
        var provisioningService = Substitute.For<INyxChannelBotAdoptionService>();
        provisioningService.AdoptAsync(Arg.Any<NyxChannelBotAdoptionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotAdoptionResult(
                Succeeded: false,
                Status: "error",
                Platform: "telegram",
                Error: "missing_nyx_channel_bot_id")));

        var http = CreateJsonHttpContext(
            """{"platform":"telegram","nyx_channel_bot_id":"bot-telegram","webhook_base_url":"https://aevatar.example.com"}""",
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync("HandleRegisterAsync", http, CreateRegistrationFacade(provisioningService), NullLoggerFactory.Instance, CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        response.Body.Should().Contain("missing_nyx_channel_bot_id");
    }

    [Fact]
    public async Task HandleRegisterAsync_ParsesRuntimeConfigBeforeAdoption()
    {
        ChannelRelayRegistrationRequest? capturedRequest = null;
        var provisioningService = Substitute.For<INyxChannelBotAdoptionService>();
        provisioningService.AdoptAsync(
                Arg.Do<NyxChannelBotAdoptionRequest>(request => capturedRequest = request.Registration),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotAdoptionResult(
                Succeeded: false,
                Status: "error",
                Platform: "telegram",
                Error: "missing_nyx_channel_bot_id")));

        var http = CreateJsonHttpContext(
            """
            {
              "platform": "telegram","nyx_channel_bot_id":"bot-telegram",
              "webhook_base_url": "https://aevatar.example.com",
              "runtime_config": {
                "instructions": "Book dinner only after explicit confirmation.",
                "default_skill": {
                  "name": "booking-capacity",
                  "version": "1.0.0"
                },
                "tool_set_refs": ["channel.reply.default"],
                "nyxid_service_selectors": [
                  {
                    "service_slug": "api-google-workspace",
                    "endpoint_names": ["calendar_create_event"]
                  }
                ],
                "credential_source_mode": "registration_agent_key",
                "agent_key_service_requirements": {
                  "allowed_service_slugs": ["api-google-workspace"]
                }
              }
            }
            """,
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleRegisterAsync",
            http,
            CreateRegistrationFacade(provisioningService),
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        response.Body.Should().Contain("missing_nyx_channel_bot_id");
        capturedRequest.Should().NotBeNull();
        capturedRequest!.RuntimeConfig.Should().NotBeNull();
        capturedRequest.RuntimeConfig!.Instructions.Should().Be("Book dinner only after explicit confirmation.");
        capturedRequest.RuntimeConfig.DefaultSkill.Name.Should().Be("booking-capacity");
        capturedRequest.RuntimeConfig.ToolSetRefs.Should().BeEquivalentTo("channel.reply.default");
        capturedRequest.RuntimeConfig.NyxidServiceSelectors.Single().ServiceSlug.Should().Be("api-google-workspace");
        capturedRequest.RuntimeConfig.NyxidServiceSelectors.Single().EndpointNames.Should().BeEquivalentTo("calendar_create_event");
        capturedRequest.RuntimeConfig.CredentialSourceMode.Should().Be(ChannelBotRuntimeCredentialSourceMode.RegistrationAgentKey);
    }

    [Fact]
    public async Task HandleRegisterAsync_ReturnsBadRequest_WhenWebhookBaseUrlInsecure()
    {
        // A non-HTTPS webhook_base_url is a client input error, so the local validation failure must
        // map to 400 (like its missing_* siblings), not fall through to the 502 catch-all.
        var provisioningService = Substitute.For<INyxChannelBotAdoptionService>();
        provisioningService.AdoptAsync(Arg.Any<NyxChannelBotAdoptionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotAdoptionResult(
                Succeeded: false,
                Status: "error",
                Platform: "lark",
                Error: "insecure_webhook_base_url")));

        var http = CreateJsonHttpContext(
            """{"platform":"lark","nyx_channel_bot_id":"bot-lark","app_id":"cli_123","app_secret":"secret","webhook_base_url":"https://aevatar.example.com"}""",
            "scope-1");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync("HandleRegisterAsync", http, CreateRegistrationFacade(provisioningService), NullLoggerFactory.Instance, CancellationToken.None);
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
        resolver.ResolveAsync("test-token", scopeId, Arg.Any<CancellationToken>())
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
        var authorizationPort = Substitute.For<IChannelRegistrationNyxIdAuthorizationPort>();
        authorizationPort.ReadUserServicesAsync("test-token", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxIdApiAccessResult<NyxIdUserServices>(
                new NyxIdUserServices(services.Select(static service => new NyxIdUserService(
                    service.ServiceId,
                    service.Slug,
                    Label: service.Slug,
                    CatalogServiceName: service.Slug,
                    IsActive: true,
                    CredentialSource: new NyxIdUserServiceCredentialSource(
                        NyxIdUserServiceCredentialSourceKind.Organization,
                        OrganizationId: "scope-1",
                        OrganizationRole: NyxIdOrganizationRole.Admin,
                        Allowed: true))).ToArray()),
                null)));
        return new ChannelRegistrationAuthorizationPlanner(authorizationPort);
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

        var result = await InvokeAsync("HandleListRegistrationsAsync", http, queryPort, AdminAuthorizer(false), (string?)null, CancellationToken.None);
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

        var result = await InvokeAsync(
            "HandleListRegistrationsAsync",
            http,
            queryPort,
            AdminAuthorizer(false),
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
            "test-token",
            Arg.Is<NyxChannelBotDeprovisioningRequest>(request =>
                request.RegistrationId == "reg-1" &&
                request.ConversationRouteId == "route-1" &&
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
        response.Body.Should().Contain("nyx_agent_key_delete_failed");
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
        response.Body.Should().Contain("nyx_agent_key_delete_failed");
        await ((IActorDispatchPort)actorRuntime).DidNotReceiveWithAnyArgs()
            .DispatchAsync(default!, default!, default);
    }

    [Fact]
    public async Task HandleDeleteRegistrationAsync_RouteCleanupFailure_ReturnsIncompleteAndKeepsRegistrationRetryable()
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
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        var deprovision = Substitute.For<INyxChannelBotDeprovisioningService>();
        deprovision.DeprovisionAsync(
                Arg.Any<string>(), Arg.Any<NyxChannelBotDeprovisioningRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotDeprovisioningResult(
                AgentKeyRemoved: true,
                Succeeded: false,
                Warnings: new[] { "conversation_route_delete_failed id=route-1" },
                ConversationRouteRemoved: false)));
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
        response.Body.Should().Contain("nyx_conversation_route_delete_failed");
        response.Body.Should().Contain("route-1");
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
                true, true, new[] { "vault_revoke_failed" })));

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
            "test-token",
            Arg.Is<NyxChannelBotDeprovisioningRequest>(request =>
                request.RegistrationId == "reg-tg" &&
                request.ConversationRouteId == "route-tg" &&
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
        ChannelRelayRegistrationFacade registrationFacade,
        IChannelWorkflowResultDeliveryRepairService? repairService = null)
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
        params INyxChannelBotAdoptionService[] provisioningServices) =>
        ChannelAdoptionFacadeTestSupport.Create(provisioningServices.SingleOrDefault());

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

    private sealed class AcceptedProvisioningService : INyxChannelBotAdoptionService
    {

        public Task<NyxChannelBotAdoptionResult> AdoptAsync(
            NyxChannelBotAdoptionRequest request,
            CancellationToken ct)
        {
            request.Registration.AccessToken.Should().Be(RawToken);
            request.Registration.ScopeId.Should().Be("scope-1");
            return Task.FromResult(new NyxChannelBotAdoptionResult(
                Succeeded: true,
                Status: "accepted",
                Platform: request.Registration.Platform,
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
