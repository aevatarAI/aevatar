using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Queries;

public sealed class ServiceRunQueryReader : IServiceRunQueryPort
{
    private readonly IProjectionDocumentReader<ServiceRunCurrentStateReadModel, string> _documentStore;
    private readonly bool _enabled;

    public ServiceRunQueryReader(
        IProjectionDocumentReader<ServiceRunCurrentStateReadModel, string> documentStore,
        ServiceProjectionOptions? options = null)
    {
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
        _enabled = options?.Enabled ?? true;
    }

    public async Task<IReadOnlyList<ServiceRunSnapshot>> ListAsync(
        ServiceRunQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!_enabled)
            return [];

        var boundedTake = Math.Clamp(query.Take, 1, 200);
        var filters = new List<ProjectionDocumentFilter>(2);
        if (!string.IsNullOrWhiteSpace(query.ScopeId))
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(ServiceRunCurrentStateReadModel.ScopeId),
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(query.ScopeId),
            });
        }
        if (!string.IsNullOrWhiteSpace(query.ServiceId))
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(ServiceRunCurrentStateReadModel.ServiceId),
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(query.ServiceId),
            });
        }

        var result = await _documentStore.QueryAsync(
            new ProjectionDocumentQuery
            {
                Take = boundedTake,
                Filters = filters,
                Sorts = new[]
                {
                    new ProjectionDocumentSort
                    {
                        FieldPath = nameof(ServiceRunCurrentStateReadModel.UpdatedAt),
                        Direction = ProjectionDocumentSortDirection.Desc,
                    },
                    new ProjectionDocumentSort
                    {
                        FieldPath = nameof(ServiceRunCurrentStateReadModel.RunId),
                        Direction = ProjectionDocumentSortDirection.Asc,
                    },
                },
            },
            ct);
        return result.Items.Take(boundedTake).Select(Map).ToList();
    }

    public async Task<ServiceRunSnapshot?> GetByRunIdAsync(
        string scopeId,
        string serviceId,
        string runId,
        CancellationToken ct = default)
    {
        if (!_enabled)
            return null;
        if (string.IsNullOrWhiteSpace(runId) ||
            string.IsNullOrWhiteSpace(scopeId) ||
            string.IsNullOrWhiteSpace(serviceId))
        {
            return null;
        }

        var direct = await _documentStore.GetAsync(
            ServiceRunIds.BuildKey(scopeId, serviceId, runId),
            ct);
        return direct == null ? null : Map(direct);
    }

    public async Task<ServiceRunSnapshot?> GetByCommandIdAsync(
        string scopeId,
        string serviceId,
        string commandId,
        CancellationToken ct = default)
    {
        if (!_enabled)
            return null;
        if (string.IsNullOrWhiteSpace(commandId))
            return null;

        var matches = await QueryByEqualityAsync(
            scopeId,
            serviceId,
            nameof(ServiceRunCurrentStateReadModel.CommandId),
            commandId.Trim(),
            ct);
        return matches.FirstOrDefault();
    }

    private async Task<IReadOnlyList<ServiceRunSnapshot>> QueryByEqualityAsync(
        string scopeId,
        string serviceId,
        string fieldPath,
        string value,
        CancellationToken ct)
    {
        var filters = new List<ProjectionDocumentFilter>(3)
        {
            new ProjectionDocumentFilter
            {
                FieldPath = fieldPath,
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(value),
            },
        };
        if (!string.IsNullOrWhiteSpace(scopeId))
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(ServiceRunCurrentStateReadModel.ScopeId),
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(scopeId),
            });
        }
        if (!string.IsNullOrWhiteSpace(serviceId))
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(ServiceRunCurrentStateReadModel.ServiceId),
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(serviceId),
            });
        }

        var result = await _documentStore.QueryAsync(
            new ProjectionDocumentQuery
            {
                Take = 5,
                Filters = filters,
            },
            ct);
        return result.Items.Select(Map).ToList();
    }

    private static ServiceRunSnapshot Map(ServiceRunCurrentStateReadModel readModel) =>
        new(
            readModel.ScopeId,
            readModel.ServiceId,
            readModel.ServiceKey,
            readModel.RunId,
            readModel.CommandId,
            readModel.CorrelationId,
            readModel.EndpointId,
            (ServiceImplementationKind)readModel.ImplementationKind,
            readModel.TargetActorId,
            readModel.RevisionId,
            readModel.DeploymentId,
            (ServiceRunStatus)readModel.Status,
            readModel.ActorId,
            readModel.TenantId,
            readModel.AppId,
            readModel.Namespace,
            readModel.StateVersion,
            readModel.LastEventId,
            readModel.CreatedAt,
            readModel.UpdatedAt);
}
