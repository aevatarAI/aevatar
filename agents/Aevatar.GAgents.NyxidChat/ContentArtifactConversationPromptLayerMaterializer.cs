using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Prompting;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Microsoft.Extensions.Logging;

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

    // Fix (review round 1, F2/F3):
    //   Sealed attachments were omitted when the read port or caller authority was unavailable.
    //   Every declared attachment now degrades to a paired placeholder and diagnostic.
    internal static async Task<ConversationContextPromptLayer?> MaterializeOrDegradeAsync(
        ContentArtifactConversationPromptLayerMaterializer? materializer,
        ConversationContextAttachmentSet? attachments,
        string? scopeId,
        string? principalId,
        CancellationToken ct = default,
        ILogger? logger = null)
    {
        if (attachments is not { Attachments.Count: > 0 })
            return null;
        if (materializer is null)
        {
            return CreateUnavailableLayer(
                attachments,
                ConversationContextAttachmentUnavailableReason.ReadModelUnavailable);
        }

        var normalizedScopeId = scopeId?.Trim();
        var normalizedPrincipalId = principalId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedScopeId) ||
            string.IsNullOrWhiteSpace(normalizedPrincipalId))
        {
            return CreateUnavailableLayer(
                attachments,
                ConversationContextAttachmentUnavailableReason.AccessDenied);
        }

        try
        {
            return await materializer.MaterializeAsync(
                    normalizedScopeId,
                    new ContentArtifactPrincipalContract(normalizedPrincipalId, "nyxid"),
                    attachments,
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger?.LogWarning(
                exception,
                "Conversation context attachment materialization failed closed to a degraded layer.");
            return CreateUnavailableLayer(
                attachments,
                ConversationContextAttachmentUnavailableReason.ReadModelUnavailable);
        }
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
                    AppendUnavailable(sections, diagnostics, artifactId, revisionId,
                        ConversationContextAttachmentUnavailableReason.RevisionUnavailable);
                    continue;
                }

                if (!string.Equals(revision.Availability, ContentArtifactRevisionAvailabilityNames.Available, StringComparison.Ordinal))
                {
                    AppendUnavailable(
                        sections,
                        diagnostics,
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
                    AppendUnavailable(sections, diagnostics, artifactId, revisionId,
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
                    AppendUnavailable(sections, diagnostics, artifactId, revisionId,
                        ConversationContextAttachmentUnavailableReason.OverBudget);
                    continue;
                }

                sections.Append(section);
                usedBytes += sectionBytes;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ContentArtifactContentUnavailableException exception)
            {
                AppendUnavailable(sections, diagnostics, artifactId, revisionId,
                    MapUnavailableReason(exception.Reason));
            }
            catch (ContentArtifactNotFoundException)
            {
                AppendUnavailable(sections, diagnostics, artifactId, revisionId,
                    ConversationContextAttachmentUnavailableReason.NotFound);
            }
            catch (IOException)
            {
                AppendUnavailable(sections, diagnostics, artifactId, revisionId,
                    ConversationContextAttachmentUnavailableReason.BackingUnavailable);
            }
            catch
            {
                AppendUnavailable(sections, diagnostics, artifactId, revisionId,
                    ConversationContextAttachmentUnavailableReason.ReadModelUnavailable);
            }
        }

        return CreateLayer(sections, diagnostics);
    }

    // Fix (review round 1, F5):
    //   Typed placeholder objects were allocated into a list that no consumer observed.
    //   The visible marker and its diagnostic are now emitted directly from the typed reason.
    private static void AppendUnavailable(
        StringBuilder sections,
        List<PromptLayerDiagnostic> diagnostics,
        string artifactId,
        string revisionId,
        ConversationContextAttachmentUnavailableReason reason)
    {
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

    private static ConversationContextPromptLayer CreateUnavailableLayer(
        ConversationContextAttachmentSet attachments,
        ConversationContextAttachmentUnavailableReason reason)
    {
        var sections = new StringBuilder();
        var diagnostics = new List<PromptLayerDiagnostic>();
        foreach (var attachment in attachments.Attachments)
        {
            AppendUnavailable(
                sections,
                diagnostics,
                attachment.ArtifactId.Trim(),
                attachment.PinnedRevisionId.Trim(),
                reason);
        }

        return CreateLayer(sections, diagnostics);
    }

    private static ConversationContextPromptLayer CreateLayer(
        StringBuilder sections,
        List<PromptLayerDiagnostic> diagnostics) =>
        new(
            sections.ToString(),
            new ConversationContextPromptProvenance("content-artifact-verified-read"),
            new PromptLayerBounds(MaximumLayerBytes, MaximumLayerTokens),
            diagnostics);

    private static string PrefixHash(string? hash) =>
        string.IsNullOrWhiteSpace(hash)
            ? "unknown"
            : hash.Trim()[..Math.Min(16, hash.Trim().Length)];

    private static ConversationContextAttachmentUnavailableReason MapUnavailableReason(
        ContentArtifactContentUnavailableReason reason) =>
        reason switch
        {
            ContentArtifactContentUnavailableReason.Tombstoned => ConversationContextAttachmentUnavailableReason.Tombstoned,
            ContentArtifactContentUnavailableReason.Redacted => ConversationContextAttachmentUnavailableReason.Redacted,
            ContentArtifactContentUnavailableReason.RetentionExpired => ConversationContextAttachmentUnavailableReason.RetentionExpired,
            ContentArtifactContentUnavailableReason.BackingUnavailable => ConversationContextAttachmentUnavailableReason.BackingUnavailable,
            _ => ConversationContextAttachmentUnavailableReason.ReadModelUnavailable,
        };
}
