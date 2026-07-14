using Aevatar.Studio.Application.Authorization;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.Studio.Tests;

public sealed class ScheduledInvocationAuthorizationPlannerTests
{
    [Fact]
    public async Task PlanAsync_NormalizesExactGrantsAndSeparatesInvocationIdentity()
    {
        var now = DateTimeOffset.Parse("2026-07-14T10:00:00Z");
        var owner = Owner("subject-personal");
        var snapshot = new NyxIdCatalogSnapshot(
            owner, 17, now.AddMinutes(-1), now.AddMinutes(14), "etag-1", "catalog-digest-1",
            [Service("us-connector", ("node-fallback", false), ("node-primary", true))]);
        var planner = CreatePlanner(new StubSnapshotQueryPort(snapshot));

        var result = await planner.PlanAsync(Request(owner, now));

        result.Success.Should().BeTrue();
        result.Plan!.InvocationTarget.Studio.MemberId.Should().Be("m-alpha");
        result.Plan.InvocationTarget.Studio.WorkflowId.Should().Be("wf-alpha");
        result.Plan.InvocationTarget.Studio.PublishedServiceId.Should().Be("svc-alpha");
        result.Plan.NyxIdServiceGrants.Single().UserServiceId.Should().Be("us-connector");
        result.Plan.NyxIdServiceGrants.Single().NodeGrants.Select(static node => node.NodeId)
            .Should().Equal("node-primary", "node-fallback");
        result.Plan.Disclosure.BrowserReceivesRawKey.Should().BeFalse();
    }

    [Fact]
    public async Task ComputeDigest_IsStableAndIncludesEveryPermissionBearingField()
    {
        var now = DateTimeOffset.Parse("2026-07-14T10:00:00Z");
        var owner = Owner("subject-personal");
        var snapshot = new NyxIdCatalogSnapshot(
            owner, 17, now.AddMinutes(-1), now.AddMinutes(14), "etag-1", "catalog-digest-1",
            [Service("us-connector", ("node-primary", true))]);
        var result = await CreatePlanner(new StubSnapshotQueryPort(snapshot))
            .PlanAsync(Request(owner, now));
        var plan = result.Plan!;
        var baseline = ScheduledInvocationAuthorizationPlanner.ComputeDigest(plan);

        ScheduledInvocationAuthorizationPlanner.ComputeDigest(plan.Clone()).Should().Be(baseline);

        var changedServiceGrant = plan.Clone();
        changedServiceGrant.NyxIdServiceGrants[0].UserServiceId = "us-different";
        var changedNodeGrant = plan.Clone();
        changedNodeGrant.NyxIdServiceGrants[0].NodeGrants[0].NodeId = "node-different";
        var changedOwner = plan.Clone();
        changedOwner.Owner.OwnerSubject = "subject-different";
        var changedCredentialPolicy = plan.Clone();
        changedCredentialPolicy.CredentialPolicy.Scopes = "read";
        var changedAuthorityVersion = plan.Clone();
        changedAuthorityVersion.Authority.MemberStateVersion++;

        new[]
        {
            changedServiceGrant,
            changedNodeGrant,
            changedOwner,
            changedCredentialPolicy,
            changedAuthorityVersion,
        }.Select(ScheduledInvocationAuthorizationPlanner.ComputeDigest)
            .Should().OnlyContain(digest => digest != baseline);
    }

    [Fact]
    public async Task PlanAsync_WhenSnapshotIsStale_FailsClosed()
    {
        var now = DateTimeOffset.Parse("2026-07-14T10:00:00Z");
        var owner = Owner("subject-personal");
        var snapshot = new NyxIdCatalogSnapshot(
            owner, 17, now.AddMinutes(-20), now, "", "catalog-digest-1", [Service("us-connector", ("node-primary", true))]);

        var result = await CreatePlanner(new StubSnapshotQueryPort(snapshot))
            .PlanAsync(Request(owner, now));

        result.FailureCode.Should().Be(ScheduledInvocationAuthorizationFailureCode.SnapshotStale);
    }

