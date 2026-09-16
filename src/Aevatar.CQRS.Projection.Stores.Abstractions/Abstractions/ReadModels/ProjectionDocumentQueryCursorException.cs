namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public sealed class ProjectionDocumentQueryCursorException : Exception
{
    public ProjectionDocumentQueryCursorException(string message)
        : base(message)
    {
    }

    public ProjectionDocumentQueryCursorException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
