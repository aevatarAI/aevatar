using System.Text;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using FluentAssertions;
using Google.Protobuf.Reflection;

namespace Aevatar.GAgentService.Tests.Abstractions;

public sealed class AgentProfileContractsTests
{
    private const string SkillGuid = "2d05bf2e-88ee-4f76-9998-728ba2f9db10";

    [Fact]
    public void ValidateReference_ShouldAcceptCanonicalHumanReferences()
    {
        AgentProfilePolicies.ValidateReference(new AgentProfileReference
        {
            OwnerHandle = "eanzhao",
            ProfileSlug = "xiaomi-home-assistant",
        }).Should().BeEmpty();

        AgentProfilePolicies.ValidateReference(new AgentProfileReference
        {
            OwnerHandle = "system",
            ProfileSlug = "studio",
        }).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Eanzhao", "profile", "INVALID_OWNER_HANDLE")]
    [InlineData("ean--zhao", "profile", "INVALID_OWNER_HANDLE")]
    [InlineData(".", "profile", "INVALID_OWNER_HANDLE")]
    [InlineData("..", "profile", "INVALID_OWNER_HANDLE")]
    [InlineData("eanzhao/dev", "profile", "INVALID_OWNER_HANDLE")]
    [InlineData("eanzhao\n", "profile", "INVALID_OWNER_HANDLE")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "profile", "INVALID_OWNER_HANDLE")]
    [InlineData("eanzhao", "Profile", "INVALID_PROFILE_SLUG")]
    [InlineData("eanzhao", "home--assistant", "INVALID_PROFILE_SLUG")]
    [InlineData("eanzhao", ".", "INVALID_PROFILE_SLUG")]
    [InlineData("eanzhao", "..", "INVALID_PROFILE_SLUG")]
    [InlineData("eanzhao", "home/assistant", "INVALID_PROFILE_SLUG")]
    [InlineData("eanzhao", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "INVALID_PROFILE_SLUG")]
    public void ValidateReference_ShouldRejectNonCanonicalSegments(
        string ownerHandle,
        string profileSlug,
        string expectedCode)
    {
        var diagnostics = AgentProfilePolicies.ValidateReference(new AgentProfileReference
        {
            OwnerHandle = ownerHandle,
            ProfileSlug = profileSlug,
        });

        diagnostics.Should().ContainSingle(x => x.Code == expectedCode);
    }

    [Fact]
    public void ValidateUserOwnerHandle_ShouldRejectReservedSystemClaim()
    {
        AgentProfilePolicies.ValidateUserOwnerHandle("system")
            .Should().ContainSingle(x => x.Code == "RESERVED_OWNER_HANDLE");
    }

    [Theory]
    [InlineData("1.4")]
    [InlineData("0.0")]
    [InlineData("10.27")]
    public void ValidateExactSkillReference_ShouldAcceptLiteralMajorMinorVersions(string literalVersion)
    {
        AgentProfilePolicies.ValidateExactSkillReference(ExactReference(literalVersion))
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("v1.4")]
    [InlineData("1")]
    [InlineData("1.4.0")]
    [InlineData("1.x")]
    [InlineData("01.4")]
    [InlineData("1.4\n")]
    public void ValidateExactSkillReference_ShouldRejectNonLiteralVersions(string literalVersion)
    {
        AgentProfilePolicies.ValidateExactSkillReference(new ExactOrnnSkillReference
        {
            SkillGuid = SkillGuid,
            LiteralVersion = literalVersion,
            ExpectedName = "xiaomi-home-control",
            ExpectedPublisherId = "publisher-alpha",
        }).Should().ContainSingle(x => x.Code == "INVALID_LITERAL_VERSION");
    }

    [Fact]
    public void ComputeDraftSha256_ShouldCanonicalizeToolOrderingAndLineEndings()
    {
        var first = Content(
            purpose: "Control devices.\r\nUse exact state.",
            instructions: "First line.\r\nSecond line.",
            toolNames: ["zeta", "alpha", "alpha"]);
        var second = Content(
            purpose: "Control devices.\nUse exact state.",
            instructions: "First line.\nSecond line.",
            toolNames: ["alpha", "zeta"]);

        AgentProfileDeterminism.ComputeDraftSha256(first)
            .Should().Equal(AgentProfileDeterminism.ComputeDraftSha256(second));
    }

