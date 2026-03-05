using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.App.GAgents;

public sealed class UserProfileGAgent : GAgentBase<UserProfileState>
{
    [EventHandler]
    public async Task HandleCreateProfile(ProfileCreatedEvent evt)
    {
        if (State.Profile?.UserId is { Length: > 0 })
            throw new InvalidOperationException("Profile already exists");

        evt.CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        await PersistDomainEventAsync(evt);
    }

    [EventHandler]
    public async Task HandleUpdateProfile(ProfileUpdatedEvent evt)
    {
        if (State.Profile?.UserId is not { Length: > 0 })
            throw new InvalidOperationException("Profile not found");

        var profile = evt.Profile ?? State.Profile.Clone();
        profile.UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        await PersistDomainEventAsync(new ProfileUpdatedEvent
        {
            UserId = State.Profile.UserId,
            Profile = profile,
            UpdatedAt = profile.UpdatedAt,
        });
    }

    [EventHandler]
    public Task HandleDeleteProfile(ProfileDeletedEvent evt) =>
        PersistDomainEventAsync(new ProfileDeletedEvent
        {
            UserId = State.Profile?.UserId ?? string.Empty,
        });

    protected override UserProfileState TransitionState(UserProfileState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ProfileCreatedEvent>((s, created) =>
            {
                var profile = new Profile
                {
                    UserId = created.UserId,
                    FirstName = created.FirstName,
                    LastName = created.LastName,
                    Gender = created.Gender,
                    DateOfBirth = created.DateOfBirth,
                    Timezone = created.Timezone,
                    Purpose = created.Purpose,
                    NotificationsEnabled = created.NotificationsEnabled,
                    ReminderTime = created.ReminderTime,
                    CreatedAt = created.CreatedAt,
                    UpdatedAt = created.CreatedAt,
                };
                profile.Interests.AddRange(created.Interests);
                s.Profile = profile;
                return s;
            })
            .On<ProfileUpdatedEvent>((s, updated) =>
            {
                s.Profile = updated.Profile;
                return s;
            })
            .On<ProfileDeletedEvent>((s, _) =>
            {
                s.Profile = null;
                return s;
            })
            .OrCurrent();
}
