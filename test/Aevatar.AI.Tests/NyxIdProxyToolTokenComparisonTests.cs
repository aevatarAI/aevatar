using Aevatar.AI.ToolProviders.NyxId.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

/// <summary>
/// L4: dual-token routing must decide "same token" with a constant-time compare so the
/// decision does not leak a timing signal about how much of the org token matches the user
/// token. <see cref="NyxIdProxyTool.TokensEqual"/> is behaviorally identical to <c>==</c>
/// for equal/unequal tokens; only the null handling is spelled out (reference semantics).
/// </summary>
public class NyxIdProxyToolTokenComparisonTests
{
    [Fact]
    public void EqualTokens_CompareTrue()
    {
        var token = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.same-token-value";

        NyxIdProxyTool.TokensEqual(token, new string(token.ToCharArray())).Should().BeTrue(
            "two distinct string instances with identical contents are equal");
    }

    [Theory]
    [InlineData("token-alpha", "token-beta")]
    [InlineData("short", "short-but-longer")]
    [InlineData("", "x")]
    [InlineData("Token", "token")]
    public void UnequalTokens_CompareFalse(string left, string right)
    {
        NyxIdProxyTool.TokensEqual(left, right).Should().BeFalse(
            "different token contents (including differing length or case) are not equal");
    }

    [Fact]
    public void BothNull_CompareTrue()
    {
        NyxIdProxyTool.TokensEqual(null, null).Should().BeTrue(
            "the null case falls back to reference semantics; two nulls are equal");
    }

    [Theory]
    [InlineData(null, "token")]
    [InlineData("token", null)]
    public void OneNull_CompareFalse(string? left, string? right)
    {
        NyxIdProxyTool.TokensEqual(left, right).Should().BeFalse(
            "a null operand is never equal to a non-null token");
    }
}
