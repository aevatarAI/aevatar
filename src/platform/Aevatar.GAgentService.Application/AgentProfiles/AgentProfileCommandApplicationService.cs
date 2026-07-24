using System.Text;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.GAgentService.Application.AgentProfiles;

public sealed class AgentProfileCommandApplicationService : IAgentProfileCommandService
{
    private readonly IAgentProfileActorPort _actorPort;
    private readonly AgentProfileDraftValidator _draftValidator;
    private readonly AgentProfileSkillSealer _skillSealer;
    private readonly AgentProfileOperationFactory _operationFactory;
    private readonly AgentProfileOwnerSnapshotResolver _ownerResolver;

    public AgentProfileCommandApplicationService(
        IAgentProfileNamespaceQueryPort namespaceQuery,
        IAgentProfileManagementQueryPort managementQuery,
        IAgentProfileActorPort actorPort,
        AgentProfileDraftValidator draftValidator,
        AgentProfileSkillSealer skillSealer,
        AgentProfileOperationFactory operationFactory)
    {
        ArgumentNullException.ThrowIfNull(namespaceQuery);
        ArgumentNullException.ThrowIfNull(managementQuery);
        _actorPort = actorPort ?? throw new ArgumentNullException(nameof(actorPort));
        _draftValidator = draftValidator ?? throw new ArgumentNullException(nameof(draftValidator));
        _skillSealer = skillSealer ?? throw new ArgumentNullException(nameof(skillSealer));
        _operationFactory = operationFactory ?? throw new ArgumentNullException(nameof(operationFactory));
        _ownerResolver = new AgentProfileOwnerSnapshotResolver(namespaceQuery, managementQuery);
    }

    public async Task<AgentProfileAcceptedReceipt> CreateAsync(
        AgentProfileCallerContext caller,
        CreateAgentProfileRequest request,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var normalizedCaller = NormalizeCaller(caller);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new AgentProfileRequestException("IDEMPOTENCY_KEY_REQUIRED");
        if (HasBoundaryWhitespace(idempotencyKey))
            throw new AgentProfileRequestException("INVALID_IDEMPOTENCY_KEY");

        var ownerHandle = request.OwnerHandle ?? caller.Username;
        ThrowRequestDiagnostics(AgentProfilePolicies.ValidateUserOwnerHandle(ownerHandle));
        var reference = NormalizeUserReference(ownerHandle!, request.ProfileSlug);
        var initialContent = NormalizeContent(new AgentProfileContent
        {
            DisplayName = request.DisplayName,
            Purpose = request.Purpose,
            Instructions = request.Instructions,
            ToolPolicy = request.ToolPolicy,
        });
        var profileId = AgentProfileDeterminism.CreateProfileId(
            normalizedCaller.Owner.User,
            normalizedCaller.ScopeId,
            idempotencyKey);
        var identity = NormalizeIdentity(new AgentProfileIdentity
        {
            ProfileId = profileId,
            Owner = normalizedCaller.Owner,
            OwningScopeId = normalizedCaller.ScopeId,
            Reference = reference,
        });
        var operation = _operationFactory.CreateCreate(
            identity.Owner.User,
            identity.OwningScopeId,
            idempotencyKey,
            identity,
            initialContent);
        var targets = _actorPort.ResolveCreateTargets(profileId);
        var command = new CreateAgentProfileCommand
        {
            Operation = operation,
            Identity = identity,
            InitialContent = initialContent,
            ProfileActorId = targets.ProfileActorId,
        };
        var admission = await _actorPort.DispatchCreateAsync(command, ct);
        return AcceptedReceipt(
            identity.OwningScopeId,
            reference.ProfileSlug,
            profileId,
            operation,
            admission);
    }

