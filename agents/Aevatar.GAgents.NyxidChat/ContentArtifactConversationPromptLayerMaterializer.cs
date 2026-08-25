using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Prompting;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Materializes the sealed conversation attachment set from the verified artifact read path.
/// The materialized body is turn-local and is never copied into actor state or transcript history.
/// </summary>
public sealed class ContentArtifactConversationPromptLayerMaterializer
{
    public const int MaximumAttachmentBytes = ConversationContextAttachmentAdmission.MaximumAttachmentBytes;
    public const int MaximumLayerBytes = 131072;
    public const int MaximumLayerTokens = 32768;

    private readonly IContentArtifactQueryPort _artifacts;

    public ContentArtifactConversationPromptLayerMaterializer(IContentArtifactQueryPort artifacts)
    {
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
    }

    public async Task<ConversationContextPromptLayer> MaterializeAsync(
        string scopeId,
        ContentArtifactPrincipalContract requester,
        ConversationContextAttachmentSet? attachments,
        CancellationToken ct = default)
    {
        var normalizedScopeId = scopeId.Trim();
        var sections = new StringBuilder();
        var diagnostics = new List<PromptLayerDiagnostic>();
        var unavailable = new List<ConversationContextAttachmentUnavailablePlaceholder>();
        var usedBytes = 0;

        foreach (var attachment in attachments?.Attachments ?? [])
        {
            var artifactId = attachment.ArtifactId.Trim();
            var revisionId = attachment.PinnedRevisionId.Trim();
            try
            {
                var artifact = await _artifacts.GetAsync(normalizedScopeId, artifactId, ct);
                if (artifact is null)
                {
                    AppendUnavailable(
                        sections,
                        diagnostics,
                        unavailable,
                        artifactId,
                        revisionId,
                        ConversationContextAttachmentUnavailableReason.NotFound);
                    continue;
                }

                if (!string.Equals(artifact.LifecycleStatus, ContentArtifactLifecycleStatusNames.Active, StringComparison.Ordinal))
                {
                    AppendUnavailable(
                        sections,
                        diagnostics,
                        unavailable,
                        artifactId,
                        revisionId,
                        artifact.LifecycleStatus == ContentArtifactLifecycleStatusNames.Tombstoned
                            ? ConversationContextAttachmentUnavailableReason.Tombstoned
                            : ConversationContextAttachmentUnavailableReason.Inactive);
                    continue;
                }

                if (!ConversationContextAttachmentAdmission.IsAllowedKind(artifact.Kind) ||
                    !ConversationContextAttachmentAdmission.IsAuthorized(artifact, requester.PrincipalId))
                {
                    AppendUnavailable(
                        sections,
                        diagnostics,
                        unavailable,
                        artifactId,
                        revisionId,
                        ConversationContextAttachmentUnavailableReason.AccessDenied);
                    continue;
                }

                revisionId = attachment.RevisionMode == ConversationContextAttachmentRevisionMode.PinnedRevision
                    ? revisionId
                    : artifact.CurrentRevisionId?.Trim() ?? string.Empty;
                var revision = artifact.Revisions.FirstOrDefault(item =>
                    string.Equals(item.RevisionId, revisionId, StringComparison.Ordinal));
                if (revision is null)
                {
                    AppendUnavailable(sections, diagnostics, unavailable, artifactId, revisionId,
                        ConversationContextAttachmentUnavailableReason.RevisionUnavailable);
                    continue;
                }

                if (!string.Equals(revision.Availability, ContentArtifactRevisionAvailabilityNames.Available, StringComparison.Ordinal))
                {
                    AppendUnavailable(
                        sections,
                        diagnostics,
                        unavailable,
                        artifactId,
                        revisionId,
                        revision.Availability switch
                        {
                            ContentArtifactRevisionAvailabilityNames.Redacted => ConversationContextAttachmentUnavailableReason.Redacted,
                            ContentArtifactRevisionAvailabilityNames.RetentionExpired => ConversationContextAttachmentUnavailableReason.RetentionExpired,
                            _ => ConversationContextAttachmentUnavailableReason.RevisionUnavailable,
                        });
                    continue;
                }

                var content = await _artifacts.GetRevisionContentAsync(
                    normalizedScopeId,
                    artifactId,
                    revisionId,
                    requester,
                    ct);
                if (content.Content.LongLength > MaximumAttachmentBytes)
                {
                    AppendUnavailable(sections, diagnostics, unavailable, artifactId, revisionId,
                        ConversationContextAttachmentUnavailableReason.OverBudget);
                    continue;
                }

                var header = $"[content-artifact artifact_id={artifactId} revision_id={revisionId} " +
                             $"content_hash={PrefixHash(content.Reference.ContentHash)} media_type={content.Reference.MediaType}]\n";
                var body = Encoding.UTF8.GetString(content.Content);
                var section = header + body + "\n[/content-artifact]\n";
                var sectionBytes = Encoding.UTF8.GetByteCount(section);
                if (usedBytes + sectionBytes > MaximumLayerBytes)
                {
                    AppendUnavailable(sections, diagnostics, unavailable, artifactId, revisionId,
                        ConversationContextAttachmentUnavailableReason.OverBudget);
                    continue;
                }

                sections.Append(section);
                usedBytes += sectionBytes;
            }
            catch (ContentArtifactContentUnavailableException exception)
            {
                AppendUnavailable(sections, diagnostics, unavailable, artifactId, revisionId,
                    MapUnavailableReason(exception.Message));
            }
            catch (ContentArtifactNotFoundException)
            {
                AppendUnavailable(sections, diagnostics, unavailable, artifactId, revisionId,
                    ConversationContextAttachmentUnavailableReason.NotFound);
            }
            catch
            {
                AppendUnavailable(sections, diagnostics, unavailable, artifactId, revisionId,
                    ConversationContextAttachmentUnavailableReason.ReadModelUnavailable);
            }
        }

        return new ConversationContextPromptLayer(
            sections.ToString(),
            new ConversationContextPromptProvenance("content-artifact-verified-read"),
            new PromptLayerBounds(MaximumLayerBytes, MaximumLayerTokens),
            diagnostics);
    }

