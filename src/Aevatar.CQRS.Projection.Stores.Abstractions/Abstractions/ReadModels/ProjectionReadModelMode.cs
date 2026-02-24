namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public enum ProjectionReadModelMode
{
    CustomReadModel = 0,
    DefaultReadModel = 1,
    StateOnly = 2,
}