    public async Task<AgentProfileAcceptedReceipt> UpdateDraftAsync(
        AgentProfileCallerContext caller,
        string profileSlug,
        long expectedAuthorityStateVersion,
        UpdateAgentProfileDraftRequest request,
        string? idempotencyKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var owned = await ResolveOwnedOrThrowAsync(caller, profileSlug, ct);
        var content = new AgentProfileContent
        {
            DisplayName = request.DisplayName,
            Purpose = request.Purpose,
            Instructions = request.Instructions,
            ToolPolicy = request.ToolPolicy,
        };
        content.SkillBindings.Add(owned.Management.Draft.SkillBindings
            .Select(static binding => binding.Clone()));
        var normalizedContent = NormalizeContent(content);
        var operation = _operationFactory.CreateUpdateDraft(
            owned.Management.ProfileId,
            idempotencyKey,
            owned.Management.Identity,
            normalizedContent);
        ThrowIfKnownStale(owned.Management, expectedAuthorityStateVersion, operation.OperationId);
        var command = new UpdateAgentProfileDraftCommand
        {
            Operation = operation,
            Identity = owned.Management.Identity,
            ExpectedAuthorityStateVersion = expectedAuthorityStateVersion,
            Content = normalizedContent,
        };
        var admission = await _actorPort.DispatchUpdateDraftAsync(command, ct);
        return AcceptedReceipt(
            owned.NamespaceEntry.OwningScopeId,
            owned.NamespaceEntry.Reference.ProfileSlug,
            owned.Management.ProfileId,
            operation,
            admission);
    }

    public async Task<AgentProfileAcceptedReceipt> UpsertSkillBindingAsync(
        AgentProfileCallerContext caller,
        string profileSlug,
        string bindingId,
        long expectedAuthorityStateVersion,
        UpsertAgentProfileSkillBindingRequest request,
        string? idempotencyKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var owned = await ResolveOwnedOrThrowAsync(caller, profileSlug, ct);
        var binding = NormalizeBinding(new AgentProfileSkillBinding
        {
            BindingId = bindingId,
            ActivationMode = request.ActivationMode,
            Skill = request.Skill,
        });
        var operation = _operationFactory.CreateUpsertSkillBinding(
            owned.Management.ProfileId,
            idempotencyKey,
            owned.Management.Identity,
            binding);
        ThrowIfKnownStale(owned.Management, expectedAuthorityStateVersion, operation.OperationId);
        var command = new UpsertAgentProfileSkillBindingCommand
        {
            Operation = operation,
            Identity = owned.Management.Identity,
            ExpectedAuthorityStateVersion = expectedAuthorityStateVersion,
            Binding = binding,
        };
        var admission = await _actorPort.DispatchUpsertSkillBindingAsync(command, ct);
        return AcceptedReceipt(
            owned.NamespaceEntry.OwningScopeId,
            owned.NamespaceEntry.Reference.ProfileSlug,
            owned.Management.ProfileId,
            operation,
            admission);
    }

    public async Task<AgentProfileAcceptedReceipt> RemoveSkillBindingAsync(
        AgentProfileCallerContext caller,
        string profileSlug,
        string bindingId,
        long expectedAuthorityStateVersion,
        string? idempotencyKey,
        CancellationToken ct = default)
    {
        var owned = await ResolveOwnedOrThrowAsync(caller, profileSlug, ct);
        ThrowRequestDiagnostics(AgentProfilePolicies.ValidateBindingId(bindingId));
        var normalizedBindingId = bindingId.Normalize(NormalizationForm.FormC);
        var operation = _operationFactory.CreateRemoveSkillBinding(
            owned.Management.ProfileId,
            idempotencyKey,
            owned.Management.Identity,
            normalizedBindingId);
        ThrowIfKnownStale(owned.Management, expectedAuthorityStateVersion, operation.OperationId);
        var command = new RemoveAgentProfileSkillBindingCommand
        {
            Operation = operation,
            Identity = owned.Management.Identity,
            ExpectedAuthorityStateVersion = expectedAuthorityStateVersion,
            BindingId = normalizedBindingId,
        };
        var admission = await _actorPort.DispatchRemoveSkillBindingAsync(command, ct);
        return AcceptedReceipt(
            owned.NamespaceEntry.OwningScopeId,
            owned.NamespaceEntry.Reference.ProfileSlug,
            owned.Management.ProfileId,
            operation,
            admission);
    }

