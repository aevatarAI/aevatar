using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgents.StatusDashboard;
using Aevatar.GAgents.StatusDashboard.Executors;
using Aevatar.Mainnet.Host.Api.Status;
using Aevatar.Presentation.AGUI;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Foundation.Abstractions.Connectors;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using System.Net;

namespace Aevatar.Hosting.Tests;

public sealed class ResponsesForwardTeamInternalProbeExecutorTests
{
    [Fact]
    public async Task RoutePolicyStage_ReturnsOk_WhenResponsesRouteForwardsToExpectedTeam()
    {
        var executor = NewExecutor(
            policy: new ChatRoutePolicySnapshot(
                new ChatRouteAction
                {
                    ForwardToTeam = new ForwardToTeam
                    {
                        TeamId = "team-1",
                        EndpointId = "chat",
                    },
                },
                rules: []));

        var outcome = await executor.ProbeAsync(
            Descriptor("route-policy"),
            CancellationToken.None);

        outcome.Status.Should().Be(HealthOutcomeStatus.Ok);
        outcome.ObservedAt.Should().NotBeNull();
        outcome.Detail.Should().Be("forward_to_team:team-1/chat");
    }

    [Fact]
    public async Task TeamEntryMemberStage_ReturnsOk_WhenResolverMatchesExpectedMember()
    {
        var executor = NewExecutor();

        var outcome = await executor.ProbeAsync(
            Descriptor("team-entry-member"),
            CancellationToken.None);

        outcome.Status.Should().Be(HealthOutcomeStatus.Ok);
        outcome.Detail.Should().Be("entry_member:member-1");
    }

    [Fact]
    public async Task MemberBindingStage_ReturnsOk_WhenLastBindingMatchesPublishedService()
    {
        var executor = NewExecutor();

        var outcome = await executor.ProbeAsync(
            Descriptor("member-binding"),
            CancellationToken.None);

        outcome.Status.Should().Be(HealthOutcomeStatus.Ok);
        outcome.Detail.Should().Be("binding:rev-1");
    }

    [Fact]
    public async Task DirectTeamInvokeStage_ReturnsOk_AndUsesStaticInvocationPort()
    {
        var invocation = new RecordingInvocationPort();
        var executor = NewExecutor(invocation: invocation);

        var outcome = await executor.ProbeAsync(
            Descriptor("direct-team-invoke"),
            CancellationToken.None);

        outcome.Status.Should().Be(HealthOutcomeStatus.Ok);
        outcome.Detail.Should().Be("invoke:runfinished");
        invocation.LastRequest.Should().NotBeNull();
        invocation.LastRequest!.Identity.ServiceId.Should().Be("member-member-1");
        invocation.LastRequest.EndpointId.Should().Be("chat");
        invocation.LastRequest.Input.Headers.Should().NotContainKey("nyxid.access_token");
    }

    [Fact]
    public async Task DirectTeamInvokeStage_ReturnsDown_WhenRunErrorIsEmitted()
    {
        var executor = NewExecutor(invocation: new RecordingInvocationPort(emitRunError: true));

        var outcome = await executor.ProbeAsync(
            Descriptor("direct-team-invoke"),
            CancellationToken.None);

        outcome.Status.Should().Be(HealthOutcomeStatus.Down);
        outcome.Detail.Should().Be("exception");
        outcome.ErrorMessage.Should().Contain("probe run error");
    }

