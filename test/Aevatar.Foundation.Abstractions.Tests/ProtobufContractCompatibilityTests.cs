using Aevatar.Foundation.Abstractions.Compatibility;
using Google.Protobuf.WellKnownTypes;
using Shouldly;

namespace Aevatar.Foundation.Abstractions.Tests;

public sealed class ProtobufContractCompatibilityTests
{
    [Fact]
    public void MatchesPayload_ShouldRejectDuplicateLegacyProtoAlias_OnSameMessageType()
    {
        var payload = CreateAny("type.googleapis.com/aevatar.tests.DuplicateLegacyAlias");

        var exception = Should.Throw<InvalidOperationException>(
            () => ProtobufContractCompatibility.MatchesPayload(payload, typeof(DuplicateLegacyAliasMessage)));

        exception.Message.ShouldContain("Duplicate legacy protobuf TypeUrl alias");
        exception.Message.ShouldContain("type.googleapis.com/aevatar.tests.DuplicateLegacyAlias");
        exception.Message.ShouldContain(nameof(DuplicateLegacyAliasMessage));
    }

    [Fact]
    public void MatchesPayload_ShouldRejectDuplicateLegacyProtoAlias_AcrossMessageTypes()
    {
        var payload = CreateAny("type.googleapis.com/aevatar.tests.SharedLegacyAlias");

        ProtobufContractCompatibility.MatchesPayload(payload, typeof(FirstLegacyAliasOwner)).ShouldBeTrue();

        var exception = Should.Throw<InvalidOperationException>(
            () => ProtobufContractCompatibility.MatchesPayload(payload, typeof(SecondLegacyAliasOwner)));

        exception.Message.ShouldContain("Duplicate legacy protobuf TypeUrl alias");
        exception.Message.ShouldContain("type.googleapis.com/aevatar.tests.SharedLegacyAlias");
        exception.Message.ShouldContain(nameof(FirstLegacyAliasOwner));
        exception.Message.ShouldContain(nameof(SecondLegacyAliasOwner));
    }

    private static Any CreateAny(string typeUrl) => new() { TypeUrl = typeUrl };

    [LegacyProtoFullName("aevatar.tests.DuplicateLegacyAlias")]
    [LegacyProtoFullName("aevatar.tests.DuplicateLegacyAlias")]
    private sealed class DuplicateLegacyAliasMessage;

    [LegacyProtoFullName("aevatar.tests.SharedLegacyAlias")]
    private sealed class FirstLegacyAliasOwner;

    [LegacyProtoFullName("aevatar.tests.SharedLegacyAlias")]
    private sealed class SecondLegacyAliasOwner;
}
