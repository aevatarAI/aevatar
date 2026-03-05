using FluentAssertions;
using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.Application.Projection.Reducers;
using Aevatar.App.GAgents;
using Google.Protobuf.WellKnownTypes;
using static Aevatar.App.Application.Tests.Projection.ProjectionTestHelpers;

namespace Aevatar.App.Application.Tests.Projection;

public sealed class UserProfileReducerTests
{
    private readonly DateTimeOffset _now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ProfileCreated_Populates_AllFields()
    {
        var reducer = new ProfileCreatedEventReducer();
        var model = new AppUserProfileReadModel { Id = "userprofile:u1" };
        var createdAt = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        var dob = new DateTime(1990, 5, 15, 0, 0, 0, DateTimeKind.Utc);
        var evt = new ProfileCreatedEvent
        {
            UserId = "u1",
            FirstName = "Alice",
            LastName = "Smith",
            Gender = "female",
            DateOfBirth = Timestamp.FromDateTime(dob),
            Purpose = "wellness",
            Timezone = "UTC+8",
            NotificationsEnabled = true,
            ReminderTime = "08:00",
            CreatedAt = Timestamp.FromDateTime(createdAt),
        };
        evt.Interests.AddRange(["yoga", "meditation"]);

        reducer.Reduce(model, CreateContext("userprofile:u1"), PackEnvelope(evt), _now).Should().BeTrue();

        model.UserId.Should().Be("u1");
        model.FirstName.Should().Be("Alice");
        model.LastName.Should().Be("Smith");
        model.Gender.Should().Be("female");
        model.DateOfBirth.Should().Be(new DateTimeOffset(dob));
        model.Purpose.Should().Be("wellness");
        model.Timezone.Should().Be("UTC+8");
        model.NotificationsEnabled.Should().BeTrue();
        model.ReminderTime.Should().Be("08:00");
        model.Interests.Should().BeEquivalentTo(["yoga", "meditation"]);
        model.HasProfile.Should().BeTrue();
        model.ProfileUpdatedAt.Should().Be(new DateTimeOffset(createdAt));
    }

    [Fact]
    public void ProfileUpdated_Updates_Fields_From_Profile()
    {
        var reducer = new ProfileUpdatedEventReducer();
        var model = new AppUserProfileReadModel
        {
            Id = "userprofile:u1",
            FirstName = "Old",
            HasProfile = true,
        };
        var updatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var evt = new ProfileUpdatedEvent
        {
            Profile = new Profile
            {
                FirstName = "Bob",
                LastName = "Jones",
                Gender = "male",
                Timezone = "UTC-5",
                Purpose = "fitness",
                NotificationsEnabled = false,
                ReminderTime = "09:00",
            },
            UpdatedAt = Timestamp.FromDateTime(updatedAt),
        };
        evt.Profile.Interests.AddRange(["running"]);

        reducer.Reduce(model, CreateContext("userprofile:u1"), PackEnvelope(evt), _now).Should().BeTrue();

        model.FirstName.Should().Be("Bob");
        model.LastName.Should().Be("Jones");
        model.Timezone.Should().Be("UTC-5");
        model.Interests.Should().BeEquivalentTo(["running"]);
        model.ProfileUpdatedAt.Should().Be(new DateTimeOffset(updatedAt));
    }

    [Fact]
    public void ProfileUpdated_Uses_Now_When_UpdatedAt_Missing()
    {
        var reducer = new ProfileUpdatedEventReducer();
        var model = new AppUserProfileReadModel { Id = "userprofile:u1" };
        var evt = new ProfileUpdatedEvent
        {
            Profile = new Profile { FirstName = "X" },
        };

        reducer.Reduce(model, CreateContext("userprofile:u1"), PackEnvelope(evt), _now);

        model.ProfileUpdatedAt.Should().Be(_now);
    }

    [Fact]
    public void ProfileDeleted_Clears_AllFields()
    {
        var reducer = new ProfileDeletedEventReducer();
        var model = new AppUserProfileReadModel
        {
            Id = "userprofile:u1",
            HasProfile = true,
            FirstName = "Alice",
            LastName = "Smith",
            Gender = "female",
            Timezone = "UTC+8",
            Purpose = "wellness",
            NotificationsEnabled = true,
            ReminderTime = "08:00",
            Interests = ["yoga"],
            DateOfBirth = new DateTimeOffset(1990, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
        var evt = new ProfileDeletedEvent();

        reducer.Reduce(model, CreateContext("userprofile:u1"), PackEnvelope(evt), _now).Should().BeTrue();

        model.HasProfile.Should().BeFalse();
        model.FirstName.Should().BeEmpty();
        model.LastName.Should().BeEmpty();
        model.Gender.Should().BeEmpty();
        model.Timezone.Should().BeEmpty();
        model.Purpose.Should().BeEmpty();
        model.NotificationsEnabled.Should().BeFalse();
        model.ReminderTime.Should().BeEmpty();
        model.Interests.Should().BeEmpty();
        model.DateOfBirth.Should().BeNull();
    }
}
