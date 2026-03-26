using Aevatar.AppPlatform.Abstractions.Access;
using Aevatar.AppPlatform.Application.Services;
using FluentAssertions;
using Xunit;

namespace Aevatar.AppPlatform.Tests;

public class OwnerScopeAppAccessAuthorizerTests
{
    private readonly OwnerScopeAppAccessAuthorizer _authorizer = new();

    [Fact]
    public async Task AuthorizeAsync_ShouldAllowPublicReadWithoutAuthenticatedSubject()
    {
        var decision = await _authorizer.AuthorizeAsync(new AppAccessRequest(
            SubjectScopeId: null,
            Action: AppAccessActions.Read,
            Resource: new AppAccessResource("scope-owner", IsPublic: true)));

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task AuthorizeAsync_ShouldDenyPrivateReadWithoutAuthenticatedSubject()
    {
        var decision = await _authorizer.AuthorizeAsync(new AppAccessRequest(
            SubjectScopeId: null,
            Action: AppAccessActions.Read,
            Resource: new AppAccessResource("scope-owner")));

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("authenticated scope");
    }

    [Fact]
    public async Task AuthorizeAsync_ShouldAllowPrivateReadForOwnerScope()
    {
        var decision = await _authorizer.AuthorizeAsync(new AppAccessRequest(
            SubjectScopeId: "scope-owner",
            Action: AppAccessActions.Read,
            Resource: new AppAccessResource("scope-owner")));

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task AuthorizeAsync_ShouldRestrictManageToOwnerScope()
    {
        var denied = await _authorizer.AuthorizeAsync(new AppAccessRequest(
            SubjectScopeId: "scope-other",
            Action: AppAccessActions.Manage,
            Resource: new AppAccessResource("scope-owner")));
        var allowed = await _authorizer.AuthorizeAsync(new AppAccessRequest(
            SubjectScopeId: "scope-owner",
            Action: AppAccessActions.Manage,
            Resource: new AppAccessResource("scope-owner")));

        denied.Allowed.Should().BeFalse();
        denied.Reason.Should().Contain("owner scope");
        allowed.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task AuthorizeAsync_ShouldAllowPublicInvokeWithoutAuthenticatedSubject()
    {
        var decision = await _authorizer.AuthorizeAsync(new AppAccessRequest(
            SubjectScopeId: null,
            Action: AppAccessActions.Invoke,
            Resource: new AppAccessResource("scope-owner", IsPublic: true)));

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task AuthorizeAsync_ShouldAllowObserveForOwnerScope()
    {
        var decision = await _authorizer.AuthorizeAsync(new AppAccessRequest(
            SubjectScopeId: "scope-owner",
            Action: AppAccessActions.Observe,
            Resource: new AppAccessResource("scope-owner")));

        decision.Allowed.Should().BeTrue();
    }

    [Theory]
    [InlineData(AppAccessActions.Write)]
    [InlineData(AppAccessActions.Publish)]
    [InlineData(AppAccessActions.ManageResources)]
    public async Task AuthorizeAsync_ShouldRestrictMutationActionsToOwnerScope(string action)
    {
        var denied = await _authorizer.AuthorizeAsync(new AppAccessRequest(
            SubjectScopeId: "scope-other",
            Action: action,
            Resource: new AppAccessResource("scope-owner")));
        var allowed = await _authorizer.AuthorizeAsync(new AppAccessRequest(
            SubjectScopeId: "scope-owner",
            Action: action,
            Resource: new AppAccessResource("scope-owner")));

        denied.Allowed.Should().BeFalse();
        allowed.Allowed.Should().BeTrue();
    }
}
