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
/// HTTP-handler invariants for the one-call provisioning endpoint (C1):
/// <list type="bullet">
///   <item>the scope-access guard short-circuits with 403 before the service is
///   touched (cross-scope) and 401 when unauthenticated;</item>
///   <item>a bound provision returns 200 with the run id + links;</item>
///   <item>a still-pending provision (bind timed out) returns 202;</item>
///   <item>domain validation failures map to a stable 400 code.</item>
/// </list>
/// </summary>
public sealed class StudioProvisioningEndpointsTests
{
    private const string ScopeId = "scope-1";

    [Fact]
    public async Task HandleProvisionWorkflowAsync_ShouldReturnOk_WhenBound()
    {
        var response = NewResponse(ProvisionWorkflowBindingStatusNames.Succeeded) with
        {
            RunId = "run-1",
        };
        var service = new RecordingProvisioningService { Response = response };

        var result = await InvokeHandle<IResult>(
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Prompt: "go"),
            service,
            CancellationToken.None);

        result.Should().BeOfType<Ok<ProvisionWorkflowResponse>>()
            .Which.Value.Should().BeSameAs(response);
        service.ProvisionInvoked.Should().BeTrue();
        service.ProvisionScopeId.Should().Be(ScopeId);
    }

    [Fact]
    public async Task HandleProvisionWorkflowAsync_ShouldReturnAccepted_WhenPending()
    {
        var response = NewResponse(ProvisionWorkflowBindingStatusNames.Pending);
        var service = new RecordingProvisioningService { Response = response };

        var result = await InvokeHandle<IResult>(
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor"),
            service,
            CancellationToken.None);

        var accepted = result.Should().BeOfType<Accepted<ProvisionWorkflowResponse>>().Subject;
        accepted.Value!.BindingStatus.Should().Be(ProvisionWorkflowBindingStatusNames.Pending);
        accepted.Value.RunId.Should().BeNull();
        accepted.Location.Should().Be($"/api/scopes/{ScopeId}/members/{response.MemberId}");
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
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: string.Empty),
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
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor"),
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
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor"),
            service,
            CancellationToken.None);

        service.ProvisionInvoked.Should().BeFalse();
        AssertIsJsonStatus(result, StatusCodes.Status403Forbidden);
    }

    private static ProvisionWorkflowResponse NewResponse(string bindingStatus) => new(
        MemberId: "member-1",
        ScopeId: ScopeId,
        BindingStatus: bindingStatus,
        ObservatoryUrl: "/workflow/observatory")
    {
        PublishedServiceId = bindingStatus == ProvisionWorkflowBindingStatusNames.Succeeded
            ? "published-svc-1"
            : null,
        RevisionId = bindingStatus == ProvisionWorkflowBindingStatusNames.Succeeded ? "rev-1" : null,
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
        public ProvisionWorkflowRequest? ProvisionRequest { get; private set; }

        public Task<ProvisionWorkflowResponse> ProvisionAsync(
            string scopeId, ProvisionWorkflowRequest request, CancellationToken ct = default)
        {
            ProvisionInvoked = true;
            ProvisionScopeId = scopeId;
            ProvisionRequest = request;
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
