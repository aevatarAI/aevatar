using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.AI.Tests;

public sealed class AgentProfileSnapshotCodecTests
{
    [Fact]
    public void ExactReferences_ShouldKeepFrozenWireShape()
    {
        ExactRemoteSkillRef.Descriptor.Fields.InFieldNumberOrder()
            .Select(static field => (field.Name, field.FieldNumber))
            .Should()
            .Equal(("guid", 1), ("literal_version", 2));
        ExactRemoteSkillsetRef.Descriptor.Fields.InFieldNumberOrder()
            .Select(static field => (field.Name, field.FieldNumber))
            .Should()
            .Equal(("guid", 1), ("literal_version", 2));
        ExactRemoteSkillRef.Descriptor.Fields.InFieldNumberOrder()
            .Should().NotContain(static field => field.Name == "name");
        ExactRemoteSkillsetRef.Descriptor.Fields.InFieldNumberOrder()
            .Should().NotContain(static field => field.Name == "name");
    }

    [Fact]
    public void AdditiveFields_ShouldRemainAbsentForOldBytes()
    {
        RoleGAgentState.Parser.ParseFrom(Array.Empty<byte>()).AgentProfile.Should().BeNull();
        NyxIdChatConversationCreateCommand.Parser.ParseFrom(Array.Empty<byte>()).AgentProfile.Should().BeNull();
    }

    [Fact]
    public void PublishedWrapperDigest_ShouldNotLiveInsideLegacyRuntimeSnapshot()
    {
        AgentProfileSnapshot.Descriptor.Fields.InFieldNumberOrder()
            .Select(static field => (field.Name, field.FieldNumber))
            .Should().NotContain(("published_snapshot_sha256", 19));
    }

    [Fact]
    public void Seal_ShouldCloneInputAndExcludeDigestFromItsOwnHash()
    {
        var input = BuildProfile("profile-v1");

        var sealedProfile = AgentProfileSnapshotCodec.Seal(input);
        input.ProfileVersion = "mutated";

        sealedProfile.ProfileVersion.Should().Be("profile-v1");
        sealedProfile.DeterministicPolicySha256.Length.Should().Be(32);
        AgentProfileSnapshotCodec.Verify(sealedProfile).Should().BeTrue();
        var resealable = sealedProfile.Clone();
        resealable.DeterministicPolicySha256 = ByteString.Empty;
        AgentProfileSnapshotCodec.Seal(resealable)
            .DeterministicPolicySha256
            .Should()
            .Equal(sealedProfile.DeterministicPolicySha256);
    }

    [Fact]
    public void Seal_ShouldRejectSnapshotWithExistingDigest()
    {
        var sealedProfile = AgentProfileSnapshotCodec.Seal(BuildProfile("profile-v1"));

        var act = () => AgentProfileSnapshotCodec.Seal(sealedProfile);

        act.Should()
            .Throw<ArgumentException>()
            .WithMessage("*digest must be empty before sealing*");
    }

    [Fact]
    public void Verify_ShouldRejectTamperedSnapshot()
    {
        var sealedProfile = AgentProfileSnapshotCodec.Seal(BuildProfile("profile-v1"));
        sealedProfile.ProfileVersion = "profile-v2";

        AgentProfileSnapshotCodec.Verify(sealedProfile).Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void Verify_ShouldRejectDigestWithInvalidLength(int digestLength)
    {
        var profile = BuildProfile("profile-v1");
        profile.DeterministicPolicySha256 = ByteString.CopyFrom(new byte[digestLength]);

        AgentProfileSnapshotCodec.Verify(profile).Should().BeFalse();
    }

    [Fact]
    public void Seal_ShouldPreserveRepeatedOrderInDigestAndEquality()
    {
        var left = BuildProfile("profile-v1");
        left.MaximumToolPolicy.ToolNames.Add(["alpha", "beta"]);
        var right = BuildProfile("profile-v1");
        right.MaximumToolPolicy.ToolNames.Add(["beta", "alpha"]);

        var sealedLeft = AgentProfileSnapshotCodec.Seal(left);
        var sealedRight = AgentProfileSnapshotCodec.Seal(right);

        sealedLeft.DeterministicPolicySha256.Should().NotEqual(sealedRight.DeterministicPolicySha256);
        AgentProfileSnapshotCodec.ByteEquivalent(sealedLeft, sealedRight).Should().BeFalse();
    }

    [Fact]
    public void Seal_ShouldIncludeUnknownFieldsInDigest()
    {
        var baselineBytes = BuildProfile("profile-v1").ToByteArray();
        var withUnknownA = AgentProfileSnapshot.Parser.ParseFrom(
            baselineBytes.Concat(new byte[] { 0xA0, 0x06, 0x01 }).ToArray());
        var withUnknownB = AgentProfileSnapshot.Parser.ParseFrom(
            baselineBytes.Concat(new byte[] { 0xA0, 0x06, 0x02 }).ToArray());

        var sealedA = AgentProfileSnapshotCodec.Seal(withUnknownA);
        var sealedB = AgentProfileSnapshotCodec.Seal(withUnknownB);

        sealedA.DeterministicPolicySha256.Should().NotEqual(sealedB.DeterministicPolicySha256);
        AgentProfileSnapshotCodec.Verify(sealedA).Should().BeTrue();
    }

    private static AgentProfileSnapshot BuildProfile(string profileVersion) => new()
    {
        ProfileId = "profile-alpha",
        ProfileVersion = profileVersion,
        MaximumToolPolicy = new AgentProfileToolPolicy(),
    };
}
