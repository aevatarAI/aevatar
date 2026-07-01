using System.Reflection;
using System.Security.Claims;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Hosting.Endpoints;
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
                DisplayName: "Monitor", WorkflowYaml: "name: monitor", Prompt: "go", Caller: Caller),
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
                    Platform: "lark", ExternalUserId: "ou-1", Scope: "proxy", Tenant: "t-1")),
            service,
            CancellationToken.None);

        service.ProvisionCaller.Should().NotBeNull();
        service.ProvisionCaller!.Platform.Should().Be("lark");
        service.ProvisionCaller.ExternalUserId.Should().Be("ou-1");
        service.ProvisionCaller.Scope.Should().Be("proxy");
        service.ProvisionCaller.Tenant.Should().Be("t-1");
    }

    [Fact]
    public async Task HandleProvisionWorkflowAsync_ShouldExtractBearerToken_AndThreadIntoService()
    {
        var service = new RecordingProvisioningService { Response = NewResponse() };
        var context = CreateAuthenticatedContext(ScopeId);
        context.Request.Headers.Authorization = "Bearer forwarded-caller-token";

        await InvokeHandle<IResult>(
            context,
            ScopeId,
            new ProvisionWorkflowRequest(
                DisplayName: "Monitor", WorkflowYaml: "name: monitor", Caller: Caller),
            service,
            CancellationToken.None);

        // The forwarded caller bearer token (Authorization header) is extracted at
        // the endpoint and threaded as an explicit boundary parameter; the service
        // holds no HttpContext and schedule auth must not persist the bearer.
        service.ProvisionCallerBearerToken.Should().Be("forwarded-caller-token");
    }

    [Fact]
    public async Task HandleProvisionWorkflowAsync_ShouldThreadNullBearerToken_WhenAuthorizationMissing()
    {
        var service = new RecordingProvisioningService { Response = NewResponse() };

        await InvokeHandle<IResult>(
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            new ProvisionWorkflowRequest(
                DisplayName: "Monitor", WorkflowYaml: "name: monitor", Caller: Caller),
            service,
            CancellationToken.None);

        service.ProvisionCallerBearerToken.Should().BeNull();
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
                    Platform: "nyxid", ExternalUserId: "user-42", Scope: "")),
            service,
            CancellationToken.None);

        service.ProvisionCaller!.Scope.Should().Be(ProvisionWorkflowCallerCredential.DefaultScope);
    }

    [Fact]
    public async Task HandleProvisionWorkflowAsync_ShouldReturnBadRequest_WhenCallerSubjectMissing()
    {
        var service = new RecordingProvisioningService { Response = NewResponse() };

        var result = await InvokeHandle<IResult>(
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Caller: null),
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
                DisplayName: "Monitor", WorkflowYaml: string.Empty, Caller: Caller),
            service,
            CancellationToken.None);

        AssertBadRequestResult(result, "INVALID_PROVISION_WORKFLOW_REQUEST");
    }

    [Fact]
    public async Task HandleProvisionWorkflowAsync_ShouldReturnForbidden_WhenScopeAccessDenied()
    {
        var service = new RecordingProvisioningService();

        var result = await InvokeHandle<IResult>(
            CreateAuthenticatedContext("other-scope"),
            ScopeId,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Caller: Caller),
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
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Caller: Caller),
            service,
            CancellationToken.None);

        service.ProvisionInvoked.Should().BeFalse();
        AssertIsJsonStatus(result, StatusCodes.Status403Forbidden);
    }

    private static ProvisionWorkflowResponse NewResponse() => new(
        MemberId: "member-1",
        ScopeId: ScopeId,
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
        var task = (Task<IResult>)method.Invoke(null, args)!;
        return (TResult)(object)await task;
    }

    private static HttpContext CreateAuthenticatedContext(string claimedScopeId)
    {
        var identity = new ClaimsIdentity([new Claim("scope_id", claimedScopeId)], "test");
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
            RequestServices = BuildAuthEnabledServices(),
        };
    }

    private static HttpContext CreateUnauthenticatedContext() =>
        new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity()),
            RequestServices = BuildAuthEnabledServices(),
        };

    private static IServiceProvider BuildAuthEnabledServices() =>
        new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Aevatar:Authentication:Enabled"] = "true",
                })
                .Build())
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment())
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
        public string? ProvisionCallerBearerToken { get; private set; }

        public Task<ProvisionWorkflowResponse> ProvisionAsync(
            string scopeId,
            ProvisionWorkflowCallerCredential callerCredential,
            ProvisionWorkflowRequest request,
            string? callerBearerToken = null,
            CancellationToken ct = default)
        {
            ProvisionInvoked = true;
            ProvisionScopeId = scopeId;
            ProvisionCaller = callerCredential;
            ProvisionRequest = request;
            ProvisionCallerBearerToken = callerBearerToken;
            if (ProvisionException != null) throw ProvisionException;
            return Task.FromResult(Response!);
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
