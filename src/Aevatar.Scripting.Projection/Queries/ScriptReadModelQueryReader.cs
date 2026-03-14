using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Core.Artifacts;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Serialization;
using Aevatar.Scripting.Projection.ReadModels;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Projection.Queries;

public sealed class ScriptReadModelQueryReader : IScriptReadModelQueryReader
{
    private readonly IProjectionDocumentStore<ScriptReadModelDocument, string> _documentStore;
    private readonly IScriptDefinitionSnapshotPort _definitionSnapshotPort;
    private readonly IScriptBehaviorArtifactResolver _artifactResolver;
    private readonly IProtobufMessageCodec _codec;

    public ScriptReadModelQueryReader(
        IProjectionDocumentStore<ScriptReadModelDocument, string> documentStore,
        IScriptDefinitionSnapshotPort definitionSnapshotPort,
        IScriptBehaviorArtifactResolver artifactResolver,
        IProtobufMessageCodec codec)
    {
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
        _definitionSnapshotPort = definitionSnapshotPort ?? throw new ArgumentNullException(nameof(definitionSnapshotPort));
        _artifactResolver = artifactResolver ?? throw new ArgumentNullException(nameof(artifactResolver));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
    }

    public async Task<ScriptReadModelSnapshot?> GetSnapshotAsync(
        string actorId,
        CancellationToken ct = default)
    {
        var document = await _documentStore.GetAsync(actorId, ct);
        return document == null ? null : Map(document);
    }

    public async Task<IReadOnlyList<ScriptReadModelSnapshot>> ListSnapshotsAsync(
        int take = 200,
        CancellationToken ct = default)
    {
        var boundedTake = Math.Clamp(take, 1, 1000);
        var documents = await _documentStore.ListAsync(boundedTake, ct);
        return documents.Select(Map).ToArray();
    }

    public async Task<Any?> ExecuteDeclaredQueryAsync(
        string actorId,
        Any queryPayload,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(queryPayload);

        var snapshot = await GetSnapshotAsync(actorId, ct)
            ?? throw new InvalidOperationException($"Script read model not found: {actorId}");
        var definitionSnapshot = await _definitionSnapshotPort.GetRequiredAsync(
            snapshot.DefinitionActorId,
            snapshot.Revision,
            ct);
        var scriptPackage = definitionSnapshot.ScriptPackage?.Clone() ?? ScriptPackageModel.CreateSingleSourcePackage(definitionSnapshot.SourceText);
        var artifact = _artifactResolver.Resolve(new ScriptBehaviorArtifactRequest(
            definitionSnapshot.ScriptId,
            definitionSnapshot.Revision,
            scriptPackage,
            definitionSnapshot.SourceHash));
        if (!artifact.Descriptor.Queries.TryGetValue(queryPayload.TypeUrl ?? string.Empty, out var queryRegistration))
        {
            throw new InvalidOperationException(
                $"Script read model `{actorId}` rejected query payload type `{queryPayload.TypeUrl}`. " +
                $"Declared query types: {string.Join(", ", artifact.Descriptor.Queries.Keys)}.");
        }

        var behavior = artifact.CreateBehavior();
        try
        {
            var typedQuery = _codec.Unpack(queryPayload, queryRegistration.QueryClrType)
                ?? throw new InvalidOperationException($"Failed to unpack query payload `{queryPayload.TypeUrl}`.");
            var typedReadModel = _codec.Unpack(snapshot.ReadModelPayload, artifact.Descriptor.ReadModelClrType);
            var result = await behavior.ExecuteQueryAsync(
                typedQuery,
                new ScriptTypedReadModelSnapshot(
                    snapshot.ActorId,
                    snapshot.ScriptId,
                    snapshot.DefinitionActorId,
                    snapshot.Revision,
                    snapshot.ReadModelTypeUrl,
                    typedReadModel,
                    snapshot.StateVersion,
                    snapshot.LastEventId,
                    snapshot.UpdatedAt),
                ct);
            ValidateQueryResultContract(actorId, queryRegistration, result);
            return _codec.Pack(result)?.Clone();
        }
        finally
        {
            if (behavior is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else if (behavior is IDisposable disposable)
                disposable.Dispose();
        }
    }

    private static ScriptReadModelSnapshot Map(ScriptReadModelDocument document)
    {
        return new ScriptReadModelSnapshot(
            ActorId: document.Id,
            ScriptId: document.ScriptId,
            DefinitionActorId: document.DefinitionActorId,
            Revision: document.Revision,
            ReadModelTypeUrl: document.ReadModelTypeUrl,
            ReadModelPayload: document.ReadModelPayload?.Clone(),
            StateVersion: document.StateVersion,
            LastEventId: document.LastEventId,
            UpdatedAt: document.UpdatedAt);
    }

    private static void ValidateQueryResultContract(
        string actorId,
        ScriptQueryRegistration queryRegistration,
        IMessage? result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(queryRegistration);

        if (result == null)
            return;

        var expectedTypeUrl = ScriptMessageTypes.GetTypeUrl(queryRegistration.ResultClrType);
        var actualTypeUrl = ScriptMessageTypes.GetTypeUrl(result);
        if (string.Equals(expectedTypeUrl, actualTypeUrl, StringComparison.Ordinal))
            return;

        throw new InvalidOperationException(
            $"Script read model `{actorId}` returned `{actualTypeUrl}` for query `{queryRegistration.TypeUrl}`, " +
            $"but the declared result type is `{expectedTypeUrl}`.");
    }
}
