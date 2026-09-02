using System.Security.Claims;
using Aevatar.Capabilities;
using FluentAssertions;

namespace Aevatar.Capabilities.Tests;

public sealed class AevatarPrincipalSubjectResolverTests
{
    [Theory]
    [InlineData("uid")]
    [InlineData("sub")]
    [InlineData(ClaimTypes.NameIdentifier)]
    [InlineData("user_id")]
    public void TryResolveNyxIdSubject_WithOneRecognizedClaim_ReturnsTrimmedValue(string claimType)
    {
        var principal = Principal(new Claim(claimType, " user-audit-alpha "));

        AevatarPrincipalSubjectResolver.TryResolveNyxIdSubject(principal, out var subject)
            .Should().BeTrue();
        subject.Should().Be("user-audit-alpha");
    }

    [Fact]
    public void TryResolveNyxIdSubject_WithDuplicateAliasesForSameValue_Succeeds()
    {
        var principal = Principal(
            new Claim("uid", "user-audit-alpha"),
            new Claim("SUB", " user-audit-alpha "));

        AevatarPrincipalSubjectResolver.TryResolveNyxIdSubject(principal, out var subject)
            .Should().BeTrue();
        subject.Should().Be("user-audit-alpha");
    }

    [Fact]
    public void TryResolveNyxIdSubject_WithConflictingAliases_FailsClosed()
    {
        var principal = Principal(
            new Claim("uid", "user-audit-alpha"),
            new Claim("sub", "user-audit-beta"));

        AevatarPrincipalSubjectResolver.TryResolveNyxIdSubject(principal, out var subject)
            .Should().BeFalse();
        subject.Should().BeEmpty();
    }

    [Fact]
    public void TryResolveNyxIdSubject_WithWhitespaceOrUnauthenticatedPrincipal_Fails()
    {
        AevatarPrincipalSubjectResolver.TryResolveNyxIdSubject(
                Principal(new Claim("sub", "   ")),
                out _)
            .Should().BeFalse();
        AevatarPrincipalSubjectResolver.TryResolveNyxIdSubject(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-audit-alpha")])),
                out _)
            .Should().BeFalse();
        AevatarPrincipalSubjectResolver.TryResolveNyxIdSubject(new ClaimsPrincipal(), out _)
            .Should().BeFalse();
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "test"));
}
