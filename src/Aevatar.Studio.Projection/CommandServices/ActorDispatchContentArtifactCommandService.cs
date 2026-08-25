using System.Security.Cryptography;
using Aevatar.ContentArtifacts.Abstractions;
using Aevatar.GAgents.ContentArtifacts;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.CommandServices;

internal sealed class ActorDispatchContentArtifactCommandService : IContentArtifactCommandPort
{
    private const string PublisherId = "aevatar.studio.projection.content-artifact";

    private readonly IStudioActorBootstrap _bootstrap;
    private readonly StudioProjectionActorCommandDispatch _commandDispatch;

    public ActorDispatchContentArtifactCommandService(
        IStudioActorBootstrap bootstrap,
        StudioProjectionActorCommandDispatch commandDispatch)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _commandDispatch = commandDispatch ?? throw new ArgumentNullException(nameof(commandDispatch));
    }

    public Task<ContentArtifactAcceptedReceipt> CreateAsync(
        string scopeId,
        CreateContentArtifactRequest request,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default)
    {
        var artifactId = ContentArtifactConventions.BuildArtifactId(scopeId, request.DedupKey);
        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        var command = new CreateContentArtifact
        {
            ArtifactId = artifactId,
            DedupKey = request.DedupKey,
            ScopeId = scopeId,
            TeamId = request.TeamId ?? string.Empty,
            Kind = ToKind(request.Kind),
            Title = request.Title,
            Classification = request.Classification,
            AccessPolicy = ToAccessPolicy(request.AccessPolicy, requester),
            WorkOrderId = request.WorkOrderId ?? string.Empty,
            FirstRevision = ToRevision(
                artifactId,
                revisionNumber: 1,
                request.FirstRevision,
                now),
            ExpectedConcurrencyVersion = 0,
            RequestedAtUtc = now,
        };
        if (ToRetentionPolicy(request.RetentionPolicy) is { } retentionPolicy)
            command.RetentionPolicy = retentionPolicy;
        foreach (var (key, value) in request.Labels ?? new Dictionary<string, string>(StringComparer.Ordinal))
            command.Labels.Add(key, value);
        return DispatchAsync(scopeId, artifactId, command, "create", 0, ct);
    }

    public Task<ContentArtifactAcceptedReceipt> AppendRevisionAsync(
        string scopeId,
        string artifactId,
        AppendContentArtifactRevisionRequest request,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default)
    {
        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        return DispatchAsync(
            scopeId,
            artifactId,
            new AppendContentArtifactRevision
            {
                ArtifactId = artifactId,
                RequestedBy = ToPrincipal(requester),
                Revision = ToRevision(artifactId, revisionNumber: 0, request.Revision, now),
                RequestedAtUtc = now,
            },
            "append",
            0,
            ct);
    }

    public Task<ContentArtifactAcceptedReceipt> AdvanceCurrentRevisionAsync(
        string scopeId,
        string artifactId,
        AdvanceContentArtifactCurrentRevisionRequest request,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default) =>
        DispatchAsync(
            scopeId,
            artifactId,
            new AdvanceContentArtifactCurrentRevision
            {
                ArtifactId = artifactId,
                ExpectedConcurrencyVersion = request.ExpectedConcurrencyVersion,
                RequestedBy = ToPrincipal(requester),
                RevisionId = request.RevisionId,
                RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            "advance",
            request.ExpectedConcurrencyVersion,
            ct);

    public Task<ContentArtifactAcceptedReceipt> RedactRevisionAsync(
        string scopeId,
        string artifactId,
        string revisionId,
        RedactContentArtifactRevisionRequest request,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default) =>
        DispatchAsync(
            scopeId,
            artifactId,
            new RedactContentArtifactRevision
            {
                ArtifactId = artifactId,
                ExpectedConcurrencyVersion = request.ExpectedConcurrencyVersion,
                RequestedBy = ToPrincipal(requester),
                RevisionId = revisionId,
                Reason = request.Reason,
                RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            "redact",
            request.ExpectedConcurrencyVersion,
            ct);

    public Task<ContentArtifactAcceptedReceipt> ExpireRevisionAsync(
        string scopeId,
        string artifactId,
        string revisionId,
        ExpireContentArtifactRevisionRequest request,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default) =>
        DispatchAsync(
            scopeId,
            artifactId,
            new ExpireContentArtifactRevision
            {
                ArtifactId = artifactId,
                ExpectedConcurrencyVersion = request.ExpectedConcurrencyVersion,
                RequestedBy = ToPrincipal(requester),
                RevisionId = revisionId,
                RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            "expire",
            request.ExpectedConcurrencyVersion,
            ct);

    public Task<ContentArtifactAcceptedReceipt> TombstoneAsync(
        string scopeId,
        string artifactId,
        TombstoneContentArtifactRequest request,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default) =>
        DispatchAsync(
            scopeId,
            artifactId,
            new TombstoneContentArtifact
            {
                ArtifactId = artifactId,
                ExpectedConcurrencyVersion = request.ExpectedConcurrencyVersion,
                RequestedBy = ToPrincipal(requester),
                Reason = request.Reason,
                RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            "tombstone",
            request.ExpectedConcurrencyVersion,
            ct);

    private async Task<ContentArtifactAcceptedReceipt> DispatchAsync(
        string scopeId,
        string artifactId,
        IMessage payload,
        string operation,
        long expectedConcurrencyVersion,
        CancellationToken ct)
    {
        var normalizedScopeId = ContentArtifactConventions.NormalizeScopeId(scopeId);
        var normalizedArtifactId = ContentArtifactConventions.NormalizeArtifactId(artifactId);
        var commandId = BuildCommandId(operation, normalizedArtifactId, expectedConcurrencyVersion, payload);
        var actorId = ContentArtifactConventions.BuildActorId(normalizedScopeId, normalizedArtifactId);
        var actor = await _bootstrap.EnsureAsync<ContentArtifactGAgent>(actorId, ct);
        var receipt = await _commandDispatch.DispatchAsync(
            actor,
            payload,
            PublisherId,
            commandId,
            commandId,
            commandId,
            ct);
        return new ContentArtifactAcceptedReceipt(
            normalizedArtifactId,
            receipt.CommandId,
            receipt.CorrelationId,
            ContentArtifactCommandStageNames.DispatchAccepted,
            receipt.AckedAt);
    }

    private static ContentArtifactRevision ToRevision(
        string artifactId,
        long revisionNumber,
        ContentArtifactRevisionWriteRequest request,
        Timestamp createdAt)
    {
        var revision = new ContentArtifactRevision
        {
            RevisionId = revisionNumber > 0
                ? ContentArtifactConventions.BuildRevisionId(artifactId, revisionNumber)
                : string.Empty,
            RevisionNumber = revisionNumber,
            DedupKey = request.DedupKey,
            ParentRevisionId = request.ParentRevisionId ?? string.Empty,
            MediaType = request.MediaType,
            ByteLength = request.ByteLength,
            ContentHash = request.ContentHash.ToLowerInvariant(),
            Content = ToContent(request),
            Provenance = ToProvenance(request.Provenance),
            CreatedAtUtc = createdAt.Clone(),
            Availability = ContentArtifactRevisionAvailability.Available,
            SupersessionReason = request.SupersessionReason ?? string.Empty,
        };
        revision.Citations.Add((request.Citations ?? []).Select(ToCitation));
        return revision;
    }

    private static ContentArtifactRevisionContent ToContent(ContentArtifactRevisionWriteRequest request)
    {
        if (request.InlineContent != null && request.BackingObject != null)
            throw new InvalidOperationException("A ContentArtifact revision cannot contain both inline and backing content.");
        if (request.InlineContent != null)
            return new ContentArtifactRevisionContent { InlineContent = ByteString.CopyFrom(request.InlineContent) };
        if (request.BackingObject != null)
        {
            return new ContentArtifactRevisionContent
            {
                BackingObject = new ContentArtifactBackingObjectReference
                {
                    Provider = request.BackingObject.Provider,
                    ObjectKey = request.BackingObject.ObjectKey,
                },
            };
        }
        throw new InvalidOperationException("A ContentArtifact revision content location is required.");
    }

    private static ContentArtifactExecutionProvenance ToProvenance(
        ContentArtifactExecutionProvenanceContract provenance) =>
        new()
        {
            ScopeId = provenance.ScopeId,
            TeamId = provenance.TeamId ?? string.Empty,
            MemberId = provenance.MemberId ?? string.Empty,
            WorkflowId = provenance.WorkflowId ?? string.Empty,
            PublishedServiceId = provenance.PublishedServiceId ?? string.Empty,
            RunId = provenance.RunId ?? string.Empty,
            WorkOrderId = provenance.WorkOrderId ?? string.Empty,
        };

    private static ContentArtifactCitation ToCitation(ContentArtifactCitationContract citation)
    {
        var result = new ContentArtifactCitation
        {
            CitationId = citation.CitationId,
            Label = citation.Label ?? string.Empty,
        };
        if (citation.Locator != null)
        {
            result.Locator = new ContentArtifactCitationLocator
            {
                Section = citation.Locator.Section ?? string.Empty,
                StartOffset = citation.Locator.StartOffset ?? 0,
                EndOffset = citation.Locator.EndOffset ?? 0,
                Selector = citation.Locator.Selector ?? string.Empty,
            };
        }
        if (citation.ArtifactRevision != null)
        {
            result.ArtifactRevision = new ContentArtifactRevisionCitationSource
            {
                Reference = new ContentArtifactReference
                {
                    ArtifactId = citation.ArtifactRevision.ArtifactId,
                    RevisionId = citation.ArtifactRevision.RevisionId,
                    ContentHash = citation.ArtifactRevision.ContentHash,
                    MediaType = citation.ArtifactRevision.MediaType,
                },
            };
        }
        if (citation.ExternalSource != null)
        {
            var externalSource = new ContentArtifactExternalCitationSource
            {
                SourceUri = citation.ExternalSource.SourceUri ?? string.Empty,
                StableExternalId = citation.ExternalSource.StableExternalId ?? string.Empty,
                DocumentRevision = citation.ExternalSource.DocumentRevision ?? string.Empty,
                ContentHash = citation.ExternalSource.ContentHash ?? string.Empty,
            };
            if (ToTimestamp(citation.ExternalSource.PublishedAtUtc) is { } publishedAtUtc)
                externalSource.PublishedAtUtc = publishedAtUtc;
            if (ToTimestamp(citation.ExternalSource.FetchedAtUtc) is { } fetchedAtUtc)
                externalSource.FetchedAtUtc = fetchedAtUtc;
            result.ExternalSource = externalSource;
        }
        return result;
    }

    private static ContentArtifactAccessPolicy ToAccessPolicy(
        ContentArtifactAccessPolicyContract? policy,
        ContentArtifactPrincipalContract owner)
    {
        var result = new ContentArtifactAccessPolicy { Owner = ToPrincipal(owner) };
        result.ReaderPrincipalIds.Add(policy?.ReaderPrincipalIds ?? []);
        result.WriterPrincipalIds.Add(policy?.WriterPrincipalIds ?? []);
        return result;
    }

    private static ContentArtifactRetentionPolicy? ToRetentionPolicy(ContentArtifactRetentionPolicyContract? policy) =>
        policy == null
            ? null
            : new ContentArtifactRetentionPolicy
            {
                PolicyId = policy.PolicyId,
                ExpiresAtUtc = ToTimestamp(policy.ExpiresAtUtc),
            };

    private static ContentArtifactPrincipal ToPrincipal(ContentArtifactPrincipalContract principal) =>
        new()
        {
            PrincipalId = principal.PrincipalId,
            PrincipalKind = principal.PrincipalKind,
        };

    private static ContentArtifactKind ToKind(string value) => value.Trim().ToLowerInvariant() switch
    {
        "text" => ContentArtifactKind.Text,
        "markdown" => ContentArtifactKind.Markdown,
        "structured_document" => ContentArtifactKind.StructuredDocument,
        "other_content" => ContentArtifactKind.OtherContent,
        _ => throw new InvalidOperationException($"Unsupported ContentArtifact kind '{value}'."),
    };

    private static Timestamp? ToTimestamp(DateTimeOffset? value) =>
        value.HasValue ? Timestamp.FromDateTimeOffset(value.Value) : null;

    private static string BuildCommandId(
        string operation,
        string artifactId,
        long expectedConcurrencyVersion,
        IMessage payload)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(BuildCanonicalCommandBytes(payload)));
        return $"content-artifact-{operation}-{artifactId}-v{expectedConcurrencyVersion}-{digest}";
    }

    private static byte[] BuildCanonicalCommandBytes(IMessage payload)
    {
        switch (payload)
        {
            case CreateContentArtifact command:
            {
                var canonical = command.Clone();
                canonical.RequestedAtUtc = null;
                canonical.FirstRevision.CreatedAtUtc = null;
                return canonical.ToByteArray();
            }
            case AppendContentArtifactRevision command:
            {
                var canonical = command.Clone();
                canonical.RequestedAtUtc = null;
                canonical.Revision.CreatedAtUtc = null;
                return canonical.ToByteArray();
            }
            case AdvanceContentArtifactCurrentRevision command:
            {
                var canonical = command.Clone();
                canonical.RequestedAtUtc = null;
                return canonical.ToByteArray();
            }
            case RedactContentArtifactRevision command:
            {
                var canonical = command.Clone();
                canonical.RequestedAtUtc = null;
                return canonical.ToByteArray();
            }
            case ExpireContentArtifactRevision command:
            {
                var canonical = command.Clone();
                canonical.RequestedAtUtc = null;
                return canonical.ToByteArray();
            }
            case TombstoneContentArtifact command:
            {
                var canonical = command.Clone();
                canonical.RequestedAtUtc = null;
                return canonical.ToByteArray();
            }
            default:
                throw new InvalidOperationException(
                    $"Unsupported ContentArtifact command payload '{payload.Descriptor.FullName}'.");
        }
    }
}
