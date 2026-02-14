namespace Aevatar.Cqrs.Projections.Stores;

public sealed class ChatRunReadModelNotFoundException : KeyNotFoundException
{
    public string RunId { get; }

    public ChatRunReadModelNotFoundException(string runId)
        : base($"Chat run read model not found: '{runId}'.")
    {
        RunId = runId;
    }
}
