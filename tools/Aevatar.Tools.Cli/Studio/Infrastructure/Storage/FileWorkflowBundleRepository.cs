using System.Text.Json;
using Aevatar.Tools.Cli.Studio.Application.Abstractions;
using Aevatar.Tools.Cli.Studio.Domain.Models;
using Microsoft.Extensions.Options;

namespace Aevatar.Tools.Cli.Studio.Infrastructure.Storage;

public sealed class FileWorkflowBundleRepository : IWorkflowBundleRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly StudioStorageOptions _options;

    public FileWorkflowBundleRepository(IOptions<StudioStorageOptions> options)
    {
        _options = options.Value.ResolveRootDirectory();
        Directory.CreateDirectory(_options.RootDirectory);
    }

    public async Task<IReadOnlyList<ProjectIndexEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<ProjectIndexEntry>();
        foreach (var bundleFile in Directory.EnumerateFiles(_options.RootDirectory, "bundle.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bundle = await ReadBundleAsync(bundleFile, cancellationToken);
            if (bundle is null)
            {
                continue;
            }

            results.Add(new ProjectIndexEntry
            {
                BundleId = bundle.Id,
                Name = bundle.Name,
                EntryWorkflowName = bundle.EntryWorkflowName,
                Tags = bundle.Tags,
                WorkflowCount = bundle.Workflows.Count,
                UpdatedAtUtc = bundle.UpdatedAtUtc,
                LatestVersion = bundle.Versions.Count == 0 ? 0 : bundle.Versions.Max(version => version.VersionNumber),
            });
        }

        return results
            .OrderByDescending(item => item.UpdatedAtUtc)
            .ToList();
    }

    public async Task<WorkflowBundle?> GetAsync(string bundleId, CancellationToken cancellationToken = default)
    {
        var filePath = GetBundleFilePath(bundleId);
        if (!File.Exists(filePath))
        {
            return null;
        }

        return await ReadBundleAsync(filePath, cancellationToken);
    }

    public async Task<WorkflowBundle> UpsertAsync(WorkflowBundle bundle, CancellationToken cancellationToken = default)
    {
        var directory = GetBundleDirectory(bundle.Id);
        Directory.CreateDirectory(directory);

        var filePath = GetBundleFilePath(bundle.Id);
        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, bundle, JsonOptions, cancellationToken);
        return bundle;
    }

    public Task<bool> DeleteAsync(string bundleId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = GetBundleDirectory(bundleId);
        if (!Directory.Exists(directory))
        {
            return Task.FromResult(false);
        }

        Directory.Delete(directory, recursive: true);
        return Task.FromResult(true);
    }

    private async Task<WorkflowBundle?> ReadBundleAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        return await JsonSerializer.DeserializeAsync<WorkflowBundle>(stream, JsonOptions, cancellationToken);
    }

    private string GetBundleDirectory(string bundleId) => Path.Combine(_options.RootDirectory, bundleId);

    private string GetBundleFilePath(string bundleId) => Path.Combine(GetBundleDirectory(bundleId), "bundle.json");
}

internal static class StudioStorageOptionsExtensions
{
    public static StudioStorageOptions ResolveRootDirectory(this StudioStorageOptions options)
    {
        var rootDirectory = Path.IsPathRooted(options.RootDirectory)
            ? options.RootDirectory
            : Path.GetFullPath(options.RootDirectory, AppContext.BaseDirectory);

        return new StudioStorageOptions
        {
            RootDirectory = rootDirectory,
            DefaultRuntimeBaseUrl = options.DefaultRuntimeBaseUrl,
        };
    }
}
