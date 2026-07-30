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
    [Fact]
    public void Create_ShouldMapOnlyTypedExplicitRequestConfirmationFields()
    {
        var http = new DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim("sub", "authenticated-owner-alpha")],
                    "test")),
        };
        var inputs = new[]
        {
            new NyxIdExplicitRequestConfirmationInput(
                "wf-alpha/request-alpha",
                "digest-alpha",
                "read_only"),
        };

        var serviceAdmission = WorkflowCapabilityAdmissionHttpContext.Create(
            http,
            ExternalCapabilityExecutionMode.Durable,
            explicitRequestConfirmations: inputs);
        var studioAdmission = StudioWorkflowCapabilityAdmissionHttpContext.Create(
            http,
            ExternalCapabilityExecutionMode.Interactive,
            inputs);

        foreach (var admission in new[] { serviceAdmission, studioAdmission })
        {
            admission.CallerId.Should().Be("authenticated-owner-alpha");
            admission.ExplicitRequestConfirmations.Should().ContainSingle().Which.Should()
                .BeEquivalentTo(new NyxIdExplicitRequestConfirmation
                {
                    CallSiteId = "wf-alpha/request-alpha",
                    RequestContractDigest = "digest-alpha",
                    AttestedRisk = NyxIdOperationRisk.ReadOnly,
                });
        }
    }

    [Fact]
    public void Create_WithNullExplicitRequestConfirmation_ShouldRejectAtSharedHttpBoundary()
    {
        var http = new DefaultHttpContext();
        NyxIdExplicitRequestConfirmationInput[] inputs = [null!];

        var serviceAction = () => WorkflowCapabilityAdmissionHttpContext.Create(
            http,
            explicitRequestConfirmations: inputs);
        var studioAction = () => StudioWorkflowCapabilityAdmissionHttpContext.Create(
            http,
            ExternalCapabilityExecutionMode.Interactive,
            inputs);

        foreach (var action in new[] { serviceAction, studioAction })
        {
            action.Should().Throw<InvalidOperationException>()
                .WithMessage("Explicit request confirmations cannot contain null values.");
        }
    }

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
