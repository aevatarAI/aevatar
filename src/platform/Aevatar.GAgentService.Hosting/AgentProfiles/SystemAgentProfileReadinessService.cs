using System.Text;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Abstractions.Ports;
using Google.Protobuf;

namespace Aevatar.GAgentService.Hosting.AgentProfiles;

public sealed class SystemAgentProfileReadinessService : ISystemAgentProfileReadinessService
{
    private readonly IReadOnlyList<ISystemAgentProfileDefinitionSource> _definitionSources;
    private readonly IAgentProfileNamespaceQueryPort _namespaceQuery;
    private readonly IAgentProfileManagementQueryPort _managementQuery;
    private readonly IAgentProfileExecutionSnapshotQueryPort _executionQuery;
    private readonly ISystemAgentProfileOrnnAccessTokenProvider _accessTokenProvider;

    public SystemAgentProfileReadinessService(
        IEnumerable<ISystemAgentProfileDefinitionSource> definitionSources,
        IAgentProfileNamespaceQueryPort namespaceQuery,
        IAgentProfileManagementQueryPort managementQuery,
        IAgentProfileExecutionSnapshotQueryPort executionQuery,
        ISystemAgentProfileOrnnAccessTokenProvider accessTokenProvider)
    {
        ArgumentNullException.ThrowIfNull(definitionSources);
        _definitionSources = definitionSources.ToArray();
        _namespaceQuery = namespaceQuery ?? throw new ArgumentNullException(nameof(namespaceQuery));
        _managementQuery = managementQuery ?? throw new ArgumentNullException(nameof(managementQuery));
        _executionQuery = executionQuery ?? throw new ArgumentNullException(nameof(executionQuery));
        _accessTokenProvider = accessTokenProvider ??
            throw new ArgumentNullException(nameof(accessTokenProvider));
    }

    public async Task<SystemAgentProfileReadinessSnapshot> GetAsync(
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var profiles = new List<SystemAgentProfileReadinessEntry>();
        foreach (var definition in ReadDefinitions())
        {
            ct.ThrowIfCancellationRequested();
            profiles.Add(await InspectAsync(definition, ct));
        }

        return new SystemAgentProfileReadinessSnapshot(profiles);
    }

    private async Task<SystemAgentProfileReadinessEntry> InspectAsync(
        SystemAgentProfileDefinition definition,
        CancellationToken ct)
    {
        var desiredContent = definition.Content;
        var desiredDigest = AgentProfileDeterminism.ComputeSourceDraftSha256(desiredContent);
        var reference = SystemReference(definition.ProfileSlug);
        var entry = await _namespaceQuery.GetByReferenceAsync(reference, ct);
        if (entry is null)
        {
            return Result(
                definition,
                reference,
                desiredDigest,
                SystemAgentProfileReadinessStatus.Pending,
                SystemAgentProfileReadinessReason.NamespaceMissing);
        }

        if (!IsOwnedSystemEntry(entry, reference))
        {
            return Result(
                definition,
                reference,
                desiredDigest,
                SystemAgentProfileReadinessStatus.Unhealthy,
                SystemAgentProfileReadinessReason.NamespaceConflict,
                entry);
        }

        if (entry.Status != AgentProfileProvisioningStatus.Active)
        {
            var failed = entry.Status == AgentProfileProvisioningStatus.Failed;
            return Result(
                definition,
                reference,
                desiredDigest,
                failed
                    ? SystemAgentProfileReadinessStatus.Unhealthy
                    : SystemAgentProfileReadinessStatus.Pending,
                failed
                    ? SystemAgentProfileReadinessReason.NamespaceProvisioningFailed
                    : SystemAgentProfileReadinessReason.NamespaceProvisioning,
                entry);
        }

        var management = await _managementQuery.GetAsync(entry.ProfileId, ct);
        if (management is null)
        {
            return Result(
                definition,
                reference,
                desiredDigest,
                SystemAgentProfileReadinessStatus.Pending,
                SystemAgentProfileReadinessReason.ManagementSnapshotMissing,
                entry);
        }

        if (!HasExpectedIdentity(management.Identity, entry, reference))
        {
            return Result(
                definition,
                reference,
                desiredDigest,
                SystemAgentProfileReadinessStatus.Unhealthy,
                SystemAgentProfileReadinessReason.ProfileIdentityConflict,
                entry,
                management);
        }

        if (!management.Draft.Equals(desiredContent) ||
            !management.DraftSha256.Equals(desiredDigest))
        {
            return Result(
                definition,
                reference,
                desiredDigest,
                SystemAgentProfileReadinessStatus.Pending,
                SystemAgentProfileReadinessReason.DraftDrift,
                entry,
                management);
        }

        if (management.PublishedRevision <= 0 ||
            !management.PublishedSourceDraftSha256.Equals(desiredDigest))
        {
            if (desiredContent.SkillBindings.Count > 0)
            {
                var accessToken = await _accessTokenProvider.GetAccessTokenAsync(
                    definition.DefinitionKey,
                    ct);
                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    return Result(
                        definition,
                        reference,
                        desiredDigest,
                        SystemAgentProfileReadinessStatus.Unavailable,
                        SystemAgentProfileReadinessReason.OrnnAccessTokenUnavailable,
                        entry,
                        management);
                }
            }

            return Result(
                definition,
                reference,
                desiredDigest,
                SystemAgentProfileReadinessStatus.Pending,
                SystemAgentProfileReadinessReason.PublicationPending,
                entry,
                management);
        }

