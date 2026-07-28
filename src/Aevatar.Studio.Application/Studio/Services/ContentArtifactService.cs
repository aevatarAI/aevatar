using System.Security.Cryptography;
using Aevatar.ContentArtifacts.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class ContentArtifactService : IContentArtifactService
{
    private readonly IStudioTeamQueryPort _teamQueryPort;
    private readonly IContentArtifactCommandPort _commandPort;
    private readonly IContentArtifactQueryPort _queryPort;
    private readonly IServiceRunQueryPort _serviceRunQueryPort;
    private readonly IServiceRunResultArtifactAttachmentPort _serviceRunResultArtifactAttachmentPort;

    public ContentArtifactService(
        IStudioTeamQueryPort teamQueryPort,
        IContentArtifactCommandPort commandPort,
        IContentArtifactQueryPort queryPort,
        IServiceRunQueryPort serviceRunQueryPort,
        IServiceRunResultArtifactAttachmentPort serviceRunResultArtifactAttachmentPort)
    {
        _teamQueryPort = teamQueryPort ?? throw new ArgumentNullException(nameof(teamQueryPort));
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _serviceRunQueryPort = serviceRunQueryPort ?? throw new ArgumentNullException(nameof(serviceRunQueryPort));
        _serviceRunResultArtifactAttachmentPort = serviceRunResultArtifactAttachmentPort
                                                  ?? throw new ArgumentNullException(nameof(serviceRunResultArtifactAttachmentPort));
    }

    public async Task<ContentArtifactAcceptedReceipt> CreateAsync(
        string scopeId,
        CreateContentArtifactRequest request,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedRequester = NormalizePrincipal(requester);
        var normalizedRequest = NormalizeCreateRequest(normalizedScopeId, request);
        var occupied = await _queryPort.GetByDedupKeyAsync(
            normalizedScopeId,
            normalizedRequest.DedupKey,
            ct);
        if (occupied != null && !PrincipalEquals(occupied.Owner, normalizedRequester))
            throw new ContentArtifactIdentityConflictException(normalizedRequest.DedupKey);
        if (normalizedRequest.TeamId != null)
            await EnsureActiveTeamAsync(normalizedScopeId, normalizedRequest.TeamId, ct);
        await ValidateExecutionProvenanceAsync(normalizedRequest.FirstRevision.Provenance, ct);
        return await _commandPort.CreateAsync(
            normalizedScopeId,
            normalizedRequest,
            normalizedRequester,
            ct);
    }

    public Task<ContentArtifactListResponse> ListAsync(
        string scopeId,
        ContentArtifactQueryRequest query,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default) =>
        _queryPort.ListAsync(
            NormalizeRequired(scopeId, nameof(scopeId)),
            NormalizePrincipal(requester).PrincipalId,
            NormalizeQuery(query),
            ct);

    public async Task<ContentArtifactCurrentStateResponse> GetAsync(
        string scopeId,
        string artifactId,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default) =>
        await GetAuthorizedCurrentAsync(scopeId, artifactId, requester, ct);

    public async Task<ContentArtifactRevisionResponse> GetRevisionAsync(
        string scopeId,
        string artifactId,
        string revisionId,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default)
    {
        var current = await GetAuthorizedCurrentAsync(scopeId, artifactId, requester, ct);
        return FindRevision(current, revisionId);
    }

    public async Task<ContentArtifactRevisionResponse> GetCurrentRevisionAsync(
        string scopeId,
        string artifactId,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default)
    {
        var current = await GetAuthorizedCurrentAsync(scopeId, artifactId, requester, ct);
        if (string.Equals(current.LifecycleStatus, ContentArtifactLifecycleStatusNames.Tombstoned, StringComparison.Ordinal))
        {
            throw new ContentArtifactContentUnavailableException(
                current.ArtifactId,
                string.Empty,
                "artifact is tombstoned");
        }
        if (string.IsNullOrWhiteSpace(current.CurrentRevisionId))
            throw new ContentArtifactNotFoundException(current.ScopeId, current.ArtifactId);
        return FindRevision(current, current.CurrentRevisionId);
    }

    public async Task<ContentArtifactRevisionContentResponse> GetRevisionContentAsync(
        string scopeId,
        string artifactId,
        string revisionId,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default)
    {
        var current = await GetAuthorizedCurrentAsync(scopeId, artifactId, requester, ct);
        var revision = FindRevision(current, revisionId);
        if (string.Equals(current.LifecycleStatus, ContentArtifactLifecycleStatusNames.Tombstoned, StringComparison.Ordinal))
        {
            throw new ContentArtifactContentUnavailableException(
                current.ArtifactId,
                revision.RevisionId,
                "artifact is tombstoned");
        }
        if (revision.Availability is ContentArtifactRevisionAvailabilityNames.Redacted or
            ContentArtifactRevisionAvailabilityNames.RetentionExpired)
        {
            throw new ContentArtifactContentUnavailableException(
                current.ArtifactId,
                revision.RevisionId,
                revision.Availability);
        }
        if (!string.Equals(revision.Availability, ContentArtifactRevisionAvailabilityNames.Available, StringComparison.Ordinal))
            throw new InvalidOperationException("ContentArtifact revision availability is invalid.");
        return await _queryPort.GetRevisionContentAsync(
            current.ScopeId,
            current.ArtifactId,
            revision.RevisionId,
            NormalizePrincipal(requester),
            ct);
    }

    public async Task<ContentArtifactAcceptedReceipt> AppendRevisionAsync(
        string scopeId,
        string artifactId,
        AppendContentArtifactRevisionRequest request,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedArtifactId = NormalizeRequired(artifactId, nameof(artifactId));
        var principal = NormalizePrincipal(requester);
        var current = await _queryPort.GetAsync(normalizedScopeId, normalizedArtifactId, ct);
        if (current != null)
        {
            var canWrite = PrincipalEquals(current.Owner, principal) ||
                           current.WriterPrincipalIds.Contains(principal.PrincipalId, StringComparer.Ordinal);
            if (!string.Equals(current.ScopeId, normalizedScopeId, StringComparison.Ordinal) || !canWrite)
                throw new ContentArtifactNotFoundException(normalizedScopeId, normalizedArtifactId);
        }
        var normalizedRevision = NormalizeRevisionWrite(
            request.Revision,
            normalizedScopeId,
            current?.TeamId ?? NormalizeOptional(request.Revision.Provenance?.TeamId));
        await ValidateExecutionProvenanceAsync(normalizedRevision.Provenance, ct);
        return await _commandPort.AppendRevisionAsync(
            normalizedScopeId,
            normalizedArtifactId,
            request with { Revision = normalizedRevision },
            principal,
            ct);
    }

    public async Task<ContentArtifactAcceptedReceipt> AdvanceCurrentRevisionAsync(
        string scopeId,
        string artifactId,
        AdvanceContentArtifactCurrentRevisionRequest request,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = await GetCurrentForMutationAsync(
            scopeId,
            artifactId,
            request.ExpectedConcurrencyVersion,
            requester,
            ownerOnly: false,
            ct);
        var revisionId = NormalizeRequired(request.RevisionId, nameof(request.RevisionId));
        var revision = FindRevision(current, revisionId);
        if (!string.Equals(revision.Availability, ContentArtifactRevisionAvailabilityNames.Available, StringComparison.Ordinal))
            throw new InvalidOperationException("Only an available ContentArtifact revision can become current.");
        return await _commandPort.AdvanceCurrentRevisionAsync(
            current.ScopeId,
            current.ArtifactId,
            request with { RevisionId = revisionId },
            NormalizePrincipal(requester),
            ct);
    }

    public async Task<ContentArtifactAcceptedReceipt> RedactRevisionAsync(
        string scopeId,
        string artifactId,
        string revisionId,
        RedactContentArtifactRevisionRequest request,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = await GetCurrentForMutationAsync(
            scopeId,
            artifactId,
            request.ExpectedConcurrencyVersion,
            requester,
            ownerOnly: false,
            ct);
        var normalizedRevisionId = FindRevision(current, revisionId).RevisionId;
        return await _commandPort.RedactRevisionAsync(
            current.ScopeId,
            current.ArtifactId,
            normalizedRevisionId,
            request with { Reason = NormalizeRequired(request.Reason, nameof(request.Reason)) },
            NormalizePrincipal(requester),
            ct);
    }

    public async Task<ContentArtifactAcceptedReceipt> ExpireRevisionAsync(
        string scopeId,
        string artifactId,
        string revisionId,
        ExpireContentArtifactRevisionRequest request,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = await GetCurrentForMutationAsync(
            scopeId,
            artifactId,
            request.ExpectedConcurrencyVersion,
            requester,
            ownerOnly: false,
            ct);
        var normalizedRevisionId = FindRevision(current, revisionId).RevisionId;
        return await _commandPort.ExpireRevisionAsync(
            current.ScopeId,
            current.ArtifactId,
            normalizedRevisionId,
            request,
            NormalizePrincipal(requester),
            ct);
    }

    public async Task<ContentArtifactAcceptedReceipt> TombstoneAsync(
        string scopeId,
        string artifactId,
        TombstoneContentArtifactRequest request,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = await GetCurrentForMutationAsync(
            scopeId,
            artifactId,
            request.ExpectedConcurrencyVersion,
            requester,
            ownerOnly: true,
            ct);
        return await _commandPort.TombstoneAsync(
            current.ScopeId,
            current.ArtifactId,
            request with { Reason = NormalizeRequired(request.Reason, nameof(request.Reason)) },
            NormalizePrincipal(requester),
            ct);
    }

    public async Task<ContentArtifactRunAttachmentReceipt> AttachToRunAsync(
        string scopeId,
        AttachContentArtifactsToRunRequest request,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var serviceId = NormalizeRequired(request.PublishedServiceId, nameof(request.PublishedServiceId));
        var runId = NormalizeRequired(request.RunId, nameof(request.RunId));
        var principal = NormalizePrincipal(requester);
        var run = await _serviceRunQueryPort.GetByRunIdAsync(normalizedScopeId, serviceId, runId, ct)
                  ?? throw new InvalidOperationException("Service Run was not found in the requested Scope and published service.");
        if (!string.Equals(run.ScopeId, normalizedScopeId, StringComparison.Ordinal) ||
            !string.Equals(run.ServiceId, serviceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Service Run was not found in the requested Scope and published service.");
        }
        if (run.StateVersion != request.ExpectedRunStateVersion)
        {
            throw new InvalidOperationException(
                $"Service Run state version is {run.StateVersion}, not {request.ExpectedRunStateVersion}.");
        }
        if (request.Artifacts == null || request.Artifacts.Count == 0)
            throw new InvalidOperationException("At least one ContentArtifact reference is required.");

        var references = new List<ContentArtifactReference>(request.Artifacts.Count);
        foreach (var requestedReference in request.Artifacts)
        {
            var current = await GetAuthorizedCurrentAsync(
                normalizedScopeId,
                requestedReference.ArtifactId,
                principal,
                ct);
            var revision = FindRevision(current, requestedReference.RevisionId);
            if (!string.Equals(revision.ContentHash, requestedReference.ContentHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(revision.MediaType, requestedReference.MediaType, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("ContentArtifact reference does not match the exact stored revision.");
            }
            if (!string.Equals(revision.Provenance.ScopeId, normalizedScopeId, StringComparison.Ordinal) ||
                !string.Equals(revision.Provenance.PublishedServiceId, serviceId, StringComparison.Ordinal) ||
                !string.Equals(revision.Provenance.RunId, runId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("ContentArtifact revision provenance does not match the target Service Run.");
            }
            references.Add(new ContentArtifactReference
            {
                ArtifactId = current.ArtifactId,
                RevisionId = revision.RevisionId,
                ContentHash = revision.ContentHash,
                MediaType = revision.MediaType,
            });
        }

        var result = await _serviceRunResultArtifactAttachmentPort.AttachResultArtifactsAsync(
            run.ActorId,
            run.RunId,
            request.ExpectedRunStateVersion,
            references,
            ct);
        return new ContentArtifactRunAttachmentReceipt(
            result.RunId,
            result.CommandId,
            result.CorrelationId,
            ContentArtifactCommandStageNames.DispatchAccepted,
            result.AcceptedAtUtc);
    }

    private async Task<ContentArtifactCurrentStateResponse> GetCurrentForMutationAsync(
        string scopeId,
        string artifactId,
        long expectedConcurrencyVersion,
        ContentArtifactPrincipalContract requester,
        bool ownerOnly,
        CancellationToken ct)
    {
        var current = await GetAuthorizedCurrentAsync(
            scopeId,
            artifactId,
            requester,
            ct);
        var principal = NormalizePrincipal(requester);
        var isOwner = PrincipalEquals(current.Owner, principal);
        if (ownerOnly)
        {
            if (!isOwner)
                throw new ContentArtifactNotFoundException(current.ScopeId, current.ArtifactId);
        }
        else if (!isOwner && !current.WriterPrincipalIds.Contains(principal.PrincipalId, StringComparer.Ordinal))
        {
            throw new ContentArtifactNotFoundException(current.ScopeId, current.ArtifactId);
        }
        if (!string.Equals(current.LifecycleStatus, ContentArtifactLifecycleStatusNames.Active, StringComparison.Ordinal))
            throw new InvalidOperationException("ContentArtifact is tombstoned.");
        if (current.ConcurrencyVersion != expectedConcurrencyVersion)
        {
            throw new InvalidOperationException(
                $"ContentArtifact concurrency version is {current.ConcurrencyVersion}, not {expectedConcurrencyVersion}.");
        }
        return current;
    }

    private async Task<ContentArtifactCurrentStateResponse> GetAuthorizedCurrentAsync(
        string scopeId,
        string artifactId,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedArtifactId = NormalizeRequired(artifactId, nameof(artifactId));
        var principal = NormalizePrincipal(requester);
        var current = await _queryPort.GetAsync(normalizedScopeId, normalizedArtifactId, ct)
                      ?? throw new ContentArtifactNotFoundException(normalizedScopeId, normalizedArtifactId);
        if (!string.Equals(current.ScopeId, normalizedScopeId, StringComparison.Ordinal))
            throw new ContentArtifactNotFoundException(normalizedScopeId, normalizedArtifactId);
        var authorized = PrincipalEquals(current.Owner, principal) ||
                         current.ReaderPrincipalIds.Contains(principal.PrincipalId, StringComparer.Ordinal);
        if (!authorized)
            throw new ContentArtifactNotFoundException(normalizedScopeId, normalizedArtifactId);
        return current;
    }

    private async Task EnsureActiveTeamAsync(string scopeId, string teamId, CancellationToken ct)
    {
        var team = await _teamQueryPort.GetAsync(scopeId, teamId, ct);
        if (team == null ||
            !string.Equals(team.ScopeId, scopeId, StringComparison.Ordinal) ||
            !string.Equals(team.TeamId, teamId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Team was not found in the requested Scope.");
        }
        if (!string.Equals(team.LifecycleStage, TeamLifecycleStageNames.Active, StringComparison.Ordinal))
            throw new InvalidOperationException("ContentArtifacts can only be created for an active Team.");
    }

    private async Task ValidateExecutionProvenanceAsync(
        ContentArtifactExecutionProvenanceContract provenance,
        CancellationToken ct)
    {
        var hasRun = !string.IsNullOrWhiteSpace(provenance.RunId);
        var hasService = !string.IsNullOrWhiteSpace(provenance.PublishedServiceId);
        if (hasRun != hasService)
            throw new InvalidOperationException("Execution provenance requires both Run and published service identities.");
        if (!hasRun)
            return;
        var run = await _serviceRunQueryPort.GetByRunIdAsync(
            provenance.ScopeId,
            provenance.PublishedServiceId!,
            provenance.RunId!,
            ct);
        if (run == null ||
            !string.Equals(run.ScopeId, provenance.ScopeId, StringComparison.Ordinal) ||
            !string.Equals(run.ServiceId, provenance.PublishedServiceId, StringComparison.Ordinal) ||
            !string.Equals(run.RunId, provenance.RunId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Execution provenance does not identify a Service Run in the artifact Scope.");
        }
    }

    private static CreateContentArtifactRequest NormalizeCreateRequest(
        string scopeId,
        CreateContentArtifactRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var teamId = NormalizeOptional(request.TeamId);
        var kind = NormalizeKind(request.Kind);
        return request with
        {
            TeamId = teamId,
            Kind = kind,
            Title = NormalizeRequired(request.Title, nameof(request.Title)),
            Classification = NormalizeRequired(request.Classification, nameof(request.Classification)),
            DedupKey = NormalizeRequired(request.DedupKey, nameof(request.DedupKey)),
            FirstRevision = NormalizeRevisionWrite(request.FirstRevision, scopeId, teamId),
            AccessPolicy = new ContentArtifactAccessPolicyContract(
                NormalizeIds(request.AccessPolicy?.ReaderPrincipalIds),
                NormalizeIds(request.AccessPolicy?.WriterPrincipalIds)),
            RetentionPolicy = request.RetentionPolicy == null
                ? null
                : new ContentArtifactRetentionPolicyContract(
                    NormalizeRequired(request.RetentionPolicy.PolicyId, "retentionPolicy.policyId"),
                    request.RetentionPolicy.ExpiresAtUtc),
            WorkOrderId = NormalizeOptional(request.WorkOrderId),
        };
    }

    private static ContentArtifactRevisionWriteRequest NormalizeRevisionWrite(
        ContentArtifactRevisionWriteRequest request,
        string scopeId,
        string? teamId)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.InlineContent != null && request.BackingObject != null)
            throw new InvalidOperationException("A ContentArtifact revision cannot contain both inline and backing content.");
        if (request.InlineContent == null && request.BackingObject == null)
            throw new InvalidOperationException("A ContentArtifact revision content location is required.");

        var contentHash = NormalizeRequired(request.ContentHash, "revision.contentHash").ToLowerInvariant();
        var byteLength = request.ByteLength;
        byte[]? inlineContent = null;
        if (request.InlineContent != null)
        {
            inlineContent = request.InlineContent.ToArray();
            var verifiedHash = Convert.ToHexStringLower(SHA256.HashData(inlineContent));
            if (byteLength != inlineContent.LongLength ||
                !string.Equals(contentHash, verifiedHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("ContentArtifact inline content hash or byte length is invalid.");
            }
        }

        var provenance = request.Provenance ?? throw new InvalidOperationException("revision.provenance is required.");
        return request with
        {
            DedupKey = NormalizeRequired(request.DedupKey, "revision.dedupKey"),
            MediaType = NormalizeRequired(request.MediaType, "revision.mediaType"),
            ContentHash = contentHash,
            ByteLength = byteLength,
            InlineContent = inlineContent,
            BackingObject = request.BackingObject == null
                ? null
                : new ContentArtifactBackingObjectContract(
                    NormalizeRequired(request.BackingObject.Provider, "revision.backingObject.provider").ToLowerInvariant(),
                    NormalizeRequired(request.BackingObject.ObjectKey, "revision.backingObject.objectKey")),
            ParentRevisionId = NormalizeOptional(request.ParentRevisionId),
            Provenance = new ContentArtifactExecutionProvenanceContract(
                scopeId,
                teamId,
                NormalizeOptional(provenance.MemberId),
                NormalizeOptional(provenance.WorkflowId),
                NormalizeOptional(provenance.PublishedServiceId),
                NormalizeOptional(provenance.RunId),
                NormalizeOptional(provenance.WorkOrderId)),
            Citations = request.Citations?.Select(NormalizeCitation).ToArray(),
            SupersessionReason = NormalizeOptional(request.SupersessionReason),
        };
    }

    private static ContentArtifactCitationContract NormalizeCitation(ContentArtifactCitationContract citation)
    {
        ArgumentNullException.ThrowIfNull(citation);
        return citation with
        {
            CitationId = NormalizeRequired(citation.CitationId, "citation.citationId"),
            Label = NormalizeOptional(citation.Label),
        };
    }

    private static ContentArtifactQueryRequest NormalizeQuery(ContentArtifactQueryRequest? query)
    {
        query ??= new ContentArtifactQueryRequest();
        return query with
        {
            PageToken = NormalizeOptional(query.PageToken),
            TeamId = NormalizeOptional(query.TeamId),
            Kind = query.Kind == null ? null : NormalizeKind(query.Kind),
            LifecycleStatus = NormalizeOptional(query.LifecycleStatus),
            WorkOrderId = NormalizeOptional(query.WorkOrderId),
            RunId = NormalizeOptional(query.RunId),
        };
    }

    private static ContentArtifactRevisionResponse FindRevision(
        ContentArtifactCurrentStateResponse current,
        string revisionId)
    {
        var normalizedRevisionId = NormalizeRequired(revisionId, nameof(revisionId));
        return current.Revisions.FirstOrDefault(revision =>
                   string.Equals(revision.RevisionId, normalizedRevisionId, StringComparison.Ordinal))
               ?? throw new ContentArtifactNotFoundException(
                   current.ScopeId,
                   current.ArtifactId);
    }

    private static ContentArtifactPrincipalContract NormalizePrincipal(ContentArtifactPrincipalContract principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return new ContentArtifactPrincipalContract(
            NormalizeRequired(principal.PrincipalId, "principal.principalId"),
            NormalizeRequired(principal.PrincipalKind, "principal.principalKind"));
    }

    private static bool PrincipalEquals(
        ContentArtifactPrincipalContract left,
        ContentArtifactPrincipalContract right) =>
        string.Equals(left.PrincipalId, right.PrincipalId, StringComparison.Ordinal);

    private static IReadOnlyList<string> NormalizeIds(IReadOnlyList<string>? values) =>
        (values ?? [])
            .Select(value => NormalizeRequired(value, "principalId"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string NormalizeKind(string value) => value.Trim().ToLowerInvariant() switch
    {
        "text" => "text",
        "markdown" => "markdown",
        "structured_document" => "structured_document",
        "other_content" => "other_content",
        _ => throw new InvalidOperationException($"Unsupported ContentArtifact kind '{value}'."),
    };

    private static string NormalizeRequired(string? value, string parameterName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        return normalized;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
