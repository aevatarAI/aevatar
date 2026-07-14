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

    [Fact]
    public async Task ProvisionAsync_WhenDigestChanged_DoesNotIssueCredential()
    {
        var planner = new StubPlanner(ScheduledInvocationAuthorizationPlanResult.Succeeded(new ScheduledInvocationAuthorizationPlan
        {
            PermissionDigest = "new-digest",
        }));
        var issuer = new RecordingIssuer();
        var provisioner = new ScheduledInvocationCredentialProvisioner(planner, issuer);

        var result = await provisioner.ProvisionAsync("bearer", Request(Owner("subject-personal"), DateTimeOffset.UtcNow), "old-digest", "key");

        result.Error.Should().Be("authorization_plan_changed");
        issuer.CallCount.Should().Be(0);
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
    {
        var service = new NyxIdServiceGrant { UserServiceId = id, DisplayName = id };
        service.NodeGrants.Add(nodes.Select(static node => new NyxIdNodeGrant
        {
            NodeId = node.Id, DisplayName = node.Id, Primary = node.Primary,
        }));
        return service;
    }

    private sealed class StubSnapshotQueryPort(NyxIdCatalogSnapshot snapshot) : INyxIdCatalogSnapshotQueryPort
    {
        public Task<NyxIdCatalogSnapshot?> GetAsync(NyxIdCatalogOwnerIdentity owner, CancellationToken ct = default) =>
            Task.FromResult<NyxIdCatalogSnapshot?>(snapshot);
    }

    private sealed class StubPlanner(ScheduledInvocationAuthorizationPlanResult result) : IScheduledInvocationAuthorizationPlanner
    {
        public Task<ScheduledInvocationAuthorizationPlanResult> PlanAsync(ScheduledInvocationAuthorizationRequest request, CancellationToken ct = default) =>
            Task.FromResult(result);
    }

    private sealed class RecordingIssuer : IScheduledInvocationCredentialIssuer
    {
        public int CallCount { get; private set; }
        public Task<ScheduledInvocationCredentialIssueResult> IssueAsync(string ownerBearer, ScheduledInvocationAuthorizationPlan plan, string credentialName, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new ScheduledInvocationCredentialIssueResult(true, "", "key-id", "secret", 1));
        }
    }
}