    [Fact]
    public async Task PlanAsync_WhenServiceGrantsAreExplicitlyNotRequired_ReturnsStableEmptyGrantPlan()
    {
        var now = DateTimeOffset.Parse("2026-07-14T10:00:00Z");
        var owner = Owner("subject-personal");
        var snapshot = new NyxIdCatalogSnapshot(
            owner, 17, now.AddMinutes(-1), now.AddMinutes(14), "etag-1", "catalog-digest-1", []);
        var request = Request(owner, now);
        var planner = CreatePlanner(
            new StubSnapshotQueryPort(snapshot),
            Dependencies(WorkflowServiceGrantPolicy.NotRequiredNoExternalService));

        var first = await planner.PlanAsync(request);
        var second = await planner.PlanAsync(request);

        first.Success.Should().BeTrue();
        first.Plan!.NyxIdServiceGrants.Should().BeEmpty();
        first.Plan.CredentialPolicy.ServiceGrantsNotRequired.Should().BeTrue();
        first.Plan.PermissionDigest.Should().Be(second.Plan!.PermissionDigest);
        first.Plan.PermissionDigest.Should().Be(ScheduledInvocationAuthorizationPlanner.ComputeDigest(first.Plan));
    }

    [Fact]
    public async Task PlanAsync_WhenInvocationTargetIsMissing_FailsBeforeSnapshotQuery()
    {
        var now = DateTimeOffset.Parse("2026-07-14T10:00:00Z");
        var request = Request(Owner("subject-personal"), now) with
        {
            InvocationTarget = new ScheduledInvocationTarget(),
        };
        var snapshotQueryPort = new StubSnapshotQueryPort(null);

        var result = await CreatePlanner(snapshotQueryPort).PlanAsync(request);

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(ScheduledInvocationAuthorizationFailureCode.OwnerMismatch);
        result.Detail.Should().Be("invocation_target_missing");
        snapshotQueryPort.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("missing-member", ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound, "member_current_state_not_found")]
    [InlineData("stale-member", ScheduledInvocationAuthorizationFailureCode.SnapshotStale, "member_current_state_stale")]
    [InlineData("missing-workflow", ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound, "workflow_current_state_not_found")]
    [InlineData("missing-connector", ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound, "connector_current_state_not_found")]
    [InlineData("missing-owner-llm", ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound, "owner_llm_current_state_not_found")]
    public async Task PlanAsync_WhenAuthorityReadModelIsMissingOrStale_FailsBeforeCatalogQuery(
        string scenario,
        ScheduledInvocationAuthorizationFailureCode expectedCode,
        string expectedDetail)
    {
        var now = DateTimeOffset.Parse("2026-07-14T10:00:00Z");
        var snapshotQueryPort = new StubSnapshotQueryPort(null);
        var memberQueryPort = new StubMemberQueryPort(scenario switch
        {
            "missing-member" => null,
            "stale-member" => new ScheduledInvocationMemberFact(3, "wf-alpha", "rev-stale", "svc-alpha"),
            _ => ValidMember(),
        });
        var workflowQueryPort = new StubWorkflowQueryPort(
            scenario == "missing-workflow" ? null : ValidWorkflow());
        var connectorQueryPort = new StubConnectorQueryPort(
            scenario == "missing-connector" ? null : new ScheduledInvocationVersionFact(7));
        var ownerLLMQueryPort = new StubOwnerLLMQueryPort(
            scenario == "missing-owner-llm" ? null : new ScheduledInvocationVersionFact(11));
        var planner = new ScheduledInvocationAuthorizationPlanner(
            snapshotQueryPort,
            memberQueryPort,
            workflowQueryPort,
            connectorQueryPort,
            ownerLLMQueryPort);

        var result = await planner.PlanAsync(Request(Owner("subject-personal"), now));

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(expectedCode);
        result.Detail.Should().Be(expectedDetail);
        snapshotQueryPort.CallCount.Should().Be(0);
        workflowQueryPort.CallCount.Should().Be(scenario is "missing-member" or "stale-member" ? 0 : 1);
        connectorQueryPort.CallCount.Should().Be(
            scenario is "missing-member" or "stale-member" or "missing-workflow" ? 0 : 1);
        ownerLLMQueryPort.CallCount.Should().Be(
            scenario is "missing-member" or "stale-member" or "missing-workflow" or "missing-connector" ? 0 : 1);
    }

    [Fact]
    public async Task PlanAsync_WhenWorkflowServiceGrantPolicyIsUnspecified_FailsClosed()
    {
        var now = DateTimeOffset.Parse("2026-07-14T10:00:00Z");
        var owner = Owner("subject-personal");
        var snapshotQueryPort = new StubSnapshotQueryPort(new NyxIdCatalogSnapshot(
            owner, 17, now.AddMinutes(-1), now.AddMinutes(14), "etag-1", "catalog-digest-1",
            [Service("us-connector", ("node-primary", true))]));

        var result = await CreatePlanner(
                snapshotQueryPort,
                Dependencies(WorkflowServiceGrantPolicy.Unspecified))
            .PlanAsync(Request(owner, now));

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(ScheduledInvocationAuthorizationFailureCode.ServiceNotFound);
        result.Detail.Should().Be("workflow_service_grant_policy_missing");
        snapshotQueryPort.CallCount.Should().Be(1);
    }

