namespace Aevatar.Tools.Cli.Studio.Domain.Models;

public sealed record WorkflowDocument
{
    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public WorkflowConfiguration Configuration { get; init; } = new();

    public List<RoleModel> Roles { get; init; } = [];

    public List<StepModel> Steps { get; init; } = [];
}
