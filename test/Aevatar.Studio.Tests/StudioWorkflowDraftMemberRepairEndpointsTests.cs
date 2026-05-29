using System.Reflection;
using System.Security.Claims;
using Aevatar.CQRS.Core.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Hosting.Endpoints;
using Aevatar.Studio.Projection.CommandServices;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.Repair;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Studio.Tests;

public sealed class StudioWorkflowDraftMemberRepairEndpointsTests
{
    [Fact]
    public async Task HandleRepairScopeAsync_ShouldReturnAccepted_ForExplicitScope()
    {
        var service = NewRepairService([]);

        var result = await InvokeHandle<IResult>(
            "HandleRepairScopeAsync",
            CreateAuthenticatedContext("scope-1"),
            "scope-1",
            service,
            CancellationToken.None);

        var accepted = result.Should().BeOfType<Accepted<StudioWorkflowDraftMemberRepairResult>>().Subject;
        accepted.Value!.ScopeId.Should().Be("scope-1");
        accepted.Value.DraftCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleRepairScopeAsync_ShouldReturnForbidden_WhenScopeAccessDenied()
    {
        var service = NewRepairService([]);

        var result = await InvokeHandle<IResult>(
            "HandleRepairScopeAsync",
            CreateAuthenticatedContext("other-scope"),
            "scope-1",
            service,
            CancellationToken.None);

        AssertIsJsonStatus(result, StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task HandleRepairScopeAsync_ShouldReturnBadRequest_OnDomainError()
    {
        var service = NewRepairService(new ThrowingWorkspaceQueryPort(
            new InvalidOperationException("scopeId is required.")));

        var result = await InvokeHandle<IResult>(
            "HandleRepairScopeAsync",
            CreateAuthenticatedContext("scope-1"),
            "scope-1",
            service,
            CancellationToken.None);

        AssertBadRequestResult(
            result,
            "INVALID_STUDIO_WORKFLOW_DRAFT_MEMBER_REPAIR_REQUEST");
    }

    private static StudioWorkflowDraftMemberRepairService NewRepairService(
        IReadOnlyList<StudioWorkflowDraftRecord> drafts) =>
        NewRepairService(new StubWorkspaceQueryPort(drafts));

    private static StudioWorkflowDraftMemberRepairService NewRepairService(
        IStudioWorkspaceQueryPort workspaceQueryPort) =>
        new(
            workspaceQueryPort,
            new RecordingBootstrap(),
            CreateCommandDispatch(new RecordingDispatchPort()),
            new StudioWorkflowDraftMemberEnsureCommandFactory());

    private sealed class StubWorkspaceQueryPort(IReadOnlyList<StudioWorkflowDraftRecord> drafts)
        : IStudioWorkspaceQueryPort
    {
        public Task<StudioWorkspaceSnapshot> GetAsync(CancellationToken ct = default) =>
            throw new NotSupportedException("repair must use explicit scope.");

        public Task<StudioWorkspaceSnapshot> GetAsync(string scopeId, CancellationToken ct = default) =>
            Task.FromResult(new StudioWorkspaceSnapshot(
                $"studio-workspace:{scopeId}",
                scopeId,
                new StudioWorkspaceSettings(
                    RuntimeBaseUrl: string.Empty,
                    Directories: [],
                    AppearanceTheme: "blue",
                    ColorMode: "light"),
                Directories: [],
                Drafts: drafts,
                StateVersion: 11,
                UpdatedAtUtc: DateTimeOffset.Parse("2026-05-25T00:00:00Z")));
    }

    private sealed class ThrowingWorkspaceQueryPort(Exception exception) : IStudioWorkspaceQueryPort
    {
        public Task<StudioWorkspaceSnapshot> GetAsync(CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceSnapshot>(exception);

        public Task<StudioWorkspaceSnapshot> GetAsync(string scopeId, CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceSnapshot>(exception);
    }

    private sealed class RecordingBootstrap : IStudioActorBootstrap
    {
        public Task<IActor> EnsureAsync<TAgent>(string actorId, CancellationToken ct = default)
            where TAgent : IAgent, IProjectedActor =>
            Task.FromResult<IActor>(new StubActor(actorId));
    }

    private sealed class StubActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent => throw new NotSupportedException();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default) =>
            Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
    }

    private static StudioProjectionActorCommandDispatch CreateCommandDispatch(IActorDispatchPort dispatchPort)
    {
        var service = new DefaultCommandDispatchService<
            StudioProjectionActorCommand,
            StudioProjectionActorCommandTarget,
            StudioProjectionActorCommandReceipt,
            StudioProjectionActorCommandStartError>(
            new DefaultCommandDispatchPipeline<
                StudioProjectionActorCommand,
                StudioProjectionActorCommandTarget,
                StudioProjectionActorCommandReceipt,
                StudioProjectionActorCommandStartError>(
                new StudioProjectionActorCommandTargetResolver(),
                new DefaultCommandContextPolicy(),
                new StudioProjectionActorCommandEnvelopeFactory(),
                new ActorCommandTargetDispatcher<StudioProjectionActorCommandTarget>(dispatchPort),
                new StudioProjectionActorCommandReceiptFactory()));
        return new StudioProjectionActorCommandDispatch(service);
    }

    private static HttpContext CreateAuthenticatedContext(string claimedScopeId)
    {
        var identity = new ClaimsIdentity(
            [new Claim("scope_id", claimedScopeId)],
            "test");
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Aevatar:Authentication:Enabled"] = "true",
                })
                .Build())
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment())
            .BuildServiceProvider();
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
            RequestServices = services,
        };
    }

    private static async Task<TResult> InvokeHandle<TResult>(string methodName, params object?[] args)
    {
        var method = typeof(StudioWorkflowDraftMemberRepairEndpoints)
            .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Method {methodName} not found.");
        var task = (Task<IResult>)method.Invoke(null, args)!;
        return (TResult)(object)await task;
    }

    private static void AssertIsJsonStatus(IResult result, int expectedStatus)
    {
        var statusCodeProperty = result.GetType().GetProperty("StatusCode");
        var statusCode = statusCodeProperty?.GetValue(result) as int?;
        statusCode.Should().Be(expectedStatus);
    }

    private static void AssertBadRequestResult(IResult result, string expectedCode)
    {
        result.GetType().Name.Should().StartWith("BadRequest");

        var statusCodeProp = result.GetType().GetProperty("StatusCode");
        var statusCode = statusCodeProp?.GetValue(result) as int?;
        statusCode.Should().Be(StatusCodes.Status400BadRequest);

        var valueProp = result.GetType().GetProperty("Value");
        var value = valueProp?.GetValue(result);
        value.Should().NotBeNull();

        var codeProp = value!.GetType().GetProperty("code");
        var code = codeProp?.GetValue(value) as string;
        code.Should().Be(expectedCode);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Aevatar.Studio.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
