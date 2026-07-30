using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Studio.Hosting;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowCapabilityAdmissionHttpContextTests
{
    [Theory]
    [InlineData("bearer_only", "bearer-token")]
    [InlineData("delegation_only", "delegation-token")]
    [InlineData("both_valid", "bearer-token")]
    [InlineData("malformed_authorization_with_delegation", null)]
    [InlineData("duplicate_authorization", null)]
    [InlineData("duplicate_delegation", null)]
    [InlineData("missing", null)]
    [InlineData("valid_authorization_with_malformed_delegation", "bearer-token")]
    public void Create_ShouldApplyCanonicalCallerCredentialSelection(
        string scenario,
        string? expectedToken)
    {
        foreach (var (name, create) in AdmissionFactories)
        {
            var http = new DefaultHttpContext();
            ApplyScenario(http, scenario);

            var admission = create(http);

            admission.NyxIdCallerBearerToken.Should().Be(expectedToken, name);
        }
    }

    private static IReadOnlyList<(string Name, Func<HttpContext, WorkflowCapabilityAdmissionContext> Create)>
        AdmissionFactories { get; } =
        [
            ("GAgentService", http => WorkflowCapabilityAdmissionHttpContext.Create(http)),
            ("Studio", http => StudioWorkflowCapabilityAdmissionHttpContext.Create(
                http,
                ExternalCapabilityExecutionMode.Interactive)),
        ];

    private static void ApplyScenario(DefaultHttpContext http, string scenario)
    {
        switch (scenario)
        {
            case "bearer_only":
                http.Request.Headers.Authorization = "Bearer bearer-token";
                break;
            case "delegation_only":
                http.Request.Headers["X-NyxID-Delegation-Token"] = "delegation-token";
                break;
            case "both_valid":
                http.Request.Headers.Authorization = "Bearer bearer-token";
                http.Request.Headers["X-NyxID-Delegation-Token"] = "delegation-token";
                break;
            case "malformed_authorization_with_delegation":
                http.Request.Headers.Authorization = "Bearer token with spaces";
                http.Request.Headers["X-NyxID-Delegation-Token"] = "delegation-token";
                break;
            case "duplicate_authorization":
                http.Request.Headers.Authorization = new StringValues(["Bearer first", "Bearer second"]);
                break;
            case "duplicate_delegation":
                http.Request.Headers["X-NyxID-Delegation-Token"] =
                    new StringValues(["first", "second"]);
                break;
            case "valid_authorization_with_malformed_delegation":
                http.Request.Headers.Authorization = "Bearer bearer-token";
                http.Request.Headers["X-NyxID-Delegation-Token"] = "token with spaces";
                break;
            case "missing":
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }
}