    public async Task<AgentProfileValidationReport> ValidateAsync(
        AgentProfileCallerContext caller,
        string profileSlug,
        CancellationToken ct = default)
    {
        var owned = await ResolveOwnedOrThrowAsync(caller, profileSlug, ct);
        return await _draftValidator.ValidateAsync(
            owned.Management.Identity,
            owned.Management.Draft,
            owned.Management.DraftRevision,
            owned.Management.DraftSha256,
            caller.NyxIdAccessToken,
            ct);
    }

    public async Task<AgentProfileAcceptedReceipt> PublishAsync(
        AgentProfileCallerContext caller,
        string profileSlug,
        long expectedAuthorityStateVersion,
        string? idempotencyKey,
        CancellationToken ct = default)
    {
        var owned = await ResolveOwnedOrThrowAsync(caller, profileSlug, ct);
        var preparedOperation = _operationFactory.PreparePublish(
            owned.Management.ProfileId,
            idempotencyKey);
        ThrowIfKnownStale(
            owned.Management,
            expectedAuthorityStateVersion,
            preparedOperation.OperationId);
        var sealing = await _skillSealer.ResolveAndSealAsync(
            owned.Management.Identity,
            owned.Management.Draft,
            caller.NyxIdAccessToken,
            ct);
        if (!sealing.IsSuccess || sealing.Snapshot is null)
            ThrowPublishFailure(sealing.Diagnostics);

        var snapshot = sealing.Snapshot!;
        var operation = _operationFactory.CreatePublish(
            preparedOperation,
            owned.Management.Identity,
            snapshot);
        var command = new PublishAgentProfileCommand
        {
            Operation = operation,
            Identity = owned.Management.Identity,
            ExpectedAuthorityStateVersion = expectedAuthorityStateVersion,
            ExpectedDraftRevision = owned.Management.DraftRevision,
            ExpectedDraftSha256 = owned.Management.DraftSha256,
            Snapshot = snapshot,
        };
        var admission = await _actorPort.DispatchPublishAsync(command, ct);
        return AcceptedReceipt(
            owned.NamespaceEntry.OwningScopeId,
            owned.NamespaceEntry.Reference.ProfileSlug,
            owned.Management.ProfileId,
            operation,
            admission);
    }

    private async Task<AgentProfileOwnedSnapshot> ResolveOwnedOrThrowAsync(
        AgentProfileCallerContext caller,
        string profileSlug,
        CancellationToken ct)
    {
        ValidateCaller(caller);
        ValidateProfileSlug(profileSlug);
        return await _ownerResolver.ResolveAsync(caller, profileSlug, ct) ??
            throw new AgentProfileNotFoundException();
    }

    private static (AgentProfileOwnerIdentity Owner, string ScopeId) NormalizeCaller(
        AgentProfileCallerContext? caller)
    {
        if (caller is not null &&
            string.Equals(
                caller.ScopeId,
                PlatformScopeSemantics.ReservedPlatformScopeId,
                StringComparison.Ordinal))
        {
            ThrowRequestDiagnostics(AgentProfilePolicies.ValidateUserOwningScopeId(caller.ScopeId));
        }

        if (!AgentProfileOwnerSnapshotResolver.TryNormalizeCaller(
                caller,
                out var owner,
                out var scopeId))
        {
            throw new AgentProfileRequestException("INVALID_AGENT_PROFILE_CALLER");
        }

        return (owner, scopeId);
    }

    private static AgentProfileReference NormalizeUserReference(
        string ownerHandle,
        string profileSlug)
    {
        var reference = new AgentProfileReference
        {
            OwnerHandle = ownerHandle,
            ProfileSlug = profileSlug,
        };
        ThrowRequestDiagnostics(AgentProfilePolicies.ValidateUserReference(reference));
        return AgentProfileDeterminism.NormalizeReference(reference);
    }

    private static void ValidateCaller(AgentProfileCallerContext? caller)
    {
        if (!AgentProfileOwnerSnapshotResolver.IsValidCaller(caller))
            throw new AgentProfileRequestException("INVALID_AGENT_PROFILE_CALLER");
    }

