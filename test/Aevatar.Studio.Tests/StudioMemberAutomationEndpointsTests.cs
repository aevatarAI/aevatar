using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Hosting.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Studio.Tests;

public sealed class StudioMemberAutomationEndpointsTests
{
    private const string ScopeId = "scope-alpha";
    private const string TeamId = "team-alpha";
    private const string MemberId = "m-alpha";
    private const string ScheduleId = "sch-alpha";

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

    [Theory]
    [InlineData("authorization_plan_changed", "TEAM_AUTOMATION_AUTHORIZATION_PLAN_CHANGED")]
    [InlineData("reauthorization_required", "TEAM_AUTOMATION_REAUTHORIZATION_REQUIRED")]
    public async Task Update_ShouldReturnTypedConflictWithCanonicalPreflightLocator(
        string conflictCode,
        string expectedWireCode)
    {
        var schedules = new StubSchedules
        {
            Exception = new StudioMemberAutomationPlanConflictException(
                conflictCode,
                "sensitive backend detail must not cross the boundary"),
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
        JsonSerializer.Serialize(value).Should().NotContain("sensitive backend detail");
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
        JsonSerializer.Serialize(response).Should().NotContain("fresh-owner-bearer");
        JsonSerializer.Serialize(response).Should().NotContain("binding-alpha");
        schedules.LastDelete.Should().NotBeNull();
        schedules.LastDelete!.AuthenticatedOwner!.Owner.OwnerSubject.Should().Be("nyx-owner-alpha");
        schedules.LastDelete.ProvisioningBearerToken.Should().Be("fresh-owner-bearer");
        schedules.LastDelete.MemberId.Should().Be(MemberId);
        schedules.LastDelete.ScheduleId.Should().Be(ScheduleId);
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

    private sealed class StubBindingQuery : IExternalIdentityBindingQueryPort
    {
        public Task<BindingId?> ResolveAsync(
            ExternalSubjectRef externalSubject,
            CancellationToken ct = default) =>
            Task.FromResult<BindingId?>(new BindingId { Value = "binding-alpha" });
    }

    private sealed class StubSchedules : IStudioMemberWorkflowSchedulePort
    {
        public Exception? Exception { get; init; }
        public int ListCalls { get; private set; }
        public StudioMemberAutomationActionCommand? LastDelete { get; private set; }

        public Task<StudioMemberWorkflowAuthorizationResult> PreflightAsync(
            StudioMemberWorkflowScheduleRequest request,
            CancellationToken ct = default) =>
            Result(new StudioMemberWorkflowAuthorizationResult(
                false,
                null,
                ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
                "not_configured"));

        public Task<StudioMemberWorkflowScheduleResult> CreateAsync(
            StudioMemberWorkflowScheduleRequest request,
            string confirmedPermissionDigest,
            CancellationToken ct = default) =>
            Result(new StudioMemberWorkflowScheduleResult(
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
            });

        public Task<StudioMemberWorkflowScheduleResult> ReauthorizeAsync(
            StudioMemberWorkflowScheduleRequest request,
            string confirmedPermissionDigest,
            CancellationToken ct = default) =>
            CreateAsync(request, confirmedPermissionDigest, ct);

        public Task<StudioMemberAutomationListResponse> ListAsync(
            string scopeId,
            string teamId,
            string memberId,
            int take = 50,
            string? cursor = null,
            bool includeTotalCount = false,
            CancellationToken ct = default)
        {
            ListCalls++;
            return Result(new StudioMemberAutomationListResponse([], null, 0));
        }

        public Task<StudioMemberAutomationView?> GetAsync(
            string scopeId,
            string teamId,
            string memberId,
            string scheduleId,
            CancellationToken ct = default) =>
            Result<StudioMemberAutomationView?>(null);

        public Task<StudioMemberAutomationMutationReceipt> UpdateAsync(
            StudioMemberAutomationUpdateCommand command,
            CancellationToken ct = default) =>
            Result(Receipt(command.OperationId));

        public Task<StudioMemberAutomationMutationReceipt> PauseAsync(
            StudioMemberAutomationActionCommand command,
            CancellationToken ct = default) =>
            Result(Receipt(command.OperationId));

        public Task<StudioMemberAutomationMutationReceipt> ResumeAsync(
            StudioMemberAutomationActionCommand command,
            CancellationToken ct = default) =>
            Result(Receipt(command.OperationId));

        public Task<StudioMemberAutomationMutationReceipt> RunNowAsync(
            StudioMemberAutomationActionCommand command,
            CancellationToken ct = default) =>
            Result(Receipt(command.OperationId));

        public Task<StudioMemberAutomationMutationReceipt> DeleteAsync(
            StudioMemberAutomationActionCommand command,
            CancellationToken ct = default)
        {
            LastDelete = command;
            return Result(Receipt(command.OperationId, "pending"));
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
