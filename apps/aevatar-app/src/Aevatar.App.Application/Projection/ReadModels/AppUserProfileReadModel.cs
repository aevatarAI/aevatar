using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.App.Application.Projection.ReadModels;

public sealed class AppUserProfileReadModel : IProjectionReadModel
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Gender { get; set; } = "";
    public string Timezone { get; set; } = "";
    public string Purpose { get; set; } = "";
    public bool NotificationsEnabled { get; set; }
    public string ReminderTime { get; set; } = "";
    public List<string> Interests { get; set; } = [];
    public DateTimeOffset? DateOfBirth { get; set; }
    public bool HasProfile { get; set; }
    public DateTimeOffset ProfileUpdatedAt { get; set; }
}
