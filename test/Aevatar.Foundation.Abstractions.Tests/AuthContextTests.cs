using Aevatar.Foundation.Abstractions.Credentials;
using Shouldly;

namespace Aevatar.Foundation.Abstractions.Tests;

public sealed class AuthContextTests
{
    [Fact]
    public void Bot_Factory_CreatesBotContext()
    {
        var context = AuthContext.Bot("secrets://bot/default");

        context.Principal.ShouldBe(AuthPrincipal.Bot);
        context.PrincipalId.ShouldBeNull();
        context.CredentialRef.ShouldBe("secrets://bot/default");
        context.OnBehalfOfUserId.ShouldBeNull();
        context.UsesBotIdentity.ShouldBeTrue();
    }

    [Fact]
    public void User_Factory_CreatesUserContext()
    {
        var context = AuthContext.User("user-123", "secrets://user/123");

        context.Principal.ShouldBe(AuthPrincipal.User);
        context.PrincipalId.ShouldBe("user-123");
        context.CredentialRef.ShouldBe("secrets://user/123");
        context.OnBehalfOfUserId.ShouldBeNull();
        context.UsesBotIdentity.ShouldBeFalse();
    }

    [Fact]
    public void OnBehalfOfUser_Factory_CreatesDelegatedContext()
    {
        var context = AuthContext.OnBehalfOfUser(
            principalId: "workflow-runner",
            onBehalfOfUserId: "owner-42",
            credentialRef: "secrets://workflow/owner-42");

        context.Principal.ShouldBe(AuthPrincipal.OnBehalfOfUser);
        context.PrincipalId.ShouldBe("workflow-runner");
        context.OnBehalfOfUserId.ShouldBe("owner-42");
        context.CredentialRef.ShouldBe("secrets://workflow/owner-42");
    }

    [Fact]
    public void Constructor_NormalizesWhitespaceOnlyOptionals()
    {
        var context = new AuthContext(AuthPrincipal.Bot, credentialRef: "   ");

        context.CredentialRef.ShouldBeNull();
    }

    [Fact]
    public void Constructor_RejectsBotPrincipalId()
    {
        Should.Throw<ArgumentException>(() => new AuthContext(
                AuthPrincipal.Bot,
                principalId: "bot-id"))
            .ParamName.ShouldBe("principalId");
    }

    [Fact]
    public void Constructor_RejectsMissingUserPrincipalId()
    {
        Should.Throw<ArgumentException>(() => new AuthContext(AuthPrincipal.User))
            .ParamName.ShouldBe("principalId");
    }

    [Fact]
    public void Constructor_RejectsMissingDelegatedAuditTarget()
    {
        Should.Throw<ArgumentException>(() => new AuthContext(
                AuthPrincipal.OnBehalfOfUser,
                principalId: "workflow-runner"))
            .ParamName.ShouldBe("onBehalfOfUserId");
    }
}
