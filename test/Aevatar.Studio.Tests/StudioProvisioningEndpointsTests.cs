using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Hosting.Endpoints;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Studio.Tests;

/// <summary>
/// HTTP-handler invariants for the one-call provisioning endpoint (C1, v2 async):
/// <list type="bullet">
///   <item>the scope-access guard short-circuits with 403 before the service is
///   touched (cross-scope / unauthenticated);</item>
///   <item>a successful provision always returns 202 Accepted (the bind + run are
///   asynchronous) carrying the schedule id + Observatory link, with the
///   Location pointing at the created schedule;</item>
///   <item>the caller NyxID subject reference is required and threaded into the
///   service as an input parameter; a missing subject maps to a stable 400;</item>
///   <item>domain validation failures map to a stable 400 code.</item>
/// </list>
/// </summary>
public sealed class StudioProvisioningEndpointsTests
{
    private const string ScopeId = "scope-1";
    private const string TeamId = "team-alpha";
    private const string ScheduleId = "schedule-xyz";

    private static ProvisionWorkflowCallerCredential Caller =>
        new(Platform: "nyxid", ExternalUserId: "user-42", Scope: "proxy");

    [Fact]
    public async Task HandleProvisionWorkflowAsync_ShouldReturnAccepted_WithScheduleLocation()
    {
        var response = NewResponse();
        var service = new RecordingProvisioningService { Response = response };

        var result = await InvokeHandle<IResult>(
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            new ProvisionWorkflowRequest(
                DisplayName: "Monitor", WorkflowYaml: "name: monitor", Prompt: "go", Caller: Caller)
            {
                TeamId = TeamId,
            },
            service,
            CancellationToken.None);

        var accepted = result.Should().BeOfType<Accepted<ProvisionWorkflowResponse>>().Subject;
        accepted.Value.Should().BeSameAs(response);
        accepted.Value!.BindingStatus.Should().Be(ProvisionWorkflowBindingStatusNames.Accepted);
        accepted.Location.Should().Be($"/api/schedules/{ScheduleId}");
        service.ProvisionInvoked.Should().BeTrue();
        service.ProvisionScopeId.Should().Be(ScopeId);
    }

    [Fact]
    public async Task HandleProvisionWorkflowAsync_ShouldThreadCallerCredential_IntoService()
    {
        var service = new RecordingProvisioningService { Response = NewResponse() };

        await InvokeHandle<IResult>(
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            new ProvisionWorkflowRequest(
                DisplayName: "Monitor",
                WorkflowYaml: "name: monitor",
                Caller: new ProvisionWorkflowCallerCredential(
                    Platform: "lark", ExternalUserId: "ou-1", Scope: "proxy", Tenant: "t-1"))
            {
                TeamId = TeamId,
            },
            service,
            CancellationToken.None);

        service.ProvisionCaller.Should().NotBeNull();
        service.ProvisionCaller!.Platform.Should().Be("lark");
        service.ProvisionCaller.ExternalUserId.Should().Be("ou-1");
        service.ProvisionCaller.Scope.Should().Be("proxy");
        service.ProvisionCaller.Tenant.Should().Be("t-1");
    }

