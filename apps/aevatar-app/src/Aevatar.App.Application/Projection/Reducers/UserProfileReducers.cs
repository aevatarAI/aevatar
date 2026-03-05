using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.GAgents;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.App.Application.Projection.Reducers;

public sealed class ProfileCreatedEventReducer
    : AppEventReducerBase<AppUserProfileReadModel, ProfileCreatedEvent>
{
    protected override bool Reduce(
        AppUserProfileReadModel readModel,
        AppProjectionContext context,
        EventEnvelope envelope,
        ProfileCreatedEvent evt,
        DateTimeOffset now)
    {
        readModel.UserId = evt.UserId;
        readModel.FirstName = evt.FirstName;
        readModel.LastName = evt.LastName;
        readModel.Gender = evt.Gender;
        readModel.DateOfBirth = evt.DateOfBirth?.ToDateTimeOffset();
        readModel.Purpose = evt.Purpose;
        readModel.Timezone = evt.Timezone;
        readModel.NotificationsEnabled = evt.NotificationsEnabled;
        readModel.ReminderTime = evt.ReminderTime;
        readModel.Interests = [.. evt.Interests];
        readModel.HasProfile = true;
        readModel.ProfileUpdatedAt = evt.CreatedAt?.ToDateTimeOffset() ?? now;
        return true;
    }
}

public sealed class ProfileUpdatedEventReducer
    : AppEventReducerBase<AppUserProfileReadModel, ProfileUpdatedEvent>
{
    protected override bool Reduce(
        AppUserProfileReadModel readModel,
        AppProjectionContext context,
        EventEnvelope envelope,
        ProfileUpdatedEvent evt,
        DateTimeOffset now)
    {
        if (evt.Profile is { } p)
        {
            readModel.FirstName = p.FirstName;
            readModel.LastName = p.LastName;
            readModel.Gender = p.Gender;
            readModel.Timezone = p.Timezone;
            readModel.Purpose = p.Purpose;
            readModel.NotificationsEnabled = p.NotificationsEnabled;
            readModel.ReminderTime = p.ReminderTime;
            readModel.Interests = [.. p.Interests];
            readModel.DateOfBirth = p.DateOfBirth?.ToDateTimeOffset();
        }

        readModel.ProfileUpdatedAt = evt.UpdatedAt?.ToDateTimeOffset() ?? now;
        return true;
    }
}

public sealed class ProfileDeletedEventReducer
    : AppEventReducerBase<AppUserProfileReadModel, ProfileDeletedEvent>
{
    protected override bool Reduce(
        AppUserProfileReadModel readModel,
        AppProjectionContext context,
        EventEnvelope envelope,
        ProfileDeletedEvent evt,
        DateTimeOffset now)
    {
        readModel.HasProfile = false;
        readModel.FirstName = "";
        readModel.LastName = "";
        readModel.Gender = "";
        readModel.Timezone = "";
        readModel.Purpose = "";
        readModel.NotificationsEnabled = false;
        readModel.ReminderTime = "";
        readModel.Interests = [];
        readModel.DateOfBirth = null;
        return true;
    }
}
