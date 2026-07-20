namespace Aevatar.GAgents.Channel.Runtime;

public interface IChannelRelayProxyResponseClassifier
{
    ChannelRelayProxyResponseClassification Classify(string? response);
}

public sealed record ChannelRelayProxyResponseClassification(
    bool IsError,
    ChannelRelayProxyResponseKind Kind,
    string Detail,
    string? ProviderErrorCode = null)
{
    public static ChannelRelayProxyResponseClassification Success() =>
        new(false, ChannelRelayProxyResponseKind.None, string.Empty);

    public static ChannelRelayProxyResponseClassification Error(
        ChannelRelayProxyResponseKind kind,
        string detail,
        string? providerErrorCode = null) =>
        new(true, kind, detail, providerErrorCode);
}

public enum ChannelRelayProxyResponseKind
{
    None = 0,
    ProviderError = 1,
    PermissionDenied = 2,
}
