using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.Tools.Cli.Studio.Application.Abstractions;
using Aevatar.Tools.Cli.Studio.Application.Contracts;
using Aevatar.Tools.Cli.Studio.Application.Exceptions;
using Aevatar.Tools.Cli.Studio.Domain.Models;
using Aevatar.Tools.Cli.Studio.Domain.Services;

namespace Aevatar.Tools.Cli.Studio.Application.Services;

public sealed class BundleService
{
    private readonly IWorkflowBundleRepository _repository;
    private readonly IWorkflowYamlDocumentService _yamlDocumentService;
    private readonly WorkflowDocumentNormalizer _normalizer;
    private readonly WorkflowValidator _validator;

    public BundleService(
        IWorkflowBundleRepository repository,
        IWorkflowYamlDocumentService yamlDocumentService,
        WorkflowDocumentNormalizer normalizer,
        WorkflowValidator validator)
    {
        _repository = repository;
        _yamlDocumentService = yamlDocumentService;
        _normalizer = normalizer;
        _validator = validator;
    }

    public Task<IReadOnlyList<ProjectIndexEntry>> ListAsync(CancellationToken cancellationToken = default) =>
        _repository.ListAsync(cancellationToken);

    public Task<WorkflowBundle?> GetAsync(string bundleId, CancellationToken cancellationToken = default) =>
        _repository.GetAsync(bundleId, cancellationToken);

    public async Task<WorkflowBundle> CreateAsync(
        SaveWorkflowBundleRequest request,
        CancellationToken cancellationToken = default)
    {
        var bundle = CreateBundleModel(request, bundleId: CreateBundleId(request.Name), createdAtUtc: DateTimeOffset.UtcNow);
        return await SaveInternalAsync(bundle, "Initial bundle import", cancellationToken);
    }

