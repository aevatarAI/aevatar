using Aevatar.App.Application.Errors;
using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.GAgents;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.App.Application.Services;

public sealed class UserAppService : IUserAppService
{
    private readonly IActorAccessAppService _actors;
    private readonly IProjectionDocumentStore<AppUserAccountReadModel, string> _accountStore;
    private readonly IProjectionDocumentStore<AppUserProfileReadModel, string> _profileStore;

    public UserAppService(
        IActorAccessAppService actors,
        IProjectionDocumentStore<AppUserAccountReadModel, string> accountStore,
        IProjectionDocumentStore<AppUserProfileReadModel, string> profileStore)
    {
        _actors = actors;
        _accountStore = accountStore;
        _profileStore = profileStore;
    }

    public async Task<UserInfo> GetUserInfoAsync(string userId)
    {
        var accountKey = _actors.ResolveActorId<UserAccountGAgent>(userId);
        var account = await _accountStore.GetAsync(accountKey);
        if (account is null || account.Deleted)
            throw new NotFoundException("User");

        var profileKey = _actors.ResolveActorId<UserProfileGAgent>(userId);
        var profile = await _profileStore.GetAsync(profileKey);

        return BuildUserInfo(account, profile);
    }

    public async Task<Profile> CreateProfileAsync(
        string userId,
        string? firstName,
        string? lastName,
        string? gender,
        string? dateOfBirth,
        string? timezone,
        IEnumerable<string>? interests,
        string? purpose,
        bool? notificationsEnabled,
        string? reminderTime)
    {
        var profileKey = _actors.ResolveActorId<UserProfileGAgent>(userId);
        var existing = await _profileStore.GetAsync(profileKey);
        if (existing?.HasProfile == true)
            throw new ConflictException("Profile already exists. Use PATCH to update.");

        Timestamp? dob = null;
        if (!string.IsNullOrEmpty(dateOfBirth)
            && DateTimeOffset.TryParse(dateOfBirth, out var dobParsed))
            dob = Timestamp.FromDateTimeOffset(dobParsed);

        var interestsList = (interests ?? []).ToList();
        var evt = new ProfileCreatedEvent
        {
            UserId = userId,
            FirstName = firstName ?? string.Empty,
            LastName = lastName ?? string.Empty,
            Gender = gender ?? string.Empty,
            DateOfBirth = dob,
            Timezone = timezone ?? "UTC",
            Purpose = purpose ?? string.Empty,
            NotificationsEnabled = notificationsEnabled ?? false,
            ReminderTime = reminderTime ?? string.Empty,
        };
        evt.Interests.AddRange(interestsList);

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? dobOffset = null;
        if (dob is not null)
            dobOffset = dob.ToDateTimeOffset();

        await _actors.SendCommandAsync<UserProfileGAgent>(userId, evt);

        return BuildProfileFromFields(
            userId, firstName, lastName, gender, dobOffset,
            timezone ?? "UTC", interestsList, purpose,
            notificationsEnabled ?? false, reminderTime, now);
    }

    public async Task<Profile> UpdateProfileAsync(
        string userId,
        string? firstName,
        string? lastName,
        string? gender,
        string? dateOfBirth,
        IEnumerable<string>? interests,
        string? purpose,
        string? timezone,
        bool? notificationsEnabled,
        string? reminderTime)
    {
        var profileKey = _actors.ResolveActorId<UserProfileGAgent>(userId);
        var readModel = await _profileStore.GetAsync(profileKey);
        if (readModel is null || !readModel.HasProfile)
            throw new NotFoundException("Profile");

        var updated = BuildProfileFromReadModel(readModel);
        if (firstName is not null) updated.FirstName = firstName;
        if (lastName is not null) updated.LastName = lastName;
        if (gender is not null) updated.Gender = gender;
        if (dateOfBirth is not null)
        {
            if (string.IsNullOrEmpty(dateOfBirth))
                updated.DateOfBirth = null;
            else if (DateTimeOffset.TryParse(dateOfBirth, out var dobParsed))
                updated.DateOfBirth = Timestamp.FromDateTimeOffset(dobParsed);
        }
        if (timezone is not null) updated.Timezone = timezone;
        if (interests is not null) { updated.Interests.Clear(); updated.Interests.AddRange(interests); }
        if (purpose is not null) updated.Purpose = purpose;
        if (notificationsEnabled.HasValue) updated.NotificationsEnabled = notificationsEnabled.Value;
        if (reminderTime is not null) updated.ReminderTime = reminderTime;

        await _actors.SendCommandAsync<UserProfileGAgent>(userId,
            new ProfileUpdatedEvent { Profile = updated });

        return updated;
    }

