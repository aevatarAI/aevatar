namespace Aevatar.Workflow.Application.Abstractions.Runs;

public static class WorkflowChatInputParts
{
    public static WorkflowChatInputPart FromFileRef(
        FileArtifactRef fileRef,
        WorkflowChatInputPartKind? preferredKind = null)
    {
        ArgumentNullException.ThrowIfNull(fileRef);
        if (string.IsNullOrWhiteSpace(fileRef.FileId) && string.IsNullOrWhiteSpace(fileRef.ArtifactId))
            throw new ArgumentException("Workflow chat file input requires fileId or artifactId.", nameof(fileRef));

        return new WorkflowChatInputPart
        {
            Kind = preferredKind ?? ResolveKind(fileRef.MediaType),
            Uri = ResolveUri(fileRef),
            MediaType = Normalize(fileRef.MediaType),
            Name = Normalize(fileRef.FileName),
            FileRef = fileRef,
        };
    }

    private static WorkflowChatInputPartKind ResolveKind(string? mediaType)
    {
        var normalized = Normalize(mediaType);
        if (normalized == null)
            return WorkflowChatInputPartKind.File;
        if (normalized.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return WorkflowChatInputPartKind.Image;
        if (normalized.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return WorkflowChatInputPartKind.Audio;
        if (normalized.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return WorkflowChatInputPartKind.Video;

        return WorkflowChatInputPartKind.File;
    }

    private static string? ResolveUri(FileArtifactRef fileRef) =>
        Normalize(fileRef.ArtifactId) ??
        (string.IsNullOrWhiteSpace(fileRef.FileId)
            ? null
            : $"workflow-file://{fileRef.FileId.Trim()}");

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
