using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Logging;
using ApplicationFileArtifactRef = Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactRef;
using ApplicationFileArtifactSourceKind = Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactSourceKind;
using LlmChatFileRef = Aevatar.AI.Abstractions.LLMProviders.ChatFileRef;
using LlmChatFileSourceKind = Aevatar.AI.Abstractions.LLMProviders.ChatFileSourceKind;

namespace Aevatar.Workflow.Infrastructure.Runs;

internal sealed class WorkflowFileRefLlmCallMiddleware(
    IFileArtifactReadPort fileArtifacts,
    ILogger<WorkflowFileRefLlmCallMiddleware>? logger = null) : ILLMCallMiddleware
{
    private const int MaxMediaBytes = 5 * 1024 * 1024;

    private readonly IFileArtifactReadPort _fileArtifacts =
        fileArtifacts ?? throw new ArgumentNullException(nameof(fileArtifacts));
    private readonly ILogger<WorkflowFileRefLlmCallMiddleware>? _logger = logger;

    public async Task InvokeAsync(LLMCallContext context, Func<Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        for (var messageIndex = 0; messageIndex < context.Request.Messages.Count; messageIndex++)
        {
            var message = context.Request.Messages[messageIndex];
            if (message.ContentParts is not { Count: > 0 })
                continue;

            var parts = message.ContentParts.ToArray();
            var changed = false;
            for (var partIndex = 0; partIndex < parts.Length; partIndex++)
            {
                var materialized = await MaterializeAsync(parts[partIndex], context.CancellationToken)
                    .ConfigureAwait(false);
                if (ReferenceEquals(materialized, parts[partIndex]))
                    continue;

                parts[partIndex] = materialized;
                changed = true;
            }

            if (changed)
                context.Request.Messages[messageIndex] = CopyMessage(message, parts);
        }

        await next().ConfigureAwait(false);
    }

    private async Task<ContentPart> MaterializeAsync(ContentPart part, CancellationToken ct)
    {
        if (!IsMedia(part.Kind))
            return part;
        if (!string.IsNullOrWhiteSpace(part.DataBase64))
            return HasOpaqueUri(part) ? CopyWithoutUri(part) : part;
        if (HasProviderReadyUri(part.Uri) || part.FileRef is null || !HasIdentity(part.FileRef))
            return part;

        FileArtifactContent artifact;
        try
        {
            artifact = await _fileArtifacts.OpenReadAsync(ToApplicationFileRef(part.FileRef), ct)
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return BuildUnavailableAttachmentPart(part);
        }
        catch (DirectoryNotFoundException)
        {
            return BuildUnavailableAttachmentPart(part);
        }

        await using var content = artifact.Content;
        if (artifact.FileRef.SizeBytes > MaxMediaBytes)
            throw MediaTooLarge();
        var bytes = await ReadCappedAsync(content, ct).ConfigureAwait(false);

        return new ContentPart
        {
            Kind = part.Kind,
            Text = part.Text,
            DataBase64 = Convert.ToBase64String(bytes),
            MediaType = part.MediaType,
            Uri = null,
            Name = part.Name,
            FileRef = part.FileRef,
        };
    }

    private static async Task<byte[]> ReadCappedAsync(Stream content, CancellationToken ct)
    {
        using var buffer = new MemoryStream(capacity: 81920);
        var chunk = new byte[81920];
        while (true)
        {
            var read = await content.ReadAsync(chunk.AsMemory(0, chunk.Length), ct)
                .ConfigureAwait(false);
            if (read == 0)
                return buffer.ToArray();
            if (buffer.Length + read > MaxMediaBytes)
                throw MediaTooLarge();

            buffer.Write(chunk, 0, read);
        }
    }

    private ContentPart BuildUnavailableAttachmentPart(ContentPart part)
    {
        var name = Normalize(part.FileRef?.FileName) ?? Normalize(part.Name) ?? "attachment";
        if (name.Length > 128)
            name = name[..128];

        _logger?.LogWarning(
            "Workflow file ref media unavailable for LLM request; degrading to text part: fileId={FileId} artifactId={ArtifactId} sourceKind={SourceKind} sourceMessageId={SourceMessageId} sourceResourceKey={SourceResourceKey}",
            part.FileRef?.FileId,
            part.FileRef?.ArtifactId,
            part.FileRef?.SourceKind,
            part.FileRef?.SourceMessageId,
            part.FileRef?.SourceResourceKey);
        return ContentPart.TextPart($"Attachment unavailable: '{name}' has expired or was removed.");
    }

    private static InvalidOperationException MediaTooLarge() =>
        new($"Workflow LLM media input exceeds {MaxMediaBytes} bytes.");

    private static ChatMessage CopyMessage(ChatMessage source, IReadOnlyList<ContentPart> parts) =>
        new()
        {
            Role = source.Role,
            Content = source.Content,
            ReasoningContent = source.ReasoningContent,
            ContentParts = parts,
            ToolCallId = source.ToolCallId,
            ToolCalls = source.ToolCalls,
            ToolResultView = source.ToolResultView,
        };

    private static bool IsMedia(ContentPartKind kind) =>
        kind is ContentPartKind.Image or ContentPartKind.Audio or ContentPartKind.Video;

    private static bool HasOpaqueUri(ContentPart part) =>
        !string.IsNullOrWhiteSpace(part.Uri) && !HasProviderReadyUri(part.Uri);

    private static bool HasProviderReadyUri(string? uriValue)
    {
        var value = uriValue?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return true;

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    private static ContentPart CopyWithoutUri(ContentPart source) =>
        new()
        {
            Kind = source.Kind,
            Text = source.Text,
            DataBase64 = source.DataBase64,
            MediaType = source.MediaType,
            Uri = null,
            Name = source.Name,
            FileRef = source.FileRef,
        };

    private static bool HasIdentity(LlmChatFileRef fileRef) =>
        !string.IsNullOrWhiteSpace(fileRef.FileId) ||
        !string.IsNullOrWhiteSpace(fileRef.ArtifactId);

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static ApplicationFileArtifactRef ToApplicationFileRef(LlmChatFileRef source) =>
        new()
        {
            FileId = source.FileId,
            ArtifactId = source.ArtifactId,
            SourceKind = ToApplicationSourceKind(source.SourceKind),
            SourceMessageId = source.SourceMessageId,
            SourceResourceKey = source.SourceResourceKey,
            FileName = source.FileName,
            MediaType = source.MediaType,
            SizeBytes = source.SizeBytes,
            Sha256 = source.Sha256,
            CreatedAtUnixMs = source.CreatedAtUnixMs,
            ExpiresAtUnixMs = source.ExpiresAtUnixMs,
            OwnerRunId = source.OwnerRunId,
            OwnerScopeId = source.OwnerScopeId,
        };

    private static ApplicationFileArtifactSourceKind ToApplicationSourceKind(LlmChatFileSourceKind sourceKind) =>
        sourceKind switch
        {
            LlmChatFileSourceKind.ChatInput => ApplicationFileArtifactSourceKind.ChatInput,
            LlmChatFileSourceKind.FormUpload => ApplicationFileArtifactSourceKind.FormUpload,
            LlmChatFileSourceKind.ConnectedServiceResource =>
                ApplicationFileArtifactSourceKind.ConnectedServiceResource,
            LlmChatFileSourceKind.ExternalResource => ApplicationFileArtifactSourceKind.ExternalResource,
            LlmChatFileSourceKind.Generated => ApplicationFileArtifactSourceKind.Generated,
            _ => ApplicationFileArtifactSourceKind.Unspecified,
        };
}