    [Fact]
    public async Task HandleProvisionWorkflowAsync_ShouldAttachTransientBearerAdmissionContext()
    {
        var service = new RecordingProvisioningService { Response = NewResponse() };
        var http = CreateAuthenticatedContext(ScopeId);
        ((ClaimsIdentity)http.User.Identity!).AddClaim(new Claim("sub", "caller-alpha"));
        http.Request.Headers.Authorization = "Bearer runtime-caller-credential";

        await InvokeHandle<IResult>(
            http,
            ScopeId,
            new ProvisionWorkflowRequest("Monitor", "name: monitor", Caller: Caller)
            {
                TeamId = TeamId,
            },
            service,
            CancellationToken.None);

        var context = service.ProvisionRequest!.CapabilityAdmission;
        context.Should().NotBeNull();
        context!.CallerId.Should().Be("caller-alpha");
        context.NyxIdCallerCredential?.SourceReadableUserBearerToken
            .Should().Be("runtime-caller-credential");
        context.ExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Durable);
        context.ToString().Should().NotContain("runtime-caller-credential");
        service.ProvisionRequest.AuthenticatedOwner.Should().NotBeNull();
        service.ProvisionRequest.AuthenticatedOwner!.SubjectExternalUserId.Should().Be("caller-alpha");
        service.ProvisionRequest.AuthenticatedOwner.VerifiedBindingId.Should().Be("binding-alpha");
        service.ProvisionRequest.ProvisioningBearerToken.Should().Be("runtime-caller-credential");
    }

    [Fact]
    public async Task HandleProvisionWorkflowAsync_ShouldMapAndScrubExplicitRequestConfirmations()
    {
        var service = new RecordingProvisioningService { Response = NewResponse() };

        await InvokeHandle<IResult>(
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            new ProvisionWorkflowRequest("Monitor", "name: wf-alpha", Caller: Caller)
            {
                TeamId = TeamId,
                ExplicitRequestConfirmations =
                [
                    new NyxIdExplicitRequestConfirmationInput(
                        "wf-alpha/request-alpha",
                        "digest-alpha",
                        "read_only"),
                ],
            },
            service,
            CancellationToken.None);

        service.ProvisionRequest.Should().NotBeNull();
        service.ProvisionRequest!.ExplicitRequestConfirmations.Should().BeNull();
        service.ProvisionRequest.CapabilityAdmission!.CallerId.Should().Be("caller-alpha");
        service.ProvisionRequest.CapabilityAdmission.ExplicitRequestConfirmations
            .Should().ContainSingle().Which.RequestContractDigest.Should().Be("digest-alpha");
    }

    [Fact]
    public async Task HandleProvisionWorkflowAsync_WithNullExplicitRequestConfirmation_ShouldReturnTypedBadRequestWithoutDispatch()
    {
        var service = new RecordingProvisioningService();
        var http = CreateAuthenticatedContext(ScopeId);
        http.Response.Body = new MemoryStream();

        var result = await InvokeHandle<IResult>(
            http,
            ScopeId,
            new ProvisionWorkflowRequest("Monitor", "name: wf-alpha", Caller: Caller)
            {
                TeamId = TeamId,
                ExplicitRequestConfirmations = [null!],
            },
            service,
            CancellationToken.None);

        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(http.Response.Body);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.RootElement.GetProperty("code").GetString().Should()
            .Be("INVALID_EXPLICIT_REQUEST_CONFIRMATION");
        service.ProvisionInvoked.Should().BeFalse();
    }

    [Fact]
    public async Task HandleProvisionWorkflowAsync_WithMultipleAuthorizationValues_ShouldRejectWithoutDispatch()
    {
        var service = new RecordingProvisioningService();
        var http = CreateAuthenticatedContext(ScopeId);
        http.Request.Headers.Authorization =
            new Microsoft.Extensions.Primitives.StringValues(["Bearer first", "Bearer second"]);

        var result = await InvokeHandle<IResult>(
            http,
            ScopeId,
            new ProvisionWorkflowRequest(
                DisplayName: "Monitor",
                WorkflowYaml: "name: monitor\nsteps: []\n",
                Caller: Caller)
            {
                TeamId = TeamId,
            },
            service,
            CancellationToken.None);

        AssertBadRequestResult(result, "INVALID_WORKFLOW_CALLER_CREDENTIAL");
        service.ProvisionInvoked.Should().BeFalse();
    }

    [Fact]
    public async Task HandleProvisionWorkflowAsync_ShouldReturnConflict_WhenScheduleOwnerBindingMissing()
    {
        var service = new RecordingProvisioningService { Response = NewResponse() };

        var result = await InvokeHandle<IResult>(
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            new ProvisionWorkflowRequest("Monitor", "name: monitor", Caller: Caller)
            {
                TeamId = TeamId,
            },
            service,
            new RecordingIdentityBindingQueryPort { Binding = null },
            CancellationToken.None);

        service.ProvisionInvoked.Should().BeFalse();
        AssertIsJsonStatus(result, StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task HandleProvisionWorkflowAsync_ShouldUseCallerSubjectAsScheduleOwner_WhenAuthenticationDisabled()
    {
        var service = new RecordingProvisioningService { Response = NewResponse() };
        var bindingQuery = new RecordingIdentityBindingQueryPort { Binding = null };

        await InvokeHandle<IResult>(
            CreateAuthDisabledContext(),
            ScopeId,
            new ProvisionWorkflowRequest("Monitor", "name: monitor", Caller: Caller)
            {
                TeamId = TeamId,
            },
            service,
            bindingQuery,
            CancellationToken.None);

        service.ProvisionInvoked.Should().BeTrue();
        bindingQuery.Subject.Should().NotBeNull();
        bindingQuery.Subject!.ExternalUserId.Should().Be("user-42");
        service.ProvisionRequest!.AuthenticatedOwner.Should().NotBeNull();
        service.ProvisionRequest.AuthenticatedOwner!.SubjectExternalUserId.Should().Be("user-42");
        service.ProvisionRequest.AuthenticatedOwner.VerifiedBindingId.Should().Be("auth-disabled:user-42");
        service.ProvisionRequest.ProvisioningBearerToken.Should().Be("user-42");
    }

    [Fact]
    public async Task HandleProvisionWorkflowAsync_ShouldDefaultScope_WhenCallerScopeOmitted()
    {
        var service = new RecordingProvisioningService { Response = NewResponse() };

        await InvokeHandle<IResult>(
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            new ProvisionWorkflowRequest(
                DisplayName: "Monitor",
                WorkflowYaml: "name: monitor",
                Caller: new ProvisionWorkflowCallerCredential(
                    Platform: "nyxid", ExternalUserId: "user-42", Scope: ""))
            {
                TeamId = TeamId,
            },
            service,
            CancellationToken.None);

        service.ProvisionCaller!.Scope.Should().Be(ProvisionWorkflowCallerCredential.DefaultScope);
    }

    [Fact]
    public void ProvisionWorkflowRequest_ShouldNotBindScheduleIdentityFromHttpJson()
    {
        var request = JsonSerializer.Deserialize<ProvisionWorkflowRequest>("""
            {
              "displayName": "Monitor",
              "workflowYaml": "name: monitor",
              "caller": {
                "platform": "nyxid",
                "externalUserId": "user-42",
                "scope": "proxy"
              },
              "teamId": "team-alpha",
              "scheduleOperationId": "caller-pinned-operation",
              "scheduleIdempotencyKey": "caller-pinned-key"
            }
            """,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        request.Should().NotBeNull();
        request!.ScheduleOperationId.Should().BeNull();
        request.ScheduleIdempotencyKey.Should().BeNull();
    }

    [Fact]
    public async Task HandleProvisionWorkflowAsync_ShouldReturnBadRequest_WhenTeamIdMissing()
    {
        var service = new RecordingProvisioningService { Response = NewResponse() };

        var result = await InvokeHandle<IResult>(
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Caller: Caller),
            service,
            CancellationToken.None);

        service.ProvisionInvoked.Should().BeFalse();
        AssertBadRequestResult(result, "INVALID_PROVISION_WORKFLOW_REQUEST");
    }

    [Fact]
    public async Task HandleProvisionWorkflowAsync_ShouldReturnBadRequest_WhenCallerSubjectMissing()
    {
        var service = new RecordingProvisioningService { Response = NewResponse() };

        var result = await InvokeHandle<IResult>(
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Caller: null)
            {
                TeamId = TeamId,
            },
            service,
            CancellationToken.None);

        // No subject ref → the dispatch could not re-mint a token; reject before
        // touching the service.
        service.ProvisionInvoked.Should().BeFalse();
        AssertBadRequestResult(result, "INVALID_PROVISION_WORKFLOW_REQUEST");
    }

    [Fact]
    public async Task HandleProvisionWorkflowAsync_ShouldReturnBadRequest_OnDomainError()
    {
        var service = new RecordingProvisioningService
        {
            ProvisionException = new InvalidOperationException("workflowYaml is required."),
        };

        var result = await InvokeHandle<IResult>(
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            new ProvisionWorkflowRequest(
                DisplayName: "Monitor", WorkflowYaml: string.Empty, Caller: Caller)
            {
                TeamId = TeamId,
            },
            service,
            CancellationToken.None);

        AssertBadRequestResult(result, "INVALID_PROVISION_WORKFLOW_REQUEST");
    }

    [Fact]
    public async Task HandleProvisionWorkflowAsync_ShouldReturnRetryableProjectionPending()
    {
        var service = new RecordingProvisioningService
        {
            ProvisionException = new StudioMemberAutomationProjectionPendingException(23),
        };

        var result = await InvokeHandle<IResult>(
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            new ProvisionWorkflowRequest("Monitor", "name: monitor", Caller: Caller)
            {
                TeamId = TeamId,
            },
            service,
            CancellationToken.None);

        AssertIsJsonStatus(result, StatusCodes.Status503ServiceUnavailable);
        var value = result.GetType().GetProperty("Value")?.GetValue(result);
        value.Should().NotBeNull();
        value!.GetType().GetProperty("code")?.GetValue(value)
            .Should().Be("PROVISION_WORKFLOW_AUTHORIZATION_PROJECTION_PENDING");
        value.GetType().GetProperty("retryable")?.GetValue(value).Should().Be(true);
        value.GetType().GetProperty("requiredStateVersion")?.GetValue(value).Should().Be(23L);
    }

    [Fact]
    public async Task HandleProvisionWorkflowAsync_ShouldReturnForbidden_WhenScopeAccessDenied()
    {
        var service = new RecordingProvisioningService();

        var result = await InvokeHandle<IResult>(
            CreateAuthenticatedContext("other-scope"),
            ScopeId,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Caller: Caller)
            {
                TeamId = TeamId,
            },
            service,
            CancellationToken.None);

        // The guard must short-circuit before the service is touched.
        service.ProvisionInvoked.Should().BeFalse();
        AssertIsJsonStatus(result, StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task HandleProvisionWorkflowAsync_ShouldReturnForbidden_WhenUnauthenticated()
    {
        var service = new RecordingProvisioningService();

        var result = await InvokeHandle<IResult>(
            CreateUnauthenticatedContext(),
            ScopeId,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Caller: Caller)
            {
                TeamId = TeamId,
            },
            service,
            CancellationToken.None);

        service.ProvisionInvoked.Should().BeFalse();
        AssertIsJsonStatus(result, StatusCodes.Status403Forbidden);
    }

    private static ProvisionWorkflowResponse NewResponse() => new(
        MemberId: "member-1",
        ScopeId: ScopeId,
        TeamId: TeamId,
        BindingStatus: ProvisionWorkflowBindingStatusNames.Accepted,
        ObservatoryUrl: "/workflow/observatory")
    {
        BindingRunId = "bind-run-1",
        ScheduleId = ScheduleId,
    };

    private static async Task<TResult> InvokeHandle<TResult>(params object?[] args)
    {
        var method = typeof(StudioProvisioningEndpoints)
            .GetMethod("HandleProvisionWorkflowAsync", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Method HandleProvisionWorkflowAsync not found.");
        if (args.Length == 5)
        {
            args =
            [
                args[0],
                args[1],
                args[2],
                args[3],
                new RecordingIdentityBindingQueryPort(),
                args[4],
            ];
        }

        var task = (Task<IResult>)method.Invoke(null, args)!;
        return (TResult)(object)await task;
    }

    private static HttpContext CreateAuthenticatedContext(string claimedScopeId)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim("scope_id", claimedScopeId),
            new Claim("sub", "caller-alpha"),
        ],
        "test");
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
            RequestServices = BuildAuthEnabledServices(),
        };
        http.Request.Headers.Authorization = "Bearer runtime-caller-credential";
        return http;
    }

    private static HttpContext CreateUnauthenticatedContext() =>
        new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity()),
            RequestServices = BuildAuthEnabledServices(),
        };

    private static HttpContext CreateAuthDisabledContext()
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity()),
            RequestServices = BuildAuthDisabledServices(),
        };
        http.Request.Headers.Authorization = "Bearer runtime-caller-credential";
        return http;
    }

    private static IServiceProvider BuildAuthEnabledServices() =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Aevatar:Authentication:Enabled"] = "true",
                })
                .Build())
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment())
            .BuildServiceProvider();

    private static IServiceProvider BuildAuthDisabledServices() =>
        new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Aevatar:Authentication:Enabled"] = "false",
                })
                .Build())
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment
            {
                EnvironmentName = Environments.Development,
            })
            .BuildServiceProvider();

    private static void AssertIsJsonStatus(IResult result, int expectedStatus)
    {
        var statusCode = result.GetType().GetProperty("StatusCode")?.GetValue(result) as int?;
        statusCode.Should().Be(expectedStatus,
            because: $"expected JSON result with status {expectedStatus} but got {result.GetType().Name}");
    }

    private static void AssertBadRequestResult(IResult result, string expectedCode)
    {
        result.GetType().Name.Should().StartWith("BadRequest");
        var statusCode = result.GetType().GetProperty("StatusCode")?.GetValue(result) as int?;
        statusCode.Should().Be(StatusCodes.Status400BadRequest);

        var value = result.GetType().GetProperty("Value")?.GetValue(result);
        value.Should().NotBeNull();
        var code = value!.GetType().GetProperty("code")?.GetValue(value) as string;
        code.Should().Be(expectedCode);
    }

    private sealed class RecordingProvisioningService : IStudioWorkflowProvisioningService
    {
        public ProvisionWorkflowResponse? Response { get; set; }
        public Exception? ProvisionException { get; set; }
        public bool ProvisionInvoked { get; private set; }
        public string? ProvisionScopeId { get; private set; }
        public ProvisionWorkflowCallerCredential? ProvisionCaller { get; private set; }
        public ProvisionWorkflowRequest? ProvisionRequest { get; private set; }

        public Task<ProvisionWorkflowResponse> ProvisionAsync(
            string scopeId,
            ProvisionWorkflowCallerCredential callerCredential,
            ProvisionWorkflowRequest request,
            CancellationToken ct = default)
        {
            ProvisionInvoked = true;
            ProvisionScopeId = scopeId;
            ProvisionCaller = callerCredential;
            ProvisionRequest = request;
            if (ProvisionException != null) throw ProvisionException;
            return Task.FromResult(Response!);
        }
    }

    private sealed class RecordingIdentityBindingQueryPort : IExternalIdentityBindingQueryPort
    {
        public BindingId? Binding { get; init; } = new() { Value = "binding-alpha" };
        public ExternalSubjectRef? Subject { get; private set; }

        public Task<BindingId?> ResolveAsync(
            ExternalSubjectRef externalSubject,
            CancellationToken ct = default)
        {
            Subject = externalSubject.Clone();
            return Task.FromResult(Binding);
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Aevatar.Studio.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
