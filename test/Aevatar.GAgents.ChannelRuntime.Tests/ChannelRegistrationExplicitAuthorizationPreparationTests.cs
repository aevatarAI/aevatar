using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.NyxIdRelay;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static Aevatar.GAgents.Channel.NyxIdRelay.VerifiedChannelRegistrationExplicitAuthorization;
using static Aevatar.GAgents.Channel.NyxIdRelay.VerifiedChannelRegistrationServiceSelection.VerifiedChannelRegistrationAuthorizationPlan;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelRegistrationExplicitAuthorizationPreparationTests
{
    internal static async Task<VerifiedChannelRegistrationExplicitAuthorization> PrepareVerifiedAsync(
        string? organizationId = null, bool requiresNodes = false)
    {
        var ownerId = organizationId ?? "owner-alpha";
        var actor = new NyxIdScopePlanPrincipal("owner-alpha", NyxIdScopePlanPrincipalKind.Personal);
        var owner = organizationId is null ? actor : new NyxIdScopePlanPrincipal(ownerId, NyxIdScopePlanPrincipalKind.Organization);
        var verifiedOwner = new VerifiedChannelRegistrationOwner("owner-alpha", new(
            organizationId is null ? ChannelRegistrationKeyOwnerKind.Personal : ChannelRegistrationKeyOwnerKind.Organization,
            ownerId), organizationId);
        string[] nodes = requiresNodes ? ["node-alpha", "node-beta"] : [];
        var port = Substitute.For<IChannelRegistrationNyxIdAuthorizationPort>();
        port.ReadUserServicesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new NyxIdApiAccessResult<NyxIdUserServices>(new NyxIdUserServices([
                new("svc-alpha", "svc-alpha-slug", null, null, true,
                    organizationId is null ? new(NyxIdUserServiceCredentialSourceKind.Personal) :
                        new(NyxIdUserServiceCredentialSourceKind.Organization, organizationId,
                            OrganizationRole: NyxIdOrganizationRole.Admin))]), null));
        port.PlanApiKeyScopeAsync("owner-token", Arg.Any<IReadOnlyList<string>>(), organizationId, Arg.Any<CancellationToken>())
            .Returns(new NyxIdApiAccessResult<NyxIdApiKeyScopePlan>(new(
                NyxIdApiAccessResponseParser.ScopePlanAuthority, NyxIdApiAccessResponseParser.ScopePlanContractVersion,
                NyxIdApiAccessResponseParser.ScopePlanPolicyVersion, actor, owner,
                [new("svc-alpha", owner, new(requiresNodes ? NyxIdScopePlanNodeGrantKind.Required : NyxIdScopePlanNodeGrantKind.NotRequired, nodes))],
                ["svc-alpha"], nodes, DateTimeOffset.Parse("2026-09-10T00:00:00Z"), ChannelExplicitAuthorizationTestSupport.Digest,
                new(NyxIdScopePlanFreshnessMode.MutationRevalidatedSnapshot, "scope_plan_digest", NyxIdScopePlanPostCreationDrift.FailClosed),
                new(true, true, NyxIdScopePlanRouteCandidateBasis.ActiveConfiguredRoutes, true)), null));
        var request = new ChannelRelayRegistrationRequest("telegram", "owner-token", "https://aevatar.example.com",
            ownerId, "label", "api-telegram-bot", "bot-owned", ownerId,
            RequestedServiceSelection: ChannelRegistrationServiceSelection.Explicit(["svc-alpha"]));
        var result = await ChannelExplicitAuthorizationTestSupport.Create(port).PlanAsync(request, verifiedOwner, CancellationToken.None);
        result.Succeeded.Should().BeTrue();
        return result.Authorization!;
    }

    [Theory]
    [InlineData("lark")]
    [InlineData("telegram")]
    [InlineData("matrix")]
    public async Task PrepareAsync_UsesOnlyCallerSelectedExistingServices(string platform)
    {
        var port = ChannelExplicitAuthorizationTestSupport.Port();
        var preparation = ChannelExplicitAuthorizationTestSupport.Create(port);
        var request = new ChannelRelayRegistrationRequest(platform, "caller-token", "https://aevatar.example.com",
            "owner-1", "label", "unrelated-provider-slug", "bot-owned", "owner-1",
            RequestedServiceSelection: ChannelRegistrationServiceSelection.Explicit(["svc-selected"]));
        var result = await preparation.PlanAsync(request, ChannelExplicitAuthorizationTestSupport.Owner, CancellationToken.None);
        result.Succeeded.Should().BeTrue();
        result.Authorization!.Plan.RegistrationServiceIds.Should().Equal("svc-selected");
        result.Authorization.Plan.AllowedServiceIds.Should().Equal("svc-selected");
        result.Authorization.RuntimeSelectors.Should().ContainSingle().Which.ServiceSlug.Should().Be("selected-service");
        await port.Received(1).PlanApiKeyScopeAsync("caller-token",
            Arg.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "svc-selected" })), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrepareAsync_InaccessibleSelectionDoesNotReachScopePlan()
    {
        var port = ChannelExplicitAuthorizationTestSupport.Port();
        var request = new ChannelRelayRegistrationRequest("lark", "caller-token", "https://aevatar.example.com",
            "owner-1", "label", "api-lark-bot", "bot-owned", "owner-1",
            RequestedServiceSelection: ChannelRegistrationServiceSelection.Explicit(["svc-unavailable"]));
        var result = await ChannelExplicitAuthorizationTestSupport.Create(port).PlanAsync(request,
            ChannelExplicitAuthorizationTestSupport.Owner, CancellationToken.None);
        result.ErrorCode.Should().Be("nyxid_user_service_not_accessible");
        await port.DidNotReceiveWithAnyArgs().PlanApiKeyScopeAsync(default!, default!, default, default);
    }
}

