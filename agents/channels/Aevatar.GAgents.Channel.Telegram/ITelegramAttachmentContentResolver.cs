using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Telegram;

/// <summary>
/// Resolves one channel attachment reference into content Telegram can upload.
/// </summary>
public interface ITelegramAttachmentContentResolver
{
    /// <summary>
    /// Resolves one attachment for upload.
    /// </summary>
    Task<TelegramAttachmentContent?> ResolveAsync(AttachmentRef attachment, CancellationToken ct);
}

/// <summary>
/// Carries one resolved Telegram attachment upload.
/// </summary>
/// <param name="FileName">The filename exposed to Telegram.</param>
/// <param name="ContentType">The MIME content type.</param>
/// <param name="Content">The stream to upload. The caller owns disposal.</param>
/// <param name="ExternalUrl">An optional public URL Telegram can fetch directly.</param>
/// <param name="TelegramFileId">An optional existing Telegram file id.</param>
public sealed record TelegramAttachmentContent(
    string FileName,
    string ContentType,
    Stream? Content = null,
    string? ExternalUrl = null,
    string? TelegramFileId = null);
