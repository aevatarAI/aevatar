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
        var catalogue = await _catalogueQueryPort.QueryAsync(
            new ScopeWorkflowCatalogueQuery(normalizedScopeId, Take: queryTake),
            ct);

        return catalogue.Items
            .Select(Map)
            .Where(static descriptor => descriptor != null)
            .Select(static descriptor => descriptor!)
            .Take(queryTake)
            .ToArray();
    }

    public async Task<IReadOnlyList<ScopeWorkflowPublishedServiceDescriptor>> FindByWorkflowIdAsync(
        string scopeId,
        string workflowId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedWorkflowId = NormalizeRequired(workflowId, nameof(workflowId));
        var catalogue = await _catalogueQueryPort.QueryAsync(
            new ScopeWorkflowCatalogueQuery(normalizedScopeId, Query: normalizedWorkflowId, Take: 100),
            ct);

        return catalogue.Items
            .Where(row => string.Equals(row.WorkflowId, normalizedWorkflowId, StringComparison.Ordinal))
            .Select(Map)
            .Where(static descriptor => descriptor != null)
            .Select(static descriptor => descriptor!)
            .ToArray();
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
