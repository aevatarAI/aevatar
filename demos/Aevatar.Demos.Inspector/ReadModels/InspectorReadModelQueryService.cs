using System.Text.Json;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.Registry;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Projection.ReadModels;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Aevatar.Demos.Inspector.ReadModels;

public sealed class InspectorReadModelQueryService
{
    private const int DefaultTake = 200;
    private static readonly JsonFormatter Formatter = new(
        JsonFormatter.Settings.Default
            .WithPreserveProtoFieldNames(true)
            .WithFormatDefaultValues(true)
            .WithTypeRegistry(TypeRegistry.FromMessages(GAgentRegistryState.Descriptor)));

    private readonly IProjectionDocumentReader<GAgentRegistryCurrentStateDocument, string> _registryReader;
    private readonly IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string> _workflowReader;
    private readonly IProjectionDocumentReader<WorkflowRunInsightReportDocument, string> _workflowReportReader;
    private readonly IWorkflowExecutionCurrentStateQueryPort _workflowQueryPort;

    public InspectorReadModelQueryService(
        IProjectionDocumentReader<GAgentRegistryCurrentStateDocument, string> registryReader,
        IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string> workflowReader,
        IProjectionDocumentReader<WorkflowRunInsightReportDocument, string> workflowReportReader,
        IWorkflowExecutionCurrentStateQueryPort workflowQueryPort)
    {
        _registryReader = registryReader ?? throw new ArgumentNullException(nameof(registryReader));
        _workflowReader = workflowReader ?? throw new ArgumentNullException(nameof(workflowReader));
        _workflowReportReader = workflowReportReader ?? throw new ArgumentNullException(nameof(workflowReportReader));
        _workflowQueryPort = workflowQueryPort ?? throw new ArgumentNullException(nameof(workflowQueryPort));
    }

    public async Task<IReadOnlyList<InspectorWorkflowRunDto>> ListWorkflowRunsAsync(CancellationToken ct = default)
    {
        var snapshots = await _workflowQueryPort.ListActorSnapshotsAsync(DefaultTake, ct);
        return snapshots
            .Select(MapWorkflowRun)
            .OrderByDescending(run => run.LastUpdatedAt)
            .ToList();
    }

    public async Task<IReadOnlyList<InspectorReadModelSummaryDto>> ListReadModelsAsync(CancellationToken ct = default)
    {
        var registry = await QuerySummaryAsync(
            "gagent-registry",
            typeof(GAgentRegistryCurrentStateDocument),
            _registryReader,
            ct);
        var workflowCurrent = await QuerySummaryAsync(
            "workflow-execution-current-state",
            typeof(WorkflowExecutionCurrentStateDocument),
            _workflowReader,
            ct);
        var workflowReport = await QuerySummaryAsync(
            "workflow-run-insight-report",
            typeof(WorkflowRunInsightReportDocument),
            _workflowReportReader,
            ct);
        return [registry, workflowCurrent, workflowReport];
    }

    public async Task<InspectorReadModelPageDto?> GetReadModelAsync(string name, CancellationToken ct = default)
    {
        return NormalizeName(name) switch
        {
            "gagent-registry" => await QueryPageAsync(
                "gagent-registry",
                typeof(GAgentRegistryCurrentStateDocument),
                _registryReader,
                ct),
            "workflow-execution-current-state" => await QueryPageAsync(
                "workflow-execution-current-state",
                typeof(WorkflowExecutionCurrentStateDocument),
                _workflowReader,
                ct),
            "workflow-run-insight-report" => await QueryPageAsync(
                "workflow-run-insight-report",
                typeof(WorkflowRunInsightReportDocument),
                _workflowReportReader,
                ct),
            _ => null,
        };
    }

    private static InspectorWorkflowRunDto MapWorkflowRun(WorkflowActorSnapshot snapshot)
    {
        return new InspectorWorkflowRunDto(
            snapshot.ActorId,
            string.IsNullOrWhiteSpace(snapshot.WorkflowName) ? "unknown" : snapshot.WorkflowName,
            snapshot.CompletionStatus.ToString(),
            snapshot.StateVersion,
            snapshot.LastEventId,
            snapshot.LastUpdatedAt,
            snapshot.TotalSteps,
            snapshot.CompletedSteps);
    }

    private static async Task<InspectorReadModelSummaryDto> QuerySummaryAsync<TDocument>(
        string name,
        Type documentType,
        IProjectionDocumentReader<TDocument, string> reader,
        CancellationToken ct)
        where TDocument : class, IProjectionReadModel, IMessage<TDocument>
    {
        var result = await reader.QueryAsync(new ProjectionDocumentQuery
        {
            Take = 1,
            IncludeTotalCount = true,
        }, ct);
        var latest = result.Items.FirstOrDefault();
        return new InspectorReadModelSummaryDto(
            name,
            documentType.FullName ?? documentType.Name,
            result.TotalCount,
            latest?.StateVersion,
            latest?.UpdatedAt);
    }

    private static async Task<InspectorReadModelPageDto> QueryPageAsync<TDocument>(
        string name,
        Type documentType,
        IProjectionDocumentReader<TDocument, string> reader,
        CancellationToken ct)
        where TDocument : class, IProjectionReadModel, IMessage<TDocument>
    {
        var result = await reader.QueryAsync(new ProjectionDocumentQuery
        {
            Take = DefaultTake,
            IncludeTotalCount = true,
        }, ct);
        return new InspectorReadModelPageDto(
            name,
            documentType.FullName ?? documentType.Name,
            result.Items.Count,
            result.NextCursor,
            result.Items.Select(ToJsonElement).ToList());
    }

    private static JsonElement ToJsonElement(IMessage message)
    {
        var json = Formatter.Format(message);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string NormalizeName(string name) =>
        name.Trim().ToLowerInvariant();
}
