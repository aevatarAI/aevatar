using Aevatar.Studio.Application.Delivery;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Infrastructure.Delivery;

public sealed class FileWorkflowDeliveryPackageSource : IWorkflowDeliveryPackageSource
{
    private readonly IOptions<WorkflowDeliveryOptions> _options;

    public FileWorkflowDeliveryPackageSource(IOptions<WorkflowDeliveryOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<string?> ReadSourceYamlAsync(
        string workflowName,
        CancellationToken ct = default)
    {
        var normalizedName = workflowName?.Trim() ?? string.Empty;
        if (normalizedName.Length == 0 ||
            !string.Equals(Path.GetFileName(normalizedName), normalizedName, StringComparison.Ordinal) ||
            normalizedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        var configuredDirectory = _options.Value.PackageDirectory?.Trim();
        var packageDirectory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "workflow-delivery-packages")
            : Path.IsPathRooted(configuredDirectory)
                ? Path.GetFullPath(configuredDirectory)
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredDirectory));
        var packagePath = Path.Combine(packageDirectory, $"{normalizedName}.yaml");
        if (!File.Exists(packagePath))
            return null;

        return await File.ReadAllTextAsync(packagePath, ct);
    }
}
