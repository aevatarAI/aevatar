namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public sealed class ProjectionProviderSelectionException : InvalidOperationException
{
    public ProjectionProviderSelectionException(
        Type readModelType,
        string requestedProviderName,
        IReadOnlyList<string> availableProviders,
        string reason)
        : base(BuildMessage(readModelType, requestedProviderName, availableProviders, reason))
    {
        ReadModelType = readModelType;
        RequestedProviderName = requestedProviderName;
        AvailableProviders = availableProviders;
        Reason = reason;
    }

    public Type ReadModelType { get; }

    public string RequestedProviderName { get; }

    public IReadOnlyList<string> AvailableProviders { get; }

    public string Reason { get; }

    private static string BuildMessage(
        Type readModelType,
        string requestedProviderName,
        IReadOnlyList<string> availableProviders,
        string reason)
    {
        var requested = requestedProviderName.Length == 0 ? "<unspecified>" : requestedProviderName;
        var available = availableProviders.Count == 0 ? "<none>" : string.Join(", ", availableProviders);
        return $"Provider selection failed for read-model '{readModelType.FullName}'. " +
               $"requested={requested}; available={available}; reason={reason}.";
    }
}
