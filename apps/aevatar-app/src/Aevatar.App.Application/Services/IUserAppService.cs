using Aevatar.App.GAgents;

namespace Aevatar.App.Application.Services;

public interface IUserAppService
{
    Task<UserInfo> GetUserInfoAsync(string userId);

    Task<Profile> CreateProfileAsync(
        string userId,
        string? firstName,
        string? lastName,
        string? gender,
        string? dateOfBirth,
        string? timezone,
        IEnumerable<string>? interests,
        string? purpose,
        bool? notificationsEnabled,
        string? reminderTime);

    Task<Profile> UpdateProfileAsync(
        string userId,
        string? firstName,
        string? lastName,
        string? gender,
        string? dateOfBirth,
        IEnumerable<string>? interests,
        string? purpose,
        string? timezone,
        bool? notificationsEnabled,
        string? reminderTime);

    Task DeleteAccountAsync(string userId, bool hard);
}
