namespace Aevatar.Workflow.Application.Abstractions.Workflows;

/// <summary>
/// Holds the result of registering a single workflow definition.
/// </summary>
public sealed record WorkflowDefinitionRegistration(
    string WorkflowName,
    string WorkflowYaml,
    string DefinitionActorId);

/// <summary>
/// Read-only catalog of workflow YAML definitions loaded at application startup.
///
/// <para>
/// This is a <b>startup-loaded configuration cache</b>, not a runtime-mutable fact source.
/// Built-in definitions and file-backed definitions are registered during application
/// bootstrap (DI factory + <c>IHostedService</c>). After startup completes, the catalog
/// is effectively immutable.
/// </para>
///
/// <para>
/// <b>Multi-node deployment requirement:</b> Every node must be configured with the same
/// set of workflow definition files and built-in registration options so that the catalog
/// contents are identical across the cluster. The catalog is not replicated or synchronized
/// at runtime — it relies on deterministic startup-time population from the same sources.
/// </para>
/// </summary>
public interface IWorkflowDefinitionCatalog
{
    /// <summary>
    /// Registers a workflow definition during startup bootstrap.
    /// Must not be called after application startup completes.
    /// </summary>
    void Register(string name, string yaml);

    /// <summary>
    /// Returns the full registration for the given workflow name, or <c>null</c> if not found.
    /// </summary>
    WorkflowDefinitionRegistration? GetDefinition(string name);

    /// <summary>
    /// Returns the YAML content for the given workflow name, or <c>null</c> if not found.
    /// </summary>
    string? GetYaml(string name);

    /// <summary>
    /// Returns all registered workflow names in alphabetical order.
    /// </summary>
    IReadOnlyList<string> GetNames();
}
