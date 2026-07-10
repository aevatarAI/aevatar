using System.Security.Cryptography;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using ProtoWorkflowFileRef = Aevatar.Workflow.Abstractions.WorkflowFileRef;
using ProtoWorkflowFileSourceKind = Aevatar.Workflow.Abstractions.WorkflowFileSourceKind;

namespace Aevatar.Workflow.Infrastructure.Runs;

public sealed class FileSystemFileArtifactPort :
    IFileArtifactIngressPort,
    IFileArtifactReadPort,
    IFileArtifactOwnershipPort,
    IFileArtifactCleanupPort
{
    private const string ArtifactIdPrefix = "workflow-file://";
    private const string ContentFileName = "content.bin";
    private const string DescriptorFileName = "descriptor.pb";

    private readonly IOptions<FileSystemFileArtifactOptions> _options;

    public FileSystemFileArtifactPort(IOptions<FileSystemFileArtifactOptions> options)
    {
        _options = options;
    }

    public async ValueTask<FileArtifactIngressResult> IngestAsync(
        FileArtifactIngressRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Content.IsEmpty)
            throw new ArgumentException("Workflow file content is required.", nameof(request));

        var now = DateTimeOffset.UtcNow;
        var fileId = $"wf-file-{Guid.NewGuid():N}";
        var artifactId = $"{ArtifactIdPrefix}{fileId}";
        var rootDirectory = NormalizeRootDirectory(_options.Value.RootDirectory);
        var artifactDirectory = Path.Combine(rootDirectory, fileId);
        Directory.CreateDirectory(artifactDirectory);

        var content = request.Content.ToArray();
        var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        await File.WriteAllBytesAsync(
            Path.Combine(artifactDirectory, ContentFileName),
            content,
            cancellationToken);

        var descriptor = new FileArtifactRef
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
            OwnerRunId = Normalize(request.OwnerRunId),
            OwnerScopeId = Normalize(request.OwnerScopeId),
        };
        await File.WriteAllBytesAsync(
            Path.Combine(artifactDirectory, DescriptorFileName),
            ToProto(descriptor).ToByteArray(),
            cancellationToken);

        return new FileArtifactIngressResult(descriptor);
    }

    public async ValueTask<FileArtifactRef> DescribeAsync(
        FileArtifactRef fileRef,
        CancellationToken cancellationToken = default)
    {
        var descriptor = await ReadDescriptorAsync(fileRef, cancellationToken);
        ValidateRequestedDescriptor(fileRef, descriptor);
        ValidateNotExpired(descriptor);
        return descriptor;
    }

    public async ValueTask BindOwnerAsync(
        FileArtifactRef fileRef,
        string ownerRunId,
        string? ownerScopeId,
        CancellationToken cancellationToken = default)
    {
        var normalizedRunId = Normalize(ownerRunId)
                              ?? throw new ArgumentException("Workflow file owner run id is required.", nameof(ownerRunId));
        var normalizedScopeId = Normalize(ownerScopeId);
        var descriptor = await ReadDescriptorAsync(fileRef, cancellationToken);
        ValidateRequestedDescriptorWithoutOwner(fileRef, descriptor);
        ValidateNotExpired(descriptor);
        ValidateOwnerCanBind(descriptor.OwnerRunId, normalizedRunId, "run id");
        if (normalizedScopeId != null)
            ValidateOwnerCanBind(descriptor.OwnerScopeId, normalizedScopeId, "scope id");

        var effectiveScopeId = normalizedScopeId ?? descriptor.OwnerScopeId;
        if (string.Equals(descriptor.OwnerRunId, normalizedRunId, StringComparison.Ordinal) &&
            string.Equals(descriptor.OwnerScopeId, effectiveScopeId, StringComparison.Ordinal))
            return;

        var bound = descriptor with
        {
            OwnerRunId = normalizedRunId,
            OwnerScopeId = effectiveScopeId,
        };
        await File.WriteAllBytesAsync(
            ResolveDescriptorPath(fileRef),
            ToProto(bound).ToByteArray(),
            cancellationToken);
    }

    public async ValueTask<FileArtifactContent> OpenReadAsync(
        FileArtifactRef fileRef,
        CancellationToken cancellationToken = default)
    {
        var descriptor = await DescribeAsync(fileRef, cancellationToken);
        var artifactDirectory = ResolveArtifactDirectory(descriptor);
        var contentPath = Path.Combine(artifactDirectory, ContentFileName);
        if (!File.Exists(contentPath))
            throw new FileNotFoundException("Workflow file content was not found.", contentPath);

        var length = new FileInfo(contentPath).Length;
        if (descriptor.SizeBytes > 0 && length != descriptor.SizeBytes)
            throw new InvalidOperationException("Workflow file content length does not match its descriptor.");
        await ValidateContentHashAsync(contentPath, descriptor, cancellationToken);

        return new FileArtifactContent(descriptor, File.OpenRead(contentPath));
    }

    public async ValueTask<FileArtifactCleanupResult> CleanupAsync(
        FileArtifactCleanupRequest request,
        CancellationToken cancellationToken = default)
    {
        var observedAtUnixMs = request.ObservedAtUnixMs > 0
            ? request.ObservedAtUnixMs
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var observedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(observedAtUnixMs);
        var rootDirectory = NormalizeRootDirectory(_options.Value.RootDirectory);
        if (!Directory.Exists(rootDirectory))
            return new FileArtifactCleanupResult(0, 0, 0);

        long scanned = 0;
        long deletedExpired = 0;
        long deletedIncomplete = 0;
        foreach (var artifactDirectory in Directory.EnumerateDirectories(rootDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned++;
            var fileId = Path.GetFileName(artifactDirectory);
            if (!IsSafeFileId(fileId))
                continue;

            var descriptorPath = Path.Combine(artifactDirectory, DescriptorFileName);
            if (!File.Exists(descriptorPath))
            {
                if (IsIncompleteArtifactStale(artifactDirectory, observedAtUtc, _options.Value.IncompleteArtifactAge))
                    deletedIncomplete += DeleteArtifactDirectory(artifactDirectory);
                continue;
            }

            var descriptorBytes = await File.ReadAllBytesAsync(descriptorPath, cancellationToken);
            var descriptor = ToApplication(ProtoWorkflowFileRef.Parser.ParseFrom(descriptorBytes));
            if (descriptor.ExpiresAtUnixMs > 0 && descriptor.ExpiresAtUnixMs <= observedAtUnixMs)
                deletedExpired += DeleteArtifactDirectory(artifactDirectory);
        }

        return new FileArtifactCleanupResult(scanned, deletedExpired, deletedIncomplete);
    }

    private string ResolveArtifactDirectory(FileArtifactRef fileRef)
    {
        ArgumentNullException.ThrowIfNull(fileRef);
        var fileId = ResolveFileId(fileRef);
        var rootDirectory = NormalizeRootDirectory(_options.Value.RootDirectory);
        var artifactDirectory = Path.GetFullPath(Path.Combine(rootDirectory, fileId));
        if (!IsPathInsideRoot(rootDirectory, artifactDirectory))
            throw new InvalidOperationException("Workflow file artifact path escaped the configured root directory.");
        return artifactDirectory;
    }

    private static string ResolveFileId(FileArtifactRef fileRef)
    {
        var fileId = Normalize(fileRef.FileId);
        var artifactId = Normalize(fileRef.ArtifactId);
        if (artifactId != null)
        {
            if (!artifactId.StartsWith(ArtifactIdPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException("Workflow file artifact id is not managed by the filesystem artifact store.");

            var artifactFileId = artifactId[ArtifactIdPrefix.Length..];
            if (fileId != null && !string.Equals(fileId, artifactFileId, StringComparison.Ordinal))
                throw new InvalidOperationException("Workflow file id does not match its artifact id.");
            fileId ??= artifactFileId;
        }

        if (fileId == null)
            throw new InvalidOperationException("Workflow file id is required.");
        if (!IsSafeFileId(fileId))
            throw new InvalidOperationException("Workflow file id is invalid.");
        return fileId;
    }

    private static bool IsSafeFileId(string fileId)
    {
        if (!fileId.StartsWith("wf-file-", StringComparison.Ordinal))
            return false;
        foreach (var c in fileId)
        {
            if ((c is >= 'a' and <= 'z') ||
                (c is >= 'A' and <= 'Z') ||
                (c is >= '0' and <= '9') ||
                c == '-')
                continue;
            return false;
        }

        return true;
    }

    private static bool IsPathInsideRoot(string rootDirectory, string path)
    {
        var normalizedRoot = Path.GetFullPath(rootDirectory);
        var rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return string.Equals(path, normalizedRoot, StringComparison.Ordinal) ||
            path.StartsWith(rootWithSeparator, StringComparison.Ordinal);
    }

    private static string NormalizeRootDirectory(string? rootDirectory) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "workflow-file-artifacts")
            : rootDirectory.Trim());

    private static bool IsIncompleteArtifactStale(
        string artifactDirectory,
        DateTimeOffset observedAtUtc,
        TimeSpan configuredIncompleteArtifactAge)
    {
        var incompleteArtifactAge = configuredIncompleteArtifactAge <= TimeSpan.Zero
            ? TimeSpan.FromHours(1)
            : configuredIncompleteArtifactAge;
        var lastWriteTimeUtc = Directory.GetLastWriteTimeUtc(artifactDirectory);
        return observedAtUtc.UtcDateTime - lastWriteTimeUtc >= incompleteArtifactAge;
    }

    private static int DeleteArtifactDirectory(string artifactDirectory)
    {
        Directory.Delete(artifactDirectory, recursive: true);
        return 1;
    }

    private async ValueTask<FileArtifactRef> ReadDescriptorAsync(
        FileArtifactRef fileRef,
        CancellationToken cancellationToken)
    {
        var descriptorPath = ResolveDescriptorPath(fileRef);
        if (!File.Exists(descriptorPath))
            throw new FileNotFoundException("Workflow file descriptor was not found.", descriptorPath);

        var descriptorBytes = await File.ReadAllBytesAsync(descriptorPath, cancellationToken);
        return ToApplication(ProtoWorkflowFileRef.Parser.ParseFrom(descriptorBytes));
    }

    private string ResolveDescriptorPath(FileArtifactRef fileRef) =>
        Path.Combine(ResolveArtifactDirectory(fileRef), DescriptorFileName);

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

    private static void ValidateRequestedDescriptor(FileArtifactRef requested, FileArtifactRef descriptor)
    {
        ValidateRequestedDescriptorWithoutOwner(requested, descriptor);
        if (!string.IsNullOrWhiteSpace(requested.OwnerRunId) &&
            !string.Equals(requested.OwnerRunId.Trim(), descriptor.OwnerRunId, StringComparison.Ordinal))
            throw new InvalidOperationException("Workflow file descriptor owner run id does not match the requested owner.");
        if (!string.IsNullOrWhiteSpace(requested.OwnerScopeId) &&
            !string.Equals(requested.OwnerScopeId.Trim(), descriptor.OwnerScopeId, StringComparison.Ordinal))
            throw new InvalidOperationException("Workflow file descriptor owner scope id does not match the requested owner.");
    }

    private static void ValidateRequestedDescriptorWithoutOwner(FileArtifactRef requested, FileArtifactRef descriptor)
    {
        if (!string.IsNullOrWhiteSpace(requested.FileId) &&
            !string.Equals(requested.FileId.Trim(), descriptor.FileId, StringComparison.Ordinal))
            throw new InvalidOperationException("Workflow file descriptor id does not match the requested file id.");
        if (!string.IsNullOrWhiteSpace(requested.ArtifactId) &&
            !string.Equals(requested.ArtifactId.Trim(), descriptor.ArtifactId, StringComparison.Ordinal))
            throw new InvalidOperationException("Workflow file descriptor artifact id does not match the requested artifact id.");
        if (!string.IsNullOrWhiteSpace(requested.Sha256) &&
            !string.Equals(requested.Sha256.Trim(), descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Workflow file descriptor hash does not match the requested hash.");
        if (requested.SizeBytes > 0 && requested.SizeBytes != descriptor.SizeBytes)
            throw new InvalidOperationException("Workflow file descriptor size does not match the requested size.");
    }

    private static void ValidateOwnerCanBind(string? existingOwner, string requestedOwner, string ownerName)
    {
        if (!string.IsNullOrWhiteSpace(existingOwner) &&
            !string.Equals(existingOwner.Trim(), requestedOwner, StringComparison.Ordinal))
            throw new InvalidOperationException($"Workflow file descriptor owner {ownerName} is already bound.");
    }

    private static void ValidateNotExpired(FileArtifactRef descriptor)
    {
        if (descriptor.ExpiresAtUnixMs > 0 &&
            descriptor.ExpiresAtUnixMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            throw new InvalidOperationException("Workflow file artifact has expired.");
    }

    private static async Task ValidateContentHashAsync(
        string contentPath,
        FileArtifactRef descriptor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Sha256))
            return;

        await using var stream = File.OpenRead(contentPath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        var sha256 = Convert.ToHexString(hash).ToLowerInvariant();
        if (!string.Equals(sha256, descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Workflow file content hash does not match its descriptor.");
    }

    private static ProtoWorkflowFileRef ToProto(FileArtifactRef source) =>
        new()
        {
            FileId = source.FileId ?? string.Empty,
            ArtifactId = source.ArtifactId ?? string.Empty,
            SourceKind = ToProtoSourceKind(source.SourceKind),
            SourceMessageId = source.SourceMessageId ?? string.Empty,
            SourceResourceKey = source.SourceResourceKey ?? string.Empty,
            FileName = source.FileName ?? string.Empty,
            MediaType = source.MediaType ?? string.Empty,
            SizeBytes = source.SizeBytes,
            Sha256 = source.Sha256 ?? string.Empty,
            CreatedAtUnixMs = source.CreatedAtUnixMs,
            ExpiresAtUnixMs = source.ExpiresAtUnixMs,
            OwnerRunId = source.OwnerRunId ?? string.Empty,
            OwnerScopeId = source.OwnerScopeId ?? string.Empty,
        };

    private static FileArtifactRef ToApplication(ProtoWorkflowFileRef source) =>
        new()
        {
            FileId = Normalize(source.FileId),
            ArtifactId = Normalize(source.ArtifactId),
            SourceKind = ToApplicationSourceKind(source.SourceKind),
            SourceMessageId = Normalize(source.SourceMessageId),
            SourceResourceKey = Normalize(source.SourceResourceKey),
            FileName = Normalize(source.FileName),
            MediaType = Normalize(source.MediaType),
            SizeBytes = source.SizeBytes,
            Sha256 = Normalize(source.Sha256),
            CreatedAtUnixMs = source.CreatedAtUnixMs,
            ExpiresAtUnixMs = source.ExpiresAtUnixMs,
            OwnerRunId = Normalize(source.OwnerRunId),
            OwnerScopeId = Normalize(source.OwnerScopeId),
        };

    private static ProtoWorkflowFileSourceKind ToProtoSourceKind(FileArtifactSourceKind source) =>
        source switch
        {
            FileArtifactSourceKind.ChatInput => ProtoWorkflowFileSourceKind.ChatInput,
            FileArtifactSourceKind.FormUpload => ProtoWorkflowFileSourceKind.FormUpload,
            FileArtifactSourceKind.ConnectedServiceResource => ProtoWorkflowFileSourceKind.ConnectedServiceResource,
            FileArtifactSourceKind.ExternalResource => ProtoWorkflowFileSourceKind.ExternalResource,
            FileArtifactSourceKind.Generated => ProtoWorkflowFileSourceKind.Generated,
            _ => ProtoWorkflowFileSourceKind.Unspecified,
        };

    private static FileArtifactSourceKind ToApplicationSourceKind(ProtoWorkflowFileSourceKind source) =>
        source switch
        {
            ProtoWorkflowFileSourceKind.ChatInput => FileArtifactSourceKind.ChatInput,
            ProtoWorkflowFileSourceKind.FormUpload => FileArtifactSourceKind.FormUpload,
            ProtoWorkflowFileSourceKind.ConnectedServiceResource => FileArtifactSourceKind.ConnectedServiceResource,
            ProtoWorkflowFileSourceKind.ExternalResource => FileArtifactSourceKind.ExternalResource,
            ProtoWorkflowFileSourceKind.Generated => FileArtifactSourceKind.Generated,
            _ => FileArtifactSourceKind.Unspecified,
        };
}
