namespace Aevatar.CQRS.Projection.Abstractions;

public enum ProjectionReadModelMode
{
    CustomReadModel = 0,
    DefaultReadModel = 1,
    StateOnly = 2,
}