    public async Task DeleteAccountAsync(string userId, bool hard)
    {
        if (hard)
        {
            await _actors.SendCommandAsync<SyncEntityGAgent>(userId,
                new EntitiesHardDeleteRequestedEvent());
            await _actors.SendCommandAsync<UserAccountGAgent>(userId,
                new AccountDeletedEvent { Mode = "hard" });
            await _actors.SendCommandAsync<UserProfileGAgent>(userId,
                new ProfileDeletedEvent());
            return;
        }

        await _actors.SendCommandAsync<SyncEntityGAgent>(userId,
            new EntitiesSoftDeleteRequestedEvent { UserId = userId });
        await _actors.SendCommandAsync<UserAccountGAgent>(userId,
            new AccountDeletedEvent { Mode = "soft" });
        await _actors.SendCommandAsync<UserProfileGAgent>(userId,
            new ProfileDeletedEvent());
    }

    private static UserInfo BuildUserInfo(AppUserAccountReadModel account, AppUserProfileReadModel? profileModel)
    {
        var user = new User
        {
            Id = account.UserId,
            AuthProvider = account.AuthProvider,
            AuthProviderId = account.AuthProviderId,
            Email = account.Email,
            EmailVerified = account.EmailVerified,
            CreatedAt = Timestamp.FromDateTimeOffset(account.CreatedAt),
            UpdatedAt = Timestamp.FromDateTimeOffset(account.LastLoginAt),
            LastLoginAt = Timestamp.FromDateTimeOffset(account.LastLoginAt),
        };

        Profile? profile = profileModel?.HasProfile == true
            ? BuildProfileFromReadModel(profileModel)
            : null;

        return new UserInfo(user, profile);
    }

    private static Profile BuildProfileFromReadModel(AppUserProfileReadModel m)
    {
        var profile = new Profile
        {
            UserId = m.UserId,
            FirstName = m.FirstName,
            LastName = m.LastName,
            Gender = m.Gender,
            Timezone = m.Timezone,
            Purpose = m.Purpose,
            NotificationsEnabled = m.NotificationsEnabled,
            ReminderTime = m.ReminderTime,
            DateOfBirth = m.DateOfBirth.HasValue
                ? Timestamp.FromDateTimeOffset(m.DateOfBirth.Value) : null,
            UpdatedAt = Timestamp.FromDateTimeOffset(m.ProfileUpdatedAt),
        };
        profile.Interests.AddRange(m.Interests);
        return profile;
    }

    private static Profile BuildProfileFromFields(
        string userId, string? firstName, string? lastName, string? gender,
        DateTimeOffset? dateOfBirth, string timezone, List<string> interests,
        string? purpose, bool notificationsEnabled, string? reminderTime,
        DateTimeOffset updatedAt)
    {
        var profile = new Profile
        {
            UserId = userId,
            FirstName = firstName ?? string.Empty,
            LastName = lastName ?? string.Empty,
            Gender = gender ?? string.Empty,
            Timezone = timezone,
            Purpose = purpose ?? string.Empty,
            NotificationsEnabled = notificationsEnabled,
            ReminderTime = reminderTime ?? string.Empty,
            DateOfBirth = dateOfBirth.HasValue
                ? Timestamp.FromDateTimeOffset(dateOfBirth.Value) : null,
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt),
        };
        profile.Interests.AddRange(interests);
        return profile;
    }

}

public sealed record UserInfo(User User, Profile? Profile);
