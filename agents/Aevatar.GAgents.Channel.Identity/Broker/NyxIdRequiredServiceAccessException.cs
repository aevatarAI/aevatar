namespace Aevatar.GAgents.Channel.Identity.Broker;

/// <summary>
/// Indicates that an OAuth authorization-code flow did not grant aevatar's
/// required NyxID service resource.
/// </summary>
public sealed class NyxIdRequiredServiceAccessException : Exception
{
    public string RequiredResource { get; }

    public NyxIdRequiredServiceAccessException(
        string requiredResource,
        string? message = null,
        Exception? innerException = null)
        : base(message ?? "NyxID authorization did not grant the required aevatar service.", innerException)
    {
        RequiredResource = requiredResource;
    }
}
