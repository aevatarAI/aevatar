namespace Aevatar.Workflow.Application.Workflows;

public sealed class BuiltInWorkflowDefinitionSeedSource : IWorkflowDefinitionSeedSource
{
    private readonly InMemoryWorkflowDefinitionCatalogOptions _options;

    public BuiltInWorkflowDefinitionSeedSource(InMemoryWorkflowDefinitionCatalogOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public IReadOnlyDictionary<string, string> GetSeedDefinitions()
    {
        var definitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_options.RegisterBuiltInDirectWorkflow)
            definitions["direct"] = WorkflowBuiltInDefinitions.DirectYaml;
        if (_options.RegisterBuiltInAutoWorkflow)
            definitions["auto"] = WorkflowBuiltInDefinitions.AutoYaml;
        if (_options.RegisterBuiltInAutoReviewWorkflow)
            definitions["auto_review"] = WorkflowBuiltInDefinitions.AutoReviewYaml;
        return definitions;
    }
}
