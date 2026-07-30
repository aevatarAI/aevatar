using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Application.AgentProfiles;

public sealed class AgentProfileApplicationService
{
    public const int MaximumPageSize = 100;

    private readonly IAgentProfileCatalogQueryPort _catalogQuery;
    private readonly IAgentProfileManagementQueryPort _managementQuery;
    private readonly IAgentProfileExecutionQueryPort _executionQuery;
    private readonly IAgentProfileActorPort _actorPort;
    private readonly IAgentProfileSkillSealer _skillSealer;
    private readonly TimeProvider _timeProvider;

    public AgentProfileApplicationService(
        IAgentProfileCatalogQueryPort catalogQuery,
        IAgentProfileManagementQueryPort managementQuery,
        IAgentProfileExecutionQueryPort executionQuery,
        IAgentProfileActorPort actorPort,
        IAgentProfileSkillSealer skillSealer,
        TimeProvider timeProvider)
    {
        _catalogQuery = catalogQuery ?? throw new ArgumentNullException(nameof(catalogQuery));
        _managementQuery = managementQuery ?? throw new ArgumentNullException(nameof(managementQuery));
        _executionQuery = executionQuery ?? throw new ArgumentNullException(nameof(executionQuery));
        _actorPort = actorPort ?? throw new ArgumentNullException(nameof(actorPort));
        _skillSealer = skillSealer ?? throw new ArgumentNullException(nameof(skillSealer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<AgentProfileListPage> ListAsync(
        AgentProfileOwner owner,
        string? cursor,
        int pageSize,
        CancellationToken ct = default) =>
        await ListCoreAsync(owner, cursor, pageSize, publishedOnly: false, ct);

    public async Task<AgentProfileListPage> ListPublishedAsync(
        AgentProfileOwner owner,
        string? cursor,
        int pageSize,
        CancellationToken ct = default) =>
        await ListCoreAsync(owner, cursor, pageSize, publishedOnly: true, ct);

    public async Task<AgentProfileCatalogEntry?> GetPublishedSummaryAsync(
        AgentProfileOwner owner,
        string profileSlug,
        CancellationToken ct = default)
    {
        ValidateOwner(owner);
        var slug = NormalizeSlug(profileSlug);
        var catalog = await _catalogQuery.GetAsync(owner, ct);
        if (catalog is null)
            return null;
        EnsureOwner(catalog.Owner, owner, "Agent Profile catalog owner does not match the requested owner.");
        return catalog.Profiles
            .SingleOrDefault(entry =>
                string.Equals(entry.ProfileSlug, slug, StringComparison.Ordinal) &&
                IsPublished(entry))
            ?.Clone();
    }

    private async Task<AgentProfileListPage> ListCoreAsync(
        AgentProfileOwner owner,
        string? cursor,
        int pageSize,
        bool publishedOnly,
        CancellationToken ct)
    {
        ValidateOwner(owner);
        if (pageSize is < 1 or > MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(pageSize), $"Page size must be between 1 and {MaximumPageSize}.");

        var offset = DecodeCursor(cursor);
        var catalog = await _catalogQuery.GetAsync(owner, ct);
        if (catalog is null)
            return new AgentProfileListPage([], null, 0, DateTimeOffset.MinValue);
        EnsureOwner(catalog.Owner, owner, "Agent Profile catalog owner does not match the requested owner.");

        var candidates = publishedOnly
            ? catalog.Profiles.Where(IsPublished)
            : catalog.Profiles;
        var ordered = candidates
            .OrderBy(static entry => entry.ProfileSlug, StringComparer.Ordinal)
            .ThenBy(static entry => entry.ProfileId, StringComparer.Ordinal)
            .ToArray();
        if (offset > ordered.Length)
            throw new AgentProfileInvalidCursorException("Agent Profile cursor is outside the current catalog.");

        var items = ordered.Skip(offset).Take(pageSize).Select(static entry => entry.Clone()).ToArray();
        var nextOffset = checked(offset + items.Length);
        return new AgentProfileListPage(
            items,
            nextOffset < ordered.Length ? EncodeCursor(nextOffset) : null,
            catalog.AuthorityStateVersion,
            catalog.UpdatedAt,
            catalog.LastMutation?.Clone());
    }

    public async Task<AgentProfileManagementDetail?> GetAsync(
        AgentProfileOwner owner,
        string profileSlug,
        CancellationToken ct = default)
    {
        var resolved = await ResolveIdentityAsync(owner, profileSlug, requirePublished: false, ct);
        if (resolved is null)
            return null;

        var snapshot = await _managementQuery.GetAsync(resolved.Value.Identity, ct);
        if (snapshot is null)
            throw new AgentProfileUnavailableException("Agent Profile management read model is not available yet.");
        if (!snapshot.Identity.Equals(resolved.Value.Identity))
            throw new AgentProfileIntegrityException("Agent Profile management identity does not match the catalog authority.");

        var executionAvailable = false;
        if (snapshot.PublishedRevision > 0 &&
            snapshot.PublishedSnapshotSha256.Length == 32 &&
            resolved.Value.Entry.PublishedRevision == snapshot.PublishedRevision &&
            resolved.Value.Entry.SnapshotSha256.Equals(snapshot.PublishedSnapshotSha256))
        {
            var target = new AgentProfileBindingTarget
            {
                Owner = snapshot.Identity.Owner.Clone(),
                ProfileId = snapshot.Identity.ProfileId,
                PublishedRevision = snapshot.PublishedRevision,
                SnapshotSha256 = snapshot.PublishedSnapshotSha256,
            };
            var execution = await _executionQuery.GetAsync(target, ct);
            if (execution is not null)
            {
                EnsureExecutionMatches(execution, target);
                executionAvailable = true;
            }
        }

        return new AgentProfileManagementDetail(
            snapshot,
            StrongProfileETag(snapshot.AuthorityStateVersion),
            executionAvailable);
    }

    public async Task<AgentProfileAcceptedReceipt> CreateAsync(
        AgentProfileCreateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateOwner(request.Owner);
        var slug = NormalizeSlug(request.ProfileSlug);
        var key = NormalizeRequired(request.IdempotencyKey, nameof(request.IdempotencyKey));
        var profileId = AgentProfileDeterminism.CreateProfileId(request.Owner, key);
        var operation = BuildOperation(
            request.Owner,
            "create",
            key,
            request.AuditSubject,
            $"{OwnerKey(request.Owner)}\n{profileId}\n{slug}",
            profileId);
        var command = new CreateAgentProfileCommand
        {
            Owner = request.Owner.Clone(),
            ProfileId = profileId,
            ProfileSlug = slug,
            Operation = operation,
        };

        var admission = await _actorPort.DispatchCreateAsync(command, ct);
        return Receipt(admission, operation, profileId);
    }

    public async Task<AgentProfileAcceptedReceipt> UpdateDraftAsync(
        AgentProfileDraftUpdateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Draft);
        if (request.ExpectedAuthorityStateVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(request.ExpectedAuthorityStateVersion));

        var resolved = await ResolveRequiredIdentityAsync(request.Owner, request.ProfileSlug, false, ct);
        var key = NormalizeRequired(request.IdempotencyKey, nameof(request.IdempotencyKey));
        var normalizedDraft = AgentProfileDeterminism.NormalizeDraft(request.Draft);
        var operation = BuildOperation(
            request.Owner,
            "update-draft",
            key,
            request.AuditSubject,
            $"{resolved.Identity.ProfileId}\n{request.ExpectedAuthorityStateVersion}\n{AgentProfileDeterminism.ComputeDraftDigest(normalizedDraft).ToBase64()}",
            resolved.Identity.ProfileId);
        var command = new UpdateAgentProfileDraftCommand
        {
            Identity = resolved.Identity.Clone(),
            Draft = normalizedDraft,
            ExpectedAuthorityStateVersion = request.ExpectedAuthorityStateVersion,
            Operation = operation,
        };
        var admission = await _actorPort.DispatchUpdateDraftAsync(resolved.Entry.ProfileActorId, command, ct);
        return Receipt(admission, operation, resolved.Identity.ProfileId);
    }

    public async Task<AgentProfileAcceptedReceipt> PublishAsync(
        AgentProfilePublishRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExpectedAuthorityStateVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(request.ExpectedAuthorityStateVersion));

        var resolved = await ResolveRequiredIdentityAsync(request.Owner, request.ProfileSlug, false, ct);
        var management = await GetVerifiedManagementDraftAsync(resolved, ct);

        var publishedAt = _timeProvider.GetUtcNow();
        var nextPublishedRevision = checked(management.PublishedRevision + 1);
        var sealing = await _skillSealer.ResolveAndSealAsync(
            resolved.Identity,
            management.Draft!,
            new AgentProfileSealingContext(
                management.DraftRevision,
                nextPublishedRevision,
                publishedAt,
                request.NyxIdAccessToken),
            ct);
        if (!sealing.IsSuccess)
            throw new AgentProfileSealingException(sealing.Diagnostics);

        var key = NormalizeRequired(request.IdempotencyKey, nameof(request.IdempotencyKey));
        var operation = BuildOperation(
            request.Owner,
            "publish",
            key,
            request.AuditSubject,
            $"{resolved.Identity.ProfileId}\n{request.ExpectedAuthorityStateVersion}\n" +
            $"{management.DraftRevision}\n{management.DraftSha256.ToBase64()}\n" +
            $"{nextPublishedRevision}\n{sealing.Snapshot!.SnapshotSha256.ToBase64()}",
            resolved.Identity.ProfileId);
        var command = new PublishAgentProfileCommand
        {
            Identity = resolved.Identity.Clone(),
            Snapshot = sealing.Snapshot.Clone(),
            SourceDraftSha256 = management.DraftSha256,
            ExpectedAuthorityStateVersion = request.ExpectedAuthorityStateVersion,
            Operation = operation,
        };
        var admission = await _actorPort.DispatchPublishAsync(
            resolved.Entry.ProfileActorId,
            command,
            ct);
        return Receipt(admission, operation, resolved.Identity.ProfileId);
    }

    public async Task<AgentProfileValidationResult> ValidateAsync(
        AgentProfileOwner owner,
        string profileSlug,
        string? nyxIdAccessToken,
        CancellationToken ct = default)
    {
        var resolved = await ResolveRequiredIdentityAsync(owner, profileSlug, false, ct);
        var management = await GetVerifiedManagementDraftAsync(resolved, ct);
        var sealing = await _skillSealer.ResolveAndSealAsync(
            resolved.Identity,
            management.Draft!,
            new AgentProfileSealingContext(
                management.DraftRevision,
                checked(management.PublishedRevision + 1),
                _timeProvider.GetUtcNow(),
                nyxIdAccessToken),
            ct);

        return new AgentProfileValidationResult(
            sealing.IsSuccess,
            management.DraftRevision,
            management.DraftSha256,
            sealing.Diagnostics.ToArray());
    }

    public async Task<AgentProfileAcceptedReceipt> SetBindingAsync(
        AgentProfileBindingUpdateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Reference);
        ValidateOwner(request.Owner);
        if (!AgentProfilePolicies.IsSupportedAgentKind(request.AgentKind))
            throw new ArgumentException("Unsupported Agent Profile agent kind.", nameof(request.AgentKind));
        if (request.ExpectedAuthorityStateVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(request.ExpectedAuthorityStateVersion));

        var targetOwner = ResolveReferenceOwner(request.Owner, request.Reference.OwnerKind);
        var resolved = await ResolveRequiredIdentityAsync(targetOwner, request.Reference.ProfileSlug, true, ct);
        var target = new AgentProfileBindingTarget
        {
            Owner = targetOwner.Clone(),
            ProfileId = resolved.Entry.ProfileId,
            PublishedRevision = resolved.Entry.PublishedRevision,
            SnapshotSha256 = resolved.Entry.SnapshotSha256,
        };
        var execution = await _executionQuery.GetAsync(target, ct);
        if (execution is null)
            throw new AgentProfileUnavailableException("Agent Profile protected execution read model is not available yet.");
        EnsureExecutionMatches(execution, target);

        var key = NormalizeRequired(request.IdempotencyKey, nameof(request.IdempotencyKey));
        var operation = BuildOperation(
            request.Owner,
            "set-binding",
            key,
            request.AuditSubject,
            $"{OwnerKey(request.Owner)}\n{request.AgentKind}\n{OwnerKey(targetOwner)}\n{target.ProfileId}\n{target.PublishedRevision}\n{target.SnapshotSha256.ToBase64()}\n{request.Enabled}\n{request.CohortBasisPoints}",
            target.ProfileId);
        var command = new SetAgentProfileDefaultBindingCommand
        {
            Owner = request.Owner.Clone(),
            AgentKind = request.AgentKind,
            Target = target,
            ExpectedAuthorityStateVersion = request.ExpectedAuthorityStateVersion,
            Operation = operation,
        };
        if (request.Owner.OwnerCase == AgentProfileOwner.OwnerOneofCase.Scope)
        {
            command.Scope = new AgentProfileScopeBindingAdmission();
        }
        else
        {
            if (request.CohortBasisPoints is < 0 or > AgentProfilePolicies.FullCohortBasisPoints)
                throw new ArgumentOutOfRangeException(nameof(request.CohortBasisPoints));
            command.System = new AgentProfileSystemBindingAdmission
            {
                Enabled = request.Enabled,
                CohortBasisPoints = request.CohortBasisPoints,
            };
        }

        var admission = await _actorPort.DispatchSetDefaultBindingAsync(command, ct);
        return Receipt(admission, operation, target.ProfileId);
    }