    [Fact]
    public async Task DirectTeamInvokeStage_InjectsDynamicBearer_WhenAuthIsConfigured()
    {
        var invocation = new RecordingInvocationPort();
        var executor = NewExecutor(
            invocation: invocation,
            httpHandler: req => req.RequestUri?.AbsoluteUri == "https://nyx.example.test/oauth/token"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {"access_token":"fresh-access","token_type":"Bearer","expires_in":3600}
                    """),
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        var descriptor = Descriptor("direct-team-invoke");
        descriptor.Parameters["Auth.TokenEndpoint"] = "https://nyx.example.test/oauth/token";
        descriptor.Parameters["Auth.ClientIdConfigurationKey"] = "Aevatar:Status:ResponsesForwardToTeam:ClientId";
        descriptor.Parameters["Auth.ClientSecretConfigurationKey"] = "Aevatar:Status:ResponsesForwardToTeam:ClientSecret";

        var outcome = await executor.ProbeAsync(descriptor, CancellationToken.None);

        outcome.Status.Should().Be(HealthOutcomeStatus.Ok);
        invocation.LastRequest!.Input.Headers.Should().ContainKey("nyxid.access_token")
            .WhoseValue.Should().Be("fresh-access");
        invocation.LastRequest.Input.Headers.Should().ContainKey(ConnectorRequest.HttpAuthorizationMetadataKey)
            .WhoseValue.Should().Be("Bearer fresh-access");
    }

    private static ResponsesForwardTeamInternalProbeExecutor NewExecutor(
        ChatRoutePolicySnapshot? policy = null,
        IStaticGAgentStreamInvocationPort<AGUIEvent>? invocation = null,
        Func<HttpRequestMessage, HttpResponseMessage>? httpHandler = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:Status:ResponsesForwardToTeam:BearerToken"] = "agent-key",
                ["Aevatar:Status:ResponsesForwardToTeam:ClientId"] = "client-id",
                ["Aevatar:Status:ResponsesForwardToTeam:ClientSecret"] = "client-secret",
            })
            .Build();
        var httpClientFactory = new TestHttpClientFactory(
            new DelegateHandler(req => Task.FromResult(
                httpHandler?.Invoke(req) ?? new HttpResponseMessage(HttpStatusCode.NotFound))));

        return new ResponsesForwardTeamInternalProbeExecutor(
            new StaticPolicyQueryPort(policy ?? new ChatRoutePolicySnapshot(
                new ChatRouteAction
                {
                    ForwardToTeam = new ForwardToTeam { TeamId = "team-1", EndpointId = "chat" },
                },
                rules: [])),
            new ChatRouteResolver(new StaticFallbackProvider()),
            new TeamQueryPort(NewTeam()),
            new MemberQueryPort(NewMember()),
            new Resolver(),
            invocation ?? new RecordingInvocationPort(),
            new StatusProbeAuthorizationResolver(httpClientFactory, configuration));
    }

    private static HealthProbeTargetDescriptor Descriptor(string stage)
    {
        var descriptor = new HealthProbeTargetDescriptor
        {
            Slug = $"responses-forward-team-{stage}",
            DisplayName = stage,
            Category = "feature",
            ProbeKind = ResponsesForwardTeamInternalProbeExecutor.ProbeKind,
            IntervalSeconds = 300,
            TimeoutMs = 45_000,
            Enabled = true,
        };
        descriptor.Parameters["Stage"] = stage;
        descriptor.Parameters["ScopeId"] = "scope-1";
        descriptor.Parameters["TeamId"] = "team-1";
        descriptor.Parameters["MemberId"] = "member-1";
        descriptor.Parameters["PublishedServiceId"] = "member-member-1";
        descriptor.Parameters["EndpointId"] = "chat";
        descriptor.Parameters["Model"] = "deepseek/deepseek-chat";
        descriptor.Parameters["Prompt"] = "status probe";
        return descriptor;
    }

    private static StudioTeamSummaryResponse NewTeam() =>
        new(
            TeamId: "team-1",
            ScopeId: "scope-1",
            DisplayName: "Team",
            Description: string.Empty,
            LifecycleStage: TeamLifecycleStageNames.Active,
            MemberCount: 1,
            CreatedAt: DateTimeOffset.UnixEpoch,
            UpdatedAt: DateTimeOffset.UnixEpoch)
        {
            EntryMemberId = "member-1",
        };

    private static StudioMemberDetailResponse NewMember()
    {
        var summary = new StudioMemberSummaryResponse(
            MemberId: "member-1",
            ScopeId: "scope-1",
            DisplayName: "Member",
            Description: string.Empty,
            ImplementationKind: MemberImplementationKindNames.GAgent,
            LifecycleStage: MemberLifecycleStageNames.BindReady,
            PublishedServiceId: "member-member-1",
            LastBoundRevisionId: "rev-1",
            CreatedAt: DateTimeOffset.UnixEpoch,
            UpdatedAt: DateTimeOffset.UnixEpoch)
        {
            TeamId = "team-1",
        };

        return new StudioMemberDetailResponse(
            summary,
            ImplementationRef: null,
            LastBinding: new StudioMemberBindingContractResponse(
                PublishedServiceId: "member-member-1",
                RevisionId: "rev-1",
                ImplementationKind: MemberImplementationKindNames.GAgent,
                BoundAt: DateTimeOffset.UnixEpoch));
    }

    private sealed class StaticPolicyQueryPort(ChatRoutePolicySnapshot? policy) : IChatRoutePolicyQueryPort
    {
        public Task<ChatRoutePolicySnapshot?> LookupForCallerAsync(
            OwnerScope callerScope,
            CancellationToken ct = default) =>
            Task.FromResult(policy);
    }

    private sealed class StaticFallbackProvider : IChatRouteFallbackProvider
    {
        public ChatRouteDecision GetFallbackDecision() =>
            new()
            {
                Action = new ChatRouteAction { ForwardToModel = new ForwardToModel { ModelName = "fallback" } },
                UsedFallback = true,
            };
    }

    private sealed class TeamQueryPort(StudioTeamSummaryResponse? team) : IStudioTeamQueryPort
    {
        public Task<StudioTeamRosterResponse> ListAsync(
            string scopeId,
            StudioTeamRosterPageRequest? page = null,
            CancellationToken ct = default) =>
            Task.FromResult(new StudioTeamRosterResponse(scopeId, team is null ? [] : [team]));

        public Task<StudioTeamSummaryResponse?> GetAsync(
            string scopeId,
            string teamId,
            CancellationToken ct = default) =>
            Task.FromResult(team);
    }

    private sealed class MemberQueryPort(StudioMemberDetailResponse? member) : IStudioMemberQueryPort
    {
        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId,
            StudioMemberRosterPageRequest? page = null,
            CancellationToken ct = default) =>
            Task.FromResult(new StudioMemberRosterResponse(
                scopeId,
                member is null ? [] : [member.Summary]));

        public Task<StudioMemberDetailResponse?> GetAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default) =>
            Task.FromResult(member);
    }

    private sealed class Resolver : ITeamEntryMemberResolver
    {
        public Task<TeamEntryMemberResolution> ResolveAsync(
            string scopeId,
            string teamId,
            CancellationToken ct = default) =>
            Task.FromResult(new TeamEntryMemberResolution(
                ScopeId: scopeId,
                TeamId: teamId,
                EntryMemberId: "member-1",
                PublishedServiceId: "member-member-1"));
    }

    private sealed class RecordingInvocationPort(bool emitRunError = false)
        : IStaticGAgentStreamInvocationPort<AGUIEvent>
    {
        public StaticGAgentStreamInvocationRequest? LastRequest { get; private set; }

        public async Task<StaticGAgentStreamInvocationResult> InvokeAsync(
            StaticGAgentStreamInvocationRequest request,
            Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
            Func<StaticGAgentStreamAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            LastRequest = request;
            if (emitRunError)
            {
                await emitAsync(new AGUIEvent
                {
                    RunError = new RunErrorEvent { Message = "probe run error" },
                }, ct);
            }

            return new StaticGAgentStreamInvocationResult(
                Accepted: new StaticGAgentStreamAcceptedReceipt(
                    new ServiceInvocationAcceptedReceipt
                    {
                        CommandId = "cmd-1",
                        CorrelationId = "corr-1",
                        TargetActorId = "actor-1",
                        EndpointId = request.EndpointId,
                    },
                    new GAgentDraftRunAcceptedReceipt(
                        ActorId: "actor-1",
                        ActorTypeName: "RoleGAgent",
                        CommandId: "cmd-1",
                        CorrelationId: "corr-1")),
                StartError: GAgentDraftRunStartError.None,
                CompletionStatus: GAgentDraftRunCompletionStatus.RunFinished,
                CompletionObserved: true);
        }
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            await handler(request);
    }
}
