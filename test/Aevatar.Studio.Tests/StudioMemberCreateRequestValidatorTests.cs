using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

/// <summary>
/// Application-layer enforcement of <see cref="StudioMemberInputLimits"/>.
/// These tests guard against the regression where the bounds drifted into
/// the Projection-layer command service: swap the command port and they
/// silently disappear. The validator is the single boundary now, and a
/// missing call from <see cref="StudioMemberService.CreateAsync"/> would
/// fail integration-level coverage too.
/// </summary>
public sealed class StudioMemberCreateRequestValidatorTests
{
    [Fact]
    public void Validate_ShouldRejectEmptyDisplayName()
    {
        var act = () => StudioMemberCreateRequestValidator.Validate(
            new CreateStudioMemberRequest(
                DisplayName: "   ",
                ImplementationKind: MemberImplementationKindNames.Workflow));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*displayName is required*");
    }

    [Fact]
    public void Validate_ShouldRejectDisplayNameOverCap()
    {
        var act = () => StudioMemberCreateRequestValidator.Validate(
            new CreateStudioMemberRequest(
                DisplayName: new string('a', StudioMemberInputLimits.MaxDisplayNameLength + 1),
                ImplementationKind: MemberImplementationKindNames.Workflow));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*displayName must be at most*");
    }

    [Fact]
    public void Validate_ShouldRejectDescriptionOverCap()
    {
        var act = () => StudioMemberCreateRequestValidator.Validate(
            new CreateStudioMemberRequest(
                DisplayName: "Alpha",
                ImplementationKind: MemberImplementationKindNames.Workflow,
                Description: new string('a', StudioMemberInputLimits.MaxDescriptionLength + 1)));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*description must be at most*");
    }

    [Fact]
    public void Validate_ShouldRejectMemberIdViolatingSlugPattern()
    {
        var act = () => StudioMemberCreateRequestValidator.Validate(
            new CreateStudioMemberRequest(
                DisplayName: "Alpha",
                ImplementationKind: MemberImplementationKindNames.Workflow,
                MemberId: "m bad space"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*memberId must match*");
    }

    [Fact]
    public void Validate_ShouldRejectMemberIdContainingActorIdSeparator()
    {
        var act = () => StudioMemberCreateRequestValidator.Validate(
            new CreateStudioMemberRequest(
                DisplayName: "Alpha",
                ImplementationKind: MemberImplementationKindNames.Workflow,
                MemberId: "m:nested"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*memberId must match*");
    }

    [Fact]
    public void Validate_ShouldAcceptOmittedMemberId()
    {
        // Empty memberId is allowed — the projection-layer command service
        // generates a random one. The validator must not reject this case.
        var act = () => StudioMemberCreateRequestValidator.Validate(
            new CreateStudioMemberRequest(
                DisplayName: "Alpha",
                ImplementationKind: MemberImplementationKindNames.Workflow));
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_ShouldAcceptValidSlugMemberId()
    {
        var act = () => StudioMemberCreateRequestValidator.Validate(
            new CreateStudioMemberRequest(
                DisplayName: "Alpha",
                ImplementationKind: MemberImplementationKindNames.Workflow,
                MemberId: "m-good_1"));
        act.Should().NotThrow();
    }
}
