namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public enum ProjectionDocumentValueKind
{
    None = 0,
    String = 1,
    StringList = 2,
    Int64 = 3,
    Int64List = 4,
    Double = 5,
    DoubleList = 6,
    Bool = 7,
    BoolList = 8,
    DateTime = 9,
    DateTimeList = 10,
}
