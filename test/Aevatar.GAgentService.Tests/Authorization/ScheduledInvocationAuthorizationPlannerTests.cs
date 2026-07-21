using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Application.Schedules.Authorization;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.GAgentService.Tests.Authorization;

public sealed class ScheduledInvocationAuthorizationPlannerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-16T08:00:00Z");

    [Fact]
    public async Task PlanAsync_ShouldCanonicalizeServiceAndNodePermissionSets()
    {
        var catalog = new MutableCatalogQueryPort(Snapshot(
            Service("svc-b", "provider-b", AuthorizationGrantRequirement.NotRequired),
            Service("svc-a", "provider-a", AuthorizationGrantRequirement.Required,
                "node-a",
                "node-z")));
        var planner = NewPlanner(catalog);

        var result = await planner.PlanAsync(Request(["svc-b", "svc-a", "svc-a"]));

        result.Success.Should().BeTrue();
        result.Plan!.NyxIdServiceGrants.Select(static grant => grant.UserServiceId)
            .Should().Equal("svc-a", "svc-b");
        result.Plan.NyxIdServiceGrants[0].NodeIds.Should().Equal("node-a", "node-z");
        result.Plan.NyxIdServiceGrants[0].ResourceOwner.Should().BeEquivalentTo(ResourceOwner());
        result.Plan.NyxIdServiceGrants[0].NodeGrantRequirement.Should()
            .Be(AuthorizationGrantRequirement.Required);
        result.Plan.NyxIdServiceGrants[1].NodeIds.Should().BeEmpty();
        result.Plan.NyxIdServiceGrants[1].NodeGrantRequirement.Should()
            .Be(AuthorizationGrantRequirement.NotRequired);
        result.Plan.CredentialPolicy.AllowAllServices.Should().BeFalse();
        result.Plan.CredentialPolicy.AllowAllNodes.Should().BeFalse();
        result.Plan.PermissionDigest.Should().Be(
            ScheduledInvocationAuthorizationPlanner.ComputeDigest(result.Plan));
        JsonFormatter.Default.Format(result.Plan).Should().NotContain("binding");
    }

    [Fact]
    public async Task PlanAsync_ShouldProduceSameDigestForEquivalentPermissionSets()
    {
        var planner = NewPlanner(new MutableCatalogQueryPort(Snapshot(
            Service("svc-a", "provider-a", AuthorizationGrantRequirement.Required,
                "node-a",
                "node-z"),
            Service("svc-b", "provider-b", AuthorizationGrantRequirement.NotRequired))));
        var first = await planner.PlanAsync(Request(["svc-b", "svc-a", "svc-a"]));
        var second = await planner.PlanAsync(Request(["svc-a", "svc-b"]));

        first.Success.Should().BeTrue();
        second.Success.Should().BeTrue();
        first.Plan!.ToByteArray().Should().Equal(second.Plan!.ToByteArray());
        first.Plan.PermissionDigest.Should().Be(second.Plan.PermissionDigest);
    }

    [Fact]
    public async Task PlanAsync_WithSameEvidence_ShouldProduceByteIdenticalPlan()
    {
        var catalog = new MutableCatalogQueryPort(Snapshot(
            Service("svc-a", "provider-a", AuthorizationGrantRequirement.Required,
                "node-a",
                "node-z")));
        var planner = NewPlanner(catalog);
        var request = Request(["svc-a", "svc-a"]);

        var first = await planner.PlanAsync(request);
        var second = await planner.PlanAsync(request);

        first.Success.Should().BeTrue();
        second.Success.Should().BeTrue();
        first.Plan!.ToByteArray().Should().Equal(second.Plan!.ToByteArray());
        first.Plan!.PermissionDigest.Should().Be(second.Plan!.PermissionDigest);
        first.Plan.SchemaVersion.Should().Be("scheduled-invocation-authorization/v2");
        first.Plan.CredentialPolicy.PolicyVersion.Should().Be("nyxid-api-key/scheduled-invocation/v2");
    }

    [Fact]
    public async Task ComputeDigest_ShouldCoverTargetOwnerAuthorityPolicySourceAndDisclosure()
    {
        var planner = NewPlanner(new MutableCatalogQueryPort(Snapshot(
            Service("svc-a", "provider-a", AuthorizationGrantRequirement.NotRequired))));
        var original = (await planner.PlanAsync(Request(["svc-a"]))).Plan!;
        var originalDigest = ScheduledInvocationAuthorizationPlanner.ComputeDigest(original);
        Action<ScheduledInvocationAuthorizationPlan>[] mutations =
        [
            static plan => plan.InvocationTarget.ScheduledAgent.ExecutionScopeId = "scope-other",
            static plan => plan.Owner.OwnerSubject = "owner-other",
            static plan => plan.SourceStamps[0].StateVersion = 17,
            static plan => plan.CatalogAuthority.ActorStateVersion++,
            static plan => plan.CatalogAuthority.ObservedAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(-2)),
            static plan => plan.CatalogAuthority.FreshUntil = Timestamp.FromDateTimeOffset(Now.AddMinutes(30)),
            static plan => plan.CatalogAuthority.ContentDigest = "digest-other",
            static plan => plan.CatalogAuthority.ContractVersion = "contract-other",
            static plan => plan.CatalogAuthority.PolicyVersion = "provider-policy-other",
            static plan => plan.CatalogAuthority.EvaluatedAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(-3)),
            static plan => plan.NyxIdServiceGrants[0].ResourceOwner.OwnerSubject = "resource-owner-other",
            static plan => plan.NyxIdServiceGrants[0].NodeIds.Add("node-other"),
            static plan => plan.CredentialPolicy.ExpiresAt = Timestamp.FromDateTimeOffset(Now.AddDays(31)),
            static plan => plan.CredentialPolicy.PolicyVersion = "policy-other",
            static plan => plan.CredentialPolicy.AllowAllServices = true,
            static plan => plan.Disclosures[0] = ScheduledInvocationDisclosure.BrowserNeverReceivesSecret,
        ];

        foreach (var mutate in mutations)
        {
            var changed = original.Clone();
            mutate(changed);
            ScheduledInvocationAuthorizationPlanner.ComputeDigest(changed).Should().NotBe(
                originalDigest,
                $"mutation {Array.IndexOf(mutations, mutate)} must be integrity-covered");
        }
    }

    [Fact]
    public async Task PlanAsync_ShouldFailClosedForUnknownEvidenceEnums()
    {
        var invalidAccess = Service("svc-a", "provider-a", AuthorizationGrantRequirement.NotRequired);
        invalidAccess.Access = (NyxIdAuthorizationAccess)999;
        var invalidRequirement = Service(
            "svc-a",
            "provider-a",
            AuthorizationGrantRequirement.Required,
            "node-a");
        invalidRequirement.NodeGrantRequirement = (AuthorizationGrantRequirement)999;

        foreach (var service in new[] { invalidAccess, invalidRequirement })
        {
            var result = await NewPlanner(new MutableCatalogQueryPort(Snapshot(service)))
                .PlanAsync(Request(["svc-a"]));

            result.Success.Should().BeFalse();
            result.FailureCode.Should().Be(ScheduledInvocationAuthorizationFailureCode.UnknownEnum);
        }
    }

    [Fact]
    public async Task PlanAsync_ShouldRejectViewOnlyService()
    {
        var service = Service("svc-a", "provider-a", AuthorizationGrantRequirement.NotRequired);
        service.Access = NyxIdAuthorizationAccess.ViewOnly;
        var planner = NewPlanner(new MutableCatalogQueryPort(Snapshot(service)));

        var result = await planner.PlanAsync(Request(["svc-a"]));

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(ScheduledInvocationAuthorizationFailureCode.ServiceAccessDenied);
    }

    [Fact]
    public async Task RevalidateAsync_ShouldFailWhenCurrentCatalogEvidenceChanges()
    {
        var catalog = new MutableCatalogQueryPort(Snapshot(
            Service("svc-a", "provider-a", AuthorizationGrantRequirement.NotRequired)));
        var planner = NewPlanner(catalog);
        var request = Request(["svc-a"]);
        var original = await planner.PlanAsync(request);
        catalog.Snapshot = catalog.Snapshot! with { StateVersion = 8, ContentDigest = "digest-8" };
        var revalidator = new ScheduledInvocationAuthorizationRevalidator(
            planner,
            new FakeTimeProvider(Now));

        var result = await revalidator.RevalidateAsync(
            request,
            ScheduledInvocationAuthorizationConfirmations.FromPlan(original.Plan!));

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(ScheduledInvocationAuthorizationFailureCode.AuthorizationPlanChanged);
    }

    [Fact]
    public async Task RevalidateAsync_ShouldNormalizeMissingCurrentEvidenceToPlanChanged()
    {
        var catalog = new MutableCatalogQueryPort(Snapshot(
            Service("svc-a", "provider-a", AuthorizationGrantRequirement.NotRequired)));
        var planner = NewPlanner(catalog);
        var request = Request(["svc-a"]);
        var original = await planner.PlanAsync(request);
        catalog.Snapshot = null;
        var revalidator = new ScheduledInvocationAuthorizationRevalidator(
            planner,
            new FakeTimeProvider(Now));

        var result = await revalidator.RevalidateAsync(
            request,
            ScheduledInvocationAuthorizationConfirmations.FromPlan(original.Plan!));

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(ScheduledInvocationAuthorizationFailureCode.AuthorizationPlanChanged);
        result.Detail.Should().Be("nyxid_catalog_snapshot_not_found");
    }

    [Fact]
    public async Task RevalidateAsync_ShouldReadCurrentCatalogExactlyOnce()
    {
        var catalog = new MutableCatalogQueryPort(Snapshot(
            Service("svc-a", "provider-a", AuthorizationGrantRequirement.NotRequired)));
        var planner = NewPlanner(catalog);
        var request = Request(["svc-a"]);
        var original = await planner.PlanAsync(request);
        catalog.QueryCount = 0;
        var revalidator = new ScheduledInvocationAuthorizationRevalidator(
            planner,
            new FakeTimeProvider(Now));

        var result = await revalidator.RevalidateAsync(
            request,
            ScheduledInvocationAuthorizationConfirmations.FromPlan(original.Plan!));

        result.Success.Should().BeTrue();
        catalog.QueryCount.Should().Be(1);
    }

    [Fact]
    public async Task PlanAsync_ForScheduledAgent_ShouldComposeOwnerLlmEvidenceFromExecutionScope()
    {
        var evidence = new StudioEvidencePorts
        {
            OwnerLLM = new ScheduledInvocationOwnerLLMEvidence(
                11,
                string.Empty,
                "provider-b",
                AuthorizationGrantRequirement.Required),
        };
        var planner = new ScheduledInvocationAuthorizationPlanner(
            new MutableCatalogQueryPort(Snapshot(
                Service("svc-a", "provider-a", AuthorizationGrantRequirement.NotRequired),
                Service("svc-b", "provider-b", AuthorizationGrantRequirement.NotRequired))),
            ownerLLMQueryPort: evidence);

        var result = await planner.PlanAsync(Request(["svc-a"]));

        result.Success.Should().BeTrue();
        result.Plan!.NyxIdServiceGrants.Select(static grant => grant.UserServiceId)
            .Should().Equal("svc-a", "svc-b");
        result.Plan.SourceStamps.Select(static stamp => (stamp.SourceKind, stamp.SourceId, stamp.StateVersion))
            .Should().Equal(
                (AuthorizationSourceKind.ScheduledAgentRegistration, "agent-alpha", 0),
                (AuthorizationSourceKind.OwnerLlmRoute, "scope-execution", 11));
        evidence.LastOwnerLLMScopeId.Should().Be("scope-execution");
    }

    [Fact]
    public async Task PlanAsync_ForStudioTarget_ShouldComposeStaticWorkflowAndOwnerLlmEvidence()
    {
        var evidence = new StudioEvidencePorts
        {
            Member = new ScheduledInvocationMemberEvidence(3, "wf-alpha", "rev-alpha", "svc-alpha"),
            Workflow = new ScheduledInvocationWorkflowEvidence(
                5,
                ["calendar"],
                true,
                [],
                ["provider-a"],
                AuthorizationGrantRequirement.Required),
            Connector = new ScheduledInvocationConnectorEvidence(7, ["calendar"]),
            OwnerLLM = new ScheduledInvocationOwnerLLMEvidence(
                11,
                string.Empty,
                "provider-b",
                AuthorizationGrantRequirement.Required),
        };
        var planner = new ScheduledInvocationAuthorizationPlanner(
            new MutableCatalogQueryPort(Snapshot(
                Service("nyx-service-a", "provider-a", AuthorizationGrantRequirement.NotRequired),
                Service("nyx-service-b", "provider-b", AuthorizationGrantRequirement.NotRequired))),
            evidence,
            evidence,
            evidence,
            evidence);

        var result = await planner.PlanAsync(StudioRequest());

        result.Success.Should().BeTrue();
        result.Plan!.NyxIdServiceGrants.Select(static grant => grant.UserServiceId)
            .Should().Equal("nyx-service-a", "nyx-service-b");
        result.Plan.SourceStamps.Select(static stamp => (stamp.SourceKind, stamp.SourceId, stamp.StateVersion))
            .Should().Equal(
                (AuthorizationSourceKind.StudioMember, "m-alpha", 3),
                (AuthorizationSourceKind.WorkflowRevision, "rev-alpha", 5),
                (AuthorizationSourceKind.ConnectorCatalog, "scope-alpha", 7),
                (AuthorizationSourceKind.OwnerLlmRoute, "scope-alpha", 11));
    }

    [Fact]
    public async Task PlanAsync_ForStudioTarget_ShouldNotRequireIrrelevantConnectorOrLlmDocuments()
    {
        var evidence = new StudioEvidencePorts
        {
            Member = new ScheduledInvocationMemberEvidence(3, "wf-alpha", "rev-alpha", "svc-alpha"),
            Workflow = new ScheduledInvocationWorkflowEvidence(
                5,
                [],
                false,
                [],
                [],
                AuthorizationGrantRequirement.NotRequired),
        };
        var planner = new ScheduledInvocationAuthorizationPlanner(
            new MutableCatalogQueryPort(Snapshot()),
            evidence,
            evidence,
            evidence,
            evidence);

        var result = await planner.PlanAsync(StudioRequest());

        result.Success.Should().BeTrue();
        result.Plan!.NyxIdServiceGrants.Should().BeEmpty();
        evidence.ConnectorQueries.Should().Be(0);
        evidence.OwnerLLMQueries.Should().Be(0);
    }

    [Fact]
    public void Registration_ShouldResolveWithoutStudioEvidenceAdapters()
    {
        var services = new ServiceCollection();
        services.AddSingleton<INyxIdAuthorizationCatalogQueryPort>(
            new MutableCatalogQueryPort(Snapshot()));
        services.AddScheduledInvocationAuthorization();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        provider.GetRequiredService<IScheduledInvocationAuthorizationPlanner>()
            .Should().BeOfType<ScheduledInvocationAuthorizationPlanner>();
    }

    private static ScheduledInvocationAuthorizationPlanner NewPlanner(
        INyxIdAuthorizationCatalogQueryPort catalog) =>
        new(catalog, MissingEvidencePorts.Instance, MissingEvidencePorts.Instance,
            MissingEvidencePorts.Instance, NoServiceOwnerLLMEvidencePort.Instance);

    private static ScheduledInvocationAuthorizationRequest Request(IReadOnlyList<string> serviceIds) =>
        new(
            new ScheduledInvocationTarget
            {
                ScheduledAgent = new ScheduledAgentInvocationTarget
                {
                    RegistrationScopeId = "scope-registration",
                    ExecutionScopeId = "scope-execution",
                    ScheduledAgentId = "agent-alpha",
                },
            },
            new AuthenticatedAuthorizationOwnerContext(
                Owner(), "lark", "tenant-a", "sender-a", "binding-a"),
            serviceIds,
            [],
            AuthorizationGrantRequirement.Required,
            Now.AddDays(30),
            Now,
            [new AuthorizationSourceStamp
            {
                SourceKind = AuthorizationSourceKind.ScheduledAgentRegistration,
                SourceId = "agent-alpha",
            }]);

    private static ScheduledInvocationAuthorizationRequest StudioRequest() =>
        new(
            new ScheduledInvocationTarget
            {
                StudioMember = new StudioMemberInvocationTarget
                {
                    ScopeId = "scope-alpha",
                    TeamId = "team-alpha",
                    MemberId = "m-alpha",
                    PublishedServiceId = "svc-alpha",
                    DraftWorkflowId = "wf-alpha",
                    WorkflowRevisionId = "rev-alpha",
                },
            },
            new AuthenticatedAuthorizationOwnerContext(
                Owner(), "lark", "tenant-alpha", "sender-alpha", "binding-alpha"),
            [],
            [],
            AuthorizationGrantRequirement.NotRequired,
            Now.AddDays(30),
            Now);

    private static NyxIdAuthorizationCatalogSnapshot Snapshot(
        params NyxIdAuthorizationServiceEvidence[] services) =>
        new(
            Owner(),
            7,
            Now.AddMinutes(-1),
            Now.AddMinutes(15),
            "1",
            "api-key-scope-v1",
            Now.AddMinutes(-2),
            "digest-7",
            services);

    private static AuthorizationOwnerIdentity Owner() => new()
    {
        Authority = NyxIdAuthorizationAuthorities.NyxId,
        OwnerKind = AuthorizationOwnerKind.Personal,
        OwnerSubject = "user-alpha",
    };

    private static AuthorizationOwnerIdentity ResourceOwner() => new()
    {
        Authority = NyxIdAuthorizationAuthorities.NyxId,
        OwnerKind = AuthorizationOwnerKind.Organization,
        OwnerSubject = "org-alpha",
    };

    private static NyxIdAuthorizationServiceEvidence Service(
        string id,
        string slug,
        AuthorizationGrantRequirement nodeRequirement,
        params string[] nodeIds)
    {
        var service = new NyxIdAuthorizationServiceEvidence
        {
            UserServiceId = id,
            ServiceSlug = slug,
            DisplayName = slug,
            Access = NyxIdAuthorizationAccess.Permitted,
            NodeGrantRequirement = nodeRequirement,
            ResourceOwner = ResourceOwner(),
        };
        service.NodeIds.Add(nodeIds);
        return service;
    }

    private sealed class MutableCatalogQueryPort(NyxIdAuthorizationCatalogSnapshot? snapshot)
        : INyxIdAuthorizationCatalogQueryPort
    {
        public NyxIdAuthorizationCatalogSnapshot? Snapshot { get; set; } = snapshot;
        public int QueryCount { get; set; }

        public Task<NyxIdAuthorizationCatalogSnapshot?> GetAsync(
            AuthorizationOwnerIdentity owner,
            CancellationToken ct = default)
        {
            QueryCount++;
            return Task.FromResult(Snapshot);
        }
    }

    private sealed class StudioEvidencePorts :
        IScheduledInvocationMemberEvidenceQueryPort,
        IScheduledInvocationWorkflowEvidenceQueryPort,
        IScheduledInvocationConnectorEvidenceQueryPort,
        IScheduledInvocationOwnerLLMEvidenceQueryPort
    {
        public ScheduledInvocationMemberEvidence? Member { get; init; }
        public ScheduledInvocationWorkflowEvidence? Workflow { get; init; }
        public ScheduledInvocationConnectorEvidence? Connector { get; init; }
        public ScheduledInvocationOwnerLLMEvidence? OwnerLLM { get; init; }
        public int ConnectorQueries { get; private set; }
        public int OwnerLLMQueries { get; private set; }
        public string? LastOwnerLLMScopeId { get; private set; }

        Task<ScheduledInvocationMemberEvidence?> IScheduledInvocationMemberEvidenceQueryPort.GetAsync(
            string scopeId, string memberId, CancellationToken ct) => Task.FromResult(Member);

        Task<ScheduledInvocationWorkflowEvidence?> IScheduledInvocationWorkflowEvidenceQueryPort.GetAsync(
            string scopeId,
            string publishedServiceId,
            string workflowRevisionId,
            CancellationToken ct) => Task.FromResult(Workflow);

        Task<ScheduledInvocationConnectorEvidence?> IScheduledInvocationConnectorEvidenceQueryPort.GetAsync(
            string scopeId, CancellationToken ct)
        {
            ConnectorQueries++;
            return Task.FromResult(Connector);
        }

        Task<ScheduledInvocationOwnerLLMEvidence?> IScheduledInvocationOwnerLLMEvidenceQueryPort.GetAsync(
            string scopeId, CancellationToken ct)
        {
            OwnerLLMQueries++;
            LastOwnerLLMScopeId = scopeId;
            return Task.FromResult(OwnerLLM);
        }
    }

    private sealed class MissingEvidencePorts :
        IScheduledInvocationMemberEvidenceQueryPort,
        IScheduledInvocationWorkflowEvidenceQueryPort,
        IScheduledInvocationConnectorEvidenceQueryPort,
        IScheduledInvocationOwnerLLMEvidenceQueryPort
    {
        public static readonly MissingEvidencePorts Instance = new();

        Task<ScheduledInvocationMemberEvidence?> IScheduledInvocationMemberEvidenceQueryPort.GetAsync(
            string scopeId, string memberId, CancellationToken ct) => Task.FromResult<ScheduledInvocationMemberEvidence?>(null);

        Task<ScheduledInvocationWorkflowEvidence?> IScheduledInvocationWorkflowEvidenceQueryPort.GetAsync(
            string scopeId,
            string publishedServiceId,
            string workflowRevisionId,
            CancellationToken ct) => Task.FromResult<ScheduledInvocationWorkflowEvidence?>(null);

        Task<ScheduledInvocationConnectorEvidence?> IScheduledInvocationConnectorEvidenceQueryPort.GetAsync(
            string scopeId, CancellationToken ct) => Task.FromResult<ScheduledInvocationConnectorEvidence?>(null);

        Task<ScheduledInvocationOwnerLLMEvidence?> IScheduledInvocationOwnerLLMEvidenceQueryPort.GetAsync(
            string scopeId, CancellationToken ct) => Task.FromResult<ScheduledInvocationOwnerLLMEvidence?>(null);
    }

    private sealed class NoServiceOwnerLLMEvidencePort : IScheduledInvocationOwnerLLMEvidenceQueryPort
    {
        public static readonly NoServiceOwnerLLMEvidencePort Instance = new();

        public Task<ScheduledInvocationOwnerLLMEvidence?> GetAsync(
            string scopeId,
            CancellationToken ct = default) =>
            Task.FromResult<ScheduledInvocationOwnerLLMEvidence?>(new ScheduledInvocationOwnerLLMEvidence(
                0,
                string.Empty,
                string.Empty,
                AuthorizationGrantRequirement.NotRequired));
    }
}