    public async Task<AgentProfileBindingDetail> GetBindingAsync(
        AgentProfileOwner owner,
        string agentKind,
        CancellationToken ct = default)
    {
        ValidateOwner(owner);
        if (!AgentProfilePolicies.IsSupportedAgentKind(agentKind))
            throw new ArgumentException("Unsupported Agent Profile agent kind.", nameof(agentKind));

        var catalog = await _catalogQuery.GetAsync(owner, ct);
        if (catalog is null)
        {
            return new AgentProfileBindingDetail(
                null,
                0,
                StrongBindingETag(0),
                DateTimeOffset.MinValue,
                null);
        }
        EnsureOwner(catalog.Owner, owner, "Agent Profile binding catalog owner does not match the requested owner.");
        var binding = catalog.DefaultBindings.SingleOrDefault(candidate =>
            string.Equals(candidate.AgentKind, agentKind, StringComparison.Ordinal));
        return new AgentProfileBindingDetail(
            binding?.Clone(),
            catalog.AuthorityStateVersion,
            StrongBindingETag(catalog.AuthorityStateVersion),
            catalog.UpdatedAt,
            catalog.LastMutation?.Clone());
    }

    public async Task<AgentProfileAcceptedReceipt> ClearBindingAsync(
        AgentProfileBindingClearRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateOwner(request.Owner);
        if (!AgentProfilePolicies.IsSupportedAgentKind(request.AgentKind))
            throw new ArgumentException("Unsupported Agent Profile agent kind.", nameof(request.AgentKind));
        if (request.ExpectedAuthorityStateVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(request.ExpectedAuthorityStateVersion));

        var key = NormalizeRequired(request.IdempotencyKey, nameof(request.IdempotencyKey));
        var operation = BuildOperation(
            request.Owner,
            "clear-binding",
            key,
            request.AuditSubject,
            $"{OwnerKey(request.Owner)}\n{request.AgentKind}\n{request.ExpectedAuthorityStateVersion}",
            OwnerKey(request.Owner));
        var command = new ClearAgentProfileDefaultBindingCommand
        {
            Owner = request.Owner.Clone(),
            AgentKind = request.AgentKind,
            ExpectedAuthorityStateVersion = request.ExpectedAuthorityStateVersion,
            Operation = operation,
        };
        var admission = await _actorPort.DispatchClearDefaultBindingAsync(command, ct);
        return Receipt(admission, operation, string.Empty);
    }

