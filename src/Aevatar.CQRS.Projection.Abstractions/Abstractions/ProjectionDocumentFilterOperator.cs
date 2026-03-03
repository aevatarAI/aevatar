namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public enum ProjectionDocumentFilterOperator
{
    Eq = 0,
    In = 1,
    Exists = 2,
    Gt = 3,
    Gte = 4,
    Lt = 5,
    Lte = 6,
}
