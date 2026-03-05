using FluentAssertions;

namespace Aevatar.App.GAgents.Tests;

public sealed class AuthLookupGAgentTests
{
    private static string? GetUserId(AuthLookupGAgent agent) =>
        string.IsNullOrEmpty(agent.State.UserId) ? null : agent.State.UserId;

    [Fact]
    public async Task SetUserId_StoresMapping()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<AuthLookupGAgent, AuthLookupState>("lookup:firebase:uid1");

        await GAgentTestHelper.SendCommandAsync(agent,
            new AuthLookupSetEvent { LookupKey = "firebase:uid1", UserId = "user_001" });

        GetUserId(agent).Should().Be("user_001");
        agent.State.LookupKey.Should().Be("firebase:uid1");
    }

    [Fact]
    public async Task GetUserId_WhenEmpty_ReturnsNull()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<AuthLookupGAgent, AuthLookupState>("lookup:empty");

        GetUserId(agent).Should().BeNull();
    }

    [Fact]
    public async Task Clear_RemovesMapping()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<AuthLookupGAgent, AuthLookupState>("lookup:clear-test");
        await GAgentTestHelper.SendCommandAsync(agent,
            new AuthLookupSetEvent { LookupKey = "email:test@example.com", UserId = "user_002" });

        await GAgentTestHelper.SendCommandAsync(agent, new AuthLookupClearedEvent { LookupKey = "email:test@example.com" });

        GetUserId(agent).Should().BeNull();
        agent.State.LookupKey.Should().BeEmpty();
    }

    [Fact]
    public async Task SetUserId_OverwritesPreviousMapping()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<AuthLookupGAgent, AuthLookupState>("lookup:overwrite");
        await GAgentTestHelper.SendCommandAsync(agent,
            new AuthLookupSetEvent { LookupKey = "trial:t1", UserId = "user_A" });

        await GAgentTestHelper.SendCommandAsync(agent,
            new AuthLookupSetEvent { LookupKey = "trial:t1", UserId = "user_B" });

        GetUserId(agent).Should().Be("user_B");
    }

    [Fact]
    public async Task SetUserId_ThenClear_ThenSet_WorksCorrectly()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<AuthLookupGAgent, AuthLookupState>("lookup:lifecycle");

        await GAgentTestHelper.SendCommandAsync(agent,
            new AuthLookupSetEvent { LookupKey = "firebase:uid99", UserId = "user_first" });
        GetUserId(agent).Should().Be("user_first");

        await GAgentTestHelper.SendCommandAsync(agent, new AuthLookupClearedEvent { LookupKey = "firebase:uid99" });
        GetUserId(agent).Should().BeNull();

        await GAgentTestHelper.SendCommandAsync(agent,
            new AuthLookupSetEvent { LookupKey = "trial:t99", UserId = "user_second" });
        GetUserId(agent).Should().Be("user_second");
        agent.State.LookupKey.Should().Be("trial:t99");
    }

    [Fact]
    public async Task Replay_RestoresState()
    {
        var services = GAgentTestHelper.BuildServices();

        var (agent1, _) = GAgentTestHelper.Create<AuthLookupGAgent, AuthLookupState>("lookup:replay", services);
        await agent1.ActivateAsync();
        await GAgentTestHelper.SendCommandAsync(agent1,
            new AuthLookupSetEvent { LookupKey = "firebase:uid99", UserId = "user_replay" });
        await agent1.DeactivateAsync();

        var (agent2, _) = GAgentTestHelper.Create<AuthLookupGAgent, AuthLookupState>("lookup:replay", services);
        await agent2.ActivateAsync();

        GetUserId(agent2).Should().Be("user_replay");
        agent2.State.LookupKey.Should().Be("firebase:uid99");
    }
}