    public static string StrongProfileETag(long authorityStateVersion) =>
        $"\"agent-profile-v{authorityStateVersion.ToString(CultureInfo.InvariantCulture)}\"";

    public static string StrongBindingETag(long authorityStateVersion) =>
        $"\"agent-profile-binding-v{authorityStateVersion.ToString(CultureInfo.InvariantCulture)}\"";

    private async Task<ResolvedIdentity> ResolveRequiredIdentityAsync(
        AgentProfileOwner owner,
        string profileSlug,
        bool requirePublished,
        CancellationToken ct) =>
        await ResolveIdentityAsync(owner, profileSlug, requirePublished, ct) ??
        throw new AgentProfileNotFoundException("Agent Profile was not found for the requested owner and slug.");

    private async Task<AgentProfileManagementSnapshot> GetVerifiedManagementDraftAsync(
        ResolvedIdentity resolved,
        CancellationToken ct)
    {
        var management = await _managementQuery.GetAsync(resolved.Identity, ct);
        if (management?.Draft is null || management.DraftRevision <= 0)
            throw new AgentProfileUnavailableException("Agent Profile has no materialized draft to validate.");
        if (!management.Identity.Equals(resolved.Identity) ||
            !string.Equals(management.ActorId, resolved.Entry.ProfileActorId, StringComparison.Ordinal))
        {
            throw new AgentProfileIntegrityException(
                "Agent Profile management authority does not match the catalog target.");
        }

        var computedDraftSha256 = AgentProfileDeterminism.ComputeDraftDigest(management.Draft);
        if (management.DraftSha256.Length != 32 ||
            !CryptographicOperations.FixedTimeEquals(
                computedDraftSha256.Span,
                management.DraftSha256.Span))
        {
            throw new AgentProfileIntegrityException(
                "Agent Profile management draft digest does not match its materialized draft.");
        }

        return management;
    }