    [Theory]
    [InlineData("incomplete-context", ScheduledInvocationAuthorizationFailureCode.OwnerMismatch, "authenticated_owner_context_incomplete")]
    [InlineData("missing-snapshot", ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound, "nyxid_catalog_snapshot_not_found")]
    [InlineData("owner-mismatch", ScheduledInvocationAuthorizationFailureCode.OwnerMismatch, "nyxid_catalog_owner_mismatch")]
    [InlineData("missing-service", ScheduledInvocationAuthorizationFailureCode.ServiceNotFound, "nyxid_service_not_found:us-connector")]
    [InlineData("ambiguous-slug", ScheduledInvocationAuthorizationFailureCode.ServiceNotFound, "nyxid_service_slug_not_unique:connector")]
    [InlineData("unreachable-service", ScheduledInvocationAuthorizationFailureCode.OwnerMismatch, "nyxid_service_unreachable:us-connector")]
    [InlineData("missing-node-grant", ScheduledInvocationAuthorizationFailureCode.NodeGrantMissing, "nyxid_node_grant_missing:us-connector")]
    [InlineData("empty-grants", ScheduledInvocationAuthorizationFailureCode.ServiceNotFound, "nyxid_service_grants_empty")]
    public async Task PlanAsync_WhenAuthorizationEvidenceIsInvalid_FailsClosed(
        string scenario,
        ScheduledInvocationAuthorizationFailureCode expectedCode,
        string expectedDetail)
    {
        var now = DateTimeOffset.Parse("2026-07-14T10:00:00Z");
        var owner = Owner("subject-personal");
        var request = Request(owner, now);
        NyxIdCatalogSnapshot? snapshot = new(
            owner, 17, now.AddMinutes(-1), now.AddMinutes(14), "etag-1", "catalog-digest-1",
            [Service("us-connector", ("node-primary", true))]);

        switch (scenario)
        {
            case "incomplete-context":
                request.OwnerContext.VerifiedBindingId = string.Empty;
                break;
            case "missing-snapshot":
                snapshot = null;
                break;
            case "owner-mismatch":
                snapshot = snapshot with { Owner = Owner("different-subject") };
                break;
            case "missing-service":
                snapshot = snapshot with { Services = [] };
                break;
            case "ambiguous-slug":
                snapshot = snapshot with
                {
                    Services =
                    [
                        ServiceWithSlug("service-a", "connector", ("node-a", true)),
                        ServiceWithSlug("service-b", "connector", ("node-b", true)),
                    ],
                };
                break;
            case "unreachable-service":
                snapshot = snapshot with { UnreachableServiceIds = new HashSet<string>(["us-connector"], StringComparer.Ordinal) };
                break;
            case "missing-node-grant":
                snapshot = snapshot with { Services = [Service("us-connector")] };
                break;
            case "empty-grants":
                snapshot = snapshot with { Services = [] };
                break;
        }

        var dependencies = scenario switch
        {
            "ambiguous-slug" => Dependencies(slugs: ["connector"]),
            "empty-grants" => Dependencies(slugs: []),
            _ => Dependencies(),
        };
        var result = await CreatePlanner(new StubSnapshotQueryPort(snapshot), dependencies)
            .PlanAsync(request);

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(expectedCode);
        result.Detail.Should().Be(expectedDetail);
    }

    private static ScheduledInvocationAuthorizationRequest Request(NyxIdCatalogOwnerIdentity owner, DateTimeOffset now) => new(
        new ScheduledInvocationTarget
        {
            Studio = new StudioScheduledInvocationTarget
            {
                ScopeId = "scope-alpha", TeamId = "team-alpha", MemberId = "m-alpha",
                WorkflowId = "wf-alpha", WorkflowRevision = "rev-1", PublishedServiceId = "svc-alpha",
            },
        }, new AuthenticatedNyxIdOwnerContext
        {
            Owner = owner, SubjectPlatform = "lark", SubjectExternalUserId = "sender-alpha", VerifiedBindingId = "binding-alpha",
        }, ["us-connector"], new ScheduledInvocationAuthorizationAuthority
        {
            MemberStateVersion = 3, WorkflowStateVersion = 5, ConnectorStateVersion = 7, OwnerLlmStateVersion = 11,
        }, now.AddDays(7), now);

