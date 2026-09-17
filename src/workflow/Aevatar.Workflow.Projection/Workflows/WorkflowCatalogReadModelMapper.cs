using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Workflows;

// Refactor (iter72/cluster-072-workflow-closed-world-false-capability):
//   Old pattern: ClosedWorldBlocked flag retained as always-false compatibility field
//   New principle: Removed dead capability flag; output describes available primitives only
public sealed class WorkflowCatalogReadModelMapper
{
    public WorkflowCatalogItem ToCatalogItem(WorkflowCatalogCurrentStateDocument document) =>
        new()
        {
            Name = document.WorkflowName,
            Description = document.Description,
            Category = document.Category,
            Group = document.Group,
            GroupLabel = document.GroupLabel,
            SortOrder = document.SortOrder,
            Source = document.Source,
            SourceLabel = document.SourceLabel,
            ShowInLibrary = document.ShowInLibrary,
            IsPrimitiveExample = document.IsPrimitiveExample,
            RequiresLlmProvider = document.RequiresLlmProvider,
            Primitives = document.Primitives.ToList(),
            RequiredConnectors = document.RequiredConnectors.ToList(),
            StepCount = document.Steps.Count,
            AuthorityStateVersion = document.StateVersion,
            ProjectionWatermark = document.UpdatedAt,
            LastEventId = document.LastEventId,
        };

    public WorkflowCatalogItemDetail ToCatalogItemDetail(WorkflowCatalogCurrentStateDocument document) =>
        new()
        {
            Catalog = ToCatalogItem(document),
            Yaml = document.WorkflowYaml,
            Definition = new WorkflowCatalogDefinition
            {
                Name = document.WorkflowName,
                Description = document.Description,
                ClosedWorldMode = document.ClosedWorldMode,
                Roles = document.Roles.Select(ToCatalogRole).ToList(),
                Steps = document.Steps.Select(ToCatalogStep).ToList(),
            },
            Edges = document.Edges.Select(edge => new WorkflowCatalogEdge
            {
                From = edge.From,
                To = edge.To,
                Label = edge.Label,
            }).ToList(),
        };

    private static WorkflowCatalogRole ToCatalogRole(WorkflowCatalogRoleReadModel role) =>
        new()
        {
            Id = role.Id,
            Name = role.Name,
            SystemPrompt = role.SystemPrompt,
            Provider = role.Provider,
            Model = role.Model,
            Temperature = role.Temperature,
            MaxTokens = role.MaxTokens,
            MaxToolRounds = role.MaxToolRounds,
            MaxHistoryMessages = role.MaxHistoryMessages,
            EventModules = role.EventModules.ToList(),
            EventRoutes = role.EventRoutes,
            Connectors = role.Connectors.ToList(),
        };

    private static WorkflowCatalogStep ToCatalogStep(WorkflowCatalogStepReadModel step) =>
        new()
        {
            Id = step.Id,
            Type = step.Type,
            TargetRole = step.TargetRole,
            Parameters = step.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            Next = step.Next,
            Branches = step.Branches.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            Children = step.Children.Select(child => new WorkflowCatalogChildStep
            {
                Id = child.Id,
                Type = child.Type,
                TargetRole = child.TargetRole,
            }).ToList(),
        };

    public WorkflowCapabilityWorkflow ToCapabilityWorkflow(WorkflowCatalogCurrentStateDocument workflow) =>
        new()
        {
            Name = workflow.WorkflowName,
            Description = workflow.Description,
            Source = workflow.Source,
            ClosedWorldMode = workflow.ClosedWorldMode,
            RequiresLlmProvider = workflow.RequiresLlmProvider,
            Primitives = workflow.Primitives.ToList(),
            RequiredConnectors = workflow.RequiredConnectors.ToList(),
            WorkflowCalls = workflow.WorkflowCalls.ToList(),
            Steps = workflow.Steps.Select(step => new WorkflowCapabilityWorkflowStep
            {
                Id = step.Id,
                Type = step.Type,
                Next = step.Next,
            }).ToList(),
            AuthorityStateVersion = workflow.StateVersion,
            ProjectionWatermark = workflow.UpdatedAt,
        };

}