    private async Task<ResolvedIdentity?> ResolveIdentityAsync(
        AgentProfileOwner owner,
        string profileSlug,
        bool requirePublished,
        CancellationToken ct)
    {
        ValidateOwner(owner);
        var slug = NormalizeSlug(profileSlug);
        var catalog = await _catalogQuery.GetAsync(owner, ct);
        if (catalog is null)
            return null;
        EnsureOwner(catalog.Owner, owner, "Agent Profile catalog owner does not match the requested owner.");
        var entry = catalog.Profiles.SingleOrDefault(candidate =>
            string.Equals(candidate.ProfileSlug, slug, StringComparison.Ordinal));
        if (entry is null)
            return null;
        if (entry.Status != AgentProfileProvisioningStatus.Active || string.IsNullOrWhiteSpace(entry.ProfileActorId))
            throw new AgentProfileUnavailableException("Agent Profile provisioning has not produced an active authority yet.");
        if (requirePublished && (entry.PublishedRevision <= 0 || entry.SnapshotSha256.Length != 32))
            throw new AgentProfileUnavailableException("Agent Profile has no protected published snapshot.");

        return new ResolvedIdentity(
            new AgentProfileIdentity
            {
                Owner = owner.Clone(),
                ProfileId = entry.ProfileId,
                ProfileSlug = entry.ProfileSlug,
            },
            entry.Clone());
    }

