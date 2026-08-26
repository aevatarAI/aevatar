using System.Buffers;
using System.Security.Cryptography;
using Aevatar.ContentArtifacts.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.ContentArtifacts;

/// <summary>
/// Authority actor for one durable content artifact and its immutable revisions.
/// </summary>
[GAgent("studio.content-artifact")]
public sealed class ContentArtifactGAgent : GAgentBase<ContentArtifactState>, IProjectedActor
{
    private readonly IContentArtifactBackingContentPort? _backingContentPort;

    public static string ProjectionKind => "content-artifact";

    public ContentArtifactGAgent(IContentArtifactBackingContentPort? backingContentPort = null)
    {
        _backingContentPort = backingContentPort;
    }

    [EventHandler(EndpointName = "createContentArtifact")]
    public async Task HandleCreateAsync(CreateContentArtifact command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!string.IsNullOrWhiteSpace(State.ArtifactId))
        {
            if (!string.Equals(State.CreationRequestHash, HashCreateRequest(command), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "ContentArtifact logical identity already exists with a different request.");
            }
            return;
        }

        await ValidateCreateAsync(command);

        var createdAt = command.RequestedAtUtc?.Clone()
                        ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        var request = command.Clone();
        request.RequestedAtUtc = createdAt.Clone();
        request.FirstRevision.CreatedAtUtc ??= createdAt.Clone();
        request.FirstRevision.Availability = ContentArtifactRevisionAvailability.Available;
        await PersistDomainEventAsync(new ContentArtifactCreatedEvent
        {
            Request = request,
            CreatedAtUtc = createdAt,
        });
    }

    [EventHandler(EndpointName = "appendContentArtifactRevision")]
    public async Task HandleAppendRevisionAsync(AppendContentArtifactRevision command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Revision);
        // Implement (issue #3541):
        //   Behavior: append may make the new revision current within the same committed event.
        //   Why this shape: one actor-owned transition prevents a partial append/advance outcome.
        if (command.AdvanceToCurrent)
            EnsureReaderWriter(command.RequestedBy);
        else
            EnsureWriter(command.RequestedBy);
        EnsureActiveArtifact(command.ArtifactId);
        var dedupKey = ContentArtifactConventions.NormalizeRequired(
            command.Revision.DedupKey,
            "revision.dedupKey");

        var existingRevision = State.Revisions.Values.FirstOrDefault(revision =>
            string.Equals(revision.DedupKey, dedupKey, StringComparison.Ordinal));
        if (existingRevision != null)
        {
            if (!RevisionsEqualForAppend(existingRevision, command.Revision))
                throw new InvalidOperationException("ContentArtifact revision dedup_key already exists with different facts.");
            return;
        }

        var appendedAt = command.RequestedAtUtc?.Clone()
                         ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        var revision = command.Revision.Clone();
        revision.RevisionNumber = checked(
            State.Revisions.Values.Select(static item => item.RevisionNumber).DefaultIfEmpty().Max() + 1);
        revision.RevisionId = ContentArtifactConventions.BuildRevisionId(State.ArtifactId, revision.RevisionNumber);
        revision.CreatedAtUtc ??= appendedAt.Clone();
        revision.Availability = ContentArtifactRevisionAvailability.Available;
        await ValidateRevisionAsync(revision, firstRevision: false);
        await PersistDomainEventAsync(new ContentArtifactRevisionAppendedEvent
        {
            Revision = revision,
            AppendedAtUtc = appendedAt,
            MadeCurrent = command.AdvanceToCurrent,
        });
    }

    [EventHandler(EndpointName = "advanceContentArtifactCurrentRevision")]
    public async Task HandleAdvanceCurrentRevisionAsync(AdvanceContentArtifactCurrentRevision command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureReaderWriter(command.RequestedBy);
        EnsureConcurrencyVersion(command.ExpectedConcurrencyVersion);
        EnsureActiveArtifact(command.ArtifactId);
        var revisionId = ContentArtifactConventions.NormalizeRequired(command.RevisionId, "revisionId");
        if (string.Equals(State.CurrentRevisionId, revisionId, StringComparison.Ordinal))
            return;

        if (!State.Revisions.TryGetValue(revisionId, out var revision))
            throw new InvalidOperationException($"ContentArtifact revision '{revisionId}' does not exist.");
        if (revision.Availability != ContentArtifactRevisionAvailability.Available)
            throw new InvalidOperationException($"ContentArtifact revision '{revisionId}' is not available.");

        await PersistDomainEventAsync(new ContentArtifactCurrentRevisionAdvancedEvent
        {
            RevisionId = revisionId,
            AdvancedAtUtc = ResolveRequestedAt(command.RequestedAtUtc),
        });
    }

    [EventHandler(EndpointName = "redactContentArtifactRevision")]
    public async Task HandleRedactRevisionAsync(RedactContentArtifactRevision command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureReaderWriter(command.RequestedBy);
        EnsureConcurrencyVersion(command.ExpectedConcurrencyVersion);
        EnsureActiveArtifact(command.ArtifactId);
        var revision = GetRevision(command.RevisionId);
        var reason = ContentArtifactConventions.NormalizeRequired(command.Reason, "reason");
        if (revision.Availability == ContentArtifactRevisionAvailability.Redacted)
        {
            if (!string.Equals(revision.RedactionReason, reason, StringComparison.Ordinal))
                throw new InvalidOperationException("ContentArtifact revision is already redacted for a different reason.");
            return;
        }

        await PersistDomainEventAsync(new ContentArtifactRevisionRedactedEvent
        {
            RevisionId = revision.RevisionId,
            Reason = reason,
            RedactedAtUtc = ResolveRequestedAt(command.RequestedAtUtc),
        });
    }

    [EventHandler(EndpointName = "expireContentArtifactRevision")]
    public async Task HandleExpireRevisionAsync(ExpireContentArtifactRevision command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureReaderWriter(command.RequestedBy);
        EnsureConcurrencyVersion(command.ExpectedConcurrencyVersion);
        EnsureActiveArtifact(command.ArtifactId);
        var revision = GetRevision(command.RevisionId);
        if (revision.Availability == ContentArtifactRevisionAvailability.RetentionExpired)
            return;
        if (revision.Availability == ContentArtifactRevisionAvailability.Redacted)
            throw new InvalidOperationException("A redacted ContentArtifact revision cannot transition to retention-expired.");

        await PersistDomainEventAsync(new ContentArtifactRevisionExpiredEvent
        {
            RevisionId = revision.RevisionId,
            ExpiredAtUtc = ResolveRequestedAt(command.RequestedAtUtc),
        });
    }

    [EventHandler(EndpointName = "tombstoneContentArtifact")]
    public async Task HandleTombstoneAsync(TombstoneContentArtifact command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureOwner(command.RequestedBy);
        EnsureConcurrencyVersion(command.ExpectedConcurrencyVersion);
        EnsureArtifactIdentity(command.ArtifactId);
        var reason = ContentArtifactConventions.NormalizeRequired(command.Reason, "reason");
        if (State.LifecycleStatus == ContentArtifactLifecycleStatus.Tombstoned)
        {
            if (!string.Equals(State.TombstoneReason, reason, StringComparison.Ordinal))
                throw new InvalidOperationException("ContentArtifact is already tombstoned for a different reason.");
            return;
        }

        await PersistDomainEventAsync(new ContentArtifactTombstonedEvent
        {
            Reason = reason,
            TombstonedAtUtc = ResolveRequestedAt(command.RequestedAtUtc),
        });
    }

    protected override ContentArtifactState TransitionState(ContentArtifactState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ContentArtifactCreatedEvent>(ApplyCreated)
            .On<ContentArtifactRevisionAppendedEvent>(ApplyRevisionAppended)
            .On<ContentArtifactCurrentRevisionAdvancedEvent>(ApplyCurrentRevisionAdvanced)
            .On<ContentArtifactRevisionRedactedEvent>(ApplyRevisionRedacted)
            .On<ContentArtifactRevisionExpiredEvent>(ApplyRevisionExpired)
            .On<ContentArtifactTombstonedEvent>(ApplyTombstoned)
            .OrCurrent();

    private async Task ValidateCreateAsync(CreateContentArtifact command)
    {
        var scopeId = ContentArtifactConventions.NormalizeScopeId(command.ScopeId);
        var dedupKey = ContentArtifactConventions.NormalizeRequired(command.DedupKey, "dedupKey");
        var artifactId = ContentArtifactConventions.BuildArtifactId(scopeId, dedupKey);
        if (!string.Equals(command.ArtifactId, artifactId, StringComparison.Ordinal) ||
            !string.Equals(Id, ContentArtifactConventions.BuildActorId(scopeId, artifactId), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "ContentArtifact canonical actor and artifact identities must be derived from scope_id and dedup_key.");
        }
        if (command.ExpectedConcurrencyVersion != 0)
            throw new InvalidOperationException("ContentArtifact create expected_concurrency_version must be 0.");
        if (command.Kind == ContentArtifactKind.Unspecified)
            throw new InvalidOperationException("kind is required.");
        ContentArtifactConventions.NormalizeRequired(command.Title, "title");
        ContentArtifactConventions.ValidateCanonicalLabels(command.Labels);
        ArgumentNullException.ThrowIfNull(command.AccessPolicy);
        ValidatePrincipal(command.AccessPolicy.Owner, "access_policy.owner");
        ArgumentNullException.ThrowIfNull(command.FirstRevision);
        var firstRevisionId = ContentArtifactConventions.BuildRevisionId(artifactId, 1);
        if (!string.Equals(command.FirstRevision.RevisionId, firstRevisionId, StringComparison.Ordinal))
            throw new InvalidOperationException("First ContentArtifact revision_id is not canonical for its artifact.");
        await ValidateRevisionAsync(command.FirstRevision, firstRevision: true);
        if (!string.Equals(command.FirstRevision.Provenance.ScopeId, scopeId, StringComparison.Ordinal))
            throw new InvalidOperationException("First revision provenance scope_id must match the artifact scope.");
        if (!string.Equals(command.FirstRevision.Provenance.TeamId ?? string.Empty, command.TeamId ?? string.Empty, StringComparison.Ordinal))
            throw new InvalidOperationException("First revision provenance team_id must match the artifact team.");
    }

    private async Task ValidateRevisionAsync(ContentArtifactRevision revision, bool firstRevision)
    {
        ContentArtifactConventions.NormalizeRequired(revision.RevisionId, "revision.revisionId");
        ContentArtifactConventions.NormalizeRequired(revision.DedupKey, "revision.dedupKey");
        ContentArtifactConventions.NormalizeRequired(revision.MediaType, "revision.mediaType");
        ValidateSha256(revision.ContentHash, "revision.content_hash");
        ArgumentNullException.ThrowIfNull(revision.Content);
        ArgumentNullException.ThrowIfNull(revision.Provenance);

        if (firstRevision)
        {
            if (revision.RevisionNumber != 1 || !string.IsNullOrWhiteSpace(revision.ParentRevisionId))
                throw new InvalidOperationException("First ContentArtifact revision must be revision 1 without a parent.");
        }
        else
        {
            var highestRevisionNumber = State.Revisions.Values.Max(static item => item.RevisionNumber);
            if (revision.RevisionNumber <= highestRevisionNumber)
                throw new InvalidOperationException("ContentArtifact revision_number must increase monotonically.");
            if (!string.IsNullOrWhiteSpace(revision.ParentRevisionId) &&
                !State.Revisions.ContainsKey(revision.ParentRevisionId))
            {
                throw new InvalidOperationException(
                    $"ContentArtifact parent revision '{revision.ParentRevisionId}' does not exist.");
            }
        }

        if (!firstRevision &&
            !string.Equals(
                revision.RevisionId,
                ContentArtifactConventions.BuildRevisionId(State.ArtifactId, revision.RevisionNumber),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ContentArtifact revision_id is not canonical for its artifact and revision number.");
        }

        ValidateProvenance(revision.Provenance, firstRevision ? revision.Provenance.ScopeId : State.ScopeId, firstRevision ? revision.Provenance.TeamId : State.TeamId);
        foreach (var citation in revision.Citations)
            ValidateCitation(citation);
        await VerifyContentAsync(revision);
    }

    private async Task VerifyContentAsync(ContentArtifactRevision revision)
    {
        switch (revision.Content.LocationCase)
        {
            case ContentArtifactRevisionContent.LocationOneofCase.InlineContent:
                if (revision.Content.InlineContent.Length > ContentArtifactConventions.MaxInlineContentBytes)
                    throw new InvalidOperationException("inline_content exceeds the ContentArtifact inline content limit.");
                VerifyLengthAndHash(revision, revision.Content.InlineContent.Span);
                return;
            case ContentArtifactRevisionContent.LocationOneofCase.BackingObject:
            {
                if (_backingContentPort == null)
                    throw new InvalidOperationException("ContentArtifact backing content port is not registered.");
                var reference = revision.Content.BackingObject;
                ContentArtifactConventions.NormalizeRequired(reference.Provider, "backing_object.provider");
                ContentArtifactConventions.NormalizeRequired(reference.ObjectKey, "backing_object.objectKey");
                var request = new ContentArtifactBackingContentRequest(
                    reference,
                    revision.Provenance.ScopeId,
                    string.IsNullOrWhiteSpace(revision.Provenance.RunId) ? null : revision.Provenance.RunId);
                var descriptor = await _backingContentPort.DescribeAsync(request);
                await using var stream = await _backingContentPort.OpenReadAsync(request);
                var verified = await MeasureAndHashAsync(stream);
                if (descriptor.ByteLength != verified.ByteLength ||
                    !string.Equals(descriptor.ContentHash, verified.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("ContentArtifact backing content descriptor does not match its content.");
                }
                if (revision.ByteLength != verified.ByteLength ||
                    !string.Equals(revision.ContentHash, verified.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("ContentArtifact content_hash or byte_length does not match backing content.");
                }
                return;
            }
            default:
                throw new InvalidOperationException("ContentArtifact revision content location is required.");
        }
    }

    private static ContentArtifactState ApplyCreated(ContentArtifactState state, ContentArtifactCreatedEvent evt)
    {
        var request = evt.Request ?? throw new InvalidOperationException("ContentArtifact created event request is required.");
        var createdAt = evt.CreatedAtUtc?.Clone() ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        var revision = request.FirstRevision.Clone();
        revision.CreatedAtUtc ??= createdAt.Clone();
        revision.Availability = ContentArtifactRevisionAvailability.Available;
        var next = new ContentArtifactState
        {
            ArtifactId = request.ArtifactId,
            DedupKey = request.DedupKey,
            ScopeId = request.ScopeId,
            TeamId = request.TeamId,
            Kind = request.Kind,
            Title = request.Title,
            Classification = request.Classification,
            AccessPolicy = request.AccessPolicy?.Clone(),
            RetentionPolicy = request.RetentionPolicy?.Clone(),
            WorkOrderId = request.WorkOrderId,
            CurrentRevisionId = revision.RevisionId,
            ConcurrencyVersion = 1,
            LifecycleStatus = ContentArtifactLifecycleStatus.Active,
            CreatedAtUtc = createdAt.Clone(),
            UpdatedAtUtc = createdAt.Clone(),
            CreationRequestHash = HashCreateRequest(request),
        };
        next.Labels.Add(request.Labels);
        next.Revisions.Add(revision.RevisionId, revision);
        return next;
    }

    private static ContentArtifactState ApplyRevisionAppended(ContentArtifactState state, ContentArtifactRevisionAppendedEvent evt)
    {
        var next = state.Clone();
        var revision = evt.Revision?.Clone() ?? throw new InvalidOperationException("Appended revision is required.");
        next.Revisions.Add(revision.RevisionId, revision);
        if (evt.MadeCurrent)
            next.CurrentRevisionId = revision.RevisionId;
        AdvanceVersion(next, evt.AppendedAtUtc);
        return next;
    }

    private static ContentArtifactState ApplyCurrentRevisionAdvanced(ContentArtifactState state, ContentArtifactCurrentRevisionAdvancedEvent evt)
    {
        var next = state.Clone();
        next.CurrentRevisionId = evt.RevisionId;
        AdvanceVersion(next, evt.AdvancedAtUtc);
        return next;
    }

    private static ContentArtifactState ApplyRevisionRedacted(ContentArtifactState state, ContentArtifactRevisionRedactedEvent evt)
    {
        var next = state.Clone();
        var revision = next.Revisions[evt.RevisionId].Clone();
        revision.Content = new ContentArtifactRevisionContent();
        revision.Availability = ContentArtifactRevisionAvailability.Redacted;
        revision.RedactionReason = evt.Reason;
        revision.RedactedAtUtc = evt.RedactedAtUtc?.Clone();
        next.Revisions[revision.RevisionId] = revision;
        AdvanceVersion(next, evt.RedactedAtUtc);
        return next;
    }

    private static ContentArtifactState ApplyRevisionExpired(ContentArtifactState state, ContentArtifactRevisionExpiredEvent evt)
    {
        var next = state.Clone();
        var revision = next.Revisions[evt.RevisionId].Clone();
        revision.Content = new ContentArtifactRevisionContent();
        revision.Availability = ContentArtifactRevisionAvailability.RetentionExpired;
        revision.RetentionExpiredAtUtc = evt.ExpiredAtUtc?.Clone();
        next.Revisions[revision.RevisionId] = revision;
        AdvanceVersion(next, evt.ExpiredAtUtc);
        return next;
    }

    private static ContentArtifactState ApplyTombstoned(ContentArtifactState state, ContentArtifactTombstonedEvent evt)
    {
        var next = state.Clone();
        next.LifecycleStatus = ContentArtifactLifecycleStatus.Tombstoned;
        next.CurrentRevisionId = string.Empty;
        next.TombstoneReason = evt.Reason;
        next.TombstonedAtUtc = evt.TombstonedAtUtc?.Clone();
        foreach (var revisionId in next.Revisions.Keys.ToArray())
        {
            var revision = next.Revisions[revisionId].Clone();
            revision.Content = new ContentArtifactRevisionContent();
            if (revision.Availability == ContentArtifactRevisionAvailability.Available)
            {
                revision.Availability = ContentArtifactRevisionAvailability.RetentionExpired;
                revision.RetentionExpiredAtUtc = evt.TombstonedAtUtc?.Clone();
            }
            next.Revisions[revisionId] = revision;
        }
        AdvanceVersion(next, evt.TombstonedAtUtc);
        return next;
    }

    private void EnsureActiveArtifact(string artifactId)
    {
        EnsureArtifactIdentity(artifactId);
        if (State.LifecycleStatus != ContentArtifactLifecycleStatus.Active)
            throw new InvalidOperationException("ContentArtifact is tombstoned.");
    }

    private void EnsureArtifactIdentity(string artifactId)
    {
        if (string.IsNullOrWhiteSpace(State.ArtifactId))
            throw new InvalidOperationException("ContentArtifact has not been created.");
        if (!string.Equals(State.ArtifactId, artifactId?.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException($"ContentArtifact actor '{Id}' cannot mutate artifact '{artifactId}'.");
    }

    private void EnsureConcurrencyVersion(long expected)
    {
        if (State.ConcurrencyVersion != expected)
        {
            throw new InvalidOperationException(
                $"ContentArtifact concurrency version is {State.ConcurrencyVersion}, not {expected}.");
        }
    }

    private void EnsureWriter(ContentArtifactPrincipal? principal)
    {
        ValidatePrincipal(principal, "requested_by");
        if (State.AccessPolicy != null &&
            (PrincipalEquals(State.AccessPolicy.Owner, principal!) ||
             State.AccessPolicy.WriterPrincipalIds.Contains(principal!.PrincipalId)))
        {
            return;
        }
        throw new InvalidOperationException("ContentArtifact principal is not authorized to write.");
    }

    private void EnsureOwner(ContentArtifactPrincipal? principal)
    {
        ValidatePrincipal(principal, "requested_by");
        if (State.AccessPolicy == null || !PrincipalEquals(State.AccessPolicy.Owner, principal!))
            throw new InvalidOperationException("Only the ContentArtifact owner can tombstone it.");
    }

    private void EnsureReaderWriter(ContentArtifactPrincipal? principal)
    {
        ValidatePrincipal(principal, "requested_by");
        if (State.AccessPolicy != null &&
            (PrincipalEquals(State.AccessPolicy.Owner, principal!) ||
             State.AccessPolicy.ReaderPrincipalIds.Contains(principal!.PrincipalId) &&
             State.AccessPolicy.WriterPrincipalIds.Contains(principal.PrincipalId)))
        {
            return;
        }
        throw new InvalidOperationException("ContentArtifact principal is not authorized to read and write.");
    }

    private ContentArtifactRevision GetRevision(string revisionId)
    {
        var normalized = ContentArtifactConventions.NormalizeRequired(revisionId, "revisionId");
        if (!State.Revisions.TryGetValue(normalized, out var revision))
            throw new InvalidOperationException($"ContentArtifact revision '{normalized}' does not exist.");
        return revision;
    }

    private static void ValidateProvenance(ContentArtifactExecutionProvenance provenance, string scopeId, string teamId)
    {
        if (!string.Equals(provenance.ScopeId, scopeId, StringComparison.Ordinal))
            throw new InvalidOperationException("ContentArtifact revision provenance scope_id must match the artifact scope.");
        if (!string.Equals(provenance.TeamId ?? string.Empty, teamId ?? string.Empty, StringComparison.Ordinal))
            throw new InvalidOperationException("ContentArtifact revision provenance team_id must match the artifact team.");
        var hasRun = !string.IsNullOrWhiteSpace(provenance.RunId);
        var hasService = !string.IsNullOrWhiteSpace(provenance.PublishedServiceId);
        if (hasRun != hasService)
            throw new InvalidOperationException("Execution provenance requires both run_id and published_service_id.");
    }

    private static void ValidateCitation(ContentArtifactCitation citation)
    {
        ContentArtifactConventions.NormalizeRequired(citation.CitationId, "citation.citationId");
        switch (citation.SourceCase)
        {
            case ContentArtifactCitation.SourceOneofCase.ArtifactRevision:
                var reference = citation.ArtifactRevision?.Reference;
                if (reference == null || string.IsNullOrWhiteSpace(reference.ArtifactId))
                    throw new InvalidOperationException("citation artifact_id is required.");
                if (string.IsNullOrWhiteSpace(reference.RevisionId))
                    throw new InvalidOperationException("citation revision_id is required.");
                ValidateSha256(reference.ContentHash, "citation content_hash");
                ContentArtifactConventions.NormalizeRequired(reference.MediaType, "citation.mediaType");
                break;
            case ContentArtifactCitation.SourceOneofCase.ExternalSource:
                var source = citation.ExternalSource;
                if (source == null ||
                    string.IsNullOrWhiteSpace(source.SourceUri) && string.IsNullOrWhiteSpace(source.StableExternalId))
                {
                    throw new InvalidOperationException("citation external source requires source_uri or stable_external_id.");
                }
                break;
            default:
                throw new InvalidOperationException("citation source is required.");
        }

        var locator = citation.Locator;
        if (locator != null &&
            (locator.StartOffset < 0 || locator.EndOffset < 0 ||
             locator.EndOffset > 0 && locator.EndOffset < locator.StartOffset))
        {
            throw new InvalidOperationException("citation locator offsets are invalid.");
        }
    }

    private static void ValidatePrincipal(ContentArtifactPrincipal? principal, string fieldName)
    {
        if (principal == null || string.IsNullOrWhiteSpace(principal.PrincipalId) || string.IsNullOrWhiteSpace(principal.PrincipalKind))
            throw new InvalidOperationException($"{fieldName} principal_id and principal_kind are required.");
    }

    private static bool PrincipalEquals(ContentArtifactPrincipal? left, ContentArtifactPrincipal right) =>
        left != null &&
        string.Equals(left.PrincipalId, right.PrincipalId, StringComparison.Ordinal);

    private static void VerifyLengthAndHash(ContentArtifactRevision revision, ReadOnlySpan<byte> content)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        if (revision.ByteLength != content.Length ||
            !string.Equals(revision.ContentHash, hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ContentArtifact content_hash or byte_length does not match inline content.");
        }
    }

    private static async Task<ContentArtifactBackingContentDescriptor> MeasureAndHashAsync(Stream stream)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        long length = 0;
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory())) > 0)
            {
                hash.AppendData(buffer, 0, read);
                length += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return new ContentArtifactBackingContentDescriptor(
            length,
            Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static void ValidateSha256(string value, string fieldName)
    {
        if (value?.Length != 64)
            throw new InvalidOperationException($"{fieldName} must be a SHA-256 hex digest.");
        try
        {
            _ = Convert.FromHexString(value);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{fieldName} must be a SHA-256 hex digest.", ex);
        }
    }

    private static string HashCreateRequest(CreateContentArtifact command)
    {
        var semantic = command.Clone();
        semantic.RequestedAtUtc = null;
        if (semantic.FirstRevision != null)
        {
            semantic.FirstRevision.CreatedAtUtc = null;
            semantic.FirstRevision.Availability = ContentArtifactRevisionAvailability.Available;
        }
        return Convert.ToHexStringLower(SHA256.HashData(semantic.ToByteArray()));
    }

    private static bool RevisionsEqualForAppend(ContentArtifactRevision left, ContentArtifactRevision right)
    {
        var normalizedLeft = left.Clone();
        var normalizedRight = right.Clone();
        normalizedLeft.CreatedAtUtc = null;
        normalizedRight.CreatedAtUtc = null;
        normalizedLeft.RevisionId = string.Empty;
        normalizedRight.RevisionId = string.Empty;
        normalizedLeft.RevisionNumber = 0;
        normalizedRight.RevisionNumber = 0;
        normalizedLeft.Availability = ContentArtifactRevisionAvailability.Available;
        normalizedRight.Availability = ContentArtifactRevisionAvailability.Available;
        return normalizedLeft.Equals(normalizedRight);
    }

    private static void AdvanceVersion(ContentArtifactState state, Timestamp? updatedAt)
    {
        state.ConcurrencyVersion++;
        state.UpdatedAtUtc = updatedAt?.Clone() ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
    }

    private static Timestamp ResolveRequestedAt(Timestamp? requestedAt) =>
        requestedAt?.Clone() ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
}
