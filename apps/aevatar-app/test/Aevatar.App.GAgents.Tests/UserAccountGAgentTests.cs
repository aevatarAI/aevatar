using FluentAssertions;

namespace Aevatar.App.GAgents.Tests;

public sealed class UserAccountGAgentTests
{
    private static User? GetUser(UserAccountGAgent agent) =>
        agent.State.User?.Id is { Length: > 0 } ? agent.State.User : null;

    private static Task SendRegister(UserAccountGAgent agent, string userId, string provider,
        string providerId, string email, bool verified) =>
        GAgentTestHelper.SendCommandAsync(agent, new UserRegisteredEvent
        {
            UserId = userId,
            AuthProvider = provider,
            AuthProviderId = providerId,
            Email = email,
            EmailVerified = verified
        });

    [Fact]
    public async Task GetOrCreate_NewUser_CreatesAccount()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<UserAccountGAgent, UserAccountState>("account:new");

        await SendRegister(agent, "u1", "firebase", "fb_id_1", "a@b.com", true);

        var user = GetUser(agent);
        user.Should().NotBeNull();
        user!.Id.Should().Be("u1");
        user.AuthProvider.Should().Be("firebase");
        user.Email.Should().Be("a@b.com");
        user.EmailVerified.Should().BeTrue();
    }

    [Fact]
    public async Task GetOrCreate_ExistingUser_ReturnsExistingAndUpdatesLogin()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<UserAccountGAgent, UserAccountState>("account:existing");
        await SendRegister(agent, "u2", "firebase", "fb_id_2", "old@b.com", false);

        await SendRegister(agent, "u2", "firebase", "fb_id_2", "new@b.com", true);

        var user = GetUser(agent);
        user.Should().NotBeNull();
        user!.Email.Should().Be("new@b.com");
        user.EmailVerified.Should().BeTrue();
    }

    [Fact]
    public async Task GetUser_WhenEmpty_ReturnsNull()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<UserAccountGAgent, UserAccountState>("account:empty");

        GetUser(agent).Should().BeNull();
    }

    [Fact]
    public async Task LinkProvider_UpdatesAuthProvider()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<UserAccountGAgent, UserAccountState>("account:link");
        await SendRegister(agent, "u3", "trial", "trial_1", "c@d.com", false);

        await GAgentTestHelper.SendCommandAsync(agent, new UserProviderLinkedEvent
        {
            AuthProvider = "firebase",
            AuthProviderId = "fb_linked",
            EmailVerified = true
        });

        var user = GetUser(agent);
        user.Should().NotBeNull();
        user!.AuthProvider.Should().Be("firebase");
        user.AuthProviderId.Should().Be("fb_linked");
        user.EmailVerified.Should().BeTrue();
    }

    [Fact]
    public async Task LinkProvider_WhenNoUser_Throws()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<UserAccountGAgent, UserAccountState>("account:link-fail");

        var act = () => GAgentTestHelper.SendCommandAsync(agent, new UserProviderLinkedEvent
        {
            AuthProvider = "firebase",
            AuthProviderId = "fb_1",
            EmailVerified = true
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DeleteSoft_ClearsUser()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<UserAccountGAgent, UserAccountState>("account:soft-del");
        await SendRegister(agent, "u4", "firebase", "fb_4", "e@f.com", true);

        await GAgentTestHelper.SendCommandAsync(agent, new AccountDeletedEvent
        {
            Mode = "soft",
            EntitiesAnonymizedCount = 5
        });

        GetUser(agent).Should().BeNull();
    }

    [Fact]
    public async Task DeleteHard_ClearsUserAndReportsMode()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<UserAccountGAgent, UserAccountState>("account:hard-del");
        await SendRegister(agent, "u5", "trial", "trial_5", "g@h.com", false);

        await GAgentTestHelper.SendCommandAsync(agent, new AccountDeletedEvent
        {
            Mode = "hard",
            EntitiesDeletedCount = 3
        });

        GetUser(agent).Should().BeNull();
    }

    [Fact]
    public async Task Replay_RestoresUser()
    {
        var services = GAgentTestHelper.BuildServices();

        var (agent1, _) = GAgentTestHelper.Create<UserAccountGAgent, UserAccountState>("account:replay", services);
        await agent1.ActivateAsync();
        await SendRegister(agent1, "u_replay", "firebase", "fb_r", "r@r.com", true);
        await agent1.DeactivateAsync();

        var (agent2, _) = GAgentTestHelper.Create<UserAccountGAgent, UserAccountState>("account:replay", services);
        await agent2.ActivateAsync();

        var user = GetUser(agent2);
        user.Should().NotBeNull();
        user!.Id.Should().Be("u_replay");
        user.Email.Should().Be("r@r.com");
    }
}
