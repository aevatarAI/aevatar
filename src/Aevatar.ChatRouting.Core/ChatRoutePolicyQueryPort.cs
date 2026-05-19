using Aevatar.ChatRouting.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.ChatRouting;

namespace Aevatar.ChatRouting.Core;

public sealed class ChatRoutePolicyQueryPort : IChatRoutePolicyQueryPort
{
    private readonly IProjectionDocumentReader<ChatRoutePolicyCurrentStateDocument, string> _documentReader;

    public ChatRoutePolicyQueryPort(
        IProjectionDocumentReader<ChatRoutePolicyCurrentStateDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    // Implement (issue #693):
    //   Behavior: read caller-scoped policy snapshots from the projection readmodel; missing rows return null.
    //   Why this shape: query path stays readmodel-only and never touches event replay, actors, or projection priming.
    public async Task<ChatRoutePolicySnapshot?> LookupForCallerAsync(
        OwnerScope callerScope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerScope);

        var result = await _documentReader.QueryAsync(
            new ProjectionDocumentQuery
            {
                Take = 1,
                Filters = BuildCallerScopeFilters(callerScope),
            },
            ct);

        var document = result.Items.FirstOrDefault();
        if (document?.DefaultTarget is null ||
            document.DefaultTarget.ActionCase == ChatRouteAction.ActionOneofCase.None)
        {
            return null;
        }

        return new ChatRoutePolicySnapshot(document.DefaultTarget, document.Rules);
    }

    private static IReadOnlyList<ProjectionDocumentFilter> BuildCallerScopeFilters(OwnerScope callerScope) =>
    [
        new ProjectionDocumentFilter
        {
            FieldPath = $"{nameof(ChatRoutePolicyCurrentStateDocument.OwnerScope)}.{nameof(ChatRouteCallerScope.NyxUserId)}",
            Operator = ProjectionDocumentFilterOperator.Eq,
            Value = ProjectionDocumentValue.FromString(callerScope.NyxUserId),
        },
        new ProjectionDocumentFilter
        {
            FieldPath = $"{nameof(ChatRoutePolicyCurrentStateDocument.OwnerScope)}.{nameof(ChatRouteCallerScope.Platform)}",
            Operator = ProjectionDocumentFilterOperator.Eq,
            Value = ProjectionDocumentValue.FromString(callerScope.Platform),
        },
        new ProjectionDocumentFilter
        {
            FieldPath = $"{nameof(ChatRoutePolicyCurrentStateDocument.OwnerScope)}.{nameof(ChatRouteCallerScope.RegistrationScopeId)}",
            Operator = ProjectionDocumentFilterOperator.Eq,
            Value = ProjectionDocumentValue.FromString(callerScope.RegistrationScopeId),
        },
        new ProjectionDocumentFilter
        {
            FieldPath = $"{nameof(ChatRoutePolicyCurrentStateDocument.OwnerScope)}.{nameof(ChatRouteCallerScope.SenderId)}",
            Operator = ProjectionDocumentFilterOperator.Eq,
            Value = ProjectionDocumentValue.FromString(callerScope.SenderId),
        },
    ];
}
