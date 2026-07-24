using System.Text;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Abstractions.Ports;
using Google.Protobuf;

namespace Aevatar.GAgentService.Application.AgentProfiles;

public sealed class SystemAgentProfileProvisioningService : ISystemAgentProfileProvisioningService
{
    private const string OperationKind = "system-agent-profile-bootstrap";
    private const string ProfileIdentityKind = "system-agent-profile-identity";

    private readonly IReadOnlyList<ISystemAgentProfileDefinitionSource> _definitionSources;
    private readonly IAgentProfileNamespaceQueryPort _namespaceQuery;
    private readonly IAgentProfileManagementQueryPort _managementQuery;
    private readonly IAgentProfileExecutionSnapshotQueryPort _executionQuery;
    private readonly IAgentProfileActorPort _actorPort;
    private readonly AgentProfileSkillSealer _skillSealer;
    private readonly ISystemAgentProfileOrnnAccessTokenProvider _accessTokenProvider;

    public SystemAgentProfileProvisioningService(
        IEnumerable<ISystemAgentProfileDefinitionSource> definitionSources,
        IAgentProfileNamespaceQueryPort namespaceQuery,
        IAgentProfileManagementQueryPort managementQuery,
        IAgentProfileExecutionSnapshotQueryPort executionQuery,
        IAgentProfileActorPort actorPort,
        AgentProfileSkillSealer skillSealer,
        ISystemAgentProfileOrnnAccessTokenProvider accessTokenProvider)
    {
        ArgumentNullException.ThrowIfNull(definitionSources);
        _definitionSources = definitionSources.ToArray();
        _namespaceQuery = namespaceQuery ?? throw new ArgumentNullException(nameof(namespaceQuery));
        _managementQuery = managementQuery ?? throw new ArgumentNullException(nameof(managementQuery));
        _executionQuery = executionQuery ?? throw new ArgumentNullException(nameof(executionQuery));
        _actorPort = actorPort ?? throw new ArgumentNullException(nameof(actorPort));
        _skillSealer = skillSealer ?? throw new ArgumentNullException(nameof(skillSealer));
        _accessTokenProvider = accessTokenProvider ??
            throw new ArgumentNullException(nameof(accessTokenProvider));
    }

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var definition in ReadDefinitions())
        {
            ct.ThrowIfCancellationRequested();
            await ReconcileDefinitionAsync(definition, ct);
        }
    }

    private async Task ReconcileDefinitionAsync(
        SystemAgentProfileDefinition definition,
        CancellationToken ct)
    {
        var desiredContent = definition.Content;
        var desiredDigest = AgentProfileDeterminism.ComputeSourceDraftSha256(desiredContent);
        var reference = SystemReference(definition.ProfileSlug);
        var entry = await _namespaceQuery.GetByReferenceAsync(reference, ct);
        if (entry is null)
        {
            await DispatchCreateAsync(definition, reference, desiredContent, desiredDigest, ct);
            return;
        }

        if (!IsOwnedSystemEntry(entry, reference) ||
            entry.Status != AgentProfileProvisioningStatus.Active)
        {
            return;
        }

        var management = await _managementQuery.GetAsync(entry.ProfileId, ct);
        if (management is null || !HasExpectedIdentity(management.Identity, entry, reference))
            return;

        var surfaceUpdate = BuildSurfaceUpdate(management.Draft, desiredContent);
        if (surfaceUpdate is not null)
        {
            var operation = Operation(
                definition,
                desiredDigest,
                "update-draft",
                AgentProfileDeterminism.ComputeUpdateAgentProfileDraftInputSha256(
                    management.Identity,
                    surfaceUpdate),
                management.AuthorityStateVersion);
            var admission = await _actorPort.DispatchUpdateDraftAsync(
                new UpdateAgentProfileDraftCommand
                {
                    Operation = operation,
                    Identity = management.Identity,
                    ExpectedAuthorityStateVersion = management.AuthorityStateVersion,
                    Content = surfaceUpdate,
                },
                ct);
            RequireAccepted(admission);
            return;
        }

        var removal = FindBindingRemoval(management.Draft, desiredContent);
        if (removal is not null)
        {
            var operation = Operation(
                definition,
                desiredDigest,
                $"remove-binding:{removal.BindingId}",
                AgentProfileDeterminism.ComputeRemoveAgentProfileSkillBindingInputSha256(
                    management.Identity,
                    removal.BindingId),
                management.AuthorityStateVersion);
            var admission = await _actorPort.DispatchRemoveSkillBindingAsync(
                new RemoveAgentProfileSkillBindingCommand
                {
                    Operation = operation,
                    Identity = management.Identity,
                    ExpectedAuthorityStateVersion = management.AuthorityStateVersion,
                    BindingId = removal.BindingId,
                },
                ct);
            RequireAccepted(admission);
            return;
        }

        var upsert = FindBindingUpsert(management.Draft, desiredContent);
        if (upsert is not null)
        {
            var operation = Operation(
                definition,
                desiredDigest,
                $"upsert-binding:{upsert.BindingId}",
                AgentProfileDeterminism.ComputeUpsertAgentProfileSkillBindingInputSha256(
                    management.Identity,
                    upsert),
                management.AuthorityStateVersion);
            var admission = await _actorPort.DispatchUpsertSkillBindingAsync(
                new UpsertAgentProfileSkillBindingCommand
                {
                    Operation = operation,
                    Identity = management.Identity,
                    ExpectedAuthorityStateVersion = management.AuthorityStateVersion,
                    Binding = upsert,
                },
                ct);
            RequireAccepted(admission);
            return;
        }

        if (!management.Draft.Equals(desiredContent) ||
            !management.DraftSha256.Equals(desiredDigest))
        {
            return;
        }

        if (management.PublishedRevision > 0 &&
            management.PublishedSourceDraftSha256.Equals(desiredDigest))
        {
            _ = await _executionQuery.GetAsync(entry.ProfileId, ct);
            return;
        }

        string? accessToken = null;
        if (desiredContent.SkillBindings.Count > 0)
        {
            accessToken = await _accessTokenProvider.GetAccessTokenAsync(
                definition.DefinitionKey,
                ct);
            if (string.IsNullOrWhiteSpace(accessToken))
                return;
        }

        var sealing = await _skillSealer.ResolveAndSealAsync(
            management.Identity,
            desiredContent,
            accessToken,
            ct);
        if (!sealing.IsSuccess || sealing.Snapshot is null)
            return;

        var snapshot = sealing.Snapshot;
        var publishOperation = Operation(
            definition,
            desiredDigest,
            "publish",
            AgentProfileDeterminism.ComputePublishAgentProfileInputSha256(
                management.Identity,
                snapshot),
            management.AuthorityStateVersion);
        var publishAdmission = await _actorPort.DispatchPublishAsync(
            new PublishAgentProfileCommand
            {
                Operation = publishOperation,
                Identity = management.Identity,
                ExpectedAuthorityStateVersion = management.AuthorityStateVersion,
                ExpectedDraftRevision = management.DraftRevision,
                ExpectedDraftSha256 = management.DraftSha256,
                Snapshot = snapshot,
            },
            ct);
        RequireAccepted(publishAdmission);
    }

    private async Task DispatchCreateAsync(
        SystemAgentProfileDefinition definition,
        AgentProfileReference reference,
        AgentProfileContent content,
        ByteString desiredDigest,
        CancellationToken ct)
    {
        var profileId = CreateProfileId(definition);
        var identity = new AgentProfileIdentity
        {
            ProfileId = profileId,
            Owner = SystemOwner(),
            OwningScopeId = string.Empty,
            Reference = reference,
        };
        identity = AgentProfileDeterminism.NormalizeIdentity(identity);
        var operation = Operation(
            definition,
            desiredDigest,
            "create",
            AgentProfileDeterminism.ComputeCreateAgentProfileInputSha256(identity, content),
            observedAuthorityVersion: null);
        var targets = _actorPort.ResolveCreateTargets(profileId);
        var admission = await _actorPort.DispatchCreateAsync(
            new CreateAgentProfileCommand
            {
                Operation = operation,
                Identity = identity,
                InitialContent = content,
                ProfileActorId = targets.ProfileActorId,
            },
            ct);
        RequireAccepted(admission);
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

        var duplicateReference = definitions
            .GroupBy(
                static definition =>
                    $"{AgentProfilePolicies.SystemOwnerHandle}/{definition.ProfileSlug}",
                StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
        if (duplicateReference is not null)
        {
            throw new InvalidOperationException(
                $"System Profile reference '{duplicateReference}' is registered more than once.");
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
        var content = AgentProfileDeterminism.NormalizeContent(definition.Content);
        return new SystemAgentProfileDefinition(
            definition.DefinitionKey.Normalize(NormalizationForm.FormC),
            reference.ProfileSlug,
            content,
            definition.Required);
    }

    private static AgentProfileContent? BuildSurfaceUpdate(
        AgentProfileContent current,
        AgentProfileContent desired)
    {
        var currentSurface = current.Clone();
        currentSurface.SkillBindings.Clear();
        var desiredSurface = desired.Clone();
        desiredSurface.SkillBindings.Clear();
        if (currentSurface.Equals(desiredSurface))
            return null;

        desiredSurface.SkillBindings.Add(
            current.SkillBindings.Select(static binding => binding.Clone()));
        return AgentProfileDeterminism.NormalizeContent(desiredSurface);
    }

    private static AgentProfileSkillBinding? FindBindingRemoval(
        AgentProfileContent current,
        AgentProfileContent desired) =>
        current.SkillBindings
            .OrderBy(static binding => binding.BindingId, StringComparer.Ordinal)
            .FirstOrDefault(binding => desired.SkillBindings.All(candidate =>
                !string.Equals(candidate.BindingId, binding.BindingId, StringComparison.Ordinal)))
            ?.Clone();

    private static AgentProfileSkillBinding? FindBindingUpsert(
        AgentProfileContent current,
        AgentProfileContent desired)
    {
        var candidates = desired.SkillBindings
            .Select(binding => new
            {
                Desired = binding,
                Current = current.SkillBindings.FirstOrDefault(candidate =>
                    string.Equals(candidate.BindingId, binding.BindingId, StringComparison.Ordinal)),
            })
            .Where(static candidate => candidate.Current is null ||
                !candidate.Current.Equals(candidate.Desired))
            .OrderByDescending(static candidate =>
                candidate.Current?.ActivationMode ==
                    AgentProfileSkillActivationMode.DefaultForUnmatchedTurn &&
                candidate.Desired.ActivationMode !=
                    AgentProfileSkillActivationMode.DefaultForUnmatchedTurn)
            .ThenBy(static candidate => candidate.Desired.BindingId, StringComparer.Ordinal)
            .FirstOrDefault();
        return candidates?.Desired.Clone();
    }

    private static AgentProfileOperationFact Operation(
        SystemAgentProfileDefinition definition,
        ByteString desiredDigest,
        string step,
        ByteString inputSha256,
        long? observedAuthorityVersion) =>
        new()
        {
            OperationId = AgentProfileDeterminism.CreateOperationId(
                OperationKind,
                definition.DefinitionKey,
                OperationIdentity(
                    desiredDigest,
                    step,
                    observedAuthorityVersion)),
            InputSha256 = inputSha256,
            CommandId = AgentProfileDeterminism.CreateCommandId(),
            CorrelationId = AgentProfileDeterminism.CreateCorrelationId(),
        };

    private static string OperationIdentity(
        ByteString desiredDigest,
        string step,
        long? observedAuthorityVersion)
    {
        var identity = $"{Convert.ToHexStringLower(desiredDigest.Span)}:{step}";
        return observedAuthorityVersion.HasValue
            ? $"{identity}:authority-version:{observedAuthorityVersion.Value}"
            : identity;
    }

    private static string CreateProfileId(SystemAgentProfileDefinition definition)
    {
        var opaqueId = AgentProfileDeterminism.CreateOperationId(
            ProfileIdentityKind,
            definition.DefinitionKey,
            definition.ProfileSlug);
        return $"prof_{opaqueId["op_".Length..]}";
    }

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

    private static void RequireAccepted(DispatchAdmission admission)
    {
        if (!admission.Accepted)
            throw new InvalidOperationException("System Profile bootstrap dispatch was rejected.");
    }
}
