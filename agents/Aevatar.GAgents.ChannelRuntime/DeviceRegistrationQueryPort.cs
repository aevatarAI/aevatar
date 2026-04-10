using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class DeviceRegistrationQueryPort : IDeviceRegistrationQueryPort
{
    private readonly IProjectionDocumentReader<DeviceRegistrationDocument, string> _documentReader;

    public DeviceRegistrationQueryPort(
        IProjectionDocumentReader<DeviceRegistrationDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<DeviceRegistrationEntry?> GetAsync(string registrationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(registrationId))
            return null;

        var document = await _documentReader.GetAsync(registrationId, ct);
        return document == null ? null : ToEntry(document);
    }

    public async Task<IReadOnlyList<DeviceRegistrationEntry>> QueryAllAsync(CancellationToken ct = default)
    {
        var result = await _documentReader.QueryAsync(
            new ProjectionDocumentQuery { Take = 1000 },
            ct);

        return result.Items
            .Select(static doc => ToEntry(doc))
            .ToArray();
    }

    private static DeviceRegistrationEntry ToEntry(DeviceRegistrationDocument document) =>
        new()
        {
            Id = document.Id ?? string.Empty,
            ScopeId = document.ScopeId ?? string.Empty,
            HmacKey = document.HmacKey ?? string.Empty,
            NyxConversationId = document.NyxConversationId ?? string.Empty,
            Description = document.Description ?? string.Empty,
        };
}
