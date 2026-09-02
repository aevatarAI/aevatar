using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Application.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;
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
    public void FailureCodeWireValues_ShouldKeepDistinctDurableAndProjectionPendingSemantics()
    {
        ((int)ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable).Should().Be(13);
        ((int)ScheduledInvocationAuthorizationFailureCode.CatalogProjectionPending).Should().Be(14);
        ((int)ScheduledInvocationAuthorizationFailureCode.OwnerLlmRouteUnavailable).Should().Be(15);
        ((int)ScheduledInvocationAuthorizationFailureCode.OwnerLlmModelNotVerifiable).Should().Be(16);
        ((int)ScheduledInvocationAuthorizationFailureCode.OwnerLlmModelUnavailable).Should().Be(17);
    }

    [Fact]
    public async Task PlanAsync_WhenNoServiceGrantsRequired_ShouldNotReadCatalog()
    {
        var catalog = new MutableCatalogQueryPort(Snapshot() with { Invalidated = true });
        var planner = NewPlanner(catalog);
        var request = Request(Array.Empty<string>()) with
        {
            ServiceGrantRequirement = AuthorizationGrantRequirement.NotRequired,
        };

        var result = await planner.PlanAsync(request);

        result.Success.Should().BeTrue();
        result.Plan!.CatalogAuthority.Should().BeNull();
        result.Plan.NyxIdServiceGrants.Should().BeEmpty();
        result.Plan.CredentialPolicy.ServiceGrantRequirement.Should()
            .Be(AuthorizationGrantRequirement.NotRequired);
        catalog.QueryCount.Should().Be(0);
    }

    [Fact]
    public async Task PlanAsync_WithLegacyCatalogWithoutLLMEvidence_ShouldStillAuthorizeNonLLMGrant()
    {
        var evidence = StudioWorkflowEvidence(NyxIdCapability("svc-a", "provider-a"));
        var snapshot = Snapshot(
            Service("svc-a", "provider-a", AuthorizationGrantRequirement.NotRequired));
        snapshot.Services[0].LlmTarget.Should().BeNull();
        snapshot.GatewayLLMTarget.Should().BeNull();
        var planner = new ScheduledInvocationAuthorizationPlanner(
            new MutableCatalogQueryPort(snapshot),
            evidence,
            evidence,
            evidence,
            evidence);
        var request = Request(Array.Empty<string>()) with
        {
            InvocationTarget = new ScheduledInvocationTarget
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
        };

        var result = await planner.PlanAsync(request);

        result.Success.Should().BeTrue();
        result.Plan!.NyxIdServiceGrants.Should().ContainSingle()
            .Which.UserServiceId.Should().Be("svc-a");
        evidence.OwnerLLMQueries.Should().Be(0);
    }

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
    public async Task PlanAsync_WithDuplicateSlugs_ShouldGrantEachExactUserServiceId()
    {
        var planner = NewPlanner(new MutableCatalogQueryPort(Snapshot(
            Service("us-home-alpha", "home-assistant", AuthorizationGrantRequirement.NotRequired),
            Service("us-home-beta", "home-assistant", AuthorizationGrantRequirement.NotRequired))));

        var result = await planner.PlanAsync(Request([
            NyxIdService("us-home-alpha", "home-assistant"),
            NyxIdService("us-home-beta", "home-assistant"),
        ]));

        result.Success.Should().BeTrue();
        result.Plan!.NyxIdServiceGrants.Select(static grant => grant.UserServiceId)
            .Should().Equal("us-home-alpha", "us-home-beta");
        result.Plan.NyxIdServiceGrants.Select(static grant => grant.ServiceSlug)
            .Should().OnlyContain(slug => slug == "home-assistant");
        result.Plan.CredentialPolicy.AllowAllServices.Should().BeFalse();
        result.Plan.CredentialPolicy.AllowAllNodes.Should().BeFalse();
    }

    [Fact]
    public async Task PlanAsync_WithMissingExactServiceId_ShouldFailDurableAuthorization()
    {
        var planner = NewPlanner(new MutableCatalogQueryPort(Snapshot(
            Service("us-home-alpha", "home-assistant", AuthorizationGrantRequirement.NotRequired))));
        var request = Request([NyxIdService(string.Empty, "home-assistant")]);

        var result = await planner.PlanAsync(request);

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(
            ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable);
        result.Detail.Should().Be("nyxid_exact_service_identity_unavailable");
    }

    [Fact]
    public async Task PlanAsync_ForPersonalOwner_ShouldBindAuthenticatedActorToOwner()
    {
        var evidence = new StudioEvidencePorts
        {
            OwnerLLM = new ScheduledInvocationOwnerLLMEvidence(11, GatewaySelection()),
        };
        var planner = new ScheduledInvocationAuthorizationPlanner(
            new MutableCatalogQueryPort(SnapshotWithGateway(
                GatewayTarget("gpt-5.5"),
                Service("svc-a", "provider-a", AuthorizationGrantRequirement.NotRequired))),
            ownerLLMQueryPort: evidence);

        var result = await planner.PlanAsync(Request(["svc-a"]));

        result.Success.Should().BeTrue();
        result.Plan!.AuthenticatedActor.Should().BeEquivalentTo(Owner());
    }

    [Fact]
    public async Task PlanAsync_ForPersonalOwnerWithDifferentAuthenticatedActor_ShouldFail()
    {
        var planner = NewPlanner(new MutableCatalogQueryPort(Snapshot(
            Service("svc-a", "provider-a", AuthorizationGrantRequirement.NotRequired))));
        var request = Request(["svc-a"]);
        request = request with
        {
            OwnerContext = request.OwnerContext with
            {
                AuthenticatedActor = Identity(AuthorizationOwnerKind.Personal, "user-other"),
            },
        };

        var result = await planner.PlanAsync(request);

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(ScheduledInvocationAuthorizationFailureCode.OwnerInvalid);
        result.Detail.Should().Be("nyxid_authenticated_actor_invalid");
    }

    [Fact]
    public async Task PlanAsync_ForOrganizationOwnerWithoutAuthenticatedAdministrator_ShouldFail()
    {
        var organization = Identity(AuthorizationOwnerKind.Organization, "org-alpha");
        var planner = NewPlanner(new MutableCatalogQueryPort(
            Snapshot(Service("svc-a", "provider-a", AuthorizationGrantRequirement.NotRequired)) with
            {
                Owner = organization.Clone(),
            }));
        var request = Request(["svc-a"]);
        request = request with
        {
            OwnerContext = request.OwnerContext with
            {
                Owner = organization,
                AuthenticatedActor = null,
            },
        };

        var result = await planner.PlanAsync(request);

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(ScheduledInvocationAuthorizationFailureCode.OwnerInvalid);
        result.Detail.Should().Be("nyxid_authenticated_actor_invalid");
    }

    [Fact]
    public async Task PlanAsync_ForOrganizationOwner_ShouldBindExplicitPersonalAdministrator()
    {
        var organization = Identity(AuthorizationOwnerKind.Organization, "org-alpha");
        var administrator = Identity(AuthorizationOwnerKind.Personal, "admin-alpha");
        var snapshot = Snapshot(Service("svc-a", "provider-a", AuthorizationGrantRequirement.NotRequired)) with
        {
            Owner = organization.Clone(),
        };
        snapshot = snapshot with
        {
            ContentDigest = NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(
                snapshot.Owner,
                snapshot.Services),
        };
        var planner = NewPlanner(new MutableCatalogQueryPort(snapshot));
        var request = Request(["svc-a"]);
        request = request with
        {
            OwnerContext = request.OwnerContext with
            {
                Owner = organization,
                AuthenticatedActor = administrator,
            },
        };

        var result = await planner.PlanAsync(request);

        result.Success.Should().BeTrue();
        result.Plan!.Owner.Should().BeEquivalentTo(organization);
        result.Plan.AuthenticatedActor.Should().BeEquivalentTo(administrator);
    }

    [Fact]
    public async Task PlanAsync_WithMissingRequiredNodeTopology_ShouldFailDurableAuthorization()
    {
        var planner = NewPlanner(new MutableCatalogQueryPort(Snapshot(
            Service("us-home-alpha", "home-assistant", AuthorizationGrantRequirement.Required))));

        var result = await planner.PlanAsync(Request(["us-home-alpha"]));

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(
            ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable);
        result.Detail.Should().Be("nyxid_node_authorization_topology_unavailable:us-home-alpha");
        result.ObservedCatalogStateVersion.Should().Be(7);
    }

    [Theory]
    [InlineData("other-authority")]
    [InlineData("NYXID")]
    [InlineData("nyxid ")]
    public async Task PlanAsync_WithNonNyxIdResourceOwner_ShouldFailDurableAuthorization(
        string authority)
    {
        var service = Service(
            "us-home-alpha",
            "home-assistant",
            AuthorizationGrantRequirement.NotRequired);
        service.ResourceOwner.Authority = authority;
        var planner = NewPlanner(new MutableCatalogQueryPort(Snapshot(service)));

        var result = await planner.PlanAsync(Request(["us-home-alpha"]));

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(
            ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable);
        result.Detail.Should().Be("nyxid_resource_owner_invalid:us-home-alpha");
        result.ObservedCatalogStateVersion.Should().Be(7);
    }

    [Fact]
    public async Task PlanAsync_WithNonNyxIdResourceOwnerOnUnrequestedService_ShouldIgnoreUnrequestedEvidence()
    {
        var invalidService = Service(
            "us-home-beta",
            "home-assistant",
            AuthorizationGrantRequirement.NotRequired);
        invalidService.ResourceOwner.Authority = "other-authority";
        var planner = NewPlanner(new MutableCatalogQueryPort(Snapshot(
            Service("us-home-alpha", "home-assistant", AuthorizationGrantRequirement.NotRequired),
            invalidService)));

        var result = await planner.PlanAsync(Request(["us-home-alpha"]));

        result.Success.Should().BeTrue();
        result.Plan!.NyxIdServiceGrants.Select(static grant => grant.UserServiceId)
            .Should().Equal("us-home-alpha");
    }

    [Fact]
    public async Task PlanAsync_WhenCatalogStampIsStaleButRequiredServiceEvidenceIsFresh_ShouldSucceed()
    {
        var service = Service(
            "us-home-alpha",
            "home-assistant",
            AuthorizationGrantRequirement.NotRequired);
        service.ObservedAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(-1));
        service.FreshUntil = Timestamp.FromDateTimeOffset(Now.AddMinutes(10));
        var snapshot = Snapshot(service) with
        {
            FreshUntilUtc = Now.AddMinutes(-1),
        };
        var planner = NewPlanner(new MutableCatalogQueryPort(snapshot));

        var result = await planner.PlanAsync(Request(["us-home-alpha"]));

        result.Success.Should().BeTrue();
        result.Plan!.NyxIdServiceGrants.Should().ContainSingle()
            .Which.UserServiceId.Should().Be("us-home-alpha");
    }

    [Fact]
    public async Task PlanAsync_WhenStaleCatalogDoesNotContainRequiredService_ShouldReturnSnapshotStale()
    {
        var snapshot = Snapshot(Service(
            "us-home-alpha",
            "home-assistant",
            AuthorizationGrantRequirement.NotRequired)) with
        {
            FreshUntilUtc = Now.AddMinutes(-1),
        };
        var planner = NewPlanner(new MutableCatalogQueryPort(snapshot));

        var result = await planner.PlanAsync(Request(["us-home-beta"]));

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(ScheduledInvocationAuthorizationFailureCode.SnapshotStale);
        result.Detail.Should().Be("nyxid_catalog_snapshot_stale");
        result.RequiredNyxIdServices.Select(static service => service.UserServiceId)
            .Should().Equal("us-home-beta");
    }

    [Theory]
    [InlineData("state_version", "nyxid_catalog_lifecycle_invalid")]
    [InlineData("negative_state_version", "nyxid_catalog_lifecycle_invalid")]
    [InlineData("not_activated", "nyxid_catalog_lifecycle_invalid")]
    [InlineData("cleaned", "nyxid_catalog_lifecycle_invalid")]
    [InlineData("default_observed_at", "nyxid_catalog_lifecycle_invalid")]
    [InlineData("blank_contract_version", "nyxid_catalog_lifecycle_invalid")]
    [InlineData("blank_policy_version", "nyxid_catalog_lifecycle_invalid")]
    [InlineData("default_evaluated_at", "nyxid_catalog_lifecycle_invalid")]
    [InlineData("missing_digest", "nyxid_catalog_content_digest_invalid")]
    [InlineData("mismatched_digest", "nyxid_catalog_content_digest_invalid")]
    public async Task PlanAsync_WithLifecycleIneligibleCatalog_ShouldFailClosed(
        string scenario,
        string expectedDetail)
    {
        var snapshot = Snapshot(Service(
            "us-home-alpha",
            "home-assistant",
            AuthorizationGrantRequirement.NotRequired));
        snapshot = scenario switch
        {
            "state_version" => snapshot with { StateVersion = 0 },
            "negative_state_version" => snapshot with { StateVersion = -1 },
            "not_activated" => snapshot with { Activated = false },
            "cleaned" => snapshot with { Cleaned = true },
            "default_observed_at" => snapshot with { ObservedAtUtc = default },
            "blank_contract_version" => snapshot with { ContractVersion = " " },
            "blank_policy_version" => snapshot with { PolicyVersion = " " },
            "default_evaluated_at" => snapshot with { EvaluatedAtUtc = default },
            "missing_digest" => snapshot with { ContentDigest = string.Empty },
            "mismatched_digest" => snapshot with { ContentDigest = "forged-digest" },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
        };
        var planner = NewPlanner(new MutableCatalogQueryPort(snapshot));

        var result = await planner.PlanAsync(Request(["us-home-alpha"]));

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound);
        result.Detail.Should().Be(expectedDetail);
        result.ObservedCatalogStateVersion.Should().Be(snapshot.StateVersion);
    }

    [Theory]
    [InlineData(
        "owner_mismatch",
        ScheduledInvocationAuthorizationFailureCode.OwnerMismatch,
        "nyxid_catalog_owner_mismatch")]
    [InlineData(
        "invalidated",
        ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
        "nyxid_catalog_snapshot_invalidated")]
    [InlineData(
        "cleaned",
        ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
        "nyxid_catalog_lifecycle_invalid")]
    [InlineData(
        "lifecycle",
        ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
        "nyxid_catalog_lifecycle_invalid")]
    [InlineData(
        "digest",
        ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
        "nyxid_catalog_content_digest_invalid")]
    public async Task PlanAsync_WithMultipleInvalidCatalogConditions_ShouldRespectValidationOrder(
        string scenario,
        ScheduledInvocationAuthorizationFailureCode expectedFailureCode,
        string expectedDetail)
    {
        var snapshot = Snapshot(Service(
            "us-home-alpha",
            "home-assistant",
            AuthorizationGrantRequirement.NotRequired));
        snapshot = scenario switch
        {
            "owner_mismatch" => snapshot with
            {
                Owner = Identity(AuthorizationOwnerKind.Personal, "user-other"),
                Invalidated = true,
                Cleaned = true,
                StateVersion = 0,
                Activated = false,
                ObservedAtUtc = default,
                ContentDigest = "forged-digest",
                FreshUntilUtc = Now,
            },
            "invalidated" => snapshot with
            {
                Invalidated = true,
                Cleaned = true,
                StateVersion = 0,
                Activated = false,
                ContentDigest = "forged-digest",
            },
            "cleaned" => snapshot with
            {
                Cleaned = true,
                StateVersion = 0,
                ContentDigest = "forged-digest",
            },
            "lifecycle" => snapshot with
            {
                StateVersion = 0,
                ContentDigest = "forged-digest",
            },
            "digest" => snapshot with
            {
                ContentDigest = "forged-digest",
                FreshUntilUtc = Now,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
        };
        var planner = NewPlanner(new MutableCatalogQueryPort(snapshot));

        var result = await planner.PlanAsync(Request(["us-home-alpha"]));

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(expectedFailureCode);
        result.Detail.Should().Be(expectedDetail);
        result.ObservedCatalogStateVersion.Should().Be(snapshot.StateVersion);
    }

    [Fact]
    public async Task PlanAsync_WithChangedSlugSnapshot_ShouldFailSnapshotStale()
    {
        var planner = NewPlanner(new MutableCatalogQueryPort(Snapshot(
            Service("us-home-alpha", "home-assistant", AuthorizationGrantRequirement.NotRequired))));

        var result = await planner.PlanAsync(Request([
            NyxIdService("us-home-alpha", "home-assistant-renamed"),
        ]));

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(ScheduledInvocationAuthorizationFailureCode.SnapshotStale);
        result.Detail.Should().Be("nyxid_service_slug_snapshot_changed:us-home-alpha");
        result.ObservedCatalogStateVersion.Should().Be(7);
    }

    [Fact]
    public async Task PlanAsync_WithConflictingSlugSnapshotsForSameId_ShouldFailSnapshotStale()
    {
        var catalog = new MutableCatalogQueryPort(Snapshot(
            Service("us-home-alpha", "home-assistant", AuthorizationGrantRequirement.NotRequired)));
        var planner = NewPlanner(catalog);

        var result = await planner.PlanAsync(Request([
            NyxIdService("us-home-alpha", "home-assistant"),
            NyxIdService("us-home-alpha", "home-assistant-renamed"),
        ]));

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(ScheduledInvocationAuthorizationFailureCode.SnapshotStale);
        result.Detail.Should().Be("nyxid_service_slug_snapshot_conflict:us-home-alpha");
        result.ObservedCatalogStateVersion.Should().Be(0);
        catalog.QueryCount.Should().Be(0);
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
        first.Plan.SchemaVersion.Should().Be("scheduled-invocation-authorization/v3");
        first.Plan.CredentialPolicy.PolicyVersion.Should().Be("nyxid-api-key/scheduled-invocation/v2");
    }

    [Fact]
    public async Task ComputeDigest_ShouldCoverTargetOwnerAuthorityPolicySourceDisclosureAndOwnerLlmSelection()
    {
        var evidence = new StudioEvidencePorts
        {
            OwnerLLM = new ScheduledInvocationOwnerLLMEvidence(11, GatewaySelection()),
        };
        var planner = new ScheduledInvocationAuthorizationPlanner(
            new MutableCatalogQueryPort(SnapshotWithGateway(
                GatewayTarget("gpt-5.5"),
                Service("svc-a", "provider-a", AuthorizationGrantRequirement.NotRequired))),
            ownerLLMQueryPort: evidence);
        var original = (await planner.PlanAsync(Request(["svc-a"]))).Plan!;
        original.OwnerLlmSelection.Should().NotBeNull();
        var originalDigest = ScheduledInvocationAuthorizationPlanner.ComputeDigest(original);
        Action<ScheduledInvocationAuthorizationPlan>[] mutations =
        [
            static plan => plan.InvocationTarget.ScheduledAgent.ExecutionScopeId = "scope-other",
            static plan => plan.Owner.OwnerSubject = "owner-other",
            static plan => plan.AuthenticatedActor.OwnerSubject = "admin-other",
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
            static plan => plan.OwnerLlmSelection.RouteKind = LLMRouteKind.NyxIdUserService,
            static plan => plan.OwnerLlmSelection.RouteValue = "/api/v1/proxy/s/provider-other",
            static plan => plan.OwnerLlmSelection.NyxIdUserServiceId = "us-other",
            static plan => plan.OwnerLlmSelection.ServiceSlugSnapshot = "provider-other",
            static plan => plan.OwnerLlmSelection.Model = "gpt-other",
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

    [Theory]
    [InlineData(true, "nyxid_catalog_snapshot_invalidated")]
    [InlineData(false, "nyxid_catalog_snapshot_stale")]
    public async Task RevalidateAsync_ShouldReportObservedCatalogVersionForUnavailableCurrentEvidence(
        bool invalidated,
        string expectedDetail)
    {
        var catalog = new MutableCatalogQueryPort(Snapshot(
            Service("svc-a", "provider-a", AuthorizationGrantRequirement.NotRequired)));
        var planner = NewPlanner(catalog);
        var request = Request(["svc-a"]);
        var original = await planner.PlanAsync(request);
        catalog.Snapshot = catalog.Snapshot! with
        {
            StateVersion = 24,
            Invalidated = invalidated,
            FreshUntilUtc = invalidated ? catalog.Snapshot.FreshUntilUtc : Now,
        };
        var revalidator = new ScheduledInvocationAuthorizationRevalidator(
            planner,
            new FakeTimeProvider(Now));

        var result = await revalidator.RevalidateAsync(
            request,
            ScheduledInvocationAuthorizationConfirmations.FromPlan(original.Plan!));

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(ScheduledInvocationAuthorizationFailureCode.AuthorizationPlanChanged);
        result.Detail.Should().Be(expectedDetail);
        result.ObservedCatalogStateVersion.Should().Be(24);
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
    public async Task PlanAsync_ForScheduledAgentGateway_ShouldBindSelectionWithoutAddingServiceGrant()
    {
        var selection = GatewaySelection();
        var evidence = new StudioEvidencePorts
        {
            OwnerLLM = new ScheduledInvocationOwnerLLMEvidence(11, selection),
        };
        var planner = new ScheduledInvocationAuthorizationPlanner(
            new MutableCatalogQueryPort(SnapshotWithGateway(
                GatewayTarget("gpt-5.5"),
                Service("svc-a", "provider-a", AuthorizationGrantRequirement.NotRequired))),
            ownerLLMQueryPort: evidence);

        var result = await planner.PlanAsync(Request(["svc-a"]));

        result.Success.Should().BeTrue();
        result.Plan!.NyxIdServiceGrants.Select(static grant => grant.UserServiceId)
            .Should().Equal("svc-a");
        result.Plan.OwnerLlmSelection.Should().BeEquivalentTo(selection);
        result.Plan.OwnerLlmSelection.Should().NotBeSameAs(selection);
        result.Plan.PermissionDigest.Should().Be(
            ScheduledInvocationAuthorizationPlanIntegrity.ComputeDigest(result.Plan));
    }

    [Fact]
    public async Task PlanAsync_WithExactEnumeratedUserServiceModel_ShouldBindCatalogVersionAndModel()
    {
        var service = Service(
            "us-alpha",
            "chrono-llm-public",
            AuthorizationGrantRequirement.NotRequired);
        service.LlmTarget = ServiceTarget("us-alpha", "chrono-llm-public", "gpt-5.5");
        var snapshot = Snapshot(service) with { StateVersion = 29 };
        var evidence = new StudioEvidencePorts
        {
            OwnerLLM = new ScheduledInvocationOwnerLLMEvidence(
                17,
                ServiceSelection("us-alpha", "chrono-llm-public")),
        };
        var planner = new ScheduledInvocationAuthorizationPlanner(
            new MutableCatalogQueryPort(snapshot),
            ownerLLMQueryPort: evidence);

        var result = await planner.PlanAsync(Request(Array.Empty<string>()));

        result.Success.Should().BeTrue();
        result.Plan!.OwnerLlmSelection.Model.Should().Be("gpt-5.5");
        result.Plan.CatalogAuthority.ActorStateVersion.Should().Be(29);
        result.Plan.NyxIdServiceGrants.Should().ContainSingle()
            .Which.UserServiceId.Should().Be("us-alpha");
        ScheduledInvocationAuthorizationPlanIntegrity.IsValid(result.Plan).Should().BeTrue();
    }

    [Fact]
    public async Task PlanAsync_WithDuplicateExactUserServiceEvidence_ShouldFailClosed()
    {
        var first = Service("us-alpha", "chrono-llm-public", AuthorizationGrantRequirement.NotRequired);
        first.LlmTarget = ServiceTarget("us-alpha", "chrono-llm-public", "gpt-5.5");
        var second = Service("us-alpha", "chrono-llm-public", AuthorizationGrantRequirement.NotRequired);
        second.LlmTarget = ServiceTarget("us-alpha", "chrono-llm-public", "gpt-5.5");
        var evidence = new StudioEvidencePorts
        {
            OwnerLLM = new ScheduledInvocationOwnerLLMEvidence(
                17,
                ServiceSelection("us-alpha", "chrono-llm-public")),
        };
        var planner = new ScheduledInvocationAuthorizationPlanner(
            new MutableCatalogQueryPort(Snapshot(first, second)),
            ownerLLMQueryPort: evidence);

        var result = await planner.PlanAsync(Request(Array.Empty<string>()));

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(
            ScheduledInvocationAuthorizationFailureCode.OwnerLlmRouteUnavailable);
        result.Detail.Should().Be("owner_llm_route_unavailable");
    }

    [Theory]
    [InlineData(
        LLMModelCatalogCertainty.Unavailable,
        ScheduledInvocationAuthorizationFailureCode.OwnerLlmRouteUnavailable,
        "owner_llm_route_unavailable")]
    [InlineData(
        LLMModelCatalogCertainty.NotVerifiable,
        ScheduledInvocationAuthorizationFailureCode.OwnerLlmModelNotVerifiable,
        "owner_llm_model_not_verifiable")]
    [InlineData(
        LLMModelCatalogCertainty.Enumerated,
        ScheduledInvocationAuthorizationFailureCode.OwnerLlmModelUnavailable,
        "owner_llm_model_unavailable")]
    public async Task PlanAsync_WhenGatewayModelEvidenceCannotAuthorizeExactModel_ShouldReturnTypedFailure(
        LLMModelCatalogCertainty certainty,
        ScheduledInvocationAuthorizationFailureCode expectedFailure,
        string expectedDetail)
    {
        var target = GatewayTarget(certainty == LLMModelCatalogCertainty.Enumerated
            ? "gpt-other"
            : "gpt-5.5");
        target.ModelCatalog.Certainty = certainty;
        if (certainty != LLMModelCatalogCertainty.Enumerated)
        {
            target.ModelCatalog.ModelIds.Clear();
            target.ModelCatalog.DefaultModelId = string.Empty;
            target.ModelCatalog.DiagnosticKind = certainty == LLMModelCatalogCertainty.Unavailable
                ? LLMModelCatalogDiagnosticKind.AccessDenied
                : LLMModelCatalogDiagnosticKind.NotPublished;
        }
        var evidence = new StudioEvidencePorts
        {
            OwnerLLM = new ScheduledInvocationOwnerLLMEvidence(17, GatewaySelection()),
        };
        var planner = new ScheduledInvocationAuthorizationPlanner(
            new MutableCatalogQueryPort(SnapshotWithGateway(target)),
            ownerLLMQueryPort: evidence);

        var result = await planner.PlanAsync(Request(Array.Empty<string>()));

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(expectedFailure);
        result.Detail.Should().Be(expectedDetail);
        result.LLMRefreshRequirement.Should().Be(new ScheduledInvocationLLMRefreshRequirement(
            LLMRouteKind.Gateway,
            "/api/v1/llm/gateway/v1",
            string.Empty,
            string.Empty,
            "gpt-5.5",
            17));
    }

    [Fact]
    public async Task PlanAsync_WhenGatewayEvidenceIsMissing_ShouldRequireExactTargetRefresh()
    {
        var evidence = new StudioEvidencePorts
        {
            OwnerLLM = new ScheduledInvocationOwnerLLMEvidence(17, GatewaySelection()),
        };
        var planner = new ScheduledInvocationAuthorizationPlanner(
            new MutableCatalogQueryPort(Snapshot()),
            ownerLLMQueryPort: evidence);

        var result = await planner.PlanAsync(Request(Array.Empty<string>()));

        result.FailureCode.Should().Be(
            ScheduledInvocationAuthorizationFailureCode.OwnerLlmRouteUnavailable);
        result.LLMRefreshRequirement!.UserConfigStateVersion.Should().Be(17);
        result.LLMRefreshRequirement.ExplicitModelId.Should().Be("gpt-5.5");
    }

    [Fact]
    public async Task RevalidateAsync_WhenExactModelDisappears_ShouldReturnTypedModelFailure()
    {
        var evidence = new StudioEvidencePorts
        {
            OwnerLLM = new ScheduledInvocationOwnerLLMEvidence(17, GatewaySelection()),
        };
        var catalog = new MutableCatalogQueryPort(SnapshotWithGateway(GatewayTarget("gpt-5.5")));
        var planner = new ScheduledInvocationAuthorizationPlanner(catalog, ownerLLMQueryPort: evidence);
        var request = Request(Array.Empty<string>()) with
        {
            ServiceGrantRequirement = AuthorizationGrantRequirement.NotRequired,
        };
        var original = await planner.PlanAsync(request);
        catalog.Snapshot = SnapshotWithGateway(GatewayTarget("gpt-other"));
        var revalidator = new ScheduledInvocationAuthorizationRevalidator(
            planner,
            new FakeTimeProvider(Now));

        var result = await revalidator.RevalidateAsync(
            request,
            ScheduledInvocationAuthorizationConfirmations.FromPlan(original.Plan!));

        result.FailureCode.Should().Be(
            ScheduledInvocationAuthorizationFailureCode.OwnerLlmModelUnavailable);
        result.LLMRefreshRequirement!.UserConfigStateVersion.Should().Be(17);
    }

    [Fact]
    public async Task PlanAsync_ForScheduledAgent_ShouldComposeExactOwnerLlmEvidenceFromExecutionScope()
    {
        var selection = ServiceSelection("svc-b", "provider-b");
        var evidence = new StudioEvidencePorts
        {
            OwnerLLM = new ScheduledInvocationOwnerLLMEvidence(11, selection),
        };
        var llmService = Service("svc-b", "provider-b", AuthorizationGrantRequirement.NotRequired);
        llmService.LlmTarget = ServiceTarget("svc-b", "provider-b", "gpt-5.5");
        var planner = new ScheduledInvocationAuthorizationPlanner(
            new MutableCatalogQueryPort(Snapshot(
                Service("svc-a", "provider-a", AuthorizationGrantRequirement.NotRequired),
                llmService)),
            ownerLLMQueryPort: evidence);

        var result = await planner.PlanAsync(Request(["svc-a"]));

        result.Success.Should().BeTrue();
        result.Plan!.NyxIdServiceGrants.Select(static grant => grant.UserServiceId)
            .Should().Equal("svc-a", "svc-b");
        result.Plan.SourceStamps.Select(static stamp => (stamp.SourceKind, stamp.SourceId, stamp.StateVersion))
            .Should().Equal(
                (AuthorizationSourceKind.ScheduledAgentRegistration, "agent-alpha", 0),
                (AuthorizationSourceKind.OwnerLlmRoute, "scope-execution", 11));
        result.Plan.OwnerLlmSelection.Should().BeEquivalentTo(selection);
        result.Plan.OwnerLlmSelection.Should().NotBeSameAs(selection);
        evidence.LastOwnerLLMScopeId.Should().Be("scope-execution");
    }

    [Theory]
    [InlineData(LLMRouteKind.Unspecified, "", "", "", "", ScheduledInvocationAuthorizationFailureCode.OwnerLlmModelNotVerifiable)]
    [InlineData(LLMRouteKind.Gateway, "/api/v1/llm/gateway/v1", "", "", "", ScheduledInvocationAuthorizationFailureCode.OwnerLlmModelNotVerifiable)]
    [InlineData(LLMRouteKind.Gateway, " /api/v1/llm/gateway/v1", "", "", "gpt-5.5", ScheduledInvocationAuthorizationFailureCode.OwnerLlmRouteUnavailable)]
    [InlineData(LLMRouteKind.NyxIdUserService, "/api/v1/proxy/s/provider-b", "", "provider-b", "gpt-5.5", ScheduledInvocationAuthorizationFailureCode.OwnerLlmRouteUnavailable)]
    [InlineData(LLMRouteKind.NyxIdUserService, "/api/v1/proxy/s/provider-b", "svc-b", "provider-other", "gpt-5.5", ScheduledInvocationAuthorizationFailureCode.OwnerLlmRouteUnavailable)]
    public async Task PlanAsync_ForScheduledAgent_ShouldRejectInvalidOwnerLlmSelectionBeforeCatalogAccess(
        LLMRouteKind routeKind,
        string routeValue,
        string serviceId,
        string serviceSlug,
        string model,
        ScheduledInvocationAuthorizationFailureCode expectedFailureCode)
    {
        var evidence = new StudioEvidencePorts
        {
            OwnerLLM = new ScheduledInvocationOwnerLLMEvidence(
                11,
                new ScheduledInvocationOwnerLLMSelection
                {
                    RouteKind = routeKind,
                    RouteValue = routeValue,
                    NyxIdUserServiceId = serviceId,
                    ServiceSlugSnapshot = serviceSlug,
                    Model = model,
                }),
        };
        var catalog = new MutableCatalogQueryPort(Snapshot(
            Service("svc-a", "provider-a", AuthorizationGrantRequirement.NotRequired),
            Service("svc-b", "provider-b", AuthorizationGrantRequirement.NotRequired)));
        var planner = new ScheduledInvocationAuthorizationPlanner(
            catalog,
            ownerLLMQueryPort: evidence);

        var result = await planner.PlanAsync(Request(["svc-a"]));

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(expectedFailureCode);
        catalog.QueryCount.Should().Be(0);
    }

    [Fact]
    public async Task PlanAsync_ForStudioTarget_ShouldComposeStaticWorkflowAndOwnerLlmEvidence()
    {
        var evidence = new StudioEvidencePorts
        {
            Member = new ScheduledInvocationMemberEvidence(3, "wf-alpha", "rev-alpha", "svc-alpha"),
            Workflow = new ScheduledInvocationWorkflowEvidence(
                5,
                [
                    ConnectorCapability("calendar"),
                    NyxIdCapability("nyx-service-a", "provider-a"),
                ],
                true,
                AuthorizationGrantRequirement.Required),
            Connector = new ScheduledInvocationConnectorEvidence(7, ["calendar"]),
            OwnerLLM = new ScheduledInvocationOwnerLLMEvidence(
                11,
                ServiceSelection("nyx-service-b", "provider-b")),
        };
        var ownerLLMService = Service(
            "nyx-service-b",
            "provider-b",
            AuthorizationGrantRequirement.NotRequired);
        ownerLLMService.LlmTarget = ServiceTarget("nyx-service-b", "provider-b", "gpt-5.5");
        var planner = new ScheduledInvocationAuthorizationPlanner(
            new MutableCatalogQueryPort(Snapshot(
                Service("nyx-service-a", "provider-a", AuthorizationGrantRequirement.NotRequired),
                ownerLLMService)),
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
        result.Plan.OwnerLlmSelection.Should().BeEquivalentTo(
            ServiceSelection("nyx-service-b", "provider-b"));
    }

    [Fact]
    public async Task PlanAsync_ForStudioTarget_ShouldGrantExactServiceFromExplicitRequest()
    {
        var evidence = StudioWorkflowEvidence(
            ExplicitRequestCapability("usvc-explicit-alpha", "untrusted-slug-alpha"));
        var planner = new ScheduledInvocationAuthorizationPlanner(
            new MutableCatalogQueryPort(Snapshot(
                Service("usvc-explicit-alpha", "catalog-slug-alpha", AuthorizationGrantRequirement.NotRequired))),
            evidence,
            evidence,
            evidence,
            evidence);

        var result = await planner.PlanAsync(StudioRequest());

        result.Success.Should().BeTrue();
        result.Plan!.NyxIdServiceGrants.Should().ContainSingle()
            .Which.UserServiceId.Should().Be("usvc-explicit-alpha");
        result.Plan.NyxIdServiceGrants[0].ServiceSlug.Should().Be("catalog-slug-alpha");
    }

    [Fact]
    public async Task PlanAsync_ForMixedCapabilities_ShouldDeduplicateOnlyExactServiceId()
    {
        var evidence = StudioWorkflowEvidence(
            NyxIdCapability("usvc-shared-alpha", "catalog-slug-alpha"),
            ExplicitRequestCapability("usvc-shared-alpha", "different-proof-slug"));
        var planner = new ScheduledInvocationAuthorizationPlanner(
            new MutableCatalogQueryPort(Snapshot(
                Service("usvc-shared-alpha", "catalog-slug-alpha", AuthorizationGrantRequirement.NotRequired))),
            evidence,
            evidence,
            evidence,
            evidence);

        var result = await planner.PlanAsync(StudioRequest());

        result.Success.Should().BeTrue();
        result.Plan!.NyxIdServiceGrants.Should().ContainSingle()
            .Which.UserServiceId.Should().Be("usvc-shared-alpha");
    }

    [Fact]
    public async Task PlanAsync_ForMixedCapabilities_ShouldPreserveDifferentServiceIds()
    {
        var evidence = StudioWorkflowEvidence(
            NyxIdCapability("usvc-published-alpha", "published-slug-alpha"),
            ExplicitRequestCapability("usvc-explicit-beta", "proof-slug-beta"));
        var planner = new ScheduledInvocationAuthorizationPlanner(
            new MutableCatalogQueryPort(Snapshot(
                Service("usvc-published-alpha", "published-slug-alpha", AuthorizationGrantRequirement.NotRequired),
                Service("usvc-explicit-beta", "catalog-slug-beta", AuthorizationGrantRequirement.NotRequired))),
            evidence,
            evidence,
            evidence,
            evidence);

        var result = await planner.PlanAsync(StudioRequest());

        result.Success.Should().BeTrue();
        result.Plan!.NyxIdServiceGrants.Select(static grant => grant.UserServiceId)
            .Should().Equal("usvc-explicit-beta", "usvc-published-alpha");
    }

    [Fact]
    public async Task PlanAsync_ForExplicitRequestWithoutExactServiceId_ShouldFailClosed()
    {
        var evidence = StudioWorkflowEvidence(ExplicitRequestCapability(string.Empty, "proof-slug-alpha"));
        var planner = new ScheduledInvocationAuthorizationPlanner(
            new MutableCatalogQueryPort(Snapshot()),
            evidence,
            evidence,
            evidence,
            evidence);

        var result = await planner.PlanAsync(StudioRequest());

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(
            ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable);
        result.Detail.Should().Be("nyxid_exact_service_identity_unavailable");
    }

    [Fact]
    public async Task PlanAsync_ForUnknownWorkflowCapabilityVariant_ShouldFailClosed()
    {
        var evidence = StudioWorkflowEvidence(new ExternalWorkflowCapabilityRef());
        var planner = new ScheduledInvocationAuthorizationPlanner(
            new MutableCatalogQueryPort(Snapshot()),
            evidence,
            evidence,
            evidence,
            evidence);

        var result = await planner.PlanAsync(StudioRequest());

        result.Success.Should().BeFalse();
        result.Detail.Should().Be("workflow_external_capability_identity_unavailable");
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
        Request(serviceIds.Select(static serviceId => NyxIdService(serviceId, string.Empty)).ToArray());

    private static ScheduledInvocationAuthorizationRequest Request(
        IReadOnlyList<NyxIdUserServiceCapabilityRef> services) =>
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
            services,
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
            AuthorizationGrantRequirement.NotRequired,
            Now.AddDays(30),
            Now);

    private static NyxIdUserServiceCapabilityRef NyxIdService(string serviceId, string slug) => new()
    {
        UserServiceId = serviceId,
        ServiceSlugSnapshot = slug,
    };

    private static ScheduledInvocationOwnerLLMSelection GatewaySelection() => new()
    {
        RouteKind = LLMRouteKind.Gateway,
        RouteValue = ScheduledInvocationOwnerLLMSelectionPolicy.GatewayRoute,
        Model = "gpt-5.5",
    };

    private static ScheduledInvocationOwnerLLMSelection ServiceSelection(
        string serviceId,
        string serviceSlug) => new()
        {
            RouteKind = LLMRouteKind.NyxIdUserService,
            RouteValue = $"{ScheduledInvocationOwnerLLMSelectionPolicy.NyxIdProxyRoutePrefix}{serviceSlug}",
            NyxIdUserServiceId = serviceId,
            ServiceSlugSnapshot = serviceSlug,
            Model = "gpt-5.5",
        };

    private static ExternalWorkflowCapabilityRef NyxIdCapability(string serviceId, string slug) => new()
    {
        NyxIdUserService = NyxIdService(serviceId, slug),
    };

    private static ExternalWorkflowCapabilityRef ExplicitRequestCapability(
        string userServiceId,
        string proofSlug) =>
        new()
        {
            NyxIdUserRequest = new NyxIdUserRequestCapabilityRef
            {
                Request = new NyxIdRequestSelector
                {
                    UserServiceId = userServiceId,
                    Method = NyxIdRequestMethod.Get,
                    PathTemplate = "/api/resources/{resource_id}",
                    BodyMode = NyxIdRequestBodyMode.None,
                    ResponseMode = NyxIdRequestResponseMode.Text,
                },
                ServiceSlugSnapshot = proofSlug,
                ContractDigest = "request-proof-digest-alpha",
                ExplicitRequestGrantDigest = "request-grant-digest-alpha",
            },
        };

    private static StudioEvidencePorts StudioWorkflowEvidence(
        params ExternalWorkflowCapabilityRef[] capabilities) =>
        new()
        {
            Member = new ScheduledInvocationMemberEvidence(
                3,
                "wf-alpha",
                "rev-alpha",
                "svc-alpha"),
            Workflow = new ScheduledInvocationWorkflowEvidence(
                5,
                capabilities,
                false,
                capabilities.Length == 0
                    ? AuthorizationGrantRequirement.NotRequired
                    : AuthorizationGrantRequirement.Required),
        };

    private static ExternalWorkflowCapabilityRef ConnectorCapability(string connectorRef) => new()
    {
        HostConnector = new HostConnectorCapabilityRef
        {
            ConnectorCapabilityRef = connectorRef,
            OperationId = "operation-alpha",
            ContractDigest = "connector-digest-alpha",
        },
    };

    private static NyxIdAuthorizationCatalogSnapshot Snapshot(
        params NyxIdAuthorizationServiceEvidence[] services)
    {
        var owner = Owner();
        return new NyxIdAuthorizationCatalogSnapshot(
            owner,
            7,
            Now.AddMinutes(-1),
            Now.AddMinutes(15),
            "1",
            "api-key-scope-v1",
            Now.AddMinutes(-2),
            NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(owner, services),
            services,
            Activated: true);
    }

    private static NyxIdAuthorizationCatalogSnapshot SnapshotWithGateway(
        NyxIdAuthorizationLLMTargetEvidence gatewayTarget,
        params NyxIdAuthorizationServiceEvidence[] services)
    {
        var snapshot = Snapshot(services);
        return snapshot with
        {
            ContentDigest = NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(
                snapshot.Owner,
                snapshot.Services,
                gatewayTarget),
            GatewayLLMTarget = gatewayTarget,
        };
    }

    private static NyxIdAuthorizationLLMTargetEvidence GatewayTarget(params string[] modelIds) =>
        Target(
            LLMRouteKind.Gateway,
            ScheduledInvocationOwnerLLMSelectionPolicy.GatewayRoute,
            string.Empty,
            string.Empty,
            modelIds);

    private static NyxIdAuthorizationLLMTargetEvidence ServiceTarget(
        string serviceId,
        string serviceSlug,
        params string[] modelIds) =>
        Target(
            LLMRouteKind.NyxIdUserService,
            $"{ScheduledInvocationOwnerLLMSelectionPolicy.NyxIdProxyRoutePrefix}{serviceSlug}",
            serviceId,
            serviceSlug,
            modelIds);

    private static NyxIdAuthorizationLLMTargetEvidence Target(
        LLMRouteKind routeKind,
        string routeValue,
        string serviceId,
        string serviceSlug,
        params string[] modelIds)
    {
        var target = new NyxIdAuthorizationLLMTargetEvidence
        {
            RouteKind = routeKind,
            RouteValue = routeValue,
            NyxIdUserServiceId = serviceId,
            ServiceSlugSnapshot = serviceSlug,
            ModelCatalog = new LLMModelCatalog
            {
                Certainty = LLMModelCatalogCertainty.Enumerated,
                DefaultModelId = modelIds.FirstOrDefault() ?? string.Empty,
            },
            ObservedAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(-1)),
            FreshUntil = Timestamp.FromDateTimeOffset(Now.AddMinutes(15)),
            EvaluatedAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(-2)),
            AuthorityContractVersion = "openai-models/v1",
            AuthorityPolicyVersion = "nyxid-exact-route-models/v1",
        };
        target.ModelCatalog.ModelIds.Add(modelIds.Order(StringComparer.Ordinal));
        return target;
    }

    private static AuthorizationOwnerIdentity Owner() => new()
    {
        Authority = NyxIdAuthorizationAuthorities.NyxId,
        OwnerKind = AuthorizationOwnerKind.Personal,
        OwnerSubject = "user-alpha",
    };

    private static AuthorizationOwnerIdentity Identity(
        AuthorizationOwnerKind ownerKind,
        string ownerSubject) => new()
        {
            Authority = NyxIdAuthorizationAuthorities.NyxId,
            OwnerKind = ownerKind,
            OwnerSubject = ownerSubject,
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
            string scopeId,
            CancellationToken ct)
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
            string scopeId,
            CancellationToken ct) => Task.FromResult<ScheduledInvocationOwnerLLMEvidence?>(null);
    }

    private sealed class NoServiceOwnerLLMEvidencePort : IScheduledInvocationOwnerLLMEvidenceQueryPort
    {
        public static readonly NoServiceOwnerLLMEvidencePort Instance = new();

        public Task<ScheduledInvocationOwnerLLMEvidence?> GetAsync(
            string scopeId,
            CancellationToken ct = default) =>
            Task.FromResult<ScheduledInvocationOwnerLLMEvidence?>(null);
    }
}