    private AgentProfileOperationFact BuildOperation(
        AgentProfileOwner owner,
        string kind,
        string idempotencyKey,
        string auditSubject,
        string semanticInput,
        string correlationIdentity)
    {
        var operationId = AgentProfileDeterminism.CreateOperationId(owner, $"{kind}:{idempotencyKey}");
        return new AgentProfileOperationFact
        {
            OperationId = operationId,
            CommandId = operationId,
            CorrelationId = $"agent-profile:{correlationIdentity}",
            InputSha256 = ByteString.CopyFrom(SHA256.HashData(Encoding.UTF8.GetBytes(semanticInput))),
            RequestedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            AuditSubject = NormalizeRequired(auditSubject, nameof(auditSubject)),
        };
    }

    private static AgentProfileAcceptedReceipt Receipt(
        DispatchAdmission admission,
        AgentProfileOperationFact operation,
        string profileId) =>
        new(
            admission.Accepted,
            operation.OperationId,
            profileId,
            admission.CommandId,
            admission.CorrelationId,
            admission.ActorId,
            admission.AckedAt);

    private static AgentProfileOwner ResolveReferenceOwner(
        AgentProfileOwner bindingOwner,
        AgentProfileReferenceOwnerKind referenceOwnerKind) =>
        referenceOwnerKind switch
        {
            AgentProfileReferenceOwnerKind.Caller
                when bindingOwner.OwnerCase == AgentProfileOwner.OwnerOneofCase.Scope => bindingOwner.Clone(),
            AgentProfileReferenceOwnerKind.System => AgentProfileOwners.ForSystem(),
            _ => throw new ArgumentException("Agent Profile reference owner is not allowed for this binding owner."),
        };

