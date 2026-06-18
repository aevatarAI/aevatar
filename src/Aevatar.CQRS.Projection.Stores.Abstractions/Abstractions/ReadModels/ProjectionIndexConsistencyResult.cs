namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public enum ProjectionIndexConsistencyStatus
{
    Consistent = 0,
    Drifted = 1,
    Missing = 2,
}

public sealed record ProjectionIndexConsistencyResult(
    string Provider,
    string IndexAlias,
    string ExpectedPhysicalIndex,
    string? CurrentPhysicalIndex,
    ProjectionIndexConsistencyStatus Status,
    string Message)
{
    public bool IsConsistent => Status == ProjectionIndexConsistencyStatus.Consistent;

    public IReadOnlyDictionary<string, string> ToDiagnosticDetails()
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["provider"] = Provider,
            ["indexAlias"] = IndexAlias,
            ["expectedPhysicalIndex"] = ExpectedPhysicalIndex,
            ["status"] = Status.ToString(),
        };

        if (!string.IsNullOrWhiteSpace(CurrentPhysicalIndex))
            details["currentPhysicalIndex"] = CurrentPhysicalIndex;

        return details;
    }
}
