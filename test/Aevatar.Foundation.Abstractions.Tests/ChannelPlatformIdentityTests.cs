using Shouldly;

namespace Aevatar.Foundation.Abstractions.Tests;

public sealed class ChannelPlatformIdentityTests
{
    [Theory]
    [InlineData(" MATRIX ", "matrix")]
    [InlineData("Future.Platform:V2", "future.platform:v2")]
    public void ExternalPlatform_NormalizesOnceAndComparesOrdinally(string raw, string expected)
    {
        ChannelPlatformId.ParseExternal(raw).ShouldBe(ChannelPlatformId.FromCanonical(expected));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("matrix room")]
    [InlineData("matrix\u0000")]
    [InlineData("\tmatrix")]
    public void ExternalPlatform_RejectsInvalidIdentities(string raw) =>
        Should.Throw<ArgumentException>(() => ChannelPlatformId.ParseExternal(raw));

    [Theory]
    [InlineData("matrix")]
    [InlineData("future.platform:v2")]
    [InlineData("feishu")]
    public void OwnerScope_AcceptsArbitraryCanonicalChannelPlatform(string platform)
    {
        var scope = OwnerScope.ForChannel("user-a", platform, "registration-a", "sender-a");
        scope.TryValidate(out var error).ShouldBeTrue();
        error.ShouldBeNull();
        scope.IsNyxIdNative.ShouldBeFalse();
    }

    [Theory]
    [InlineData(" MATRIX ")]
    [InlineData("Matrix")]
    [InlineData("matrix room")]
    [InlineData("matrix\u0000")]
    public void OwnerScope_InternalFactoryRejectsNonCanonicalPlatform(string platform)
    {
        Should.Throw<ArgumentException>(() =>
            OwnerScope.ForChannel("user-a", platform, "registration-a", "sender-a"));
    }

    [Fact]
    public void OwnerScope_PlatformEqualityIsOrdinal()
    {
        var canonical = OwnerScope.ForChannel("user-a", "matrix", "registration-a", "sender-a");
        var malformed = canonical.Clone();
        malformed.Platform = "MATRIX";
        canonical.MatchesStrictly(malformed).ShouldBeFalse();
    }

    [Fact]
    public void OwnerScope_NativeIdentityRetainsNyxIdAccountSemantics()
    {
        var native = OwnerScope.ForNyxIdNative("user-a");
        native.TryValidate(out _).ShouldBeTrue();
        native.IsNyxIdNative.ShouldBeTrue();
        native.MatchesStrictly(OwnerScope.ForNyxIdNative("user-b")).ShouldBeFalse();
        native.MatchesStrictly(OwnerScope.ForChannel("user-a", "matrix", "registration-a", "sender-a"))
            .ShouldBeFalse();
    }
}
