using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.ToolProviders.Workflow.Ports;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.Infrastructure.Local.Adapters;

public sealed class LocalWorkflowDefinitionCommandAdapter : IWorkflowDefinitionCommandAdapter
{
    private readonly IWorkflowYamlValidator _validator;
    private readonly string _workflowsDirectory;
    private readonly ILogger<LocalWorkflowDefinitionCommandAdapter>? _logger;

    public LocalWorkflowDefinitionCommandAdapter(
        IWorkflowYamlValidator validator,
        string? workflowsDirectory = null,
        ILogger<LocalWorkflowDefinitionCommandAdapter>? logger = null)
    {
        _validator = validator;
        _workflowsDirectory = workflowsDirectory
            ?? Aevatar.Configuration.AevatarPaths.Workflows;
        _logger = logger;
        Directory.CreateDirectory(_workflowsDirectory);
    }

    public Task<IReadOnlyList<WorkflowDefinitionSummary>> ListDefinitionsAsync(CancellationToken ct = default)
    {
        var results = new List<WorkflowDefinitionSummary>();
        if (!Directory.Exists(_workflowsDirectory))
            return Task.FromResult<IReadOnlyList<WorkflowDefinitionSummary>>(results);

        foreach (var file in Directory.EnumerateFiles(_workflowsDirectory, "*.yaml"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var yaml = File.ReadAllText(file);
                var validation = _validator.Validate(yaml);
                var name = validation.NormalizedName ?? Path.GetFileNameWithoutExtension(file);
                results.Add(new WorkflowDefinitionSummary(
                    name,
                    validation.Description,
                    validation.StepCount,
                    validation.RoleCount,
                    ComputeRevisionId(yaml)));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to parse workflow file {File}", file);
            }
        }
        return Task.FromResult<IReadOnlyList<WorkflowDefinitionSummary>>(results);
    }

    public Task<WorkflowDefinitionSnapshot?> GetDefinitionAsync(string workflowName, CancellationToken ct = default)
    {
        var path = ResolvePath(workflowName);
        if (!File.Exists(path))
            return Task.FromResult<WorkflowDefinitionSnapshot?>(null);

        var yaml = File.ReadAllText(path);
        var lastModified = File.GetLastWriteTimeUtc(path);
        return Task.FromResult<WorkflowDefinitionSnapshot?>(
            new WorkflowDefinitionSnapshot(workflowName, yaml, ComputeRevisionId(yaml), lastModified));
    }

    public Task<WorkflowDefinitionCommandResult> CreateAsync(string workflowName, string yaml, CancellationToken ct = default)
    {
        var path = ResolvePath(workflowName);
        if (File.Exists(path))
            return Task.FromResult(new WorkflowDefinitionCommandResult(
                false, workflowName, null, null,
                [new WorkflowYamlDiagnostic("error", $"Workflow '{workflowName}' already exists. Use update to modify it.")]));

        var validation = _validator.Validate(yaml);
        if (!validation.Success)
            return Task.FromResult(new WorkflowDefinitionCommandResult(
                false, workflowName, null, null, validation.Diagnostics));

        Directory.CreateDirectory(_workflowsDirectory);
        File.WriteAllText(path, yaml);
        var revisionId = ComputeRevisionId(yaml);
        _logger?.LogInformation("Created workflow definition {Name} (revision {Rev})", workflowName, revisionId);

        return Task.FromResult(new WorkflowDefinitionCommandResult(
            true, workflowName, revisionId, yaml, []));
    }

    public Task<WorkflowDefinitionCommandResult> UpdateAsync(
        string workflowName, string yaml, string expectedRevisionId, CancellationToken ct = default)
    {
        var path = ResolvePath(workflowName);
        if (!File.Exists(path))
            return Task.FromResult(new WorkflowDefinitionCommandResult(
                false, workflowName, null, null,
                [new WorkflowYamlDiagnostic("error", $"Workflow '{workflowName}' not found.")]));

        var currentYaml = File.ReadAllText(path);
        var currentRevision = ComputeRevisionId(currentYaml);
        if (!string.Equals(currentRevision, expectedRevisionId, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new WorkflowDefinitionCommandResult(
                false, workflowName, currentRevision, currentYaml,
                [new WorkflowYamlDiagnostic("error",
                    $"Revision conflict: expected '{expectedRevisionId}' but current is '{currentRevision}'. Re-read and retry.")]));

        var validation = _validator.Validate(yaml);
        if (!validation.Success)
            return Task.FromResult(new WorkflowDefinitionCommandResult(
                false, workflowName, currentRevision, null, validation.Diagnostics));

        File.WriteAllText(path, yaml);
        var newRevision = ComputeRevisionId(yaml);
        _logger?.LogInformation("Updated workflow definition {Name} ({OldRev} → {NewRev})",
            workflowName, currentRevision, newRevision);

        return Task.FromResult(new WorkflowDefinitionCommandResult(
            true, workflowName, newRevision, yaml, []));
    }

    private string ResolvePath(string workflowName)
    {
        var sanitized = SanitizeName(workflowName);
        return Path.Combine(_workflowsDirectory, sanitized + ".yaml");
    }

    private static string SanitizeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                sb.Append(char.ToLowerInvariant(c));
            else if (c == ' ')
                sb.Append('-');
        }
        var result = sb.ToString().Trim('-', '_');
        return string.IsNullOrEmpty(result) ? "unnamed" : result;
    }

    private static string ComputeRevisionId(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }
}