    private static void ValidateProfileSlug(string profileSlug)
    {
        var diagnostics = AgentProfilePolicies.ValidateReference(new AgentProfileReference
        {
            OwnerHandle = "owner",
            ProfileSlug = profileSlug ?? string.Empty,
        });
        ThrowRequestDiagnostics(diagnostics);
    }

    private static AgentProfileContent NormalizeContent(AgentProfileContent content)
    {
        try
        {
            return AgentProfileDeterminism.NormalizeContent(content);
        }
        catch (AgentProfileContractValidationException exception)
        {
            throw new AgentProfileRequestException(
                exception.Diagnostics.FirstOrDefault()?.Code ?? "INVALID_AGENT_PROFILE_CONTENT",
                exception.Diagnostics);
        }
    }

    private static AgentProfileSkillBinding NormalizeBinding(AgentProfileSkillBinding binding)
    {
        try
        {
            return AgentProfileDeterminism.NormalizeSkillBinding(binding);
        }
        catch (AgentProfileContractValidationException exception)
        {
            throw new AgentProfileRequestException(
                exception.Diagnostics.FirstOrDefault()?.Code ?? "INVALID_AGENT_PROFILE_BINDING",
                exception.Diagnostics);
        }
    }

    private static AgentProfileIdentity NormalizeIdentity(AgentProfileIdentity identity)
    {
        try
        {
            return AgentProfileDeterminism.NormalizeIdentity(identity);
        }
        catch (AgentProfileContractValidationException exception)
        {
            throw new AgentProfileRequestException(
                exception.Diagnostics.FirstOrDefault()?.Code ?? "INVALID_AGENT_PROFILE_IDENTITY",
                exception.Diagnostics);
        }
    }

    private static void ThrowRequestDiagnostics(
        IReadOnlyList<AgentProfileSafeDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
            return;
        throw new AgentProfileRequestException(
            diagnostics[0].Code,
            diagnostics);
    }

    private static void ThrowIfKnownStale(
        AgentProfileManagementSnapshot management,
        long expectedAuthorityStateVersion,
        string operationId)
    {
        if (expectedAuthorityStateVersion == management.AuthorityStateVersion ||
            (expectedAuthorityStateVersion < management.AuthorityStateVersion &&
             string.Equals(
                 management.LastMutation?.Operation?.OperationId,
                 operationId,
                 StringComparison.Ordinal)))
        {
            return;
        }

        throw new AgentProfilePreconditionException(
            expectedAuthorityStateVersion,
            management.AuthorityStateVersion);
    }

    private static void ThrowPublishFailure(
        IReadOnlyList<AgentProfileSafeDiagnostic> diagnostics)
    {
        if (diagnostics.Any(static diagnostic =>
                string.Equals(
                    diagnostic.Code,
                    "ORNN_ACCESS_TOKEN_REQUIRED",
                    StringComparison.Ordinal)))
        {
            throw new AgentProfileAuthenticationRequiredException(diagnostics);
        }

        if (diagnostics.Any(static diagnostic =>
                string.Equals(
                    diagnostic.Code,
                    "ORNN_DEPENDENCY_UNAVAILABLE",
                    StringComparison.Ordinal)))
        {
            throw new AgentProfileDependencyUnavailableException(diagnostics);
        }

        throw new AgentProfilePublishValidationException(diagnostics);
    }

    private static bool HasBoundaryWhitespace(string value) =>
        value.Length > 0 &&
        (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));

    private static AgentProfileAcceptedReceipt AcceptedReceipt(
        string owningScopeId,
        string profileSlug,
        string profileId,
        AgentProfileOperationFact operation,
        DispatchAdmission admission)
    {
        if (!admission.Accepted)
            throw new AgentProfileDispatchRejectedException();

        return new AgentProfileAcceptedReceipt(
            Accepted: true,
            AckStage: "accepted",
            OperationId: operation.OperationId,
            CommandId: admission.CommandId,
            CorrelationId: admission.CorrelationId,
            ActorId: admission.ActorId,
            ProfileId: profileId,
            ResourceUrl: $"/api/scopes/{owningScopeId}/agent-profiles/{profileSlug}");
    }
}