    [Fact]
    public void ComputeSealedSkillSha256_ShouldCanonicalizeAssetOrderingAndLineEndings()
    {
        var first = SealedSkill(
            "publisher-alpha",
            [
                new AgentProfileNamedTextAsset { Path = "zeta.txt", Content = "Zeta\r\nline" },
                new AgentProfileNamedTextAsset { Path = "alpha.txt", Content = "Alpha\r\nline" },
            ]);
        var second = SealedSkill(
            "publisher-alpha",
            [
                new AgentProfileNamedTextAsset { Path = "alpha.txt", Content = "Alpha\nline" },
                new AgentProfileNamedTextAsset { Path = "zeta.txt", Content = "Zeta\nline" },
            ]);

        AgentProfileDeterminism.ComputeSealedSkillSha256(first)
            .Should().Equal(AgentProfileDeterminism.ComputeSealedSkillSha256(second));
    }

    [Fact]
    public void ComputeSealedSkillSha256_ShouldBindExpectedPublisherIdentity()
    {
        var first = SealedSkill("publisher-alpha", []);
        var second = SealedSkill("publisher-beta", []);

        AgentProfileDeterminism.ComputeSealedSkillSha256(first)
            .Should().NotEqual(AgentProfileDeterminism.ComputeSealedSkillSha256(second));
    }

    [Fact]
    public void Purpose_ShouldAffectDraftDigestButNotExecutionSnapshotDigest()
    {
        var firstContent = Content("First purpose", "Instructions", ["alpha"]);
        var secondContent = Content("Second purpose", "Instructions", ["alpha"]);
        var firstSnapshot = PublishedSnapshot(firstContent);
        var secondSnapshot = PublishedSnapshot(secondContent);

        AgentProfileDeterminism.ComputeDraftSha256(firstContent)
            .Should().NotEqual(AgentProfileDeterminism.ComputeDraftSha256(secondContent));
        AgentProfileDeterminism.ComputePublishedSnapshotSha256(firstSnapshot)
            .Should().Equal(AgentProfileDeterminism.ComputePublishedSnapshotSha256(secondSnapshot));
    }

    [Fact]
    public void CreateIds_ShouldBeStableForOwnerScopeAndIdempotencyKey()
    {
        var owner = UserOwner("subject-alpha");

        var operationId = AgentProfileDeterminism.CreateOperationId(owner, "scope-gamma", "key-alpha");
        var profileId = AgentProfileDeterminism.CreateProfileId(owner, "scope-gamma", "key-alpha");

        AgentProfileDeterminism.CreateOperationId(owner, "scope-gamma", "key-alpha")
            .Should().Be(operationId);
        AgentProfileDeterminism.CreateProfileId(owner, "scope-gamma", "key-alpha")
            .Should().Be(profileId);
    }

    [Fact]
    public void CreateIds_ShouldChangeWhenOwnerOrIdempotencyKeyChanges()
    {
        var owner = UserOwner("subject-alpha");
        var otherOwner = UserOwner("subject-beta");
        var operationId = AgentProfileDeterminism.CreateOperationId(owner, "scope-gamma", "key-alpha");
        var profileId = AgentProfileDeterminism.CreateProfileId(owner, "scope-gamma", "key-alpha");

        AgentProfileDeterminism.CreateOperationId(otherOwner, "scope-gamma", "key-alpha")
            .Should().NotBe(operationId);
        AgentProfileDeterminism.CreateProfileId(otherOwner, "scope-gamma", "key-alpha")
            .Should().NotBe(profileId);
        AgentProfileDeterminism.CreateOperationId(owner, "scope-gamma", "key-beta")
            .Should().NotBe(operationId);
        AgentProfileDeterminism.CreateProfileId(owner, "scope-gamma", "key-beta")
            .Should().NotBe(profileId);
    }

    [Fact]
    public void CreateCommandId_ShouldReturnANewDispatchIdentity()
    {
        AgentProfileDeterminism.CreateCommandId()
            .Should().NotBe(AgentProfileDeterminism.CreateCommandId());
    }

