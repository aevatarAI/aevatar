namespace Aevatar.Foundation.Abstractions.ExternalLinks;

/// <summary>
/// Factory for creating transport instances. One factory per transport type,
/// registered in DI and resolved by <see cref="ExternalLinkDescriptor.TransportType"/>.
/// </summary>
public interface IExternalLinkTransportFactory
{
    bool CanCreate(string transportType);
    IExternalLinkTransport Create();
}
