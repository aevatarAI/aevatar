using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Hosting.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Studio.Tests;

public sealed class StudioMemberBindingHostedConsistencyTests
{
    private const string ScopeId = "scope-1";
    private const string MemberId = "member-1";

    [Fact]
    public async Task BindingEndpoints_ShouldStayConsistentAcrossAcceptedRunAndBindingView()
    {
        await using var host = await StudioMemberEndpointHostedTestHost.StartAsync();

        var bindResponse = await host.Client.PutAsJsonAsync(
            $"/api/scopes/{ScopeId}/members/{MemberId}/binding",
            new UpdateStudioMemberBindingRequest(
                Workflow: new StudioMemberWorkflowBindingSpec(
                    "workflow-stable-id",
                    ["name: main\nsteps:\n  - run: echo hello"])));

        bindResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var accepted = await bindResponse.Content.ReadFromJsonAsync<StudioMemberBindingAcceptedResponse>();
        accepted.Should().NotBeNull();
        accepted!.Status.Should().Be(StudioMemberBindingRunStatusNames.Accepted);
        accepted.BindingRunId.Should().StartWith("bind-");
        accepted.ScopeId.Should().Be(ScopeId);
        accepted.MemberId.Should().Be(MemberId);
        accepted.AckStage.Should().Be(StudioMemberBindingAckStageNames.DispatchAccepted);
        accepted.BindingRunRole.Should().Be(StudioMemberBindingRunRoleNames.Candidate);
        accepted.ObserveBindingRunUrl.Should()
            .Be($"/api/scopes/{ScopeId}/members/{MemberId}/binding-runs/{accepted.BindingRunId}");
        accepted.ObserveMemberBindingUrl.Should()
            .Be($"/api/scopes/{ScopeId}/members/{MemberId}/binding");
        bindResponse.Headers.Location!.OriginalString.Should()
            .Be($"/api/scopes/{ScopeId}/members/{MemberId}/binding-runs/{accepted.BindingRunId}");

        var startedRun = host.MemberCommandPort.StartedRuns.Should().ContainSingle().Which;
        startedRun.BindingRunId.Should().Be(accepted.BindingRunId);
        startedRun.ScopeId.Should().Be(ScopeId);
        startedRun.MemberId.Should().Be(MemberId);
        startedRun.ImplementationKind.Should().Be(MemberImplementationKindNames.Workflow);
        startedRun.Binding.Workflow!.WorkflowId.Should().Be("workflow-stable-id");
        startedRun.Binding.Workflow.WorkflowYamls.Should().ContainSingle()
            .Which.Should().Contain("echo hello");

        var runWhilePending = await host.Client.GetFromJsonAsync<StudioMemberBindingRunStatusResponse>(
            $"/api/scopes/{ScopeId}/members/{MemberId}/binding-runs/{accepted.BindingRunId}");
        runWhilePending.Should().NotBeNull();
        runWhilePending!.BindingRunId.Should().Be(accepted.BindingRunId);
        runWhilePending.Status.Should().Be(StudioMemberBindingRunStatusNames.PlatformBindingPending);
        runWhilePending.PlatformBindingCommandId.Should().Be("platform-bind-1");

        host.BindingRunQueryPort.Requests.Clear();
        var bindingWhilePending = await host.Client.GetFromJsonAsync<StudioMemberBindingViewResponse>(
            $"/api/scopes/{ScopeId}/members/{MemberId}/binding");
        bindingWhilePending.Should().NotBeNull();
        bindingWhilePending!.LastBinding.Should().BeNull();
        bindingWhilePending.CurrentBindingRun.Should().NotBeNull();
        bindingWhilePending.CurrentBindingRun!.BindingRunId.Should().Be(accepted.BindingRunId);
        bindingWhilePending.CurrentBindingRun.Status.Should().Be(StudioMemberBindingRunStatusNames.PlatformBindingPending);
        bindingWhilePending.CurrentBindingRun.PlatformBindingCommandId.Should().Be("platform-bind-1");
        host.BindingRunQueryPort.Requests.Should().ContainSingle()
            .Which.Should().Be((ScopeId, MemberId, accepted.BindingRunId));

        var rosterWhilePending = await host.Client.GetFromJsonAsync<StudioMemberRosterResponse>(
            $"/api/scopes/{ScopeId}/members");
        rosterWhilePending.Should().NotBeNull();
        var pendingMember = rosterWhilePending!.Members.Should().ContainSingle().Which;
        pendingMember.PublishedServiceId.Should().BeEmpty();

        host.Scenario.CompleteBinding();

        var completedRoster = await host.Client.GetFromJsonAsync<StudioMemberRosterResponse>(
            $"/api/scopes/{ScopeId}/members");
        completedRoster.Should().NotBeNull();
        var completedMember = completedRoster!.Members.Should().ContainSingle().Which;
        completedMember.PublishedServiceId.Should().Be("member-member-1");
        completedMember.LastBoundRevisionId.Should().Be("rev-1");

        var completedDetail = await host.Client.GetFromJsonAsync<StudioMemberDetailResponse>(
            $"/api/scopes/{ScopeId}/members/{MemberId}");
        completedDetail.Should().NotBeNull();
        completedDetail!.Summary.PublishedServiceId.Should().Be("member-member-1");
        completedDetail.Summary.LastBoundRevisionId.Should().Be("rev-1");
        completedDetail.ImplementationRef.Should().NotBeNull();
        completedDetail.ImplementationRef!.WorkflowId.Should().Be("workflow-stable-id");
        completedDetail.ImplementationRef.WorkflowRevision.Should().Be("rev-1");

        var completedRun = await host.Client.GetFromJsonAsync<StudioMemberBindingRunStatusResponse>(
            $"/api/scopes/{ScopeId}/members/{MemberId}/binding-runs/{accepted.BindingRunId}");
        completedRun.Should().NotBeNull();
        completedRun!.Status.Should().Be(StudioMemberBindingRunStatusNames.Succeeded);
        completedRun.PlatformBindingCommandId.Should().Be("platform-bind-1");

        host.BindingRunQueryPort.Requests.Clear();
        var completedBinding = await host.Client.GetFromJsonAsync<StudioMemberBindingViewResponse>(
            $"/api/scopes/{ScopeId}/members/{MemberId}/binding");
        completedBinding.Should().NotBeNull();
        completedBinding!.LastBinding.Should().NotBeNull();
        completedBinding.LastBinding!.PublishedServiceId.Should().Be("member-member-1");
        completedBinding.LastBinding.RevisionId.Should().Be("rev-1");
        completedBinding.CurrentBindingRun.Should().NotBeNull();
        completedBinding.CurrentBindingRun!.Status.Should().Be(StudioMemberBindingRunStatusNames.Succeeded);
        completedBinding.CurrentBindingRun.BindingRunId.Should().Be(accepted.BindingRunId);
        completedBinding.CurrentBindingRun.PlatformBindingCommandId.Should().Be("platform-bind-1");
        host.BindingRunQueryPort.Requests.Should().ContainSingle()
            .Which.Should().Be((ScopeId, MemberId, accepted.BindingRunId));
    }

