using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.Channel.Runtime;

public sealed class ConversationDeliveryQueryPort : IConversationDeliveryQueryPort
{
    private readonly IProjectionDocumentReader<ConversationDeliveryCurrentStateDocument, string> _documentReader;

    public ConversationDeliveryQueryPort(
        IProjectionDocumentReader<ConversationDeliveryCurrentStateDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public Task<ConversationDeliveryCurrentStateDocument?> GetAsync(string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return Task.FromResult<ConversationDeliveryCurrentStateDocument?>(null);

        return _documentReader.GetAsync(actorId.Trim(), ct);
    }
}
