using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Scripting.Projection.ReadModels;
using Aevatar.Studio.Application.Scripts.Contracts;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

internal sealed class ScriptNativeDocumentRuntimeActivityQueryPort : IScriptRuntimeActivityQueryPort
{
    private readonly IProjectionDocumentReader<ScriptNativeDocumentReadModel, string> _documentReader;

    public ScriptNativeDocumentRuntimeActivityQueryPort(
        IProjectionDocumentReader<ScriptNativeDocumentReadModel, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<ScriptRuntimeActivitySnapshot?> GetAsync(
        string actorId,
        CancellationToken ct = default)
    {
        var document = await _documentReader.GetAsync(actorId, ct);
        return document == null ? null : Map(document);
    }

    public async Task<IReadOnlyList<ScriptRuntimeActivitySnapshot>> ListAsync(
        int take = 200,
        CancellationToken ct = default)
    {
        var documents = await _documentReader.QueryAsync(
            new ProjectionDocumentQuery
            {
                Take = Math.Clamp(take, 1, 1000),
                Sorts =
                [
                    new ProjectionDocumentSort
                    {
                        FieldPath = nameof(ScriptNativeDocumentReadModel.UpdatedAtUtcValue),
                        Direction = ProjectionDocumentSortDirection.Desc,
                    },
                ],
            },
            ct);

        return documents.Items.Select(Map).ToArray();
    }

    private static ScriptRuntimeActivitySnapshot Map(ScriptNativeDocumentReadModel document)
    {
        var fields = document.FieldsValue?.Fields;
        return new ScriptRuntimeActivitySnapshot(
            document.Id,
            document.ScriptId,
            document.DefinitionActorId,
            document.Revision,
            GetString(fields, AppScriptProtocol.InputField),
            GetString(fields, AppScriptProtocol.OutputField),
            GetString(fields, AppScriptProtocol.StatusField),
            GetString(fields, AppScriptProtocol.LastCommandIdField),
            GetStringList(fields, AppScriptProtocol.NotesField),
            document.StateVersion,
            document.LastEventId,
            document.UpdatedAt);
    }

    private static string GetString(IDictionary<string, Value>? fields, string key)
    {
        if (fields?.TryGetValue(key, out var value) != true)
            return string.Empty;

        return value?.KindCase == Value.KindOneofCase.StringValue
            ? value.StringValue
            : string.Empty;
    }

    private static IReadOnlyList<string> GetStringList(IDictionary<string, Value>? fields, string key)
    {
        if (fields?.TryGetValue(key, out var value) != true ||
            value?.KindCase != Value.KindOneofCase.ListValue ||
            value.ListValue == null)
        {
            return [];
        }

        return value.ListValue.Values
            .Where(static item => item.KindCase == Value.KindOneofCase.StringValue)
            .Select(static item => item.StringValue)
            .ToArray();
    }
}
