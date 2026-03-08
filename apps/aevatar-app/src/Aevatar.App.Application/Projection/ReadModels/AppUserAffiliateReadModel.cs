using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.App.Application.Projection.ReadModels;

public sealed class AppUserAffiliateReadModel : IProjectionReadModel
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public string Platform { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