    private static void AppendUnavailable(
        StringBuilder sections,
        List<PromptLayerDiagnostic> diagnostics,
        List<ConversationContextAttachmentUnavailablePlaceholder> unavailable,
        string artifactId,
        string revisionId,
        ConversationContextAttachmentUnavailableReason reason)
    {
        var placeholder = new ConversationContextAttachmentUnavailablePlaceholder
        {
            ArtifactId = artifactId,
            RevisionId = revisionId,
            Reason = reason,
        };
        unavailable.Add(placeholder);
        sections.Append("[content-artifact-unavailable artifact_id=")
            .Append(artifactId)
            .Append(" revision_id=")
            .Append(revisionId)
            .Append(" reason=")
            .Append(reason.ToString())
            .Append("]\n");
        diagnostics.Add(new PromptLayerDiagnostic(
            PromptLayerDiagnosticCode.ProviderReported,
            $"content artifact {artifactId} revision {revisionId} unavailable: {reason}"));
    }

    private static string PrefixHash(string? hash) =>
        string.IsNullOrWhiteSpace(hash)
            ? "unknown"
            : hash.Trim()[..Math.Min(16, hash.Trim().Length)];

    private static ConversationContextAttachmentUnavailableReason MapUnavailableReason(string detail) =>
        detail.Contains("tombstoned", StringComparison.OrdinalIgnoreCase)
            ? ConversationContextAttachmentUnavailableReason.Tombstoned
            : detail.Contains("redacted", StringComparison.OrdinalIgnoreCase)
                ? ConversationContextAttachmentUnavailableReason.Redacted
                : detail.Contains("retention", StringComparison.OrdinalIgnoreCase)
                    ? ConversationContextAttachmentUnavailableReason.RetentionExpired
                    : detail.Contains("backing", StringComparison.OrdinalIgnoreCase)
                        ? ConversationContextAttachmentUnavailableReason.BackingUnavailable
                        : ConversationContextAttachmentUnavailableReason.ReadModelUnavailable;
}
