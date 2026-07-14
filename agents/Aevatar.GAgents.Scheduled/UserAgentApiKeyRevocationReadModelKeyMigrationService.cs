using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Scheduled;

internal sealed class UserAgentApiKeyRevocationReadModelKeyMigrationService : IHostedService
{
    private const int PageSize = 200;

    private readonly IProjectionDocumentReader<UserAgentApiKeyRevocationDocument, string> _documentReader;
    private readonly IProjectionWriteDispatcher<UserAgentApiKeyRevocationDocument> _writeDispatcher;
    private readonly ILogger<UserAgentApiKeyRevocationReadModelKeyMigrationService> _logger;

    public UserAgentApiKeyRevocationReadModelKeyMigrationService(
        IProjectionDocumentReader<UserAgentApiKeyRevocationDocument, string> documentReader,
        IProjectionWriteDispatcher<UserAgentApiKeyRevocationDocument> writeDispatcher,
        ILogger<UserAgentApiKeyRevocationReadModelKeyMigrationService> logger)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var result = await MigrateAsync(ct);
        _logger.LogInformation(
            "Scheduled credential revocation read-model key migration completed: migrated={MigratedCount} maxStateVersion={MaxStateVersion}",
            result.MigratedCount,
            result.MaxStateVersion);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    internal async Task<UserAgentApiKeyRevocationReadModelKeyMigrationResult> MigrateAsync(
        CancellationToken ct = default)
    {
        var legacyDocuments = await ReadLegacyDocumentsAsync(ct);
        var migratedCount = 0;
        long? maxStateVersion = null;
        foreach (var legacyDocument in legacyDocuments)
        {
            var migratedDocument = legacyDocument.Clone();
            migratedDocument.Id = BuildCanonicalDocumentId(legacyDocument);

            var upsert = await _writeDispatcher.UpsertAsync(migratedDocument, ct);
            EnsureMigrationWriteAccepted(upsert, legacyDocument.Id, migratedDocument.Id);

            var delete = await _writeDispatcher.DeleteAsync(legacyDocument.Id, ct);
            EnsureMigrationWriteAccepted(delete, legacyDocument.Id, migratedDocument.Id);

            migratedCount++;
            maxStateVersion = !maxStateVersion.HasValue
                ? legacyDocument.StateVersion
                : Math.Max(maxStateVersion.Value, legacyDocument.StateVersion);
        }

        return new UserAgentApiKeyRevocationReadModelKeyMigrationResult(
            migratedCount,
            maxStateVersion);
    }

    private async Task<IReadOnlyList<UserAgentApiKeyRevocationDocument>> ReadLegacyDocumentsAsync(
        CancellationToken ct)
    {
        var legacyDocuments = new List<UserAgentApiKeyRevocationDocument>();
        string? cursor = null;
        do
        {
            var page = await _documentReader.QueryAsync(
                new ProjectionDocumentQuery
                {
                    Take = PageSize,
                    Cursor = cursor,
                },
                ct);
            foreach (var document in page.Items)
            {
                if (!string.Equals(document.Id, document.AgentId, StringComparison.Ordinal))
                    continue;

                if (string.IsNullOrWhiteSpace(document.AgentId) ||
                    string.IsNullOrWhiteSpace(document.ApiKeyId))
                {
                    throw new InvalidOperationException(
                        $"Legacy scheduled credential revocation document '{document.Id}' has an incomplete natural identity.");
                }

                legacyDocuments.Add(document.Clone());
            }

            cursor = page.NextCursor;
        }
        while (!string.IsNullOrEmpty(cursor));

        return legacyDocuments;
    }

    private static string BuildCanonicalDocumentId(UserAgentApiKeyRevocationDocument document)
    {
        var secretReference = ScheduledAgentCredentialRevocationIdentity.ResolveSecretReferenceRef(document);
        return string.IsNullOrEmpty(secretReference)
            ? ScheduledAgentCredentialRevocationDocumentIds.BuildBlocked(
                document.AgentId.Trim(),
                document.ApiKeyId.Trim())
            : ScheduledAgentCredentialRevocationDocumentIds.Build(
                document.AgentId.Trim(),
                document.ApiKeyId.Trim(),
                secretReference);
    }

    private static void EnsureMigrationWriteAccepted(
        ProjectionWriteResult result,
        string legacyDocumentId,
        string canonicalDocumentId)
    {
        if (result.IsApplied || result.IsNonTerminal)
            return;

        throw new InvalidOperationException(
            $"Scheduled credential revocation read-model key migration was rejected for '{legacyDocumentId}' -> '{canonicalDocumentId}': {result.Disposition}.");
    }
}

internal sealed record UserAgentApiKeyRevocationReadModelKeyMigrationResult(
    int MigratedCount,
    long? MaxStateVersion);
