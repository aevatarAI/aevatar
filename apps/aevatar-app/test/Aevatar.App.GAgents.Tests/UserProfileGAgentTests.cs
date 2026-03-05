using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.App.GAgents.Tests;

public sealed class UserProfileGAgentTests
{
    private static Profile? GetProfile(UserProfileGAgent agent) =>
        agent.State.Profile?.UserId is { Length: > 0 } ? agent.State.Profile : null;

    private static Task SendCreate(UserProfileGAgent agent, string userId, string firstName,
        string lastName, string? gender, Timestamp? dob, string? timezone,
        IEnumerable<string> interests, string? purpose, bool notif, string? reminder)
    {
        var evt = new ProfileCreatedEvent
        {
            UserId = userId,
            FirstName = firstName,
            LastName = lastName,
            Gender = gender ?? string.Empty,
            DateOfBirth = dob,
            Timezone = timezone ?? "UTC",
            Purpose = purpose ?? string.Empty,
            NotificationsEnabled = notif,
            ReminderTime = reminder ?? string.Empty,
        };
        evt.Interests.AddRange(interests);
        return GAgentTestHelper.SendCommandAsync(agent, evt);
    }

    [Fact]
    public async Task Create_StoresProfile()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<UserProfileGAgent, UserProfileState>("profile:create");

        await SendCreate(agent, "u1", "Alice", "Smith", "female",
            Timestamp.FromDateTimeOffset(new DateTimeOffset(1990, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            "America/New_York",
            ["gardening", "meditation"], "growth", true, "09:00");

        var profile = GetProfile(agent);
        profile.Should().NotBeNull();
        profile!.UserId.Should().Be("u1");
        profile.FirstName.Should().Be("Alice");
        profile.LastName.Should().Be("Smith");
        profile.Purpose.Should().Be("growth");
        profile.Timezone.Should().Be("America/New_York");
        profile.Interests.Should().Contain("gardening");
        profile.Interests.Should().Contain("meditation");
    }

    [Fact]
    public async Task Create_WhenAlreadyExists_Throws()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<UserProfileGAgent, UserProfileState>("profile:dup");
        await SendCreate(agent, "u2", "Bob", "Jones", "male", null, "UTC", [], "", false, null);

        var act = () => SendCreate(agent, "u2", "Dup", "Test", "male", null, "UTC", [], "", false, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    [Fact]
    public async Task GetProfile_WhenEmpty_ReturnsNull()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<UserProfileGAgent, UserProfileState>("profile:empty");

        GetProfile(agent).Should().BeNull();
    }

    [Fact]
    public async Task Update_ModifiesFields()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<UserProfileGAgent, UserProfileState>("profile:update");
        await SendCreate(agent, "u3", "Charlie", "Brown", "male", null, "UTC", ["reading"], "learning", true, null);

        var existing = GetProfile(agent)!;
        var merged = existing.Clone();
        merged.FirstName = "Charles";
        merged.Purpose = "wisdom";
        await GAgentTestHelper.SendCommandAsync(agent, new ProfileUpdatedEvent { Profile = merged });

        var updated = GetProfile(agent);
        updated.Should().NotBeNull();
        updated!.FirstName.Should().Be("Charles");
        updated.Purpose.Should().Be("wisdom");
        updated.LastName.Should().Be("Brown");
    }

    [Fact]
    public async Task Update_WhenNotExists_Throws()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<UserProfileGAgent, UserProfileState>("profile:no-update");

        var merged = new Profile { FirstName = "X" };
        var act = () => GAgentTestHelper.SendCommandAsync(agent, new ProfileUpdatedEvent { Profile = merged });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task Delete_ClearsProfile()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<UserProfileGAgent, UserProfileState>("profile:delete");
        await SendCreate(agent, "u4", "Dave", "Wilson", "male", null, "UTC", [], "", false, null);

        await GAgentTestHelper.SendCommandAsync(agent, new ProfileDeletedEvent());

        GetProfile(agent).Should().BeNull();
    }

    [Fact]
    public async Task Update_ClearDateOfBirth_SetsNull()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<UserProfileGAgent, UserProfileState>("profile:clear-dob");
        var dob = Timestamp.FromDateTimeOffset(new DateTimeOffset(1995, 6, 15, 0, 0, 0, TimeSpan.Zero));
        await SendCreate(agent, "u5", "Eve", "Adams", "female", dob, "UTC", [], "", false, null);

        var existing = GetProfile(agent)!;
        var merged = existing.Clone();
        merged.DateOfBirth = null;
        await GAgentTestHelper.SendCommandAsync(agent, new ProfileUpdatedEvent { Profile = merged });

        var updated = GetProfile(agent);
        updated.Should().NotBeNull();
        updated!.DateOfBirth.Should().BeNull();
    }

    [Fact]
    public async Task Replay_RestoresProfile()
    {
        var services = GAgentTestHelper.BuildServices();

        var (agent1, _) = GAgentTestHelper.Create<UserProfileGAgent, UserProfileState>("profile:replay", services);
        await agent1.ActivateAsync();
        await SendCreate(agent1, "u_replay", "Replay", "User", "other", null, "UTC", ["test"], "replay", false, null);
        await agent1.DeactivateAsync();

        var (agent2, _) = GAgentTestHelper.Create<UserProfileGAgent, UserProfileState>("profile:replay", services);
        await agent2.ActivateAsync();

        var profile = GetProfile(agent2);
        profile.Should().NotBeNull();
        profile!.UserId.Should().Be("u_replay");
        profile.FirstName.Should().Be("Replay");
    }
}
