using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.Scheduled;

public sealed class SkillRunnerExecutionQueryPort : ISkillRunnerExecutionQueryPort
{
    private readonly IProjectionDocumentReader<SkillRunnerExecutionDocument, string> _documentReader;

    public SkillRunnerExecutionQueryPort(
        IProjectionDocumentReader<SkillRunnerExecutionDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<SkillRunnerExecutionDocument?> GetAsync(string agentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return null;

        return await _documentReader.GetAsync(agentId.Trim(), ct);
    }

    public async Task<IReadOnlyDictionary<string, SkillRunnerExecutionDocument>> QueryByAgentIdsAsync(
        IReadOnlyCollection<string> agentIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agentIds);

        var ids = agentIds
            .Select(static id => id?.Trim())
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
            return new Dictionary<string, SkillRunnerExecutionDocument>(StringComparer.Ordinal);

        var result = await _documentReader.QueryAsync(
            new ProjectionDocumentQuery
            {
                Take = ids.Length,
                Filters =
                [
                    new ProjectionDocumentFilter
                    {
                        FieldPath = nameof(SkillRunnerExecutionDocument.Id),
                        Operator = ids.Length == 1
                            ? ProjectionDocumentFilterOperator.Eq
                            : ProjectionDocumentFilterOperator.In,
                        Value = ids.Length == 1
                            ? ProjectionDocumentValue.FromString(ids[0])
                            : ProjectionDocumentValue.FromStrings(ids),
                    },
                ],
            },
            ct);

        return result.Items
            .Where(static doc => !string.IsNullOrWhiteSpace(doc.Id))
            .GroupBy(static doc => doc.Id, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderByDescending(doc => doc.StateVersion).First(),
                StringComparer.Ordinal);
    }
}
