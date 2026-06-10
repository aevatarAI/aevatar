using System.Security.Cryptography;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Infrastructure.Runs;

public sealed class FileSystemWorkflowFileIngressPort : IWorkflowFileIngressPort
{
    private readonly IOptions<FileSystemWorkflowFileIngressOptions> _options;

    public FileSystemWorkflowFileIngressPort(IOptions<FileSystemWorkflowFileIngressOptions> options)
    {
        _options = options;
    }

    public async ValueTask<WorkflowFileIngressResult> IngestAsync(
        WorkflowFileIngressRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Content.IsEmpty)
            throw new ArgumentException("Workflow file content is required.", nameof(request));

        var now = DateTimeOffset.UtcNow;
        var fileId = $"wf-file-{Guid.NewGuid():N}";
        var artifactId = $"workflow-file://{fileId}";
        var rootDirectory = NormalizeRootDirectory(_options.Value.RootDirectory);
        var artifactDirectory = Path.Combine(rootDirectory, fileId);
        Directory.CreateDirectory(artifactDirectory);

        var content = request.Content.ToArray();
        var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        await File.WriteAllBytesAsync(
            Path.Combine(artifactDirectory, "content.bin"),
            content,
            cancellationToken);

        var descriptor = new WorkflowFileRef
        {
            FileId = fileId,
            ArtifactId = artifactId,
            SourceKind = request.SourceKind,
            SourceMessageId = Normalize(request.SourceMessageId),
            SourceResourceKey = Normalize(request.SourceResourceKey),
            FileName = Normalize(request.FileName),
            MediaType = Normalize(request.MediaType),
            SizeBytes = content.LongLength,
            Sha256 = sha256,
            CreatedAtUnixMs = now.ToUnixTimeMilliseconds(),
            ExpiresAtUnixMs = ResolveExpiresAtUnixMs(now, request.ExpiresAtUnixMs, _options.Value.TimeToLive),
        };

        return new WorkflowFileIngressResult(descriptor);
    }

    private static string NormalizeRootDirectory(string? rootDirectory) =>
        string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "workflow-file-artifacts")
            : Path.GetFullPath(rootDirectory.Trim());

    private static long ResolveExpiresAtUnixMs(
        DateTimeOffset now,
        long? requestedExpiresAtUnixMs,
        TimeSpan configuredTimeToLive)
    {
        if (requestedExpiresAtUnixMs is > 0)
            return requestedExpiresAtUnixMs.Value;

        var ttl = configuredTimeToLive <= TimeSpan.Zero
            ? TimeSpan.FromDays(1)
            : configuredTimeToLive;
        return now.Add(ttl).ToUnixTimeMilliseconds();
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