    [Fact]
    public void FailedResolution_ShouldBoundSafeDiagnosticMessage()
    {
        var result = ExactOrnnSkillResolutionResult.Failed(
            "ORNN_DEPENDENCY_UNAVAILABLE",
            new string('x', 600));

        Encoding.UTF8.GetByteCount(result.Failure!.Message).Should().BeLessThanOrEqualTo(512);
    }

    [Fact]
    public void SealedAndPublishedMessages_ShouldExposeNoCredentialOrGenericBagFields()
    {
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Metadata",
            "Headers",
            "Items",
            "AccessToken",
            "Bearer",
            "ApiKey",
            "Cookie",
            "Credential",
        };

        var fieldNames = ReachableFields(
                SealedAgentProfileSkill.Descriptor,
                AgentProfilePublishedSnapshot.Descriptor)
            .Select(static field => field.PropertyName)
            .ToArray();

        fieldNames.Should().NotContain(name => forbidden.Contains(name));
    }

    private static ExactOrnnSkillReference ExactReference(string literalVersion = "1.4") => new()
    {
        SkillGuid = SkillGuid,
        LiteralVersion = literalVersion,
        ExpectedName = "xiaomi-home-control",
        ExpectedPublisherId = "publisher-alpha",
    };

    private static AgentProfileUserOwnerIdentity UserOwner(string subjectId) => new()
    {
        IdentityProvider = "nyxid",
        SubjectId = subjectId,
    };

    private static AgentProfileContent Content(
        string purpose,
        string instructions,
        IEnumerable<string> toolNames)
    {
        var content = new AgentProfileContent
        {
            DisplayName = "Home assistant",
            Purpose = purpose,
            Instructions = instructions,
            ToolPolicy = new AgentProfileToolPolicy
            {
                Mode = AgentProfileToolPolicyMode.ExplicitAllowlist,
            },
        };
        content.ToolPolicy.ToolNames.Add(toolNames);
        return content;
    }

    private static SealedAgentProfileSkill SealedSkill(
        string expectedPublisherId,
        IEnumerable<AgentProfileNamedTextAsset> assets)
    {
        var skill = new SealedAgentProfileSkill
        {
            ExactReference = ExactReference(),
            Package = new ResolvedOrnnSkillPackage
            {
                SkillGuid = SkillGuid,
                LiteralVersion = "1.4",
                CanonicalName = "xiaomi-home-control",
                PublisherId = expectedPublisherId,
                UpstreamSkillHash = "hash-alpha",
                Description = "Controls a home",
                Instructions = "Inspect state.\nApply requested change.",
                Arguments = "device and action",
                WhenToUse = "Use for home control",
                ModelInvocable = true,
                UserInvocable = true,
            },
        };
        skill.ExactReference.ExpectedPublisherId = expectedPublisherId;
        skill.Package.DeclaredToolNames.Add(["zeta", "alpha"]);
        skill.Package.Assets.Add(assets);
        return skill;
    }

    private static AgentProfilePublishedSnapshot PublishedSnapshot(AgentProfileContent content)
    {
        var snapshot = new AgentProfilePublishedSnapshot
        {
            Identity = new AgentProfileIdentity
            {
                ProfileId = "prof-alpha",
                Owner = new AgentProfileOwnerIdentity
                {
                    User = UserOwner("subject-alpha"),
                },
                OwningScopeId = "scope-gamma",
                Reference = new AgentProfileReference
                {
                    OwnerHandle = "eanzhao",
                    ProfileSlug = "xiaomi-home-assistant",
                },
            },
            DisplayName = content.DisplayName,
            Purpose = content.Purpose,
            Instructions = content.Instructions,
            ToolPolicy = content.ToolPolicy.Clone(),
            PublishedRevision = 7,
        };
        return snapshot;
    }

    private static IEnumerable<FieldDescriptor> ReachableFields(params MessageDescriptor[] roots)
    {
        var pending = new Stack<MessageDescriptor>(roots);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        while (pending.TryPop(out var message))
        {
            if (!visited.Add(message.FullName))
                continue;

            foreach (var field in message.Fields.InFieldNumberOrder())
            {
                yield return field;
                if (field.FieldType == FieldType.Message && field.MessageType != null)
                    pending.Push(field.MessageType);
            }
        }
    }
}