        var execution = await _executionQuery.GetAsync(entry.ProfileId, ct);
        if (execution is null)
        {
            return Result(
                definition,
                reference,
                desiredDigest,
                SystemAgentProfileReadinessStatus.Pending,
                SystemAgentProfileReadinessReason.ExecutionSnapshotMissing,
                entry,
                management);
        }

        if (!ExecutionMatches(execution, management, desiredDigest))
        {
            return Result(
                definition,
                reference,
                desiredDigest,
                SystemAgentProfileReadinessStatus.Pending,
                SystemAgentProfileReadinessReason.ExecutionSnapshotLagging,
                entry,
                management,
                execution);
        }

        return Result(
            definition,
            reference,
            desiredDigest,
            SystemAgentProfileReadinessStatus.Ready,
            SystemAgentProfileReadinessReason.None,
            entry,
            management,
            execution);
    }

    private IReadOnlyList<SystemAgentProfileDefinition> ReadDefinitions()
    {
        var definitions = _definitionSources
            .SelectMany(static source =>
                source.GetDefinitions() ??
                throw new InvalidOperationException("System Profile definition sources cannot return null."))
            .Select(NormalizeDefinition)
            .OrderBy(static definition => definition.DefinitionKey, StringComparer.Ordinal)
            .ToArray();
        var duplicate = definitions
            .GroupBy(static definition => definition.DefinitionKey, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"System Profile definition key '{duplicate.Key}' is registered more than once.");
        }

        return definitions;
    }

    private static SystemAgentProfileDefinition NormalizeDefinition(
        SystemAgentProfileDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.DefinitionKey) ||
            HasBoundaryWhitespace(definition.DefinitionKey))
        {
            throw new InvalidOperationException("System Profile definition key is invalid.");
        }

        var reference = SystemReference(definition.ProfileSlug);
        return new SystemAgentProfileDefinition(
            definition.DefinitionKey.Normalize(NormalizationForm.FormC),
            reference.ProfileSlug,
            AgentProfileDeterminism.NormalizeContent(definition.Content),
            definition.Required);
    }

    private static SystemAgentProfileReadinessEntry Result(
        SystemAgentProfileDefinition definition,
        AgentProfileReference reference,
        ByteString desiredDigest,
        SystemAgentProfileReadinessStatus status,
        SystemAgentProfileReadinessReason reason,
        AgentProfileNamespaceEntrySnapshot? entry = null,
        AgentProfileManagementSnapshot? management = null,
        AgentProfileExecutionSnapshot? execution = null) =>
        new(
            definition.DefinitionKey,
            definition.Required,
            reference,
            status,
            reason,
            entry?.ProfileId ?? string.Empty,
            management?.DraftRevision ?? 0,
            desiredDigest,
            management?.DraftSha256 ?? ByteString.Empty,
            management?.PublishedRevision ?? 0,
            management?.PublishedSourceDraftSha256 ?? ByteString.Empty,
            management?.PublishedSnapshotSha256 ?? ByteString.Empty,
            execution?.Snapshot.PublishedRevision ?? 0,
            execution?.Snapshot.SnapshotSha256 ?? ByteString.Empty);

    private static bool ExecutionMatches(
        AgentProfileExecutionSnapshot execution,
        AgentProfileManagementSnapshot management,
        ByteString desiredDigest) =>
        HasExpectedIdentity(
            execution.Snapshot.Identity,
            new AgentProfileNamespaceEntrySnapshot(
                0,
                string.Empty,
                management.ProfileId,
                management.Identity.Reference,
                management.Identity.Owner,
                management.Identity.OwningScopeId,
                AgentProfileProvisioningStatus.Active,
                null),
            management.Identity.Reference) &&
        execution.Snapshot.PublishedRevision == management.PublishedRevision &&
        execution.Snapshot.SourceDraftSha256.Equals(desiredDigest) &&
        execution.Snapshot.SnapshotSha256.Equals(management.PublishedSnapshotSha256);

    private static AgentProfileReference SystemReference(string profileSlug) =>
        AgentProfileDeterminism.NormalizeReference(new AgentProfileReference
        {
            OwnerHandle = AgentProfilePolicies.SystemOwnerHandle,
            ProfileSlug = profileSlug,
        });

    private static AgentProfileOwnerIdentity SystemOwner() =>
        new()
        {
            System = new AgentProfileSystemOwnerIdentity
            {
                PlatformId = AgentProfilePolicies.AevatarPlatformId,
            },
        };

    private static bool IsOwnedSystemEntry(
        AgentProfileNamespaceEntrySnapshot entry,
        AgentProfileReference reference) =>
        entry.Reference.Equals(reference) &&
        entry.Owner.Equals(SystemOwner()) &&
        string.IsNullOrEmpty(entry.OwningScopeId) &&
        !string.IsNullOrWhiteSpace(entry.ProfileId);

    private static bool HasExpectedIdentity(
        AgentProfileIdentity identity,
        AgentProfileNamespaceEntrySnapshot entry,
        AgentProfileReference reference) =>
        string.Equals(identity.ProfileId, entry.ProfileId, StringComparison.Ordinal) &&
        identity.Reference.Equals(reference) &&
        identity.Owner.Equals(SystemOwner()) &&
        string.IsNullOrEmpty(identity.OwningScopeId);

    private static bool HasBoundaryWhitespace(string value) =>
        char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]);
}
