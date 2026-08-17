using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class CatalogueScopeWorkflowDescriptorSource
    : IScopeWorkflowPublishedServiceDescriptorSource
{
    private readonly IWorkflowCatalogueQueryPort _catalogueQueryPort;

    public CatalogueScopeWorkflowDescriptorSource(IWorkflowCatalogueQueryPort catalogueQueryPort)
    {
        _catalogueQueryPort = catalogueQueryPort ?? throw new ArgumentNullException(nameof(catalogueQueryPort));
    }

    public async Task<IReadOnlyList<ScopeWorkflowPublishedServiceDescriptor>> ListAsync(
        string scopeId,
        int take,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var queryTake = Math.Clamp(take, 1, 100);
        var descriptors = new List<ScopeWorkflowPublishedServiceDescriptor>(queryTake);
        string? cursor = null;

        do
        {
            var catalogue = await _catalogueQueryPort.QueryAsync(
                new ScopeWorkflowCatalogueQuery(normalizedScopeId, Cursor: cursor, Take: queryTake),
                ct);

            foreach (var row in catalogue.Items)
            {
                var descriptor = Map(row);
                if (descriptor == null)
                    continue;

                descriptors.Add(descriptor);
                if (descriptors.Count == queryTake)
                    return descriptors;
            }

            cursor = catalogue.NextPageToken;
        } while (!string.IsNullOrWhiteSpace(cursor));

        return descriptors;
    }

    public async Task<IReadOnlyList<ScopeWorkflowPublishedServiceDescriptor>> FindByWorkflowIdAsync(
        string scopeId,
        string workflowId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedWorkflowId = NormalizeRequired(workflowId, nameof(workflowId));
        string? cursor = null;

        do
        {
            var catalogue = await _catalogueQueryPort.QueryAsync(
                new ScopeWorkflowCatalogueQuery(normalizedScopeId, Query: normalizedWorkflowId, Cursor: cursor, Take: 100),
                ct);

            var exactRow = catalogue.Items.FirstOrDefault(row =>
                string.Equals(row.WorkflowId, normalizedWorkflowId, StringComparison.Ordinal));
            var descriptor = exactRow == null ? null : Map(exactRow);
            if (descriptor != null)
                return [descriptor];

            cursor = catalogue.NextPageToken;
        } while (!string.IsNullOrWhiteSpace(cursor));

        return [];
    }

    private static ScopeWorkflowPublishedServiceDescriptor? Map(ScopeWorkflowCatalogueRow row)
    {
        var committed = row.Committed;
        if (!row.HasCommittedSource ||
            committed == null ||
            string.IsNullOrWhiteSpace(row.PublishedServiceId) ||
            string.IsNullOrWhiteSpace(committed.ServiceAppId) ||
            string.IsNullOrWhiteSpace(committed.ServiceNamespace))
        {
            return null;
        }

        return new ScopeWorkflowPublishedServiceDescriptor(
            row.ScopeId,
            row.WorkflowId,
            committed.ServiceAppId.Trim(),
            committed.ServiceNamespace.Trim(),
            row.PublishedServiceId.Trim(),
            string.IsNullOrWhiteSpace(row.Name) ? row.WorkflowId : row.Name.Trim(),
            row.UpdatedAtUtc);
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{fieldName} is required.");
        return normalized;
    }
}
