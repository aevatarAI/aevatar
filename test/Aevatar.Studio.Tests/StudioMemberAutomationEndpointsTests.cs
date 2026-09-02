using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Hosting.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Studio.Tests;

public sealed class StudioMemberAutomationEndpointsTests
{
    private const string ScopeId = "scope-alpha";
    private const string TeamId = "team-alpha";
    private const string MemberId = "m-alpha";
    private const string ScheduleId = "sch-alpha";

    [Fact]
    public void Map_ShouldExposeOnlyCanonicalScopeTeamMemberAutomationRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IStudioMemberWorkflowSchedulePort, StubSchedules>();
        builder.Services.AddSingleton<IExternalIdentityBindingQueryPort, StubBindingQuery>();
        builder.Services.AddRouting();
        var app = builder.Build();

        StudioMemberAutomationEndpoints.Map(app);

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(static endpoint => endpoint.RoutePattern.RawText)
            .ToArray();

        const string canonicalBase =
            "/api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations";
        routes.Should().ContainSingle(route => route == $"{canonicalBase}/preflight");
        routes.Count(route => route == canonicalBase).Should().Be(2);
        routes.Count(route => route == $"{canonicalBase}/{{scheduleId}}").Should().Be(3);
        routes.Should().ContainSingle(route => route == $"{canonicalBase}/{{scheduleId}}/reauthorize");
        routes.Should().ContainSingle(route => route == $"{canonicalBase}/{{scheduleId}}/retry-revocation");
        routes.Should().ContainSingle(route => route == $"{canonicalBase}/{{scheduleId}}/pause");
        routes.Should().ContainSingle(route => route == $"{canonicalBase}/{{scheduleId}}/resume");
        routes.Should().ContainSingle(route => route == $"{canonicalBase}/{{scheduleId}}/run-now");
        routes.Should().NotContain(route => route != null && route.StartsWith("/api/teams/", StringComparison.Ordinal));
    }

    [Fact]
    public void RetryRevocation_ShouldNotAcceptBrowserOwnedOperationIdentity()
    {
        var method = typeof(StudioMemberAutomationEndpoints)
            .GetMethod("HandleRetryRevocationAsync", System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.NonPublic);

        method.Should().NotBeNull();
        method!.GetParameters()
            .Should().NotContain(parameter =>
                parameter.ParameterType == typeof(StudioMemberAutomationActionRequest));
    }

    [Fact]
    public async Task Preflight_WhenNyxIdBindingIsMissing_ShouldReturnRecoverableTypedConflict()
    {
        var result = await StudioMemberAutomationEndpoints.HandlePreflightAsync(
            CreateContext(ScopeId),
            ScopeId,
            TeamId,
            MemberId,
            new StudioMemberAutomationPreflightRequest(
                "0 9 * * *",
                "UTC",
                "run daily digest",
                "Daily digest",
                true),
            new StubSchedules(),
            new StubBindingQuery { Binding = null },
            NullLoggerFactory.Instance,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status409Conflict);
        var value = Value(result);
        StringProperty(value, "code")
            .Should().Be("TEAM_AUTOMATION_AUTHORIZATION_BINDING_REQUIRED");
        StringProperty(value, "message")
            .Should().Be("Reconnect NyxID to authorize this automation.");
    }

    [Fact]
    public async Task List_ShouldKeepScopeMismatchAsForbidden()
    {
        var schedules = new StubSchedules();

        var result = await StudioMemberAutomationEndpoints.HandleListAsync(
            CreateContext("scope-other"),
            ScopeId,
            TeamId,
            MemberId,
            schedules,
            take: null,
            cursor: null,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status403Forbidden);
        schedules.ListCalls.Should().Be(0);
    }

    [Fact]
    public async Task List_ShouldHideCrossTeamMemberOwnershipAsNotFound()
    {
        var schedules = new StubSchedules
        {
            Exception = new StudioMemberAutomationNotFoundException(),
        };

        var result = await StudioMemberAutomationEndpoints.HandleListAsync(
            CreateContext(ScopeId),
            ScopeId,
            "team-other",
            MemberId,
            schedules,
            take: null,
            cursor: null,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status404NotFound);
        var value = Value(result);
        StringProperty(value, "code").Should().Be("TEAM_AUTOMATION_NOT_FOUND");
        StringProperty(value, "message").Should().Be("Team automation resource was not found.");
        JsonSerializer.Serialize(value).Should().NotContain("team-other");
    }

    [Fact]
    public async Task Preflight_ShouldPassFreshBearerToApplicationBoundary()
    {
        var schedules = new StubSchedules();

        var result = await StudioMemberAutomationEndpoints.HandlePreflightAsync(
            CreateContext(ScopeId),
            ScopeId,
            TeamId,
            MemberId,
            new StudioMemberAutomationPreflightRequest(
                "0 9 * * *",
                "UTC",
                "run daily digest",
                "Daily digest",
                true),
            schedules,
            new StubBindingQuery(),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status200OK);
        schedules.WritePreflightCalls.Should().Be(1);
        schedules.LastPreflight.Should().NotBeNull();
        schedules.LastPreflight!.ScopeId.Should().Be(ScopeId);
        schedules.LastPreflight.TeamId.Should().Be(TeamId);
        schedules.LastPreflight.MemberId.Should().Be(MemberId);
        schedules.LastPreflight.ProvisioningBearerToken.Should().Be("fresh-owner-bearer");
        schedules.LastPreflight.AuthenticatedOwner.Owner.OwnerSubject.Should().Be("nyx-owner-alpha");
    }

    [Fact]
    public async Task Preflight_WhenPlannerDeniesService_ShouldReturnSanitizedTypedForbidden()
    {
        var schedules = new StubSchedules
        {
            PreflightResult = new StudioMemberWorkflowAuthorizationResult(
                false,
                null,
                ScheduledInvocationAuthorizationFailureCode.ServiceAccessDenied,
                "private-service-id"),
        };

        var result = await StudioMemberAutomationEndpoints.HandlePreflightAsync(
            CreateContext(ScopeId),
            ScopeId,
            TeamId,
            MemberId,
            new StudioMemberAutomationPreflightRequest(
                "0 9 * * *",
                "UTC",
                "run daily digest",
                "Daily digest",
                true),
            schedules,
            new StubBindingQuery(),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status403Forbidden);
        var value = Value(result);
        StringProperty(value, "code").Should().Be(
            "TEAM_AUTOMATION_AUTHORIZATION_SERVICE_ACCESS_DENIED");
        StringProperty(value, "message").Should().Be(
            "This automation is not authorized to use one or more required services.");
        value.GetType().GetProperty("retryable")?.GetValue(value).Should().Be(false);
        JsonSerializer.Serialize(value).Should().NotContain("private-service-id");
        AssertNoCredentialMaterial(value);
    }

    [Fact]
    public async Task Preflight_WhenPlannerAuthorizationIsUnavailable_ShouldReturnRetryableTypedUnavailable()
    {
        var schedules = new StubSchedules
        {
            PreflightResult = new StudioMemberWorkflowAuthorizationResult(
                false,
                null,
                ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable,
                "private-catalog-detail"),
        };

        var result = await StudioMemberAutomationEndpoints.HandlePreflightAsync(
            CreateContext(ScopeId),
            ScopeId,
            TeamId,
            MemberId,
            new StudioMemberAutomationPreflightRequest(
                "0 9 * * *",
                "UTC",
                "run daily digest",
                "Daily digest",
                true),
            schedules,
            new StubBindingQuery(),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status503ServiceUnavailable);
        var value = Value(result);
        StringProperty(value, "code").Should().Be(
            "TEAM_AUTOMATION_AUTHORIZATION_DURABLE_AUTHORIZATION_UNAVAILABLE");
        StringProperty(value, "message").Should().Be(
            "Authorization is temporarily unavailable. Retry this request.");
        value.GetType().GetProperty("retryable")?.GetValue(value).Should().Be(true);
        JsonSerializer.Serialize(value).Should().NotContain("private-catalog-detail");
        AssertNoCredentialMaterial(value);
    }

    [Fact]
    public async Task Preflight_WhenNyxIdBindingIsMissing_ShouldReturnTypedConflictWithoutSecrets()
    {
        var schedules = new StubSchedules();
        var result = await StudioMemberAutomationEndpoints.HandlePreflightAsync(
            CreateContext(ScopeId),
            ScopeId,
            TeamId,
            MemberId,
            new StudioMemberAutomationPreflightRequest(
                "0 9 * * *",
                "UTC",
                "run daily digest",
                "Daily digest",
                true),
            schedules,
            new StubBindingQuery { Binding = null },
            NullLoggerFactory.Instance,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status409Conflict);
        StringProperty(Value(result), "code").Should().Be(
            "TEAM_AUTOMATION_AUTHORIZATION_BINDING_REQUIRED");
        schedules.LastPreflight.Should().BeNull();
        var json = JsonSerializer.Serialize(Value(result));
        json.Should().NotContain("binding-alpha");
        json.Should().NotContain("nyx-owner-alpha");
        json.Should().NotContain("fresh-owner-bearer");
    }

    [Fact]
    public async Task Preflight_WhenBearerIsMalformed_ShouldReturnUnauthorizedWithoutEchoingHeader()
    {
        var schedules = new StubSchedules();
        var context = CreateContext(ScopeId);
        context.Request.Headers.Authorization =
            "Bearer secret-one, secret-two";

        var result = await StudioMemberAutomationEndpoints.HandlePreflightAsync(
            context,
            ScopeId,
            TeamId,
            MemberId,
            new StudioMemberAutomationPreflightRequest(
                "0 9 * * *",
                "UTC",
                "run daily digest",
                "Daily digest",
                true),
            schedules,
            new StubBindingQuery(),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status401Unauthorized);
        StringProperty(Value(result), "code").Should().Be(
            "TEAM_AUTOMATION_UNAUTHORIZED");
        schedules.LastPreflight.Should().BeNull();
        var json = JsonSerializer.Serialize(Value(result));
        json.Should().NotContain("secret-one");
        json.Should().NotContain("secret-two");
    }

    [Fact]
    public async Task Create_ShouldKeepCanonicalOwnerIdentityAndReturnPendingReceipt()
    {
        var schedules = new StubSchedules();

        var result = await StudioMemberAutomationEndpoints.HandleCreateAsync(
            CreateContext(ScopeId),
            ScopeId,
            TeamId,
            MemberId,
            new StudioMemberAutomationMutationRequest(
                "0 9 * * *",
                "UTC",
                "run daily digest",
                "Daily digest",
                true,
                "permission-digest-alpha",
                "scheduled-invocation-auth/v1",
                "dedicated_scheduled_invocation_agent_key",
                "op-create",
                "idem-create"),
            schedules,
            new StubBindingQuery(),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status202Accepted);
        var receipt = Value(result).Should().BeOfType<StudioMemberAutomationMutationReceipt>().Subject;
        receipt.Accepted.Should().BeTrue();
        receipt.Status.Should().Be("pending");
        receipt.Status.Should().NotBe("active");
        receipt.ScheduleId.Should().Be(ScheduleId);
        receipt.OperationId.Should().Be("op-create");
        receipt.CommandId.Should().Be("cmd-alpha");
        AssertNoCredentialMaterial(receipt);

        schedules.ScheduleMutationCalls.Should().Be(1);
        schedules.LastCreate.Should().NotBeNull();
        schedules.LastCreate!.ScopeId.Should().Be(ScopeId);
        schedules.LastCreate.TeamId.Should().Be(TeamId);
        schedules.LastCreate.MemberId.Should().Be(MemberId);
        schedules.LastCreate.OperationId.Should().Be("op-create");
        schedules.LastCreate.IdempotencyKey.Should().Be("idem-create");
        schedules.LastCreate.ProvisioningBearerToken.Should().Be("fresh-owner-bearer");
        schedules.LastCreate.AuthenticatedOwner.Owner.OwnerSubject.Should().Be("nyx-owner-alpha");
        schedules.LastConfirmedPermissionDigest.Should().Be("permission-digest-alpha");
    }

    [Fact]
    public async Task ReadResponses_ShouldExposeCanonicalIdentityWithoutCredentialMaterial()
    {
        var view = new StudioMemberAutomationView(
            ScopeId,
            TeamId,
            MemberId,
            ScheduleId,
            "svc-alpha",
            "Daily digest",
            "run daily digest",
            "0 9 * * *",
            "UTC",
            true,
            "active",
            DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
            string.Empty,
            "op-alpha",
            3,
            false,
            DateTimeOffset.Parse("2026-07-17T09:00:00Z"),
            DateTimeOffset.Parse("2026-07-16T09:00:00Z"),
            17);
        SetRequiredStringProperty(view, "NyxIdRevocationStatus", "nyx-track-terminal");
        SetRequiredStringProperty(view, "VaultRevocationStatus", "vault-track-terminal");
        var schedules = new StubSchedules { View = view };

        var getResult = await StudioMemberAutomationEndpoints.HandleGetAsync(
            CreateContext(ScopeId),
            ScopeId,
            TeamId,
            MemberId,
            ScheduleId,
            schedules,
            CancellationToken.None);
        var listResult = await StudioMemberAutomationEndpoints.HandleListAsync(
            CreateContext(ScopeId),
            ScopeId,
            TeamId,
            MemberId,
            schedules,
            take: 20,
            cursor: "cursor-alpha",
            CancellationToken.None);

        StatusCode(getResult).Should().Be(StatusCodes.Status200OK);
        var getResponse = Value(getResult).Should().BeOfType<StudioMemberAutomationView>().Subject;
        getResponse.ScopeId.Should().Be(ScopeId);
        getResponse.TeamId.Should().Be(TeamId);
        getResponse.MemberId.Should().Be(MemberId);
        getResponse.ScheduleId.Should().Be(ScheduleId);
        using (var json = JsonDocument.Parse(JsonSerializer.Serialize(
                   getResponse,
                   new JsonSerializerOptions(JsonSerializerDefaults.Web))))
        {
            json.RootElement.GetProperty("nyxIdRevocationStatus").GetString()
                .Should().Be("nyx-track-terminal");
            json.RootElement.GetProperty("vaultRevocationStatus").GetString()
                .Should().Be("vault-track-terminal");
        }
        AssertNoCredentialMaterial(getResponse);

        StatusCode(listResult).Should().Be(StatusCodes.Status200OK);
        var listResponse = Value(listResult).Should().BeOfType<StudioMemberAutomationListResponse>().Subject;
        listResponse.Items.Should().ContainSingle().Which.Should().BeSameAs(view);
        AssertNoCredentialMaterial(listResponse);
        schedules.LastGet.Should().Be((ScopeId, TeamId, MemberId, ScheduleId));
        schedules.LastList.Should().Be((ScopeId, TeamId, MemberId, 20, "cursor-alpha"));
    }

    [Theory]
    [InlineData(
        "authorization_plan_changed",
        "TEAM_AUTOMATION_AUTHORIZATION_PLAN_CHANGED",
        ScheduledAuthorizationPlanMismatchReason.AllowedNodeIdsMismatch,
        "allowed_node_ids_mismatch")]
    [InlineData(
        "reauthorization_required",
        "TEAM_AUTOMATION_REAUTHORIZATION_REQUIRED",
        ScheduledAuthorizationPlanMismatchReason.Unspecified,
        null)]
    public async Task Update_ShouldReturnTypedConflictWithCanonicalPreflightLocator(
        string conflictCode,
        string expectedWireCode,
        ScheduledAuthorizationPlanMismatchReason mismatchReason,
        string? expectedMismatchReason)
    {
        var schedules = new StubSchedules
        {
            Exception = new StudioMemberAutomationPlanConflictException(
                conflictCode,
                "sensitive backend detail must not cross the boundary",
                mismatchReason),
        };

        var result = await StudioMemberAutomationEndpoints.HandleUpdateAsync(
            CreateContext(ScopeId),
            ScopeId,
            TeamId,
            MemberId,
            ScheduleId,
            new StudioMemberAutomationUpdateRequest(
                "0 9 * * *",
                "UTC",
                "prompt",
                "name",
                true,
                "op-alpha",
                "idem-alpha"),
            schedules,
            new StubBindingQuery(),
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status409Conflict);
        var value = Value(result);
        StringProperty(value, "code").Should().Be(expectedWireCode);
        StringProperty(value, "preflightLocator").Should().Be(
            $"/api/scopes/{ScopeId}/teams/{TeamId}/members/{MemberId}/automations/preflight");
        StringProperty(value, "authorizationPlanMismatchReason").Should().Be(expectedMismatchReason);
        var serialized = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        serialized.Should().NotContain("sensitive backend detail");
        if (expectedMismatchReason is null)
            serialized.Should().NotContain("authorizationPlanMismatchReason");
        else
            serialized.Should().Contain(expectedMismatchReason);
        AssertNoCredentialMaterial(value);
        schedules.LastUpdate.Should().NotBeNull();
        schedules.LastUpdate!.ScopeId.Should().Be(ScopeId);
        schedules.LastUpdate.TeamId.Should().Be(TeamId);
        schedules.LastUpdate.MemberId.Should().Be(MemberId);
        schedules.LastUpdate.ScheduleId.Should().Be(ScheduleId);
        schedules.ScheduleMutationCalls.Should().Be(0);
    }

    [Fact]
    public async Task Update_ShouldReturnRetryableProjectionPendingWithRequiredStateVersion()
    {
        var schedules = new StubSchedules
        {
            Exception = new StudioMemberAutomationProjectionPendingException(23),
        };

        var result = await StudioMemberAutomationEndpoints.HandleUpdateAsync(
            CreateContext(ScopeId),
            ScopeId,
            TeamId,
            MemberId,
            ScheduleId,
            new StudioMemberAutomationUpdateRequest(
                "0 9 * * *",
                "UTC",
                "prompt",
                "name",
                true,
                "op-alpha",
                "idem-alpha"),
            schedules,
            new StubBindingQuery(),
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status503ServiceUnavailable);
        var value = Value(result);
        StringProperty(value, "code").Should().Be("TEAM_AUTOMATION_AUTHORIZATION_PROJECTION_PENDING");
        value.GetType().GetProperty("retryable")?.GetValue(value).Should().Be(true);
        value.GetType().GetProperty("requiredStateVersion")?.GetValue(value).Should().Be(23L);
        AssertNoCredentialMaterial(value);
        schedules.ScheduleMutationCalls.Should().Be(0);
    }

    [Fact]
    public async Task Update_ShouldReturnRetryableCatalogRefreshSuperseded()
    {
        var schedules = new StubSchedules
        {
            Exception = new StudioMemberAutomationCatalogRefreshSupersededException(),
        };

        var result = await StudioMemberAutomationEndpoints.HandleUpdateAsync(
            CreateContext(ScopeId),
            ScopeId,
            TeamId,
            MemberId,
            ScheduleId,
            new StudioMemberAutomationUpdateRequest(
                "0 9 * * *",
                "UTC",
                "prompt",
                "name",
                true,
                "op-alpha",
                "idem-alpha"),
            schedules,
            new StubBindingQuery(),
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status503ServiceUnavailable);
        var value = Value(result);
        StringProperty(value, "code").Should().Be("TEAM_AUTOMATION_AUTHORIZATION_REFRESH_SUPERSEDED");
        value.GetType().GetProperty("retryable")?.GetValue(value).Should().Be(true);
        AssertNoCredentialMaterial(value);
        schedules.ScheduleMutationCalls.Should().Be(0);
    }

    [Fact]
    public async Task Update_ShouldReturnSanitizedRetryableCatalogRefreshUnavailable()
    {
        var schedules = new StubSchedules
        {
            Exception = new StudioMemberAutomationCatalogRefreshUnavailableException(),
        };

        var result = await StudioMemberAutomationEndpoints.HandleUpdateAsync(
            CreateContext(ScopeId),
            ScopeId,
            TeamId,
            MemberId,
            ScheduleId,
            new StudioMemberAutomationUpdateRequest(
                "0 9 * * *",
                "UTC",
                "prompt",
                "name",
                true,
                "op-alpha",
                "idem-alpha"),
            schedules,
            new StubBindingQuery(),
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status503ServiceUnavailable);
        var value = Value(result);
        StringProperty(value, "code").Should().Be("TEAM_AUTOMATION_AUTHORIZATION_REFRESH_UNAVAILABLE");
        value.GetType().GetProperty("retryable")?.GetValue(value).Should().Be(true);
        JsonSerializer.Serialize(value).Should().NotContain("private-provider-detail");
        AssertNoCredentialMaterial(value);
        schedules.ScheduleMutationCalls.Should().Be(0);
    }

    [Fact]
    public async Task Update_ShouldPassFreshBearerOnlyToApplicationCommand()
    {
        var schedules = new StubSchedules();

        var result = await StudioMemberAutomationEndpoints.HandleUpdateAsync(
            CreateContext(ScopeId),
            ScopeId,
            TeamId,
            MemberId,
            ScheduleId,
            new StudioMemberAutomationUpdateRequest(
                "0 9 * * *",
                "UTC",
                "prompt",
                "name",
                true,
                "op-alpha",
                "idem-alpha"),
            schedules,
            new StubBindingQuery(),
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status202Accepted);
        schedules.LastUpdate.Should().NotBeNull();
        schedules.LastUpdate!.ProvisioningBearerToken.Should().Be("fresh-owner-bearer");
        AssertNoCredentialMaterial(Value(result));
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("run-now")]
    public async Task Action_ShouldReturnHonestAcceptedReceiptWithCanonicalIdentity(string action)
    {
        var schedules = new StubSchedules();
        var request = new StudioMemberAutomationActionRequest($"op-{action}", $"idem-{action}");

        var result = action switch
        {
            "pause" => await StudioMemberAutomationEndpoints.HandlePauseAsync(
                CreateContext(ScopeId), ScopeId, TeamId, MemberId, ScheduleId,
                request, schedules, CancellationToken.None),
            "resume" => await StudioMemberAutomationEndpoints.HandleResumeAsync(
                CreateContext(ScopeId), ScopeId, TeamId, MemberId, ScheduleId,
                request, schedules, CancellationToken.None),
            "run-now" => await StudioMemberAutomationEndpoints.HandleRunNowAsync(
                CreateContext(ScopeId), ScopeId, TeamId, MemberId, ScheduleId,
                request, schedules, CancellationToken.None),
            _ => throw new InvalidOperationException($"Unsupported action '{action}'."),
        };

        StatusCode(result).Should().Be(StatusCodes.Status202Accepted);
        var receipt = Value(result).Should().BeOfType<StudioMemberAutomationMutationReceipt>().Subject;
        receipt.Accepted.Should().BeTrue();
        receipt.Status.Should().Be("accepted");
        receipt.Status.Should().NotBe("active");
        receipt.ScheduleId.Should().Be(ScheduleId);
        receipt.OperationId.Should().Be($"op-{action}");
        receipt.CommandId.Should().Be("cmd-alpha");
        AssertNoCredentialMaterial(receipt);

        schedules.ScheduleMutationCalls.Should().Be(1);
        schedules.LastActionName.Should().Be(action);
        schedules.LastAction.Should().NotBeNull();
        schedules.LastAction!.ScopeId.Should().Be(ScopeId);
        schedules.LastAction.TeamId.Should().Be(TeamId);
        schedules.LastAction.MemberId.Should().Be(MemberId);
        schedules.LastAction.ScheduleId.Should().Be(ScheduleId);
        schedules.LastAction.OperationId.Should().Be($"op-{action}");
        schedules.LastAction.IdempotencyKey.Should().Be($"idem-{action}");
    }

    [Theory]
    [InlineData("team-other", MemberId)]
    [InlineData(TeamId, "m-other")]
    public async Task Resume_ShouldFailClosedForCrossTeamOrMemberWithoutScheduleMutation(
        string routeTeamId,
        string routeMemberId)
    {
        var schedules = new StubSchedules();

        var result = await StudioMemberAutomationEndpoints.HandleResumeAsync(
            CreateContext(ScopeId),
            ScopeId,
            routeTeamId,
            routeMemberId,
            ScheduleId,
            new StudioMemberAutomationActionRequest("op-resume", "idem-resume"),
            schedules,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status404NotFound);
        var value = Value(result);
        StringProperty(value, "code").Should().Be("TEAM_AUTOMATION_NOT_FOUND");
        StringProperty(value, "message").Should().Be("Team automation resource was not found.");
        var serialized = JsonSerializer.Serialize(value);
        serialized.Should().NotContain(routeTeamId == TeamId ? routeMemberId : routeTeamId);
        schedules.ScheduleMutationCalls.Should().Be(0);
    }

    [Fact]
    public async Task Delete_ShouldPassFreshBearerAndAuthenticatedOwnerOnlyToApplicationBoundary()
    {
        var schedules = new StubSchedules();

        var result = await StudioMemberAutomationEndpoints.HandleDeleteAsync(
            CreateContext(ScopeId),
            ScopeId,
            TeamId,
            MemberId,
            ScheduleId,
            new StudioMemberAutomationActionRequest("op-delete", "idem-delete"),
            schedules,
            new StubBindingQuery(),
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status202Accepted);
        var response = Value(result);
        StringProperty(response, "Status").Should().Be("pending");
        StringProperty(response, "Status").Should().NotBe("active");
        AssertNoCredentialMaterial(response);
        schedules.LastDelete.Should().NotBeNull();
        schedules.LastDelete!.AuthenticatedOwner!.Owner.OwnerSubject.Should().Be("nyx-owner-alpha");
        schedules.LastDelete.ProvisioningBearerToken.Should().Be("fresh-owner-bearer");
        schedules.LastDelete.ScopeId.Should().Be(ScopeId);
        schedules.LastDelete.TeamId.Should().Be(TeamId);
        schedules.LastDelete.MemberId.Should().Be(MemberId);
        schedules.LastDelete.ScheduleId.Should().Be(ScheduleId);
    }

    [Fact]
    public async Task RetryRevocation_ShouldPassOnlyCanonicalIdentityAndFreshOwnerCredentialToApplicationBoundary()
    {
        var schedules = new StubSchedules();

        var result = await StudioMemberAutomationEndpoints.HandleRetryRevocationAsync(
            CreateContext(ScopeId),
            ScopeId,
            TeamId,
            MemberId,
            ScheduleId,
            schedules,
            new StubBindingQuery(),
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status202Accepted);
        var response = Value(result).Should().BeOfType<StudioMemberAutomationMutationReceipt>().Subject;
        response.Status.Should().Be("pending");
        response.OperationId.Should().Be("op-delete-committed");
        AssertNoCredentialMaterial(response);
        schedules.LastRetryRevocation.Should().NotBeNull();
        schedules.LastRetryRevocation!.AuthenticatedOwner!.Owner.OwnerSubject.Should().Be("nyx-owner-alpha");
        schedules.LastRetryRevocation.ProvisioningBearerToken.Should().Be("fresh-owner-bearer");
        schedules.LastRetryRevocation.ScopeId.Should().Be(ScopeId);
        schedules.LastRetryRevocation.TeamId.Should().Be(TeamId);
        schedules.LastRetryRevocation.MemberId.Should().Be(MemberId);
        schedules.LastRetryRevocation.ScheduleId.Should().Be(ScheduleId);
    }

    [Theory]
    [InlineData("publishedServiceId", "svc-forged")]
    [InlineData("workflowId", "wf-forged")]
    [InlineData("serviceGrants", "[]")]
    [InlineData("secretReference", "secret-forged")]
    [InlineData("apiKeyId", "ak-forged")]
    [InlineData("owner", "owner-forged")]
    [InlineData("credentialExpiresAtUtc", "2026-10-01T00:00:00Z")]
    public void MutationRequest_ShouldRejectForbiddenUnmappedFields(
        string propertyName,
        string propertyValue)
    {
        var encodedValue = propertyValue == "[]"
            ? propertyValue
            : JsonSerializer.Serialize(propertyValue);
        var json = $$"""
            {
              "scheduleCron": "0 9 * * *",
              "scheduleTimezone": "UTC",
              "prompt": "run",
              "displayName": "daily",
              "enabled": true,
              "confirmedPermissionDigest": "digest-alpha",
              "confirmedPolicyVersion": "policy-alpha",
              "credentialProvisioningKind": "dedicated_scheduled_invocation_agent_key",
              "operationId": "op-alpha",
              "idempotencyKey": "idem-alpha",
              "{{propertyName}}": {{encodedValue}}
            }
            """;

        var action = () => JsonSerializer.Deserialize<StudioMemberAutomationMutationRequest>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        action.Should().Throw<JsonException>();
    }

    [Fact]
    public void EveryMutationRequest_ShouldDisallowUnmappedJsonMembers()
    {
        var requestTypes = new[]
        {
            typeof(StudioMemberAutomationPreflightRequest),
            typeof(StudioMemberAutomationMutationRequest),
            typeof(StudioMemberAutomationUpdateRequest),
            typeof(StudioMemberAutomationActionRequest),
        };

        foreach (var requestType in requestTypes)
        {
            requestType.GetCustomAttributes(typeof(JsonUnmappedMemberHandlingAttribute), inherit: true)
                .Should().ContainSingle()
                .Which.Should().BeOfType<JsonUnmappedMemberHandlingAttribute>()
                .Which.UnmappedMemberHandling.Should().Be(JsonUnmappedMemberHandling.Disallow);
        }
    }

    private static HttpContext CreateContext(string claimedScopeId)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim("scope_id", claimedScopeId),
            new Claim(ClaimTypes.NameIdentifier, "nyx-owner-alpha"),
        ], "test");
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Aevatar:Authentication:Enabled"] = "true",
                })
                .Build())
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment())
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
            RequestServices = services,
        };
        context.Request.Headers.Authorization = "Bearer fresh-owner-bearer";
        return context;
    }

    private static int? StatusCode(IResult result) =>
        (result as IStatusCodeHttpResult)?.StatusCode;

    private static object Value(IResult result) =>
        result.GetType().GetProperty("Value")?.GetValue(result)
        ?? throw new InvalidOperationException($"{result.GetType().Name} has no response value.");

    private static string? StringProperty(object value, string propertyName) =>
        value.GetType().GetProperty(propertyName)?.GetValue(value) as string;

    private static void SetRequiredStringProperty(object target, string propertyName, string value)
    {
        var property = target.GetType().GetProperty(propertyName);
        property.Should().NotBeNull($"{propertyName} is part of the public revocation evidence contract");
        property!.SetValue(target, value);
    }

    private static void AssertNoCredentialMaterial(object response)
    {
        var serialized = JsonSerializer.Serialize(
            response,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var normalized = serialized.ToLowerInvariant();
        normalized.Should().NotContain("bearer");
        normalized.Should().NotContain("raw-key");
        normalized.Should().NotContain("secretreference");
        normalized.Should().NotContain("apikeyid");
        normalized.Should().NotContain("credentialid");
        normalized.Should().NotContain("callerauthority");
        normalized.Should().NotContain("verifiedbindingid");
        normalized.Should().NotContain("vaultref");
        normalized.Should().NotContain("ciphertext");
        normalized.Should().NotContain("refreshtoken");
        normalized.Should().NotContain("fullkey");
        normalized.Should().NotContain("binding-alpha");
        normalized.Should().NotContain("nyx-owner-alpha");
    }

    private sealed class StubBindingQuery : IExternalIdentityBindingQueryPort
    {
        public BindingId? Binding { get; init; } = new BindingId { Value = "binding-alpha" };

        public Task<BindingId?> ResolveAsync(
            ExternalSubjectRef externalSubject,
            CancellationToken ct = default) =>
            Task.FromResult(Binding);
    }

    private sealed class StubSchedules : IStudioMemberWorkflowSchedulePort
    {
        public Exception? Exception { get; init; }
        public StudioMemberWorkflowAuthorizationResult PreflightResult { get; init; } =
            new(
                true,
                new ScheduledInvocationAuthorizationPlan(),
                ScheduledInvocationAuthorizationFailureCode.Unspecified,
                string.Empty);
        public int ListCalls { get; private set; }
        public int ScheduleMutationCalls { get; private set; }
        public int WritePreflightCalls { get; private set; }
        public StudioMemberAutomationView? View { get; init; }
        public StudioMemberWorkflowScheduleRequest? LastPreflight { get; private set; }
        public StudioMemberWorkflowScheduleRequest? LastCreate { get; private set; }
        public StudioMemberWorkflowScheduleResult? CreateResult { get; init; }
        public Queue<StudioMemberWorkflowScheduleResult> CreateResults { get; } = [];
        public string? LastConfirmedPermissionDigest { get; private set; }
        public StudioMemberAutomationUpdateCommand? LastUpdate { get; private set; }
        public StudioMemberAutomationActionCommand? LastAction { get; private set; }
        public string? LastActionName { get; private set; }
        public StudioMemberAutomationActionCommand? LastDelete { get; private set; }
        public StudioMemberAutomationRetryRevocationCommand? LastRetryRevocation { get; private set; }
        public (string ScopeId, string TeamId, string? MemberId, int Take, string? Cursor)? LastList { get; private set; }
        public (string ScopeId, string TeamId, string MemberId, string ScheduleId)? LastGet { get; private set; }

        public Task<StudioMemberWorkflowAuthorizationResult> PreflightAsync(
            StudioMemberWorkflowScheduleRequest request,
            CancellationToken ct = default)
        {
            LastPreflight = request;
            return Result(PreflightResult);
        }

        public Task<StudioMemberWorkflowAuthorizationResult> PreflightForWriteAsync(
            StudioMemberWorkflowScheduleRequest request,
            CancellationToken ct = default)
        {
            WritePreflightCalls++;
            return PreflightAsync(request, ct);
        }

        public Task<StudioMemberWorkflowScheduleResult> CreateAsync(
            StudioMemberWorkflowScheduleRequest request,
            string confirmedPermissionDigest,
            CancellationToken ct = default)
        {
            LastCreate = request;
            LastConfirmedPermissionDigest = confirmedPermissionDigest;
            return MutationResult(
                request.ScopeId,
                request.TeamId,
                request.MemberId,
                CreateResults.Count > 0
                    ? CreateResults.Dequeue()
                    : CreateResult ?? new StudioMemberWorkflowScheduleResult(
                    true,
                    request.ScopeId,
                    request.MemberId,
                    ScheduleId,
                    "svc-alpha",
                    "/workflow/observatory",
                    "pending")
                {
                    OperationId = request.OperationId ?? "op-alpha",
                    CommandId = "cmd-alpha",
                    NewOperationCommitted = true,
                });
        }

        public Task<StudioMemberWorkflowScheduleResult> ReauthorizeAsync(
            StudioMemberWorkflowScheduleRequest request,
            string confirmedPermissionDigest,
            CancellationToken ct = default) =>
            CreateAsync(request, confirmedPermissionDigest, ct);

        public Task<StudioMemberAutomationListResponse> ListAsync(
            string scopeId,
            string teamId,
            string? memberId,
            int take = 50,
            string? cursor = null,
            bool includeTotalCount = false,
            CancellationToken ct = default)
        {
            ListCalls++;
            LastList = (scopeId, teamId, memberId, take, cursor);
            return Result(new StudioMemberAutomationListResponse(
                View == null ? [] : [View],
                null,
                View == null ? 0 : 1));
        }

        public Task<StudioMemberAutomationView?> GetAsync(
            string scopeId,
            string teamId,
            string memberId,
            string scheduleId,
            CancellationToken ct = default)
        {
            LastGet = (scopeId, teamId, memberId, scheduleId);
            return Result(View);
        }

        public Task<StudioMemberAutomationMutationReceipt> UpdateAsync(
            StudioMemberAutomationUpdateCommand command,
            CancellationToken ct = default)
        {
            LastUpdate = command;
            return MutationResult(
                command.ScopeId,
                command.TeamId,
                command.MemberId,
                Receipt(command.OperationId));
        }

        public Task<StudioMemberAutomationMutationReceipt> PauseAsync(
            StudioMemberAutomationActionCommand command,
            CancellationToken ct = default) =>
            ActionResult("pause", command);

        public Task<StudioMemberAutomationMutationReceipt> ResumeAsync(
            StudioMemberAutomationActionCommand command,
            CancellationToken ct = default) =>
            ActionResult("resume", command);

        public Task<StudioMemberAutomationMutationReceipt> RunNowAsync(
            StudioMemberAutomationActionCommand command,
            CancellationToken ct = default) =>
            ActionResult("run-now", command);

        public Task<StudioMemberAutomationMutationReceipt> DeleteAsync(
            StudioMemberAutomationActionCommand command,
            CancellationToken ct = default)
        {
            LastDelete = command;
            return MutationResult(
                command.ScopeId,
                command.TeamId,
                command.MemberId,
                Receipt(command.OperationId, "pending"));
        }

        public Task<StudioMemberAutomationMutationReceipt> RetryRevocationAsync(
            StudioMemberAutomationRetryRevocationCommand command,
            CancellationToken ct = default)
        {
            LastRetryRevocation = command;
            return MutationResult(
                command.ScopeId,
                command.TeamId,
                command.MemberId,
                Receipt("op-delete-committed", "pending"));
        }

        private Task<StudioMemberAutomationMutationReceipt> ActionResult(
            string action,
            StudioMemberAutomationActionCommand command)
        {
            LastActionName = action;
            LastAction = command;
            return MutationResult(
                command.ScopeId,
                command.TeamId,
                command.MemberId,
                Receipt(command.OperationId));
        }

        private Task<T> MutationResult<T>(
            string scopeId,
            string? teamId,
            string memberId,
            T value)
        {
            if (!string.Equals(scopeId, ScopeId, StringComparison.Ordinal) ||
                !string.Equals(teamId, TeamId, StringComparison.Ordinal) ||
                !string.Equals(memberId, MemberId, StringComparison.Ordinal))
            {
                return Task.FromException<T>(new StudioMemberAutomationNotFoundException());
            }

            if (Exception != null)
                return Task.FromException<T>(Exception);

            ScheduleMutationCalls++;
            return Task.FromResult(value);
        }

        private Task<T> Result<T>(T value) => Exception == null
            ? Task.FromResult(value)
            : Task.FromException<T>(Exception);

        private static StudioMemberAutomationMutationReceipt Receipt(
            string operationId,
            string status = "accepted") =>
            new(true, status, ScheduleId, operationId, "cmd-alpha");
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Aevatar.Studio.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
