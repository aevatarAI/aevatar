using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Studio.Hosting;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Credentials;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowCapabilityAdmissionHttpContextTests
{
    [Fact]
    public async Task CreateAsync_ShouldMapOnlyTypedExplicitRequestConfirmationFields()
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

        var serviceAdmission = await WorkflowCapabilityAdmissionHttpContext.CreateAsync(
            http,
            ExternalCapabilityExecutionMode.Durable,
            explicitRequestConfirmations: inputs);
        var studioAdmission = await StudioWorkflowCapabilityAdmissionHttpContext.CreateAsync(
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
    public async Task CreateAsync_WithNullExplicitRequestConfirmation_ShouldRejectAtSharedHttpBoundary()
    {
        var http = new DefaultHttpContext();
        NyxIdExplicitRequestConfirmationInput[] inputs = [null!];

        Func<Task> serviceAction = async () => await WorkflowCapabilityAdmissionHttpContext.CreateAsync(
            http,
            explicitRequestConfirmations: inputs);
        Func<Task> studioAction = async () => await StudioWorkflowCapabilityAdmissionHttpContext.CreateAsync(
            http,
            ExternalCapabilityExecutionMode.Interactive,
            inputs);

        foreach (var action in new[] { serviceAction, studioAction })
        {
            await action.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Explicit request confirmations cannot contain null values.");
        }
    }

    [Theory]
    [InlineData("bearer_only", "bearer-token", null)]
    [InlineData("delegation_only", null, "delegation-token")]
    [InlineData("both_valid", "bearer-token", null)]
    [InlineData("missing", null, null)]
    public async Task CreateAsync_ShouldApplyCanonicalCallerCredentialSelection(
        string scenario,
        string? expectedSourceReadableToken,
        string? expectedDelegationToken)
    {
        foreach (var (name, create) in AdmissionFactories)
        {
            var http = new DefaultHttpContext();
            ApplyScenario(http, scenario);

            var admission = await create(http);

            admission.NyxIdCallerCredential?.SourceReadableUserBearerToken.Should()
                .Be(expectedSourceReadableToken, name);
            admission.NyxIdCallerCredential?.ProxyDelegationToken.Should()
                .Be(expectedDelegationToken, name);
        }
    }

    [Theory]
    [InlineData("malformed_authorization_with_delegation")]
    [InlineData("valid_authorization_with_malformed_delegation")]
    [InlineData("duplicate_authorization")]
    [InlineData("duplicate_delegation")]
    public async Task CreateAsync_WithInvalidCallerCredentialSelection_ShouldFailClosed(string scenario)
    {
        foreach (var (name, create) in AdmissionFactories)
        {
            var http = new DefaultHttpContext();
            ApplyScenario(http, scenario);

            Func<Task> action = async () => await create(http);

            await action.Should().ThrowAsync<InvalidOperationException>(name)
                .WithMessage("Caller credential selection is invalid.");
        }
    }

    [Fact]
    public async Task CreateAsync_DelegationOnly_ShouldUseBoundSourceReadableCredential()
    {
        foreach (var (name, create) in AdmissionFactories)
        {
            var tokenProvider = new RecordingCallerAccessTokenProvider("source-readable-alpha");
            var services = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IExternalIdentityBindingQueryPort>(
                    new FixedBindingQueryPort("binding-alpha"))
                .AddSingleton<IWorkflowCallerAccessTokenProvider>(tokenProvider)
                .BuildServiceProvider();
            var http = new DefaultHttpContext
            {
                RequestServices = services,
                User = new System.Security.Claims.ClaimsPrincipal(
                    new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim("sub", "nyx-user-alpha")],
                    "nyxid")),
            };
            http.Request.Headers["X-NyxID-Delegation-Token"] = "delegation-alpha";

            var admission = await create(http);

            admission.NyxIdCallerCredential?.SourceReadableUserBearerToken.Should()
                .Be("source-readable-alpha", name);
            admission.NyxIdCallerCredential?.ProxyDelegationToken.Should().BeNull(name);
            tokenProvider.Authority?.BindingId.Should().Be("binding-alpha", name);
        }
    }

    [Fact]
    public async Task CreateAsync_WhenAuthenticationIsDisabled_ShouldUseExplicitScopeCallerFallback()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:Authentication:Enabled"] = "false",
            })
            .Build();
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment())
            .BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };

        var admission = await WorkflowCapabilityAdmissionHttpContext.CreateAsync(
            http,
            authenticationDisabledCallerId: " scope-alpha ");

        admission.CallerId.Should().Be("scope-alpha");
    }

    private static IReadOnlyList<(
        string Name,
        Func<HttpContext, ValueTask<WorkflowCapabilityAdmissionContext>> Create)>
        AdmissionFactories { get; } =
        [
            ("GAgentService", http => WorkflowCapabilityAdmissionHttpContext.CreateAsync(http)),
            ("Studio", http => StudioWorkflowCapabilityAdmissionHttpContext.CreateAsync(
                http,
                ExternalCapabilityExecutionMode.Interactive)),
        ];

    private sealed class FixedBindingQueryPort(string bindingId)
        : IExternalIdentityBindingQueryPort
    {
        public Task<BindingId?> ResolveAsync(
            ExternalSubjectRef externalSubject,
            CancellationToken ct = default) =>
            Task.FromResult<BindingId?>(new BindingId { Value = bindingId });
    }

    private sealed class RecordingCallerAccessTokenProvider(string accessToken)
        : IWorkflowCallerAccessTokenProvider
    {
        public WorkflowCallerNyxIdAuthority? Authority { get; private set; }

        public Task<string> IssueAsync(
            WorkflowCallerNyxIdAuthority authority,
            CancellationToken ct = default)
        {
            Authority = authority;
            return Task.FromResult(accessToken);
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = nameof(WorkflowCapabilityAdmissionHttpContextTests);

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

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
