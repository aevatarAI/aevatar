using Aevatar.Workflow.Abstractions;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowCallerCredentialTokensTests
{
    [Theory]
    [InlineData("token-123", "token-123")]
    [InlineData(" token-123 ", "token-123")]
    [InlineData("Bearerfoo", "Bearerfoo")]
    public void ParseOptional_ShouldReturnValidNormalizedRawToken(string rawToken, string expectedToken)
    {
        var result = WorkflowCallerCredentialTokens.ParseOptional(rawToken);

        result.Status.Should().Be(WorkflowCallerCredentialTokenParseStatus.Valid);
        result.IsValid.Should().BeTrue();
        result.IsMissing.Should().BeFalse();
        result.IsInvalid.Should().BeFalse();
        result.NormalizedBearerToken.Should().Be(expectedToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void ParseOptional_ShouldReturnMissingForAbsentToken(string? rawToken)
    {
        var result = WorkflowCallerCredentialTokens.ParseOptional(rawToken);

        result.Status.Should().Be(WorkflowCallerCredentialTokenParseStatus.Missing);
        result.IsMissing.Should().BeTrue();
        result.IsValid.Should().BeFalse();
        result.IsInvalid.Should().BeFalse();
        result.NormalizedBearerToken.Should().BeNull();
    }

    [Theory]
    [InlineData("Bearer")]
    [InlineData("Bearer token-123")]
    [InlineData("token 123")]
    [InlineData("token\t123")]
    [InlineData("token\n123")]
    public void ParseOptional_ShouldReturnInvalidForMalformedRawToken(string rawToken)
    {
        var result = WorkflowCallerCredentialTokens.ParseOptional(rawToken);

        result.Status.Should().Be(WorkflowCallerCredentialTokenParseStatus.Invalid);
        result.IsInvalid.Should().BeTrue();
        result.IsMissing.Should().BeFalse();
        result.IsValid.Should().BeFalse();
        result.NormalizedBearerToken.Should().BeNull();
    }
}