    [Fact]
    public async Task BindingEndpoints_ShouldExposeRejectedCandidateWhenActiveRunRemainsAuthoritative()
    {
        await using var host = await StudioMemberEndpointHostedTestHost.StartAsync();
        host.Scenario.SeedActiveRun("bind-active");

        var bindResponse = await host.Client.PutAsJsonAsync(
            $"/api/scopes/{ScopeId}/members/{MemberId}/binding",
            new UpdateStudioMemberBindingRequest(
                Workflow: new StudioMemberWorkflowBindingSpec(
                    "workflow-stable-id",
                    ["name: retry\nsteps:\n  - run: echo retry"])));

        bindResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var accepted = await bindResponse.Content.ReadFromJsonAsync<StudioMemberBindingAcceptedResponse>();
        accepted.Should().NotBeNull();
        accepted!.AckStage.Should().Be(StudioMemberBindingAckStageNames.DispatchAccepted);
        accepted.BindingRunRole.Should().Be(StudioMemberBindingRunRoleNames.Candidate);
        accepted.BindingRunId.Should().NotBe("bind-active");
        accepted.ObserveBindingRunUrl.Should()
            .Be($"/api/scopes/{ScopeId}/members/{MemberId}/binding-runs/{accepted.BindingRunId}");
        accepted.ObserveMemberBindingUrl.Should()
            .Be($"/api/scopes/{ScopeId}/members/{MemberId}/binding");

        host.Scenario.RejectCandidate(
            accepted.BindingRunId,
            "STUDIO_MEMBER_BINDING_RUN_ALREADY_ACTIVE",
            "member already has an active binding run.");

        var candidateRun = await host.Client.GetFromJsonAsync<StudioMemberBindingRunStatusResponse>(
            accepted.ObserveBindingRunUrl);
        candidateRun.Should().NotBeNull();
        candidateRun!.BindingRunId.Should().Be(accepted.BindingRunId);
        candidateRun.Status.Should().Be(StudioMemberBindingRunStatusNames.Rejected);
        candidateRun.Failure.Should().NotBeNull();
        candidateRun.Failure!.Code.Should().Be("STUDIO_MEMBER_BINDING_RUN_ALREADY_ACTIVE");
        candidateRun.Failure.Message.Should().Be("member already has an active binding run.");

        host.BindingRunQueryPort.Requests.Clear();
        var authoritativeBinding = await host.Client.GetFromJsonAsync<StudioMemberBindingViewResponse>(
            accepted.ObserveMemberBindingUrl);
        authoritativeBinding.Should().NotBeNull();
        authoritativeBinding!.CurrentBindingRun.Should().NotBeNull();
        authoritativeBinding.CurrentBindingRun!.BindingRunId.Should().Be("bind-active");
        authoritativeBinding.CurrentBindingRun.Status.Should()
            .Be(StudioMemberBindingRunStatusNames.PlatformBindingPending);
        authoritativeBinding.CurrentBindingRun.Failure.Should().BeNull();
        host.BindingRunQueryPort.Requests.Should().ContainSingle()
            .Which.Should().Be((ScopeId, MemberId, "bind-active"));
    }

    private sealed class StudioMemberEndpointHostedTestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private StudioMemberEndpointHostedTestHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }
        public StudioMemberBindingScenario Scenario { get; private init; } = null!;
        public RecordingMemberCommandPort MemberCommandPort { get; private init; } = null!;
        public MutableBindingRunQueryPort BindingRunQueryPort { get; private init; } = null!;

        public static async Task<StudioMemberEndpointHostedTestHost> StartAsync()
        {
            var scenario = new StudioMemberBindingScenario();
            var memberCommandPort = new RecordingMemberCommandPort(scenario);
            var bindingRunQueryPort = new MutableBindingRunQueryPort(scenario);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["Aevatar:Authentication:Enabled"] = "true";
            builder.Services.AddSingleton<IConfiguration>(builder.Configuration);
            builder.Services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
            builder.Services.AddSingleton<IStudioMemberCommandPort>(memberCommandPort);
            builder.Services.AddSingleton<IStudioMemberQueryPort>(new MutableMemberQueryPort(scenario));
            builder.Services.AddSingleton<IStudioMemberBindingRunQueryPort>(bindingRunQueryPort);
            builder.Services.AddSingleton<IStudioTeamQueryPort>(new InertTeamQueryPort());
            builder.Services.AddSingleton<IServiceLifecycleQueryPort>(new ThrowingServiceLifecycleQueryPort());
            builder.Services.AddSingleton<IScopeBindingReadinessQueryPort>(new ThrowingScopeBindingReadinessQueryPort());
            builder.Services.AddSingleton<IServiceCommandPort>(new ThrowingServiceCommandPort());
            builder.Services.AddSingleton<IStudioMemberService, StudioMemberService>();
            builder.Services.AddAuthorization();

            var app = builder.Build();
            app.Use(async (http, next) =>
            {
                http.User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("scope_id", ScopeId),
                ], authenticationType: "Test"));
                await next();
            });
            app.UseAuthorization();
            StudioMemberEndpoints.Map(app);
            await app.StartAsync();

            var addressFeature = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Server addresses are unavailable.");
            var client = new HttpClient
            {
                BaseAddress = new Uri(addressFeature.Addresses.Single()),
            };

            return new StudioMemberEndpointHostedTestHost(app, client)
            {
                Scenario = scenario,
                MemberCommandPort = memberCommandPort,
                BindingRunQueryPort = bindingRunQueryPort,
            };
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    private sealed class StudioMemberBindingScenario
    {
        public string? CurrentBindingRunId { get; private set; }
        public StudioMemberBindingRunStartRequest? LatestStartedRun { get; private set; }
        public Dictionary<string, StudioMemberBindingRunStatusResponse> Runs { get; } = new(StringComparer.Ordinal);
        public bool Completed { get; private set; }

        public void StartRun(StudioMemberBindingRunStartRequest request)
        {
            LatestStartedRun = request;
            if (CurrentBindingRunId == null)
                CurrentBindingRunId = request.BindingRunId;

            Runs[request.BindingRunId] = BuildRun(
                request.BindingRunId,
                request.ScopeId,
                request.MemberId,
                StudioMemberBindingRunStatusNames.PlatformBindingPending,
                stateVersion: 1);
        }

        public void SeedActiveRun(string bindingRunId)
        {
            CurrentBindingRunId = bindingRunId;
            Runs[bindingRunId] = BuildRun(
                bindingRunId,
                ScopeId,
                MemberId,
                StudioMemberBindingRunStatusNames.PlatformBindingPending,
                stateVersion: 3);
        }

        public void RejectCandidate(string bindingRunId, string code, string message)
        {
            Runs[bindingRunId] = BuildRun(
                bindingRunId,
                ScopeId,
                MemberId,
                StudioMemberBindingRunStatusNames.Rejected,
                stateVersion: 2,
                failure: new StudioMemberBindingFailureResponse(
                    Code: code,
                    Message: message,
                    FailedAt: DateTimeOffset.Parse("2026-05-21T00:01:00Z")));
        }

        public void CompleteBinding()
        {
            Completed = true;
            if (CurrentBindingRunId == null)
                return;

            Runs[CurrentBindingRunId] = BuildRun(
                CurrentBindingRunId,
                ScopeId,
                MemberId,
                StudioMemberBindingRunStatusNames.Succeeded,
                stateVersion: 2);
        }

        private static StudioMemberBindingRunStatusResponse BuildRun(
            string bindingRunId,
            string scopeId,
            string memberId,
            string status,
            long stateVersion,
            StudioMemberBindingFailureResponse? failure = null) =>
            new(
                BindingRunId: bindingRunId,
                ScopeId: scopeId,
                MemberId: memberId,
                Status: status,
                StateVersion: stateVersion,
                Failure: failure,
                UpdatedAt: DateTimeOffset.Parse("2026-05-21T00:00:00Z"))
            {
                PlatformBindingCommandId = status == StudioMemberBindingRunStatusNames.PlatformBindingPending
                    || status == StudioMemberBindingRunStatusNames.Succeeded
                    ? "platform-bind-1"
                    : null,
            };
    }

    private sealed class RecordingMemberCommandPort : IStudioMemberCommandPort
    {
        private readonly StudioMemberBindingScenario _scenario;

        public RecordingMemberCommandPort(StudioMemberBindingScenario scenario)
        {
            _scenario = scenario;
        }

        public List<StudioMemberBindingRunStartRequest> StartedRuns { get; } = [];

        public Task<StudioMemberSummaryResponse> CreateAsync(
            string scopeId,
            CreateStudioMemberRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException("Create is not exercised by this hosted consistency test.");

        public Task UpdateImplementationAsync(
            string scopeId,
            string memberId,
            StudioMemberImplementationRefResponse implementation,
            CancellationToken ct = default) =>
            throw new NotSupportedException("Implementation update is not exercised by this hosted consistency test.");

        public Task RenameAsync(
            string scopeId,
            string memberId,
            string displayName,
            CancellationToken ct = default) =>
            throw new NotSupportedException("Rename is not exercised by this hosted consistency test.");

        public Task StartBindingRunAsync(
            StudioMemberBindingRunStartRequest request,
            CancellationToken ct = default)
        {
            StartedRuns.Add(request);
            _scenario.StartRun(request);
            return Task.CompletedTask;
        }

        public Task PatchTeamAssignmentAsync(
            string scopeId,
            string memberId,
            string? targetTeamId,
            CancellationToken ct = default) =>
            throw new NotSupportedException("Team reassignment is not exercised by this hosted consistency test.");
    }

    private sealed class MutableMemberQueryPort : IStudioMemberQueryPort
    {
        private readonly StudioMemberBindingScenario _scenario;

        public MutableMemberQueryPort(StudioMemberBindingScenario scenario)
        {
            _scenario = scenario;
        }

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId,
            StudioMemberRosterPageRequest? page = null,
            CancellationToken ct = default) =>
            Task.FromResult(new StudioMemberRosterResponse(scopeId, [BuildDetail(scopeId, MemberId).Summary]));

        public Task<StudioMemberDetailResponse?> GetAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default) =>
            Task.FromResult<StudioMemberDetailResponse?>(BuildDetail(scopeId, memberId));

        private StudioMemberDetailResponse BuildDetail(string scopeId, string memberId)
        {
            var now = DateTimeOffset.Parse("2026-05-21T00:00:00Z");
            var completed = _scenario.Completed;
            var currentBindingRunId = _scenario.CurrentBindingRunId;

            return new StudioMemberDetailResponse(
                Summary: new StudioMemberSummaryResponse(
                    MemberId: memberId,
                    ScopeId: scopeId,
                    DisplayName: "Member One",
                    Description: "test member",
                    ImplementationKind: MemberImplementationKindNames.Workflow,
                    LifecycleStage: MemberLifecycleStageNames.BindReady,
                    PublishedServiceId: completed ? "member-member-1" : string.Empty,
                    LastBoundRevisionId: completed ? "rev-1" : null,
                    CreatedAt: now.AddMinutes(-5),
                    UpdatedAt: now),
                ImplementationRef: new StudioMemberImplementationRefResponse(
                    ImplementationKind: MemberImplementationKindNames.Workflow,
                    WorkflowId: "workflow-stable-id",
                    WorkflowRevision: completed ? "rev-1" : null),
                LastBinding: completed
                    ? new StudioMemberBindingContractResponse(
                        PublishedServiceId: "member-member-1",
                        RevisionId: "rev-1",
                        ImplementationKind: MemberImplementationKindNames.Workflow,
                        BoundAt: now)
                    : null)
            {
                CurrentBindingRun = currentBindingRunId == null
                    ? null
                    : _scenario.Runs[currentBindingRunId],
            };
        }
    }

    private sealed class MutableBindingRunQueryPort : IStudioMemberBindingRunQueryPort
    {
        private readonly StudioMemberBindingScenario _scenario;

        public MutableBindingRunQueryPort(StudioMemberBindingScenario scenario)
        {
            _scenario = scenario;
        }

        public List<(string ScopeId, string MemberId, string BindingRunId)> Requests { get; } = [];

        public Task<StudioMemberBindingRunStatusResponse?> GetAsync(
            string scopeId,
            string memberId,
            string bindingRunId,
            CancellationToken ct = default)
        {
            Requests.Add((scopeId, memberId, bindingRunId));

            if (!_scenario.Runs.TryGetValue(bindingRunId, out var run)
                || !string.Equals(run.ScopeId, scopeId, StringComparison.Ordinal)
                || !string.Equals(run.MemberId, memberId, StringComparison.Ordinal)
                || !string.Equals(run.BindingRunId, bindingRunId, StringComparison.Ordinal))
            {
                return Task.FromResult<StudioMemberBindingRunStatusResponse?>(null);
            }

            return Task.FromResult<StudioMemberBindingRunStatusResponse?>(run);
        }
    }

    private sealed class InertTeamQueryPort : IStudioTeamQueryPort
    {
        public Task<StudioTeamRosterResponse> ListAsync(
            string scopeId,
            StudioTeamRosterPageRequest? page = null,
            CancellationToken ct = default) =>
            Task.FromResult(new StudioTeamRosterResponse(scopeId, []));

        public Task<StudioTeamSummaryResponse?> GetAsync(
            string scopeId,
            string teamId,
            CancellationToken ct = default) =>
            Task.FromResult<StudioTeamSummaryResponse?>(null);
    }

    private sealed class ThrowingScopeBindingReadinessQueryPort : IScopeBindingReadinessQueryPort
    {
        public Task<ScopeBindingReadinessSnapshot> GetReadinessAsync(
            ScopeBindingReadinessRequest request,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("Member list and binding views must not query invocation readiness.");
    }

    private sealed class ThrowingServiceLifecycleQueryPort : IServiceLifecycleQueryPort
    {
        public Task<ServiceCatalogSnapshot?> GetServiceAsync(
            ServiceIdentity identity,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("Binding consistency endpoints must not query platform lifecycle state.");

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("Binding consistency endpoints must not list platform lifecycle state.");

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(
            ServiceIdentity identity,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("Binding consistency endpoints must not query platform revisions.");

        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(
            ServiceIdentity identity,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("Binding consistency endpoints must not query platform deployments.");
    }

    private sealed class ThrowingServiceCommandPort : IServiceCommandPort
    {
        private static InvalidOperationException Reject(string method) =>
            new($"Binding consistency endpoints must not call IServiceCommandPort.{method}.");

        public Task<ServiceCommandAcceptedReceipt> CreateServiceAsync(
            CreateServiceDefinitionCommand command,
            CancellationToken ct = default) => throw Reject(nameof(CreateServiceAsync));

        public Task<ServiceCommandAcceptedReceipt> UpdateServiceAsync(
            UpdateServiceDefinitionCommand command,
            CancellationToken ct = default) => throw Reject(nameof(UpdateServiceAsync));

        public Task<ServiceCommandAcceptedReceipt> CreateRevisionAsync(
            CreateServiceRevisionCommand command,
            CancellationToken ct = default) => throw Reject(nameof(CreateRevisionAsync));

        public Task<ServiceCommandAcceptedReceipt> PrepareRevisionAsync(
            PrepareServiceRevisionCommand command,
            CancellationToken ct = default) => throw Reject(nameof(PrepareRevisionAsync));

        public Task<ServiceCommandAcceptedReceipt> PublishRevisionAsync(
            PublishServiceRevisionCommand command,
            CancellationToken ct = default) => throw Reject(nameof(PublishRevisionAsync));

        public Task<ServiceCommandAcceptedReceipt> RetireRevisionAsync(
            RetireServiceRevisionCommand command,
            CancellationToken ct = default) => throw Reject(nameof(RetireRevisionAsync));

        public Task<ServiceCommandAcceptedReceipt> SetDefaultServingRevisionAsync(
            SetDefaultServingRevisionCommand command,
            CancellationToken ct = default) => throw Reject(nameof(SetDefaultServingRevisionAsync));

        public Task<ServiceCommandAcceptedReceipt> ActivateServiceRevisionAsync(
            ActivateServiceRevisionCommand command,
            CancellationToken ct = default) => throw Reject(nameof(ActivateServiceRevisionAsync));

        public Task<ServiceCommandAcceptedReceipt> DeactivateServiceDeploymentAsync(
            DeactivateServiceDeploymentCommand command,
            CancellationToken ct = default) => throw Reject(nameof(DeactivateServiceDeploymentAsync));

        public Task<ServiceCommandAcceptedReceipt> ReplaceServiceServingTargetsAsync(
            ReplaceServiceServingTargetsCommand command,
            CancellationToken ct = default) => throw Reject(nameof(ReplaceServiceServingTargetsAsync));

        public Task<ServiceCommandAcceptedReceipt> StartServiceRolloutAsync(
            StartServiceRolloutCommand command,
            CancellationToken ct = default) => throw Reject(nameof(StartServiceRolloutAsync));

        public Task<ServiceCommandAcceptedReceipt> AdvanceServiceRolloutAsync(
            AdvanceServiceRolloutCommand command,
            CancellationToken ct = default) => throw Reject(nameof(AdvanceServiceRolloutAsync));

        public Task<ServiceCommandAcceptedReceipt> PauseServiceRolloutAsync(
            PauseServiceRolloutCommand command,
            CancellationToken ct = default) => throw Reject(nameof(PauseServiceRolloutAsync));

        public Task<ServiceCommandAcceptedReceipt> ResumeServiceRolloutAsync(
            ResumeServiceRolloutCommand command,
            CancellationToken ct = default) => throw Reject(nameof(ResumeServiceRolloutAsync));

        public Task<ServiceCommandAcceptedReceipt> RollbackServiceRolloutAsync(
            RollbackServiceRolloutCommand command,
            CancellationToken ct = default) => throw Reject(nameof(RollbackServiceRolloutAsync));
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Aevatar.Studio.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
