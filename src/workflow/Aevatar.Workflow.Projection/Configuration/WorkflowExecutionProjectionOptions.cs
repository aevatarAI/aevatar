namespace Aevatar.Workflow.Projection.Configuration;

/// <summary>
/// Feature flags for chat projection pipeline.
/// </summary>
public sealed class WorkflowExecutionProjectionOptions
    : IProjectionRuntimeOptions
{
    public WorkflowExecutionProjectionOptions()
    {
        ReadModelBindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Enables projection pipeline registration.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Exposes read-side actor query endpoints.
    /// </summary>
    public bool EnableActorQueryEndpoints { get; set; } = true;

    public bool EnableRunQueryEndpoints
    {
        get => EnableActorQueryEndpoints;
        set => EnableActorQueryEndpoints = value;
    }

    /// <summary>
    /// Writes run report artifacts (json/html).
    /// </summary>
    public bool EnableRunReportArtifacts { get; set; } = true;

    /// <summary>
    /// Max wait time for one run projection completion signal.
    /// </summary>
    public int RunProjectionCompletionWaitTimeoutMs { get; set; } = 3000;

    /// <summary>
    /// Extra grace wait before force-finalize when completion status is timeout.
    /// </summary>
    public int RunProjectionFinalizeGraceTimeoutMs { get; set; } = 1500;

    /// <summary>
    /// Read-model store provider name, e.g. InMemory/Elasticsearch.
    /// </summary>
    public string ReadModelProvider { get; set; } = ProjectionReadModelProviderNames.InMemory;

    /// <summary>
    /// Whether unsupported provider capabilities should fail fast during startup registration.
    /// </summary>
    public bool FailOnUnsupportedCapabilities { get; set; } = true;

    /// <summary>
    /// Whether to pre-validate read-model provider selection and capabilities during host startup.
    /// </summary>
    public bool ValidateReadModelProviderOnStartup { get; set; } = true;

    /// <summary>
    /// Optional read-model binding requirements (ReadModelName -> IndexKind).
    /// </summary>
    public Dictionary<string, string> ReadModelBindings { get; }

    /// <summary>
    /// Read-model runtime mode.
    /// Workflow keeps CustomReadModel as default; StateOnly is rejected during DI composition.
    /// </summary>
    public ProjectionReadModelMode ReadModelMode { get; set; } = ProjectionReadModelMode.CustomReadModel;
}