    public async Task<WorkflowBundle?> UpdateAsync(
        string bundleId,
        SaveWorkflowBundleRequest request,
        CancellationToken cancellationToken = default)
    {
        var existing = await _repository.GetAsync(bundleId, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        var bundle = CreateBundleModel(
            request,
            bundleId,
            existing.CreatedAtUtc,
            existing.Versions);

        return await SaveInternalAsync(bundle, "Bundle updated", cancellationToken);
    }

    public Task<bool> DeleteAsync(string bundleId, CancellationToken cancellationToken = default) =>
        _repository.DeleteAsync(bundleId, cancellationToken);

    public async Task<WorkflowBundle?> CloneAsync(
        string bundleId,
        CloneWorkflowBundleRequest request,
        CancellationToken cancellationToken = default)
    {
        var existing = await _repository.GetAsync(bundleId, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        var clonedBundle = existing with
        {
            Id = CreateBundleId(request.Name ?? $"{existing.Name} Copy"),
            Name = string.IsNullOrWhiteSpace(request.Name) ? $"{existing.Name} Copy" : request.Name.Trim(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Versions = [],
        };

        return await SaveInternalAsync(clonedBundle, "Bundle cloned", cancellationToken);
    }

    public async Task<WorkflowBundle> ImportAsync(
        ImportWorkflowBundleRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Files.Count == 0)
        {
            throw new StudioValidationException(
                "At least one workflow file is required for import.",
                [ValidationFinding.Error("/files", "No workflow YAML files were provided.")]);
        }

        var documents = new List<WorkflowDocument>();
        var findings = new List<ValidationFinding>();

        foreach (var (fileName, yaml) in request.Files)
        {
            var parse = _yamlDocumentService.Parse(yaml);
            findings.AddRange(parse.Findings.Select(finding => finding with
            {
                Path = $"/files/{fileName}{finding.Path}",
            }));

            if (parse.Document is not null)
            {
                documents.Add(parse.Document);
            }
        }

        if (findings.Any(finding => finding.Level == ValidationLevel.Error))
        {
            throw new StudioValidationException("Workflow import failed validation.", findings);
        }

        var entryWorkflowName = string.IsNullOrWhiteSpace(request.EntryWorkflowName)
            ? documents[0].Name
            : request.EntryWorkflowName.Trim();

        var bundleName = string.IsNullOrWhiteSpace(request.Name)
            ? entryWorkflowName
            : request.Name.Trim();

        var bundle = new WorkflowBundle
        {
            Id = CreateBundleId(bundleName),
            Name = bundleName,
            EntryWorkflowName = entryWorkflowName,
            Workflows = documents,
            Tags = request.Tags?.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim()).ToList() ?? [],
            Layout = new WorkflowLayoutDocument
            {
                EntryWorkflow = entryWorkflowName,
            },
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        return await SaveInternalAsync(bundle, "Bundle imported from YAML", cancellationToken);
    }

    public async Task<WorkflowBundleExportResult?> ExportAsync(
        string bundleId,
        CancellationToken cancellationToken = default)
    {
        var bundle = await _repository.GetAsync(bundleId, cancellationToken);
        if (bundle is null)
        {
            return null;
        }

        var normalized = _normalizer.NormalizeBundleForStorage(bundle);
        var files = normalized.Workflows.ToDictionary(
            workflow => $"{workflow.Name}.yaml",
            workflow => _yamlDocumentService.Serialize(workflow),
            StringComparer.OrdinalIgnoreCase);

        return new WorkflowBundleExportResult(bundle.Id, bundle.EntryWorkflowName, files);
    }

    public async Task<IReadOnlyList<WorkflowVersion>?> GetVersionsAsync(
        string bundleId,
        CancellationToken cancellationToken = default)
    {
        var bundle = await _repository.GetAsync(bundleId, cancellationToken);
        return bundle?.Versions
            .OrderByDescending(version => version.VersionNumber)
            .ToList();
    }

    public async Task<WorkflowLayoutDocument?> GetLayoutAsync(
        string bundleId,
        CancellationToken cancellationToken = default)
    {
        var bundle = await _repository.GetAsync(bundleId, cancellationToken);
        return bundle?.Layout;
    }

    public async Task<WorkflowLayoutDocument?> SaveLayoutAsync(
        string bundleId,
        WorkflowLayoutDocument layout,
        CancellationToken cancellationToken = default)
    {
        var bundle = await _repository.GetAsync(bundleId, cancellationToken);
        if (bundle is null)
        {
            return null;
        }

        var updated = bundle with
        {
            Layout = layout with
            {
                EntryWorkflow = string.IsNullOrWhiteSpace(layout.EntryWorkflow)
                    ? bundle.EntryWorkflowName
                    : layout.EntryWorkflow,
            },
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        await _repository.UpsertAsync(updated, cancellationToken);
        return updated.Layout;
    }

    private async Task<WorkflowBundle> SaveInternalAsync(
        WorkflowBundle bundle,
        string versionMessage,
        CancellationToken cancellationToken)
    {
        var normalized = _normalizer.NormalizeBundleForStorage(bundle);
        var workflowNames = normalized.GetWorkflowNames();
        if (!workflowNames.Contains(normalized.EntryWorkflowName))
        {
            throw new StudioValidationException(
                "Entry workflow was not found in the bundle.",
                [ValidationFinding.Error("/entryWorkflowName", $"Entry workflow '{normalized.EntryWorkflowName}' does not exist.")]);
        }

        var findings = new List<ValidationFinding>();
        for (var index = 0; index < normalized.Workflows.Count; index++)
        {
            var workflow = normalized.Workflows[index];
            findings.AddRange(_validator.Validate(
                workflow,
                new WorkflowValidationOptions
                {
                    AvailableWorkflowNames = workflowNames,
                }).Select(finding => finding with
                {
                    Path = $"/workflows/{index}{finding.Path}",
                }));
        }

        var errors = findings.Where(finding => finding.Level == ValidationLevel.Error).ToList();
        if (errors.Count > 0)
        {
            throw new StudioValidationException("Bundle validation failed.", findings);
        }

        var nextVersionNumber = normalized.Versions.Count == 0
            ? 1
            : normalized.Versions.Max(version => version.VersionNumber) + 1;

        var version = new WorkflowVersion
        {
            VersionNumber = nextVersionNumber,
            SavedAtUtc = DateTimeOffset.UtcNow,
            Checksum = ComputeChecksum(normalized),
            Message = versionMessage,
        };

        var persisted = normalized with
        {
            Versions = normalized.Versions
                .OrderBy(versionItem => versionItem.VersionNumber)
                .Append(version)
                .ToList(),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        return await _repository.UpsertAsync(persisted, cancellationToken);
    }

    private static WorkflowBundle CreateBundleModel(
        SaveWorkflowBundleRequest request,
        string bundleId,
        DateTimeOffset createdAtUtc,
        IReadOnlyCollection<WorkflowVersion>? versions = null) =>
        new()
        {
            Id = bundleId,
            Name = request.Name,
            EntryWorkflowName = request.EntryWorkflowName,
            Workflows = request.Workflows.ToList(),
            Layout = request.Layout ?? new WorkflowLayoutDocument { EntryWorkflow = request.EntryWorkflowName },
            Tags = request.Tags?.ToList() ?? [],
            Versions = versions?.ToList() ?? [],
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

    private static string CreateBundleId(string name)
    {
        var slug = new string(
            name.Trim().ToLowerInvariant()
                .Select(character => char.IsLetterOrDigit(character) ? character : '-')
                .ToArray())
            .Trim('-');

        slug = string.IsNullOrWhiteSpace(slug) ? "bundle" : slug;
        return $"{slug}-{Guid.NewGuid():N}"[..(slug.Length + 9)];
    }

    private static string ComputeChecksum(WorkflowBundle bundle)
    {
        var payload = JsonSerializer.Serialize(new
        {
            bundle.Name,
            bundle.EntryWorkflowName,
            bundle.Workflows,
            bundle.Layout,
            bundle.Tags,
        });

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
