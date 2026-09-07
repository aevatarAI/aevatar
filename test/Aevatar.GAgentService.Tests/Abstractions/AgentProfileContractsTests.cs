using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Abstractions;

public sealed class AgentProfileContractsTests
{
    [Fact]
    public void OwnerFactories_ShouldKeepScopeAndSystemIdentitySeparate()
    {
        var scopeOwner = AgentProfileOwners.ForScope("scope-alpha");
        var systemOwner = AgentProfileOwners.ForSystem();

        scopeOwner.OwnerCase.Should().Be(AgentProfileOwner.OwnerOneofCase.Scope);
        scopeOwner.Scope.ScopeId.Should().Be("scope-alpha");
        scopeOwner.System.Should().BeNull();

        systemOwner.OwnerCase.Should().Be(AgentProfileOwner.OwnerOneofCase.System);
        systemOwner.System.PlatformId.Should().Be("aevatar");
        systemOwner.Scope.Should().BeNull();
    }

    [Theory]
    [InlineData("research-assistant")]
    [InlineData("nyxid-chat")]
    [InlineData("a1")]
    public void ValidateProfileSlug_ShouldAcceptCanonicalSlugs(string slug)
    {
        AgentProfilePolicies.ValidateProfileSlug(slug).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Research-Assistant")]
    [InlineData("double--hyphen")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("contains/slash")]
    public void ValidateProfileSlug_ShouldRejectAmbiguousSlugs(string slug)
    {
        AgentProfilePolicies.ValidateProfileSlug(slug)
            .Should().ContainSingle(x => x.Code == "INVALID_PROFILE_SLUG");
    }

    [Fact]
    public void ValidateExactSkillReference_ShouldRequireGuidVersionNameAndPublisher()
    {
        var member = new AgentProfileSkillMember
        {
            SkillRef = new ExactRemoteSkillRef
            {
                Guid = "2d05bf2e-88ee-4f76-9998-728ba2f9db10",
                LiteralVersion = "1.4",
            },
            ExpectedSkillName = "research-assistant",
            ReviewedPublisherId = "publisher-alpha",
        };

        AgentProfilePolicies.ValidateExactSkillReference(member).Should().BeEmpty();

        member.SkillRef.LiteralVersion = "latest";
        AgentProfilePolicies.ValidateExactSkillReference(member)
            .Should().ContainSingle(x => x.Code == "INVALID_LITERAL_VERSION");
    }

    [Fact]
    public void ValidateDraft_ShouldRejectDuplicateIntentIds()
    {
        var draft = new AgentProfileDraft
        {
            DisplayName = "Research assistant",
            Instructions = "Use verified sources.",
            RuntimeProfile = new AgentProfileSnapshot
            {
                AgentKind = AgentProfilePolicies.NyxIdChatAgentKind,
                RouteToolSetRef = AgentProfilePolicies.NyxIdChatRouteToolSet,
                MaximumToolPolicy = new AgentProfileToolPolicy(),
                RecoveryToolPolicy = new AgentProfileToolPolicy(),
                Members =
                {
                    ValidMember("research", "2d05bf2e-88ee-4f76-9998-728ba2f9db10"),
                    ValidMember("research", "6e32aa43-2035-4b39-a0ae-e8f9b3125392"),
                },
            },
        };

        AgentProfilePolicies.ValidateDraft(draft)
            .Should().ContainSingle(diagnostic =>
                diagnostic.Code == "PROFILE_INTENT_ID_DUPLICATE" &&
                diagnostic.Field == "runtimeProfile.members[1].intentId");
    }

    [Fact]
    public void ValidateDraft_ShouldAcceptDynamicReadOnlyConnectedServiceSelector()
    {
        var draft = new AgentProfileDraft
        {
            DisplayName = "Research assistant",
            Instructions = "Use verified sources.",
            RuntimeProfile = new AgentProfileSnapshot
            {
                AgentKind = AgentProfilePolicies.NyxIdChatAgentKind,
                RouteToolSetRef = AgentProfilePolicies.NyxIdChatRouteToolSet,
                MaximumToolPolicy = new AgentProfileToolPolicy(),
                RecoveryToolPolicy = new AgentProfileToolPolicy(),
                Members = { ValidMember("research", "2d05bf2e-88ee-4f76-9998-728ba2f9db10") },
            },
        };
        draft.RuntimeProfile.Members[0].TaskToolPolicy.ConnectedServiceSelectors.Add(
            new AgentProfileConnectedServiceSelector
            {
                AllowedRisks = { AgentToolOperationRiskPayload.ReadOnly },
            });

        AgentProfilePolicies.ValidateDraft(draft).Should().BeEmpty();
    }

    [Fact]
    public void ValidateDraft_ShouldAcceptEndpointOnlyReadConnectedServiceSelector()
    {
        var draft = new AgentProfileDraft
        {
            DisplayName = "Research assistant",
            Instructions = "Use verified sources.",
            RuntimeProfile = new AgentProfileSnapshot
            {
                AgentKind = AgentProfilePolicies.NyxIdChatAgentKind,
                RouteToolSetRef = AgentProfilePolicies.NyxIdChatRouteToolSet,
                MaximumToolPolicy = new AgentProfileToolPolicy(),
                RecoveryToolPolicy = new AgentProfileToolPolicy(),
                Members = { ValidMember("research", "2d05bf2e-88ee-4f76-9998-728ba2f9db10") },
            },
        };
        draft.RuntimeProfile.Members[0].TaskToolPolicy.ConnectedServiceSelectors.Add(
            new AgentProfileConnectedServiceSelector
            {
                EndpointId = "readDiningProfileContext",
                AllowedRisks = { AgentToolOperationRiskPayload.ReadOnly },
            });

        AgentProfilePolicies.ValidateDraft(draft).Should().BeEmpty();
    }

    [Fact]
    public void ValidateDraft_ShouldRejectEndpointOnlyWriteConnectedServiceSelector()
    {
        var draft = new AgentProfileDraft
        {
            DisplayName = "Research assistant",
            Instructions = "Use verified sources.",
            RuntimeProfile = new AgentProfileSnapshot
            {
                AgentKind = AgentProfilePolicies.NyxIdChatAgentKind,
                RouteToolSetRef = AgentProfilePolicies.NyxIdChatRouteToolSet,
                MaximumToolPolicy = new AgentProfileToolPolicy(),
                RecoveryToolPolicy = new AgentProfileToolPolicy(),
                Members = { ValidMember("research", "2d05bf2e-88ee-4f76-9998-728ba2f9db10") },
            },
        };
        draft.RuntimeProfile.Members[0].TaskToolPolicy.ConnectedServiceSelectors.Add(
            new AgentProfileConnectedServiceSelector
            {
                EndpointId = "updateDiningProfileContext",
                AllowedRisks = { AgentToolOperationRiskPayload.Write },
            });

        AgentProfilePolicies.ValidateDraft(draft)
            .Should().ContainSingle(diagnostic =>
                diagnostic.Code == "PROFILE_CONNECTED_SERVICE_SLUG_INVALID" &&
                diagnostic.Field == "runtimeProfile.members[0].taskToolPolicy.connectedServiceSelectors[0].catalogServiceSlug");
    }

    [Fact]
    public void CreateProfileId_ShouldBeStableForTheSameOwnerAndIdempotencyKey()
    {
        var owner = AgentProfileOwners.ForScope("scope-alpha");

        var first = AgentProfileDeterminism.CreateProfileId(owner, "idem-alpha");
        var second = AgentProfileDeterminism.CreateProfileId(owner, "idem-alpha");
        var differentOwner = AgentProfileDeterminism.CreateProfileId(
            AgentProfileOwners.ForScope("scope-beta"),
            "idem-alpha");

        second.Should().Be(first);
        differentOwner.Should().NotBe(first);
        first.Should().StartWith("prof_");
    }

    [Fact]
    public void PublicReference_ShouldCarryOneOwnerChoiceAndOneSlugOnly()
    {
        var fields = AgentProfileReference.Descriptor.Fields
            .InFieldNumberOrder()
            .Select(x => x.Name)
            .ToArray();

        fields.Should().Equal("owner_kind", "profile_slug");
        fields.Should().NotContain(["profile_id", "scope_id", "user_id", "content", "metadata"]);
    }

    [Fact]
    public void DefaultBinding_ShouldUseTypedTargetAndOwnerSpecificAdmission()
    {
        var file = AgentProfileDefaultBinding.Descriptor.File;
        var target = file.MessageTypes.SingleOrDefault(x => x.Name == "AgentProfileBindingTarget");
        var scopeAdmission = file.MessageTypes.SingleOrDefault(x => x.Name == "AgentProfileScopeBindingAdmission");
        var systemAdmission = file.MessageTypes.SingleOrDefault(x => x.Name == "AgentProfileSystemBindingAdmission");

        target.Should().NotBeNull();
        target!.Fields.InFieldNumberOrder().Select(x => x.Name).Should().Equal(
            "owner",
            "profile_id",
            "published_revision",
            "snapshot_sha256");
        scopeAdmission.Should().NotBeNull();
        scopeAdmission!.Fields.InFieldNumberOrder().Should().BeEmpty();
        systemAdmission.Should().NotBeNull();
        systemAdmission!.Fields.InFieldNumberOrder().Select(x => x.Name).Should().Equal(
            "enabled",
            "cohort_basis_points",
            "previous_reviewed_target");

        AgentProfileDefaultBinding.Descriptor.Fields.InFieldNumberOrder()
            .Select(x => x.Name)
            .Should().Equal("agent_kind", "target", "scope", "system");
        AgentProfileDefaultBinding.Descriptor.Oneofs
            .Should().ContainSingle(x => x.Name == "admission");

        SetAgentProfileDefaultBindingCommand.Descriptor.Fields.InFieldNumberOrder()
            .Select(x => x.Name)
            .Should().Equal(
                "owner",
                "agent_kind",
                "target",
                "scope",
                "system",
                "expected_authority_state_version",
                "operation");
        SetAgentProfileDefaultBindingCommand.Descriptor.Oneofs
            .Should().ContainSingle(x => x.Name == "admission");
    }

    private static AgentProfileSkillMember ValidMember(string intentId, string skillGuid) => new()
    {
        IntentId = intentId,
        RoutingDescription = "Find verified sources",
        SkillRef = new ExactRemoteSkillRef
        {
            Guid = skillGuid,
            LiteralVersion = "1.4",
        },
        ExpectedSkillName = $"skill-{intentId}",
        ReviewedPublisherId = "publisher-alpha",
        TaskToolPolicy = new AgentProfileToolPolicy(),
    };
}
