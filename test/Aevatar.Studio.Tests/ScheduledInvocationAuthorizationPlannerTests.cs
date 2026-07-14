using Aevatar.Studio.Application.Authorization;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

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
        var planner = new ScheduledInvocationAuthorizationPlanner(new StubSnapshotQueryPort(snapshot));

        var result = await planner.PlanAsync(Request(owner, now));

        result.Success.Should().BeTrue();
        result.Plan!.InvocationTarget.Studio.MemberId.Should().Be("m-alpha");
        result.Plan.InvocationTarget.Studio.WorkflowId.Should().Be("wf-alpha");
        result.Plan.InvocationTarget.Studio.PublishedServiceId.Should().Be("svc-alpha");
        result.Plan.NyxIdServiceGrants.Single().UserServiceId.Should().Be("us-connector");
        result.Plan.NyxIdServiceGrants.Single().NodeGrants.Select(static node => node.NodeId)
            .Should().Equal("node-primary", "node-fallback");
        result.Plan.PermissionDigest.Should().NotContain("svc-alpha");
        result.Plan.Disclosure.BrowserReceivesRawKey.Should().BeFalse();
    }

    [Fact]
    public async Task PlanAsync_WhenSnapshotIsStale_FailsClosed()
    {
        var now = DateTimeOffset.Parse("2026-07-14T10:00:00Z");
        var owner = Owner("subject-personal");
        var snapshot = new NyxIdCatalogSnapshot(
            owner, 17, now.AddMinutes(-20), now, "", "catalog-digest-1", [Service("us-connector", ("node-primary", true))]);

        var result = await new ScheduledInvocationAuthorizationPlanner(new StubSnapshotQueryPort(snapshot))
            .PlanAsync(Request(owner, now));

        result.FailureCode.Should().Be(ScheduledInvocationAuthorizationFailureCode.SnapshotStale);
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
                request = request with { RequiredNyxIdServiceIds = [] };
                request = request with { RequiredNyxIdServiceSlugs = ["connector"] };
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
                request = request with { RequiredNyxIdServiceIds = [] };
                break;
        }

        var result = await new ScheduledInvocationAuthorizationPlanner(new StubSnapshotQueryPort(snapshot))
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
        public Task<NyxIdCatalogSnapshot?> GetAsync(NyxIdCatalogOwnerIdentity owner, CancellationToken ct = default) =>
            Task.FromResult<NyxIdCatalogSnapshot?>(snapshot);
    }

}
