using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class StudioTeamCreateRequestValidatorTests
{
    [Fact]
    public void Validate_ShouldRejectEmptyDisplayName()
    {
        var act = () => StudioTeamCreateRequestValidator.Validate(
            new CreateStudioTeamRequest(DisplayName: "   "));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*displayName is required*");
    }

    [Fact]
    public void Validate_ShouldRejectNullDisplayName()
    {
        var act = () => StudioTeamCreateRequestValidator.Validate(
            new CreateStudioTeamRequest(DisplayName: null!));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*displayName is required*");
    }

    [Fact]
    public void Validate_ShouldRejectDisplayNameOverCap()
    {
        var act = () => StudioTeamCreateRequestValidator.Validate(
            new CreateStudioTeamRequest(
                DisplayName: new string('a', StudioTeamInputLimits.MaxDisplayNameLength + 1)));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*displayName must be at most*");
    }

    [Fact]
    public void Validate_ShouldAcceptDisplayNameAtCap()
    {
        var act = () => StudioTeamCreateRequestValidator.Validate(
            new CreateStudioTeamRequest(
                DisplayName: new string('a', StudioTeamInputLimits.MaxDisplayNameLength)));

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_ShouldRejectDescriptionOverCap()
    {
        var act = () => StudioTeamCreateRequestValidator.Validate(
            new CreateStudioTeamRequest(
                DisplayName: "Alpha",
                Description: new string('a', StudioTeamInputLimits.MaxDescriptionLength + 1)));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*description must be at most*");
    }

    [Fact]
    public void Validate_ShouldAcceptOmittedDescription()
    {
        var act = () => StudioTeamCreateRequestValidator.Validate(
            new CreateStudioTeamRequest(DisplayName: "Alpha"));

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_ShouldRejectTeamIdViolatingSlugPattern()
    {
        var act = () => StudioTeamCreateRequestValidator.Validate(
            new CreateStudioTeamRequest(
                DisplayName: "Alpha",
                TeamId: "bad team id"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*teamId must match*");
    }

    [Fact]
    public void Validate_ShouldRejectTeamIdContainingSeparator()
    {
        var act = () => StudioTeamCreateRequestValidator.Validate(
            new CreateStudioTeamRequest(
                DisplayName: "Alpha",
                TeamId: "t:nested"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*teamId must match*");
    }

    [Fact]
    public void Validate_ShouldRejectTeamIdOverCap()
    {
        var act = () => StudioTeamCreateRequestValidator.Validate(
            new CreateStudioTeamRequest(
                DisplayName: "Alpha",
                TeamId: new string('a', StudioTeamInputLimits.MaxTeamIdLength + 1)));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*teamId must be at most*");
    }

    [Fact]
    public void Validate_ShouldAcceptOmittedTeamId()
    {
        var act = () => StudioTeamCreateRequestValidator.Validate(
            new CreateStudioTeamRequest(DisplayName: "Alpha"));

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_ShouldAcceptValidSlugTeamId()
    {
        var act = () => StudioTeamCreateRequestValidator.Validate(
            new CreateStudioTeamRequest(
                DisplayName: "Alpha",
                TeamId: "t-good_1"));

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_ShouldRejectNullRequest()
    {
        var act = () => StudioTeamCreateRequestValidator.Validate(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
