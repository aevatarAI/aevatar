using Aevatar.ContentArtifacts.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Studio.Hosting.ContentArtifacts;

internal sealed class WorkflowFileContentArtifactBackingContentPort(
    IFileArtifactReadPort? workflowFileArtifactReadPort = null)
    : IContentArtifactBackingContentPort
{
    public const string Provider = "workflow-file";

    public async Task<ContentArtifactBackingContentDescriptor> DescribeAsync(
        ContentArtifactBackingContentRequest request,
        CancellationToken ct = default)
    {
        var fileRef = ToFileArtifactRef(request);
        var descriptor = await GetReadPort().DescribeAsync(fileRef, ct);
        ValidateDescriptor(request, descriptor);
        return new ContentArtifactBackingContentDescriptor(
            descriptor.SizeBytes,
            descriptor.Sha256!);
    }

    public async Task<Stream> OpenReadAsync(
        ContentArtifactBackingContentRequest request,
        CancellationToken ct = default)
    {
        var fileRef = ToFileArtifactRef(request);
        var content = await GetReadPort().OpenReadAsync(fileRef, ct);
        ValidateDescriptor(request, content.FileRef);
        return content.Content;
    }

    private IFileArtifactReadPort GetReadPort() =>
        workflowFileArtifactReadPort
        ?? throw new InvalidOperationException("Workflow file backing content provider is unavailable.");

    private static FileArtifactRef ToFileArtifactRef(ContentArtifactBackingContentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Reference);
        if (!string.Equals(request.Reference.Provider?.Trim(), Provider, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsupported ContentArtifact backing provider '{request.Reference.Provider}'.");
        }

        var objectKey = NormalizeRequired(request.Reference.ObjectKey, "backing object key");
        var scopeId = NormalizeRequired(request.ScopeId, "backing content scope id");
        return new FileArtifactRef
        {
            ArtifactId = objectKey,
            OwnerScopeId = scopeId,
            OwnerRunId = NormalizeOptional(request.RunId),
        };
    }

    private static void ValidateDescriptor(
        ContentArtifactBackingContentRequest request,
        FileArtifactRef descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var requestedScopeId = NormalizeRequired(request.ScopeId, "backing content scope id");
        if (!string.Equals(descriptor.OwnerScopeId, requestedScopeId, StringComparison.Ordinal))
            throw new InvalidOperationException("Workflow file backing content does not belong to the ContentArtifact Scope.");

        if (!string.Equals(
                NormalizeOptional(descriptor.OwnerRunId),
                NormalizeOptional(request.RunId),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Workflow file backing content does not belong to the ContentArtifact Run.");
        }
        if (descriptor.SizeBytes < 0)
            throw new InvalidOperationException("Workflow file backing content has an invalid byte length.");
        ValidateSha256(descriptor.Sha256);
    }

    private static void ValidateSha256(string? value)
    {
        if (value?.Length != 64)
            throw new InvalidOperationException("Workflow file backing content must expose a SHA-256 hash.");
        try
        {
            _ = Convert.FromHexString(value);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "Workflow file backing content must expose a SHA-256 hash.",
                ex);
        }
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException($"ContentArtifact {fieldName} is required.");
        return normalized;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