    private static NyxIdCatalogOwnerIdentity Owner(string subject) => new()
    {
        Authority = "https://nyx.example.com", OwnerKind = NyxIdCatalogOwnerKind.Personal, OwnerSubject = subject,
    };

    private static NyxIdServiceGrant Service(string id, params (string Id, bool Primary)[] nodes)
        => ServiceWithSlug(id, string.Empty, nodes);

    private static NyxIdServiceGrant ServiceWithSlug(
        string id,
        string slug,
        params (string Id, bool Primary)[] nodes)
    {
        var service = new NyxIdServiceGrant { UserServiceId = id, DisplayName = id, ServiceSlug = slug };
        service.NodeGrants.Add(nodes.Select(static node => new NyxIdNodeGrant
        {
            NodeId = node.Id, DisplayName = node.Id, Primary = node.Primary,
        }));
        return service;
    }

    private sealed class StubSnapshotQueryPort(NyxIdCatalogSnapshot? snapshot) : INyxIdCatalogSnapshotQueryPort
    {
        public int CallCount { get; private set; }

        public Task<NyxIdCatalogSnapshot?> GetAsync(NyxIdCatalogOwnerIdentity owner, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult<NyxIdCatalogSnapshot?>(snapshot);
        }
    }

    private static ScheduledInvocationAuthorizationPlanner CreatePlanner(
        INyxIdCatalogSnapshotQueryPort snapshotQueryPort,
        WorkflowAuthorizationDependencies? dependencies = null) => new(
        snapshotQueryPort,
        new StubMemberQueryPort(ValidMember()),
        new StubWorkflowQueryPort(ValidWorkflow(dependencies)),
        new StubConnectorQueryPort(new ScheduledInvocationVersionFact(7)),
        new StubOwnerLLMQueryPort(new ScheduledInvocationVersionFact(11)));

    private static ScheduledInvocationMemberFact ValidMember() =>
        new(3, "wf-alpha", "rev-1", "svc-alpha");

    private static ScheduledInvocationWorkflowFact ValidWorkflow(
        WorkflowAuthorizationDependencies? dependencies = null) =>
        new(5, dependencies ?? Dependencies());

    private static WorkflowAuthorizationDependencies Dependencies(
        WorkflowServiceGrantPolicy policy = WorkflowServiceGrantPolicy.Required,
        IReadOnlyList<string>? slugs = null)
    {
        var dependencies = new WorkflowAuthorizationDependencies { ServiceGrantPolicy = policy };
        if (policy == WorkflowServiceGrantPolicy.Required && slugs == null)
            dependencies.NyxIdServiceIds.Add("us-connector");
        dependencies.NyxIdServiceSlugs.Add(slugs ?? []);
        return dependencies;
    }

    private sealed class StubMemberQueryPort(ScheduledInvocationMemberFact? fact) : IScheduledInvocationMemberQueryPort
    {
        public Task<ScheduledInvocationMemberFact?> GetAsync(string scopeId, string memberId, CancellationToken ct = default) =>
            Task.FromResult(fact);
    }

    private sealed class StubWorkflowQueryPort(ScheduledInvocationWorkflowFact? fact)
        : IScheduledInvocationWorkflowQueryPort
    {
        public int CallCount { get; private set; }

        public Task<ScheduledInvocationWorkflowFact?> GetAsync(string workflowId, CancellationToken ct = default) =>
            Task.FromResult(Get());

        private ScheduledInvocationWorkflowFact? Get()
        {
            CallCount++;
            return fact;
        }
    }

    private sealed class StubConnectorQueryPort(ScheduledInvocationVersionFact? fact) : IScheduledInvocationConnectorQueryPort
    {
        public int CallCount { get; private set; }

        public Task<ScheduledInvocationVersionFact?> GetAsync(string scopeId, CancellationToken ct = default) =>
            Task.FromResult(Get());

        private ScheduledInvocationVersionFact? Get()
        {
            CallCount++;
            return fact;
        }
    }

    private sealed class StubOwnerLLMQueryPort(ScheduledInvocationVersionFact? fact) : IScheduledInvocationOwnerLLMQueryPort
    {
        public int CallCount { get; private set; }

        public Task<ScheduledInvocationVersionFact?> GetAsync(string scopeId, CancellationToken ct = default) =>
            Task.FromResult(Get());

        private ScheduledInvocationVersionFact? Get()
        {
            CallCount++;
            return fact;
        }
    }

}
