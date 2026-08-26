using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.AI.Abstractions;
using Google.Protobuf;

namespace Aevatar.GAgents.NyxidChat;

internal static class ConversationContextAttachmentAdmission
{
    public const int MaximumAttachments = 4;
    public const int MaximumAttachmentBytes = 65536;

    public static ConversationContextAttachmentSet CloneSet(
        IEnumerable<ConversationContextAttachment>? attachments)
    {
        var result = new ConversationContextAttachmentSet();
        if (attachments is not null)
            result.Attachments.Add(attachments.Where(static item => item is not null).Select(static item => item.Clone()));
        return result;
    }

    public static ConversationContextAttachmentSet? CloneOptionalSet(
        IEnumerable<ConversationContextAttachment>? attachments)
    {
        var result = CloneSet(attachments);
        return result.Attachments.Count == 0 ? null : result;
    }

    public static bool HasAttachments(ConversationContextAttachmentSet? set) =>
        set is { Attachments.Count: > 0 };

    public static bool ByteEquivalent(
        ConversationContextAttachmentSet left,
        ConversationContextAttachmentSet right) =>
        left.ToByteArray().AsSpan().SequenceEqual(right.ToByteArray());

    public static bool TryNormalize(
        ConversationContextAttachmentSet? source,
        out ConversationContextAttachmentSet normalized) =>
        TryNormalize(source, out normalized, out _);

    public static bool TryNormalize(
        ConversationContextAttachmentSet? source,
        out ConversationContextAttachmentSet normalized,
        out ConversationContextAttachmentAdmissionFailureReason failureReason)
    {
        normalized = CloneSet(source?.Attachments);
        failureReason = ConversationContextAttachmentAdmissionFailureReason.Unspecified;
        if (normalized.Attachments.Count > MaximumAttachments)
        {
            failureReason = ConversationContextAttachmentAdmissionFailureReason.OverLimit;
            return false;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attachment in normalized.Attachments)
        {
            attachment.ArtifactId = attachment.ArtifactId.Trim();
            attachment.PinnedRevisionId = attachment.PinnedRevisionId.Trim();
            if (string.IsNullOrWhiteSpace(attachment.ArtifactId) ||
                !ids.Add(attachment.ArtifactId))
            {
                failureReason = ConversationContextAttachmentAdmissionFailureReason.InvalidRequest;
                return false;
            }

            if (attachment.RevisionMode == ConversationContextAttachmentRevisionMode.PinnedRevision)
            {
                if (string.IsNullOrWhiteSpace(attachment.PinnedRevisionId))
                {
                    failureReason = ConversationContextAttachmentAdmissionFailureReason.InvalidRequest;
                    return false;
                }
            }
            else if (attachment.RevisionMode == ConversationContextAttachmentRevisionMode.FollowCurrent)
            {
                attachment.PinnedRevisionId = string.Empty;
            }
            else
            {
                failureReason = ConversationContextAttachmentAdmissionFailureReason.InvalidRequest;
                return false;
            }
        }

        return true;
    }

    public static bool IsAuthorized(ContentArtifactCurrentStateResponse artifact, string principalId)
    {
        var principal = principalId.Trim();
        return string.Equals(artifact.Owner.PrincipalId, principal, StringComparison.Ordinal) ||
               artifact.ReaderPrincipalIds.Any(item => string.Equals(item, principal, StringComparison.Ordinal));
    }

    public static bool IsAllowedKind(string kind) =>
        kind is "text" or "markdown" or "structured_document";
}
