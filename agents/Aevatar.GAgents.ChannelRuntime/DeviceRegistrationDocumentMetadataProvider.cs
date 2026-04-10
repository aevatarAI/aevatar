using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class DeviceRegistrationDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<DeviceRegistrationDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "device-registrations",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
