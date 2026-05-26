using Aevatar.ChatRouting.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;

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

        foreach (var lookupScope in BuildLookupScopes(callerScope))
        {
            var document = await LookupOneAsync(lookupScope, ct);
            if (TryBuildSnapshot(document, out var snapshot))
                return snapshot;
        }

        return null;
    }

    private async Task<ChatRoutePolicyCurrentStateDocument?> LookupOneAsync(
        OwnerScope lookupScope,
        CancellationToken ct)
    {
        var result = await _documentReader.QueryAsync(
            new ProjectionDocumentQuery
            {
                Take = 1,
                Filters = BuildCallerScopeFilters(lookupScope),
            },
            ct);

        return result.Items.FirstOrDefault();
    }

    private static bool TryBuildSnapshot(
        ChatRoutePolicyCurrentStateDocument? document,
        out ChatRoutePolicySnapshot? snapshot)
    {
        snapshot = null;
        if (document?.DefaultTarget is null ||
            document.DefaultTarget.ActionCase == ChatRouteAction.ActionOneofCase.None)
        {
            return false;
        }

        snapshot = new ChatRoutePolicySnapshot(document.DefaultTarget, document.Rules);
        return true;
    }

    private static IEnumerable<OwnerScope> BuildLookupScopes(OwnerScope callerScope)
    {
        yield return callerScope;

        if (string.Equals(callerScope.Platform, OwnerScope.NyxIdPlatform, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(callerScope.RegistrationScopeId) ||
            (string.IsNullOrEmpty(callerScope.NyxUserId) && string.IsNullOrEmpty(callerScope.SenderId)))
        {
            yield break;
        }

        yield return OwnerScope.ForChannel(
            string.Empty,
            callerScope.Platform,
            callerScope.RegistrationScopeId,
            string.Empty);
    }

    private static IReadOnlyList<ProjectionDocumentFilter> BuildCallerScopeFilters(OwnerScope callerScope) =>
    [
        new ProjectionDocumentFilter
        {
            FieldPath = $"{nameof(ChatRoutePolicyCurrentStateDocument.OwnerScope)}.{nameof(OwnerScope.NyxUserId)}",
            Operator = ProjectionDocumentFilterOperator.Eq,
            Value = ProjectionDocumentValue.FromString(callerScope.NyxUserId),
        },
        new ProjectionDocumentFilter
        {
            FieldPath = $"{nameof(ChatRoutePolicyCurrentStateDocument.OwnerScope)}.{nameof(OwnerScope.Platform)}",
            Operator = ProjectionDocumentFilterOperator.Eq,
            Value = ProjectionDocumentValue.FromString(callerScope.Platform),
        },
        new ProjectionDocumentFilter
        {
            FieldPath = $"{nameof(ChatRoutePolicyCurrentStateDocument.OwnerScope)}.{nameof(OwnerScope.RegistrationScopeId)}",
            Operator = ProjectionDocumentFilterOperator.Eq,
            Value = ProjectionDocumentValue.FromString(callerScope.RegistrationScopeId),
        },
        new ProjectionDocumentFilter
        {
            FieldPath = $"{nameof(ChatRoutePolicyCurrentStateDocument.OwnerScope)}.{nameof(OwnerScope.SenderId)}",
            Operator = ProjectionDocumentFilterOperator.Eq,
            Value = ProjectionDocumentValue.FromString(callerScope.SenderId),
        },
    ];
}