    private static void EnsureExecutionMatches(
        AgentProfileExecutionSnapshot execution,
        AgentProfileBindingTarget target)
    {
        if (!execution.Identity.Owner.Equals(target.Owner) ||
            !string.Equals(execution.Identity.ProfileId, target.ProfileId, StringComparison.Ordinal) ||
            execution.Snapshot.PublishedRevision != target.PublishedRevision ||
            execution.Snapshot.SnapshotSha256.Length != 32 ||
            !CryptographicOperations.FixedTimeEquals(
                execution.Snapshot.SnapshotSha256.Span,
                target.SnapshotSha256.Span))
        {
            throw new AgentProfileIntegrityException("Agent Profile protected execution snapshot does not match its binding target.");
        }
    }

    private static bool IsPublished(AgentProfileCatalogEntry entry) =>
        entry.Status == AgentProfileProvisioningStatus.Active &&
        entry.PublishedRevision > 0 &&
        entry.SnapshotSha256.Length == 32;

    private static string NormalizeSlug(string profileSlug)
    {
        var normalized = NormalizeRequired(profileSlug, nameof(profileSlug));
        if (AgentProfilePolicies.ValidateProfileSlug(normalized).Count > 0)
            throw new ArgumentException("Agent Profile slug is invalid.", nameof(profileSlug));
        return normalized;
    }

    private static string NormalizeRequired(string value, string parameterName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new ArgumentException("A non-empty value is required.", parameterName);
        return normalized;
    }

    private static void ValidateOwner(AgentProfileOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _ = OwnerKey(owner);
    }

    private static void EnsureOwner(AgentProfileOwner actual, AgentProfileOwner expected, string message)
    {
        if (!actual.Equals(expected))
            throw new AgentProfileIntegrityException(message);
    }

    private static string OwnerKey(AgentProfileOwner owner) =>
        owner.OwnerCase switch
        {
            AgentProfileOwner.OwnerOneofCase.Scope when !string.IsNullOrWhiteSpace(owner.Scope.ScopeId) =>
                $"scope:{owner.Scope.ScopeId.Trim()}",
            AgentProfileOwner.OwnerOneofCase.System when
                string.Equals(owner.System.PlatformId, AgentProfileOwners.PlatformId, StringComparison.Ordinal) =>
                $"system:{AgentProfileOwners.PlatformId}",
            _ => throw new ArgumentException("A valid Agent Profile owner is required.", nameof(owner)),
        };

    private static string EncodeCursor(int offset) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes(offset.ToString(CultureInfo.InvariantCulture)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static int DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return 0;
        try
        {
            var normalized = cursor.Trim().Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight((normalized.Length + 3) / 4 * 4, '=');
            var text = Encoding.ASCII.GetString(Convert.FromBase64String(normalized));
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset < 0)
                throw new FormatException();
            return offset;
        }
        catch (FormatException)
        {
            throw new AgentProfileInvalidCursorException("Agent Profile cursor is malformed.");
        }
    }

    private readonly record struct ResolvedIdentity(
        AgentProfileIdentity Identity,
        AgentProfileCatalogEntry Entry);
}
