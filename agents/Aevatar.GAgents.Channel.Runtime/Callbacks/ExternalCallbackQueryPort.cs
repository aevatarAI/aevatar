using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>Lookup-only current-state replica. No activation, replay or actor state reads.</summary>
public interface IExternalCallbackQueryPort
{
    Task<ExternalCallbackCurrentStateDocument?> FindAsync(string? callbackId, string? externalRequestId, CancellationToken ct = default);
}

public sealed class ExternalCallbackQueryPort(IProjectionDocumentReader<ExternalCallbackCurrentStateDocument, string> reader)
    : IExternalCallbackQueryPort
{
    public async Task<ExternalCallbackCurrentStateDocument?> FindAsync(string? callbackId, string? externalRequestId, CancellationToken ct = default)
    {
        var hasCallback = !string.IsNullOrWhiteSpace(callbackId);
        if (!hasCallback && string.IsNullOrWhiteSpace(externalRequestId)) return null;
        var result = await reader.QueryAsync(new ProjectionDocumentQuery
        {
            Take = 2,
            Filters = [new()
            {
                FieldPath = hasCallback ? nameof(ExternalCallbackCurrentStateDocument.CallbackId) : nameof(ExternalCallbackCurrentStateDocument.ExternalRequestId),
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(hasCallback ? callbackId! : externalRequestId!),
            }],
        }, ct);
        if (result.Items.Count > 1)
            throw new InvalidOperationException("External callback identity maps to multiple authorities.");
        var document = result.Items.SingleOrDefault();
        if (document is not null && !string.IsNullOrEmpty(externalRequestId) && document.ExternalRequestId != externalRequestId)
            throw new ArgumentException("External request does not match callback registration.");
        return document;
    }
}
