using System.Security.Claims;
using System.Text;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.Mainnet.Host.Api.Skills;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Aevatar.Capabilities.Tests;

public sealed class WorkflowSkillsEndpointsTests
{
    [Fact]
    public async Task InvokeSkill_ShouldExtractTypedNyxIdAuthorityIndependentlyFromWorkflowScope()
    {
        var runService = new RecordingUserSkillRunService
        {
            Outcome = SkillRunOutcome.Ok(new SkillRunReceipt(
                "run-alpha",
                "codex-check",
                "workflow",
                "/admin#/observatory?run=run-alpha")),
        };

        ExternalSubjectRef? bindingSubject = null;
        var bindingQuery = Substitute.For<IExternalIdentityBindingQueryPort>();
        bindingQuery.ResolveAsync(
                Arg.Do<ExternalSubjectRef>(value => bindingSubject = value.Clone()),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BindingId?>(new BindingId { Value = "binding-alpha" }));
        using var services = CreateRequestServices(bindingQuery);
        var http = CreateHttpContext(services, "Bearer caller-token");

        var result = await WorkflowSkillsEndpoints.InvokeSkill(
            http,
            "skill-alpha",
            runService,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status200OK);
        runService.CallerCredential.Should().BeEquivalentTo(new WorkflowCallerCredential(
            "caller-token",
            new WorkflowCallerNyxIdAuthority(
                "nyxid",
                string.Empty,
                "nyx-user-alpha",
                "proxy",
                "binding-alpha"),
            Aevatar.Workflow.Abstractions.NyxIdCallerCredentialKind.SourceReadableUserBearer));
        runService.ScopeId.Should().Be("scope-alpha");
        bindingSubject.Should().BeEquivalentTo(new ExternalSubjectRef
        {
            Platform = "nyxid",
            Tenant = string.Empty,
            ExternalUserId = "nyx-user-alpha",
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer token with spaces")]
    public async Task InvokeSkill_WhenCallerCredentialIsMissingOrMalformed_ShouldNotInvokeRunService(
        string? authorization)
    {
        var runService = new RecordingUserSkillRunService();
        var bindingQuery = Substitute.For<IExternalIdentityBindingQueryPort>();
        using var services = CreateRequestServices(bindingQuery);
        var http = CreateHttpContext(services, authorization);

        var result = await WorkflowSkillsEndpoints.InvokeSkill(
            http,
            "skill-alpha",
            runService,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status401Unauthorized);
        runService.InvocationCount.Should().Be(0);
    }

    [Fact]
    public async Task ScheduleSkill_WhenConfirmationIsRequired_ShouldReturnTypedPreviewWithHttp200()
    {
        var preview = new SkillWorkflowMountPreview(
            "workflow-alpha",
            "revision-alpha",
            "sha256:bundle",
            [],
            new SkillWorkflowMountConfirmation(
                "workflow-alpha",
                "revision-alpha",
                "sha256:bundle",
                []));
        var runService = new RecordingUserSkillRunService
        {
            ScheduleOutcome = SkillScheduleOutcome.ConfirmationRequired(
                new SkillScheduleConfirmationReceipt(
                    "confirmation_required",
                    "sha256:reviewed",
                    [preview])),
        };
        var bindingQuery = Substitute.For<IExternalIdentityBindingQueryPort>();
        bindingQuery.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BindingId?>(new BindingId { Value = "binding-alpha" }));
        using var services = CreateRequestServices(bindingQuery);
        var http = CreateHttpContext(
            services,
            "Bearer caller-token",
            "{\"prompt\":\"run\",\"cronExpression\":\"0 9 * * *\",\"timezone\":\"UTC\",\"displayName\":\"Daily Check\",\"teamId\":\"team-alpha\",\"workflowConfirmationToken\":\"sha256:supplied\"}");

        var result = await WorkflowSkillsEndpoints.ScheduleSkill(
            http,
            "skill-alpha",
            runService,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status200OK);
        ((IValueHttpResult)result).Value.Should().BeSameAs(runService.ScheduleOutcome.Confirmation);
        runService.ScheduleInvocationCount.Should().Be(1);
        runService.WorkflowConfirmationToken.Should().Be("sha256:supplied");
    }

    private static DefaultHttpContext CreateHttpContext(
        IServiceProvider services,
        string? authorization,
        string bodyJson = "{\"prompt\":\"run the check\"}")
    {
        var http = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("workflow.scope_id", "scope-alpha"),
                new Claim("uid", "nyx-user-alpha"),
            ], "test")),
        };
        if (authorization != null)
            http.Request.Headers.Authorization = authorization;

        var body = Encoding.UTF8.GetBytes(bodyJson);
        http.Request.Body = new MemoryStream(body);
        http.Request.ContentLength = body.Length;
        http.Request.ContentType = "application/json";
        return http;
    }

    private static ServiceProvider CreateRequestServices(IExternalIdentityBindingQueryPort bindingQuery)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:Authentication:Enabled"] = "true",
            })
            .Build();

        return new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton(environment)
            .AddSingleton(bindingQuery)
            .AddLogging()
            .BuildServiceProvider();
    }

    private static int StatusCode(IResult result) =>
        ((IStatusCodeHttpResult)result).StatusCode ?? StatusCodes.Status200OK;

    private sealed class RecordingUserSkillRunService : IUserSkillRunService
    {
        public SkillRunOutcome Outcome { get; init; } = SkillRunOutcome.Failed("unexpected", "not invoked");

        public int InvocationCount { get; private set; }

        public int ScheduleInvocationCount { get; private set; }

        public WorkflowCallerCredential? CallerCredential { get; private set; }

        public string? ScopeId { get; private set; }

        public SkillScheduleOutcome ScheduleOutcome { get; init; } =
            SkillScheduleOutcome.Failed("unexpected", "not invoked");

        public string? WorkflowConfirmationToken { get; private set; }

        public Task<SkillRunOutcome> InvokeOnceAsync(
            string skillGuid,
            WorkflowCallerCredential callerCredential,
            string scopeId,
            string prompt,
            CancellationToken ct = default)
        {
            InvocationCount++;
            CallerCredential = callerCredential;
            ScopeId = scopeId;
            return Task.FromResult(Outcome);
        }

        public Task<SkillScheduleOutcome> ScheduleAsync(
            string skillGuid,
            WorkflowCallerCredential callerCredential,
            string scopeId,
            string prompt,
            string cronExpression,
            string timezone,
            string displayName,
            string teamId,
            string workflowConfirmationToken,
            CancellationToken ct = default)
        {
            ScheduleInvocationCount++;
            WorkflowConfirmationToken = workflowConfirmationToken;
            return Task.FromResult(ScheduleOutcome);
        }
    }
}