internal static class ChannelExplicitAuthorizationTestSupport
{
    internal const string Digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal static readonly VerifiedChannelRegistrationOwner Owner = new("owner-1",
        new ChannelRegistrationKeyOwner(ChannelRegistrationKeyOwnerKind.Personal, "owner-1"), null);

    internal static ChannelRegistrationExplicitAuthorizationPlanner Create(IChannelRegistrationNyxIdAuthorizationPort? port = null) =>
        new(new ChannelRegistrationAuthorizationPlanner(port ?? Port()),
            NullLogger<ChannelRegistrationExplicitAuthorizationPlanner>.Instance);

    internal static IChannelRegistrationNyxIdAuthorizationPort Port()
    {
        var port = Substitute.For<IChannelRegistrationNyxIdAuthorizationPort>();
        port.ReadUserServicesAsync("caller-token", Arg.Any<CancellationToken>())
            .Returns(new NyxIdApiAccessResult<NyxIdUserServices>(new NyxIdUserServices([
                new("svc-selected", "selected-service", "Selected", null, true,
                    new NyxIdUserServiceCredentialSource(NyxIdUserServiceCredentialSourceKind.Personal)),
                new("svc-platform-looking", "api-lark-bot", "Unselected", null, true,
                    new NyxIdUserServiceCredentialSource(NyxIdUserServiceCredentialSourceKind.Personal)),
            ]), null));
        var owner = new NyxIdScopePlanPrincipal("owner-1", NyxIdScopePlanPrincipalKind.Personal);
        port.PlanApiKeyScopeAsync("caller-token", Arg.Any<IReadOnlyList<string>>(), null, Arg.Any<CancellationToken>())
            .Returns(new NyxIdApiAccessResult<NyxIdApiKeyScopePlan>(new NyxIdApiKeyScopePlan(
                NyxIdApiAccessResponseParser.ScopePlanAuthority,
                NyxIdApiAccessResponseParser.ScopePlanContractVersion,
                NyxIdApiAccessResponseParser.ScopePlanPolicyVersion,
                owner, owner, [new NyxIdScopePlanServiceGrant("svc-selected", owner,
                    new NyxIdScopePlanNodeGrant(NyxIdScopePlanNodeGrantKind.NotRequired, []))],
                ["svc-selected"], [], DateTimeOffset.Parse("2026-09-16T00:00:00Z"), Digest,
                new NyxIdScopePlanFreshness(NyxIdScopePlanFreshnessMode.MutationRevalidatedSnapshot,
                    "scope_plan_digest", NyxIdScopePlanPostCreationDrift.FailClosed),
                new NyxIdScopePlanCompleteness(true, true, NyxIdScopePlanRouteCandidateBasis.ActiveConfiguredRoutes, true)), null));
        return port;
    }
}
