using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.App.Application.Projection.ReadModels;

public sealed class AppUserAccountReadModel : IProjectionReadModel
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string AuthProvider { get; set; } = "";
    public string AuthProviderId { get; set; } = "";
    public string Email { get; set; } = "";
    public bool EmailVerified { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastLoginAt { get; set; }
    public bool Deleted { get; set; }
}
