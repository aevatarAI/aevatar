namespace Aevatar.Tools.Cli.Studio.Domain.Models;

public sealed record WorkflowLayoutDocument
{
    public Dictionary<string, WorkflowNodeLayout> NodePositions { get; init; } = new(StringComparer.Ordinal);

    public WorkflowViewport Viewport { get; init; } = new();

    public Dictionary<string, List<string>> Groups { get; init; } = new(StringComparer.Ordinal);

    public List<string> Collapsed { get; init; } = [];

    public string? EntryWorkflow { get; init; }
}

public sealed record WorkflowNodeLayout(double X, double Y);

public sealed record WorkflowViewport(double X = 0, double Y = 0, double Zoom = 1);
