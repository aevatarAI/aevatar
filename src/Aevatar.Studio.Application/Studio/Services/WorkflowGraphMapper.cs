using Aevatar.Studio.Domain.Studio.Compatibility;
using Aevatar.Studio.Domain.Studio.Graph;
using Aevatar.Studio.Domain.Studio.Models;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class WorkflowGraphMapper
{
    private readonly WorkflowCompatibilityProfile _profile;

    public WorkflowGraphMapper(WorkflowCompatibilityProfile? profile = null)
    {
        _profile = profile ?? WorkflowCompatibilityProfile.AevatarV1;
    }

    public WorkflowGraphDocument Map(WorkflowDocument document)
    {
        var nodes = new List<WorkflowGraphNode>();
        var edges = new List<WorkflowGraphEdge>();

        MapSteps(document.Steps, null, nodes, edges);

        return new WorkflowGraphDocument
        {
            WorkflowName = document.Name,
            Nodes = nodes,
            Edges = edges,
        };
    }

    private void MapSteps(
        IReadOnlyList<StepModel> steps,
        string? parentStepId,
        ICollection<WorkflowGraphNode> nodes,
        ICollection<WorkflowGraphEdge> edges)
    {
        foreach (var step in steps)
        {
            nodes.Add(new WorkflowGraphNode(
                step.Id,
                _profile.ToCanonicalType(step.Type),
                step.TargetRole,
                step.Children.Count > 0,
                _profile.IsAdvancedImportOnly(step.Type)));

            if (!string.IsNullOrWhiteSpace(parentStepId))
            {
                edges.Add(new WorkflowGraphEdge(
                    $"child:{parentStepId}:{step.Id}",
                    parentStepId,
                    step.Id,
                    "child"));
            }

            if (!string.IsNullOrWhiteSpace(step.Next))
            {
                edges.Add(new WorkflowGraphEdge(
                    $"next:{step.Id}:{step.Next}",
                    step.Id,
                    step.Next,
                    "next"));
            }

            foreach (var branch in step.Branches)
            {
                edges.Add(new WorkflowGraphEdge(
                    $"branch:{step.Id}:{branch.Key}:{branch.Value}",
                    step.Id,
                    branch.Value,
                    "branch",
                    branch.Key));
            }

            if (step.Children.Count > 0)
            {
                MapSteps(step.Children, step.Id, nodes, edges);
            }
        }
    }
}
