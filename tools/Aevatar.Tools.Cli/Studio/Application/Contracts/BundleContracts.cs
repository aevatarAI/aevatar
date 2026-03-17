using Aevatar.Tools.Cli.Studio.Domain.Models;

namespace Aevatar.Tools.Cli.Studio.Application.Contracts;

public sealed record SaveWorkflowBundleRequest(
    string Name,
    string EntryWorkflowName,
    IReadOnlyCollection<WorkflowDocument> Workflows,
    WorkflowLayoutDocument? Layout = null,
    IReadOnlyCollection<string>? Tags = null);

public sealed record CloneWorkflowBundleRequest(string? Name = null);

public sealed record ImportWorkflowBundleRequest(
    string? Name,
    string? EntryWorkflowName,
    IReadOnlyDictionary<string, string> Files,
    IReadOnlyCollection<string>? Tags = null);

public sealed record WorkflowBundleExportResult(
    string BundleId,
    string EntryWorkflowName,
    IReadOnlyDictionary<string, string> Files);
