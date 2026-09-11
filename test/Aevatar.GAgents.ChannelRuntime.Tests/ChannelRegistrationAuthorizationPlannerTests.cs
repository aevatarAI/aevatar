using System.Reflection;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.NyxIdRelay;
using FluentAssertions;
using Xunit;
using static Aevatar.GAgents.Channel.NyxIdRelay.VerifiedChannelRegistrationServiceSelection;
using static Aevatar.GAgents.Channel.NyxIdRelay.VerifiedChannelRegistrationServiceSelection.VerifiedChannelRegistrationAuthorizationPlan;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelRegistrationAuthorizationPlannerTests
{
    private const string ValidDigest =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void PlanningRequest_ExposesTrustedRegistrationOwnerScope()
    {
        var requestType = typeof(ChannelRegistrationAuthorizationPlanningRequest);
        var constructor = requestType.GetConstructors().Should().ContainSingle().Subject;

        constructor.GetParameters().Should().ContainSingle(parameter =>
            parameter.ParameterType == typeof(VerifiedChannelRegistrationOwner));
        var constructorParameterNames = constructor.GetParameters()
            .Select(static parameter => parameter.Name)
            .ToArray();
        constructorParameterNames.Should().NotContain("AuthenticatedActorId");
        constructorParameterNames.Should().NotContain("RegistrationOwnerScopeId");
        requestType.GetProperty("RegistrationOwner", BindingFlags.Instance | BindingFlags.Public)
            .Should().NotBeNull();
        typeof(VerifiedChannelRegistrationServiceSelection)
            .GetProperty("RegistrationOwnerScopeId", BindingFlags.Instance | BindingFlags.Public)
            .Should().NotBeNull();
    }

    [Fact]
    public void VerifiedPlan_PublicSurface_DoesNotExposeConstructionMutationOrRecordCloning()
    {
        var planType = typeof(VerifiedChannelRegistrationAuthorizationPlan);

        planType.GetConstructors(BindingFlags.Instance | BindingFlags.Public)
            .Should().BeEmpty();
        planType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Should().OnlyContain(property => property.GetSetMethod(nonPublic: true) == null);
        planType.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Should().BeNull();
        planType.GetProperty("EqualityContract", BindingFlags.Instance | BindingFlags.NonPublic)
            .Should().BeNull();
    }

    [Fact]
    public async Task PlanAsync_SourceAndReturnedCollections_CannotMutateVerifiedPlan()
    {
        var registrationServiceIds = new[] { "svc-alpha" };
        var providerAllowedServiceIds = new[] { "svc-alpha" };
        var providerAllowedNodeIds = new[] { "node-a" };
        var providerNodeIds = new[] { "node-a" };
        var owner = PersonalPrincipal("owner-alpha");
        var providerPlan = ScopePlan(
            "owner-alpha",
            owner,
            ServiceGrant(
                "svc-alpha",
                owner,
                new NyxIdScopePlanNodeGrant(
                    NyxIdScopePlanNodeGrantKind.Required,
                    providerNodeIds))).Value! with
        {
            AllowedServiceIds = providerAllowedServiceIds,
            AllowedNodeIds = providerAllowedNodeIds,
        };
        var port = new StubNyxIdAuthorizationPort(
            UserServices(PersonalService("svc-alpha", "alpha")),
            new NyxIdApiAccessResult<NyxIdApiKeyScopePlan>(providerPlan, null));
        var planner = new ChannelRegistrationAuthorizationPlanner(port);

        var result = await PlanAsync(planner, "owner-alpha", registrationServiceIds);
        var verifiedPlan = result.Plan!;

        registrationServiceIds[0] = "mutated-registration";
        providerAllowedServiceIds[0] = "mutated-service";
        providerAllowedNodeIds[0] = "mutated-node";
        providerNodeIds[0] = "mutated-grant-node";

        verifiedPlan.RegistrationServiceIds.Should().Equal("svc-alpha");
        verifiedPlan.AllowedServiceIds.Should().Equal("svc-alpha");
        verifiedPlan.AllowedNodeIds.Should().Equal("node-a");
        verifiedPlan.RegistrationServiceIds.Should().NotBeAssignableTo<string[]>();
        verifiedPlan.AllowedServiceIds.Should().NotBeAssignableTo<string[]>();
        verifiedPlan.AllowedNodeIds.Should().NotBeAssignableTo<string[]>();

        var mutateRegistration = () =>
            ((IList<string>)verifiedPlan.RegistrationServiceIds)[0] = "mutated";
        var mutateServices = () =>
            ((IList<string>)verifiedPlan.AllowedServiceIds)[0] = "mutated";
        var mutateNodes = () =>
            ((IList<string>)verifiedPlan.AllowedNodeIds)[0] = "mutated";

        mutateRegistration.Should().Throw<NotSupportedException>();
        mutateServices.Should().Throw<NotSupportedException>();
        mutateNodes.Should().Throw<NotSupportedException>();
        verifiedPlan.RegistrationServiceIds.Should().Equal("svc-alpha");
        verifiedPlan.AllowedServiceIds.Should().Equal("svc-alpha");
        verifiedPlan.AllowedNodeIds.Should().Equal("node-a");
    }

    [Fact]
    public async Task PlanAsync_ExactPersonalService_ReturnsVerifiedScopePlan()
    {
        var port = new StubNyxIdAuthorizationPort(
            UserServices(PersonalService("svc-alpha", "github")),
            ScopePlan(
                actorId: "owner-alpha",
                owner: PersonalPrincipal("owner-alpha"),
                ServiceGrant("svc-alpha", PersonalPrincipal("owner-alpha"))));
        var planner = new ChannelRegistrationAuthorizationPlanner(port);

        var result = await planner.PlanAsync(
            new ChannelRegistrationAuthorizationPlanningRequest(
                AccessToken: "owner-token",
                RegistrationOwner: PersonalOwner("owner-alpha"),
                RegistrationServiceIds: ["svc-alpha"],
                RequiredServiceIds: []),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.ErrorCode.Should().BeEmpty();
        result.Plan.Should().NotBeNull();
        result.Plan!.AuthenticatedActorId.Should().Be("owner-alpha");
        result.Plan.KeyOwner.Should().Be(new ChannelRegistrationKeyOwner(
            ChannelRegistrationKeyOwnerKind.Personal,
            "owner-alpha"));
        result.Plan.TargetOrganizationId.Should().BeNull();
        result.Plan.RegistrationServiceIds.Should().Equal("svc-alpha");
        result.Plan.AllowedServiceIds.Should().Equal("svc-alpha");
        result.Plan.AllowedNodeIds.Should().BeEmpty();
        result.Plan.ScopePlanDigest.Should().Be(ValidDigest);
        port.InventoryTokens.Should().Equal("owner-token");
        port.ScopePlanRequests.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ScopePlanRequest("owner-token", ["svc-alpha"], null));
    }

    [Fact]
    public async Task PlanAsync_PersonalServiceWithDifferentRegistrationOwnerScope_ReturnsOwnerForbiddenWithoutScopePlan()
    {
        var owner = PersonalPrincipal("owner-alpha");
        var port = new StubNyxIdAuthorizationPort(
            UserServices(PersonalService("svc-alpha", "github")),
            ScopePlan("owner-alpha", owner, ServiceGrant("svc-alpha", owner)));
        var planner = new ChannelRegistrationAuthorizationPlanner(port);

        var result = await PlanAsync(
            planner,
            "owner-alpha",
            ["svc-alpha"],
            registrationOwnerScopeId: "org-alpha");

        AssertFailure(result, "service_owner_forbidden");
        port.ScopePlanRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("github")]
    [InlineData("GitHub")]
    [InlineData("catalog-github")]
    [InlineData("svc")]
    [InlineData("svc-missing")]
    public async Task PlanAsync_NonIdServiceCandidate_ReturnsNotFoundWithoutScopePlan(
        string serviceIdCandidate)
    {
        var port = new StubNyxIdAuthorizationPort(
            UserServices(PersonalService(
                "svc-alpha",
                "github",
                label: "GitHub",
                catalogServiceId: "catalog-github")),
            ScopePlan(
                "owner-alpha",
                PersonalPrincipal("owner-alpha"),
                ServiceGrant("svc-alpha", PersonalPrincipal("owner-alpha"))));
        var planner = new ChannelRegistrationAuthorizationPlanner(port);

        var result = await PlanAsync(planner, "owner-alpha", [serviceIdCandidate]);

        AssertFailure(result, "user_service_not_found");
        port.ScopePlanRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanAsync_InactivePersonalService_ReturnsOwnerForbiddenWithoutScopePlan()
    {
        var port = new StubNyxIdAuthorizationPort(
            UserServices(PersonalService("svc-alpha", "github", isActive: false)),
            ScopePlan(
                "owner-alpha",
                PersonalPrincipal("owner-alpha"),
                ServiceGrant("svc-alpha", PersonalPrincipal("owner-alpha"))));
        var planner = new ChannelRegistrationAuthorizationPlanner(port);

        var result = await PlanAsync(planner, "owner-alpha", ["svc-alpha"]);

        AssertFailure(result, "service_owner_forbidden");
        port.ScopePlanRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanAsync_AuthorizedOrganizationAdmin_ReturnsOrganizationScopePlan()
    {
        var owner = OrganizationPrincipal("org-alpha");
        var port = new StubNyxIdAuthorizationPort(
            UserServices(OrganizationService("svc-alpha", "org-alpha")),
            ScopePlan("owner-alpha", owner, ServiceGrant("svc-alpha", owner)));
        var planner = new ChannelRegistrationAuthorizationPlanner(port);

        var result = await PlanAsync(
            planner,
            "owner-alpha",
            ["svc-alpha"],
            registrationOwnerScopeId: "org-alpha");

        result.Succeeded.Should().BeTrue();
        result.Plan!.KeyOwner.Should().Be(new ChannelRegistrationKeyOwner(
            ChannelRegistrationKeyOwnerKind.Organization,
            "org-alpha"));
        result.Plan.TargetOrganizationId.Should().Be("org-alpha");
        port.ScopePlanRequests.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ScopePlanRequest("owner-token", ["svc-alpha"], "org-alpha"));
    }

    [Fact]
    public async Task PlanAsync_OrganizationServiceWithDifferentRegistrationOwnerScope_ReturnsOwnerForbiddenWithoutScopePlan()
    {
        var owner = OrganizationPrincipal("org-alpha");
        var port = new StubNyxIdAuthorizationPort(
            UserServices(OrganizationService("svc-alpha", "org-alpha")),
            ScopePlan("owner-alpha", owner, ServiceGrant("svc-alpha", owner)));
        var planner = new ChannelRegistrationAuthorizationPlanner(port);

        var result = await PlanAsync(
            planner,
            "owner-alpha",
            ["svc-alpha"],
            registrationOwnerScopeId: "org-beta");

        AssertFailure(result, "service_owner_forbidden");
        port.ScopePlanRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(NyxIdOrganizationRole.Admin, false)]
    [InlineData(NyxIdOrganizationRole.Member, true)]
    [InlineData(NyxIdOrganizationRole.Viewer, true)]
    [InlineData(NyxIdOrganizationRole.Unspecified, true)]
    public async Task PlanAsync_UnmanageableOrganizationService_ReturnsOwnerForbiddenWithoutScopePlan(
        NyxIdOrganizationRole role,
        bool allowed)
    {
        var port = new StubNyxIdAuthorizationPort(
            UserServices(OrganizationService("svc-alpha", "org-alpha", role, allowed)),
            ScopePlan(
                "owner-alpha",
                OrganizationPrincipal("org-alpha"),
                ServiceGrant("svc-alpha", OrganizationPrincipal("org-alpha"))));
        var planner = new ChannelRegistrationAuthorizationPlanner(port);

        var result = await PlanAsync(
            planner,
            "owner-alpha",
            ["svc-alpha"],
            registrationOwnerScopeId: "org-alpha");

        AssertFailure(result, "service_owner_forbidden");
        port.ScopePlanRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanAsync_PersonalAndOrganizationServices_ReturnsOwnerForbiddenWithoutScopePlan()
    {
        var port = new StubNyxIdAuthorizationPort(
            UserServices(
                PersonalService("svc-personal", "personal"),
                OrganizationService("svc-org", "org-alpha")),
            ScopePlan(
                "owner-alpha",
                PersonalPrincipal("owner-alpha"),
                ServiceGrant("svc-personal", PersonalPrincipal("owner-alpha"))));
        var planner = new ChannelRegistrationAuthorizationPlanner(port);

        var result = await PlanAsync(
            planner,
            "owner-alpha",
            ["svc-org", "svc-personal"]);

        AssertFailure(result, "service_owner_forbidden");
        port.ScopePlanRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanAsync_ServicesFromDifferentOrganizations_ReturnsOwnerForbiddenWithoutScopePlan()
    {
        var port = new StubNyxIdAuthorizationPort(
            UserServices(
                OrganizationService("svc-alpha", "org-alpha"),
                OrganizationService("svc-beta", "org-beta")),
            ScopePlan(
                "owner-alpha",
                OrganizationPrincipal("org-alpha"),
                ServiceGrant("svc-alpha", OrganizationPrincipal("org-alpha"))));
        var planner = new ChannelRegistrationAuthorizationPlanner(port);

        var result = await PlanAsync(
            planner,
            "owner-alpha",
            ["svc-alpha", "svc-beta"],
            registrationOwnerScopeId: "org-alpha");

        AssertFailure(result, "service_owner_forbidden");
        port.ScopePlanRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanAsync_InventoryFailure_ReturnsScopePlanUnavailableWithoutScopePlanCall()
    {
        var inventoryFailure = new NyxIdApiAccessResult<NyxIdUserServices>(
            null,
            new NyxIdApiAccessFailure(
                NyxIdApiAccessFailureKind.Transport,
                "nyxid_user_services_failed"));
        var port = new StubNyxIdAuthorizationPort(
            inventoryFailure,
            ScopePlan(
                "owner-alpha",
                PersonalPrincipal("owner-alpha"),
                ServiceGrant("svc-alpha", PersonalPrincipal("owner-alpha"))));
        var planner = new ChannelRegistrationAuthorizationPlanner(port);

        var result = await PlanAsync(planner, "owner-alpha", ["svc-alpha"]);

        AssertFailure(result, "nyxid_scope_plan_unavailable");
        port.ScopePlanRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanAsync_RegistrationAndRequiredServices_PlansExactCombinedSet()
    {
        var owner = PersonalPrincipal("owner-alpha");
        var port = new StubNyxIdAuthorizationPort(
            UserServices(
                PersonalService("svc-alpha", "alpha"),
                PersonalService("svc-beta", "beta")),
            ScopePlan(
                "owner-alpha",
                owner,
                ServiceGrant("svc-alpha", owner),
                ServiceGrant("svc-beta", owner)));
        var planner = new ChannelRegistrationAuthorizationPlanner(port);

        var result = await PlanAsync(
            planner,
            "owner-alpha",
            ["svc-beta"],
            ["svc-alpha"]);

        result.Succeeded.Should().BeTrue();
        result.Plan!.RegistrationServiceIds.Should().Equal("svc-beta");
        result.Plan.AllowedServiceIds.Should().Equal("svc-alpha", "svc-beta");
        port.ScopePlanRequests.Should().ContainSingle().Which.ServiceIds
            .Should().Equal("svc-alpha", "svc-beta");
    }

    [Fact]
    public async Task PlanAsync_EmptyServiceSet_UsesAuthenticatedPersonalOwnerAndEmptyScopePlan()
    {
        var port = new StubNyxIdAuthorizationPort(
            UserServices(),
            ScopePlan("owner-alpha", PersonalPrincipal("owner-alpha")));
        var planner = new ChannelRegistrationAuthorizationPlanner(port);

        var result = await PlanAsync(planner, "owner-alpha", []);

        result.Succeeded.Should().BeTrue();
        result.Plan!.KeyOwner.Should().Be(new ChannelRegistrationKeyOwner(
            ChannelRegistrationKeyOwnerKind.Personal,
            "owner-alpha"));
        result.Plan.TargetOrganizationId.Should().BeNull();
        result.Plan.RegistrationServiceIds.Should().BeEmpty();
        result.Plan.AllowedServiceIds.Should().BeEmpty();
        result.Plan.AllowedNodeIds.Should().BeEmpty();
        port.ScopePlanRequests.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ScopePlanRequest("owner-token", [], null));
    }

    [Fact]
    public async Task PlanAsync_EmptyServiceSetWithOrganizationOwnerScope_UsesExactOrganizationTarget()
    {
        var port = new StubNyxIdAuthorizationPort(
            UserServices(),
            ScopePlan("owner-alpha", OrganizationPrincipal("org-alpha")));
        var planner = new ChannelRegistrationAuthorizationPlanner(port);

        var result = await PlanAsync(
            planner,
            "owner-alpha",
            [],
            registrationOwnerScopeId: "org-alpha");

        result.Succeeded.Should().BeTrue();
        result.Plan!.KeyOwner.Should().Be(new ChannelRegistrationKeyOwner(
            ChannelRegistrationKeyOwnerKind.Organization,
            "org-alpha"));
        result.Plan.TargetOrganizationId.Should().Be("org-alpha");
        port.ScopePlanRequests.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ScopePlanRequest("owner-token", [], "org-alpha"));
    }

    [Fact]
    public async Task PlanAsync_SecondPassPreservesEmptyOrganizationSelectionOwnerScope()
    {
        var owner = OrganizationPrincipal("org-alpha");
        var port = new StubNyxIdAuthorizationPort(
            UserServices(OrganizationService("svc-dependency", "org-alpha")),
            ScopePlan(
                "owner-alpha",
                owner,
                ServiceGrant("svc-dependency", owner)));
        var planner = new ChannelRegistrationAuthorizationPlanner(port);
        var verified = await planner.VerifySelectionAsync(
            new ChannelRegistrationAuthorizationPlanningRequest(
                "owner-token",
                OrganizationOwner("owner-alpha", "org-alpha"),
                [],
                []),
            CancellationToken.None);

        var result = await planner.PlanAsync(
            verified.Selection!,
            ["svc-dependency"],
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Plan!.KeyOwner.Should().Be(new ChannelRegistrationKeyOwner(
            ChannelRegistrationKeyOwnerKind.Organization,
            "org-alpha"));
        result.Plan.TargetOrganizationId.Should().Be("org-alpha");
        port.ScopePlanRequests.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ScopePlanRequest("owner-token", ["svc-dependency"], "org-alpha"));
    }

    [Fact]
    public async Task PlanAsync_ScopePlanProviderFailure_ReturnsUnavailable()
    {
        var port = new StubNyxIdAuthorizationPort(
            UserServices(PersonalService("svc-alpha", "alpha")),
            new NyxIdApiAccessResult<NyxIdApiKeyScopePlan>(
                null,
                new NyxIdApiAccessFailure(
                    NyxIdApiAccessFailureKind.Provider,
                    "api_key_scope_plan_denied")));
        var planner = new ChannelRegistrationAuthorizationPlanner(port);

        var result = await PlanAsync(planner, "owner-alpha", ["svc-alpha"]);

        AssertFailure(result, "nyxid_scope_plan_unavailable");
        port.ScopePlanRequests.Should().ContainSingle();
    }

    [Theory]
    [InlineData("authority")]
    [InlineData("contract_version")]
    [InlineData("policy_version")]
    [InlineData("authenticated_actor_id")]
    [InlineData("authenticated_actor_kind")]
    [InlineData("intended_owner_id")]
    [InlineData("intended_owner_kind")]
    [InlineData("service_set")]
    [InlineData("allowed_service_set")]
    [InlineData("resource_owner_id")]
    [InlineData("resource_owner_kind")]
    [InlineData("allowed_nodes")]
    [InlineData("freshness_mode")]
    [InlineData("freshness_precondition")]
    [InlineData("freshness_drift")]
    [InlineData("completeness_list")]
    [InlineData("completeness_duplicates")]
    [InlineData("completeness_basis")]
    [InlineData("completeness_transient_nodes")]
    [InlineData("evaluation_time_default")]
    [InlineData("evaluation_time_non_utc")]
    [InlineData("digest_prefix")]
    [InlineData("digest_uppercase")]
    public async Task PlanAsync_ScopePlanContractDrift_ReturnsUnavailable(string drift)
    {
        var owner = PersonalPrincipal("owner-alpha");
        var valid = ScopePlan(
            "owner-alpha",
            owner,
            ServiceGrant(
                "svc-alpha",
                owner,
                new NyxIdScopePlanNodeGrant(
                    NyxIdScopePlanNodeGrantKind.Required,
                    ["node-a"]))).Value!;
        var port = new StubNyxIdAuthorizationPort(
            UserServices(PersonalService("svc-alpha", "alpha")),
            new NyxIdApiAccessResult<NyxIdApiKeyScopePlan>(
                ApplyScopePlanDrift(valid, drift),
                null));
        var planner = new ChannelRegistrationAuthorizationPlanner(port);

        var result = await PlanAsync(planner, "owner-alpha", ["svc-alpha"]);

        AssertFailure(result, "nyxid_scope_plan_unavailable");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("svc-alpha ")]
    public async Task PlanAsync_UnnormalizedServiceId_RejectsBeforeInventory(string serviceId)
    {
        var port = new StubNyxIdAuthorizationPort(
            UserServices(PersonalService("svc-alpha", "alpha")),
            ScopePlan(
                "owner-alpha",
                PersonalPrincipal("owner-alpha"),
                ServiceGrant("svc-alpha", PersonalPrincipal("owner-alpha"))));
        var planner = new ChannelRegistrationAuthorizationPlanner(port);

        var act = () => PlanAsync(planner, "owner-alpha", [serviceId]);

        await act.Should().ThrowAsync<ArgumentException>();
        port.InventoryTokens.Should().BeEmpty();
        port.ScopePlanRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("org-alpha ")]
    public async Task PlanAsync_NonCanonicalRegistrationOwnerScope_RejectsBeforeInventory(string ownerScopeId)
    {
        var port = new StubNyxIdAuthorizationPort(
            UserServices(PersonalService("svc-alpha", "alpha")),
            ScopePlan(
                "owner-alpha",
                PersonalPrincipal("owner-alpha"),
                ServiceGrant("svc-alpha", PersonalPrincipal("owner-alpha"))));
        var planner = new ChannelRegistrationAuthorizationPlanner(port);

        var result = await PlanAsync(
            planner,
            "owner-alpha",
            ["svc-alpha"],
            registrationOwnerScopeId: ownerScopeId);

        AssertFailure(result, "service_owner_forbidden");
        port.InventoryTokens.Should().BeEmpty();
        port.ScopePlanRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanAsync_CanceledInventory_PropagatesCancellationWithoutScopePlanCall()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var port = new StubNyxIdAuthorizationPort(
            UserServices(PersonalService("svc-alpha", "alpha")),
            ScopePlan(
                "owner-alpha",
                PersonalPrincipal("owner-alpha"),
                ServiceGrant("svc-alpha", PersonalPrincipal("owner-alpha"))));
        var planner = new ChannelRegistrationAuthorizationPlanner(port);

        var act = () => PlanAsync(
            planner,
            "owner-alpha",
            ["svc-alpha"],
            ct: cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        port.ScopePlanRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanAsync_CanceledScopePlan_PropagatesCancellation()
    {
        var port = new StubNyxIdAuthorizationPort(
            UserServices(PersonalService("svc-alpha", "alpha")),
            ScopePlan(
                "owner-alpha",
                PersonalPrincipal("owner-alpha"),
                ServiceGrant("svc-alpha", PersonalPrincipal("owner-alpha"))))
        {
            CancelScopePlan = true,
        };
        var planner = new ChannelRegistrationAuthorizationPlanner(port);

        var act = () => PlanAsync(planner, "owner-alpha", ["svc-alpha"]);

        await act.Should().ThrowAsync<OperationCanceledException>();
        port.ScopePlanRequests.Should().ContainSingle();
    }

    private static NyxIdApiAccessResult<NyxIdUserServices> UserServices(
        params NyxIdUserService[] services) =>
        new(new NyxIdUserServices(services), null);

    private static NyxIdUserService PersonalService(
        string id,
        string slug,
        string? label = null,
        string? catalogServiceId = null,
        bool isActive = true) => new(
        id,
        slug,
        Label: label,
        CatalogServiceName: null,
        IsActive: isActive,
        new NyxIdUserServiceCredentialSource(NyxIdUserServiceCredentialSourceKind.Personal),
        CatalogServiceId: catalogServiceId);

    private static NyxIdUserService OrganizationService(
        string id,
        string organizationId,
        NyxIdOrganizationRole role = NyxIdOrganizationRole.Admin,
        bool allowed = true) => new(
        id,
        id + "-slug",
        Label: null,
        CatalogServiceName: null,
        IsActive: true,
        new NyxIdUserServiceCredentialSource(
            NyxIdUserServiceCredentialSourceKind.Organization,
            organizationId,
            organizationId + " name",
            OrganizationRole: role,
            Allowed: allowed));

    private static NyxIdScopePlanPrincipal PersonalPrincipal(string id) =>
        new(id, NyxIdScopePlanPrincipalKind.Personal);

    private static NyxIdScopePlanPrincipal OrganizationPrincipal(string id) =>
        new(id, NyxIdScopePlanPrincipalKind.Organization);

    private static NyxIdScopePlanServiceGrant ServiceGrant(
        string id,
        NyxIdScopePlanPrincipal owner,
        NyxIdScopePlanNodeGrant? nodeGrant = null) =>
        new(
            id,
            owner,
            nodeGrant ?? new NyxIdScopePlanNodeGrant(
                NyxIdScopePlanNodeGrantKind.NotRequired,
                []));

    private static NyxIdApiAccessResult<NyxIdApiKeyScopePlan> ScopePlan(
        string actorId,
        NyxIdScopePlanPrincipal owner,
        params NyxIdScopePlanServiceGrant[] services) =>
        new(
            new NyxIdApiKeyScopePlan(
                NyxIdApiAccessResponseParser.ScopePlanAuthority,
                NyxIdApiAccessResponseParser.ScopePlanContractVersion,
                NyxIdApiAccessResponseParser.ScopePlanPolicyVersion,
                PersonalPrincipal(actorId),
                owner,
                services,
                services.Select(static service => service.UserServiceId).ToArray(),
                services.SelectMany(static service => service.NodeGrant.NodeIds)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                DateTimeOffset.Parse("2026-09-10T00:00:00Z"),
                ValidDigest,
                new NyxIdScopePlanFreshness(
                    NyxIdScopePlanFreshnessMode.MutationRevalidatedSnapshot,
                    "scope_plan_digest",
                    NyxIdScopePlanPostCreationDrift.FailClosed),
                new NyxIdScopePlanCompleteness(
                    true,
                    true,
                    NyxIdScopePlanRouteCandidateBasis.ActiveConfiguredRoutes,
                    true)),
            null);

    private static NyxIdApiKeyScopePlan ApplyScopePlanDrift(
        NyxIdApiKeyScopePlan plan,
        string drift) => drift switch
        {
            "authority" => plan with { Authority = "not-nyxid" },
            "contract_version" => plan with { ContractVersion = "2" },
            "policy_version" => plan with { PolicyVersion = "api-key-scope-v2" },
            "authenticated_actor_id" => plan with
            {
                AuthenticatedActor = PersonalPrincipal("owner-other"),
            },
            "authenticated_actor_kind" => plan with
            {
                AuthenticatedActor = OrganizationPrincipal("owner-alpha"),
            },
            "intended_owner_id" => plan with
            {
                IntendedKeyOwner = PersonalPrincipal("owner-other"),
            },
            "intended_owner_kind" => plan with
            {
                IntendedKeyOwner = OrganizationPrincipal("owner-alpha"),
            },
            "service_set" => plan with
            {
                Services = [ServiceGrant("svc-other", PersonalPrincipal("owner-alpha"))],
            },
            "allowed_service_set" => plan with { AllowedServiceIds = ["svc-other"] },
            "resource_owner_id" => plan with
            {
                Services = [ServiceGrant("svc-alpha", PersonalPrincipal("owner-other"))],
            },
            "resource_owner_kind" => plan with
            {
                Services = [ServiceGrant("svc-alpha", OrganizationPrincipal("owner-alpha"))],
            },
            "allowed_nodes" => plan with { AllowedNodeIds = ["node-b"] },
            "freshness_mode" => plan with
            {
                Freshness = plan.Freshness with
                {
                    Mode = NyxIdScopePlanFreshnessMode.Unspecified,
                },
            },
            "freshness_precondition" => plan with
            {
                Freshness = plan.Freshness with { PreconditionField = "etag" },
            },
            "freshness_drift" => plan with
            {
                Freshness = plan.Freshness with
                {
                    PostCreationDrift = NyxIdScopePlanPostCreationDrift.Unspecified,
                },
            },
            "completeness_list" => plan with
            {
                Completeness = plan.Completeness with { ListComplete = false },
            },
            "completeness_duplicates" => plan with
            {
                Completeness = plan.Completeness with { NoDuplicates = false },
            },
            "completeness_basis" => plan with
            {
                Completeness = plan.Completeness with
                {
                    RouteCandidateBasis = NyxIdScopePlanRouteCandidateBasis.Unspecified,
                },
            },
            "completeness_transient_nodes" => plan with
            {
                Completeness = plan.Completeness with
                {
                    TransientNodeStateExcluded = false,
                },
            },
            "evaluation_time_default" => plan with
            {
                EvaluatedAtUtc = default,
            },
            "evaluation_time_non_utc" => plan with
            {
                EvaluatedAtUtc = new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.FromHours(8)),
            },
            "digest_prefix" => plan with
            {
                NormalizedGrantDigest = "md5:" + new string('a', 64),
            },
            "digest_uppercase" => plan with
            {
                NormalizedGrantDigest = "sha256:" + new string('A', 64),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(drift), drift, null),
        };

    private static Task<ChannelRegistrationAuthorizationPlanningResult> PlanAsync(
        ChannelRegistrationAuthorizationPlanner planner,
        string actorId,
        IReadOnlyList<string> registrationServiceIds,
        IReadOnlyList<string>? requiredServiceIds = null,
        string? registrationOwnerScopeId = null,
        CancellationToken ct = default) =>
        planner.PlanAsync(
            new ChannelRegistrationAuthorizationPlanningRequest(
                "owner-token",
                string.Equals(registrationOwnerScopeId, actorId, StringComparison.Ordinal) ||
                registrationOwnerScopeId is null
                    ? PersonalOwner(actorId)
                    : OrganizationOwner(actorId, registrationOwnerScopeId),
                registrationServiceIds,
                requiredServiceIds ?? []),
            ct);

    private static VerifiedChannelRegistrationOwner PersonalOwner(string actorId) =>
        new(
            actorId,
            new ChannelRegistrationKeyOwner(ChannelRegistrationKeyOwnerKind.Personal, actorId),
            null);

    private static VerifiedChannelRegistrationOwner OrganizationOwner(
        string actorId,
        string organizationId) =>
        new(
            actorId,
            new ChannelRegistrationKeyOwner(
                ChannelRegistrationKeyOwnerKind.Organization,
                organizationId),
            organizationId);

    private static void AssertFailure(
        ChannelRegistrationAuthorizationPlanningResult result,
        string errorCode)
    {
        result.Succeeded.Should().BeFalse();
        result.Plan.Should().BeNull();
        result.ErrorCode.Should().Be(errorCode);
    }

    private sealed record ScopePlanRequest(
        string AccessToken,
        IReadOnlyList<string> ServiceIds,
        string? TargetOrganizationId);

    private sealed class StubNyxIdAuthorizationPort(
        NyxIdApiAccessResult<NyxIdUserServices> inventory,
        NyxIdApiAccessResult<NyxIdApiKeyScopePlan> scopePlan)
        : IChannelRegistrationNyxIdAuthorizationPort
    {
        public List<string> InventoryTokens { get; } = [];

        public List<ScopePlanRequest> ScopePlanRequests { get; } = [];

        public bool CancelScopePlan { get; init; }

        public Task<NyxIdApiAccessResult<NyxIdUserServices>> ReadUserServicesAsync(
            string accessToken,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            InventoryTokens.Add(accessToken);
            return Task.FromResult(inventory);
        }

        public Task<NyxIdApiAccessResult<NyxIdApiKeyScopePlan>> PlanApiKeyScopeAsync(
            string accessToken,
            IReadOnlyList<string> selectedServiceIds,
            string? targetOrganizationId,
            CancellationToken ct)
        {
            ScopePlanRequests.Add(new ScopePlanRequest(
                accessToken,
                selectedServiceIds.ToArray(),
                targetOrganizationId));
            if (CancelScopePlan)
                throw new OperationCanceledException(ct);
            return Task.FromResult(scopePlan);
        }
    }
}
