using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.ChatHistory;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

internal sealed class ProjectionChatCreateRecoveryReader : IChatCreateRecoveryReader
{
    private readonly IProjectionDocumentReader<ChatCreateRecoveryCurrentStateDocument, string> _documentReader;

    public ProjectionChatCreateRecoveryReader(
        IProjectionDocumentReader<ChatCreateRecoveryCurrentStateDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<ChatCreateRecoveryRecord?> FindAsync(
        string scopeId,
        string createIdempotencyKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scopeId) || string.IsNullOrWhiteSpace(createIdempotencyKey))
            return null;

        var normalizedScopeId = scopeId.Trim();
        var normalizedCreateIdempotencyKey = createIdempotencyKey.Trim();
        var result = await _documentReader.QueryAsync(new ProjectionDocumentQuery
        {
            Filters =
            [
                new ProjectionDocumentFilter
                {
                    FieldPath = "scope_id",
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString(normalizedScopeId),
                },
                new ProjectionDocumentFilter
                {
                    FieldPath = "create_idempotency_key",
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString(normalizedCreateIdempotencyKey),
                },
            ],
            Take = 1,
        }, ct).ConfigureAwait(false);

        var document = result.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.ScopeId, normalizedScopeId, StringComparison.Ordinal) &&
            string.Equals(candidate.CreateIdempotencyKey, normalizedCreateIdempotencyKey, StringComparison.Ordinal));
        return document is null
            ? null
            : new ChatCreateRecoveryRecord(
                document.ScopeId,
                document.CreateIdempotencyKey,
                document.ConversationId,
                document.TurnId,
                document.Status,
                document.SourceVersion,
                document.DeliveryActorId,
                document.CreateRequestHash);
    }
}
