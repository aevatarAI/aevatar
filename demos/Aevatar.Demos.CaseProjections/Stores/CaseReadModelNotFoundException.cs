namespace Aevatar.Demos.CaseProjections.Stores;

public sealed class CaseReadModelNotFoundException : KeyNotFoundException
{
    public string RunId { get; }

    public CaseReadModelNotFoundException(string runId)
        : base($"Case read model not found: '{runId}'.") =>
        RunId = runId;
}
