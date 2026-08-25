using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Application.AgentProfiles;
using Aevatar.GAgentService.Application.Responses;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ResponsesOwnedToolCatalogPlannerTests
{
    [Fact]
    public async Task PlanAsync_WhenRouteIsGenuinelyUnprofiled_ShouldReturnRestrictedEmptyCatalog()
    {
        var planner = CreatePlanner(AgentProfileTurnSnapshotResolution.Unprofiled(), profilePlanner: null);

        var result = await planner.PlanAsync(
            Route("workspace.default"),
            "scope-a",
            "turn-a",
            "hello",
            AgentToolExecutionContext.Empty);

        result.IsSuccess.Should().BeTrue();
        result.ProfileSnapshot.Should().BeNull();
        result.ResolvedToolSetName.Should().BeEmpty();
        result.Catalog.Proof.ToolCount.Should().Be(0);
        result.Catalog.Proof.AssertMatchesExactTools([]);
    }

    [Fact]
    public async Task PlanAsync_WhenPublishedProfileIsSelected_ShouldFreezePlannerCatalogAndSnapshot()
    {
        var profile = Profile();
        var exact = new TestTool("web_search");
        var profilePlanner = new StaticProfilePlanner(profile, exact);
        var planner = CreatePlanner(
            AgentProfileTurnSnapshotResolution.Selected(profile),
            profilePlanner);

        var result = await planner.PlanAsync(
            Route(profile.RouteToolSetRef),
            "scope-a",
            "turn-a",
            "search release notes",
            AgentToolExecutionContext.Empty with
            {
                Credentials = new AgentToolCredentials("bearer", null, null),
            });

        result.IsSuccess.Should().BeTrue();
        result.ProfileSnapshot.Should().NotBeSameAs(profile);
        AgentProfileSnapshotCodec.ByteEquivalent(result.ProfileSnapshot!, profile).Should().BeTrue();
        result.ResolvedToolSetName.Should().Be(profile.RouteToolSetRef);
        result.Catalog.ExactTools.Values.Should().ContainSingle().Which.Should().BeSameAs(exact);
        result.Catalog.Proof.AssertMatchesExactTools([exact]);
        profilePlanner.PreparedUserMessage.Should().Be("search release notes");
        profilePlanner.MaterializedAccessToken.Should().Be("bearer");
    }

    [Fact]
    public async Task PlanAsync_ShadowProfile_ShouldObserveCandidateWithoutChangingOwnedModelTools()
    {
        var profile = Profile(AgentProfileActivationMode.Shadow);
        var exact = new TestTool("web_search");
        var profilePlanner = new StaticProfilePlanner(profile, exact);
        var planner = CreatePlanner(
            AgentProfileTurnSnapshotResolution.Selected(profile),
            profilePlanner);

        var result = await planner.PlanAsync(
            Route(profile.RouteToolSetRef),
            "scope-a",
            "turn-shadow",
            "search release notes",
            AgentToolExecutionContext.Empty);

        result.IsSuccess.Should().BeTrue();
        result.ProfileSnapshot.Should().NotBeNull();
        result.Catalog.Proof.ToolCount.Should().Be(0);
        result.Catalog.ExactTools.Should().BeEmpty();
        result.ShadowCandidateProof.Should().NotBeNull();
        result.ShadowCandidateProof!.ToolDescriptors.Should().ContainSingle()
            .Which.Name.Should().Be("web_search");
        profilePlanner.MaterializedAccessToken.Should().BeNull(
            "shadow observation must not enter the executor materialization path");
    }

    [Fact]
    public async Task PlanAsync_WhenRouteAndPinnedProfileToolSetsDiffer_ShouldFailClosed()
    {
        var profile = Profile();
        var planner = CreatePlanner(
            AgentProfileTurnSnapshotResolution.Selected(profile),
            new StaticProfilePlanner(profile, new TestTool("web_search")));

        var result = await planner.PlanAsync(
            Route("nyxid.chat"),
            "scope-a",
            "turn-a",
            "hello",
            AgentToolExecutionContext.Empty);

        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("agent_profile_route_mismatch");
        result.Catalog.Proof.ToolCount.Should().Be(0);
    }

    private static ResponsesOwnedToolCatalogPlanner CreatePlanner(
        AgentProfileTurnSnapshotResolution resolution,
        IAgentProfileTurnToolCatalogPlanner? profilePlanner) =>
        new(
            new StaticSnapshotResolver(resolution),
            profilePlanner,
            NullLogger<ResponsesOwnedToolCatalogPlanner>.Instance);

    private static ChatRouteAction Route(string toolSetName) => new()
    {
        ForwardToModel = new ForwardToModel
        {
            ProfileKind = ChatRouteAgentProfileKind.WorkspaceChat,
            ToolSetRef = new ChatRouteToolSetRef { Name = toolSetName },
        },
    };

    private static AgentProfileSnapshot Profile(
        AgentProfileActivationMode activationMode = AgentProfileActivationMode.Enforced) =>
        AgentProfileSnapshotCodec.Seal(new AgentProfileSnapshot
    {
        ProfileId = "profile-workspace",
        ProfileVersion = "1.0.0",
        AgentKind = "workspace.chat",
        PolicyRevision = "policy-1",
        RouteToolSetRef = "workspace.default",
        PublishedRevision = 4,
        ActivationMode = activationMode,
        MaxOwnedToolCount = 8,
        MaxSchemaBytes = 48 * 1024,
    });

    private sealed class StaticSnapshotResolver(AgentProfileTurnSnapshotResolution resolution)
        : IAgentProfileTurnSnapshotResolver
    {
        public Task<AgentProfileTurnSnapshotResolution> ResolveAsync(
            string scopeId,
            string turnIdentity,
            ChatRouteAgentProfileKind profileKind,
            ChatRouteAgentProfileRef? explicitReference,
            CancellationToken ct = default) => Task.FromResult(resolution);
    }

    private sealed class StaticProfilePlanner(AgentProfileSnapshot profile, IAgentTool exact)
        : IAgentProfileTurnToolCatalogPlanner
    {
        public string? PreparedUserMessage { get; private set; }
        public string? MaterializedAccessToken { get; private set; }

        public Task<AgentProfileTurnAuthorityPreparation> PrepareAsync(
            AgentProfileSnapshot selectedProfile,
            string sessionId,
            string userMessage,
            IReadOnlyList<IAgentTool> registeredTools,
            AgentToolExecutionContext toolContext,
            CancellationToken ct = default)
        {
            PreparedUserMessage = userMessage;
            var authority = new AgentProfileTurnAuthorityState
            {
                ReconciliationKey = new AgentProfileTurnReconciliationKey
                {
                    SessionId = sessionId,
                    Attempt = 1,
                },
                AuthorityKind = AgentProfileTurnAuthorityKind.Selected,
                CandidateRoute = new AgentProfileTurnCandidateRouteIdentity
                {
                    ProfileId = profile.ProfileId,
                    ProfileVersion = profile.ProfileVersion,
                    PolicyRevision = profile.PolicyRevision,
                    IntentId = "web",
                },
            };
            authority.AuthorityCeilingToolNames.Add(exact.Name);
            var shadowProof = selectedProfile.ActivationMode == AgentProfileActivationMode.Shadow
                ? AgentTurnToolCatalogProof.CreateShadowCandidate(
                    [exact],
                    AgentTurnToolCatalogFactory.ResolveProfileBudget(selectedProfile))
                : null;
            return Task.FromResult(AgentProfileTurnAuthorityPreparation.Create(
                authority,
                shadowCandidateProof: shadowProof));
        }

        public Task<AgentTurnToolCatalogMaterialization> MaterializeCommittedAsync(
            AgentProfileSnapshot selectedProfile,
            AgentProfileTurnAuthorityState committedAuthority,
            string? accessToken,
            IReadOnlyList<IAgentTool> registeredTools,
            AgentToolExecutionContext toolContext,
            CancellationToken ct = default)
        {
            MaterializedAccessToken = accessToken;
            var catalog = AgentTurnToolCatalogFactory.CreateForProfile(
                profile,
                [exact.Name],
                "web",
                "web",
                selectedSkillPromptLayer: null,
                diagnostics: [],
                exactTools: [exact]);
            return Task.FromResult(AgentTurnToolCatalogMaterialization.Create(
                catalog,
                committedAuthority));
        }
    }

    private sealed class TestTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Description => name;
        public string ParametersSchema => "{}";
        public bool IsReadOnly => true;
        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }
}
