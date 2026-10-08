using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Hosting.Endpoints.Schedules;

internal interface IWorkflowExternalTriggerProvisioningPort
{
    Task<WorkflowExternalTriggerProvisioningResult> ProvisionAsync(
        ScopeWorkflowSummary workflow,
        ScheduledDispatchConfiguration configuration,
        ScheduledDispatchMutationContext context,
        StudioMemberAutomationHttpAuthority authority,
        CancellationToken ct = default);
}

internal sealed record WorkflowExternalTriggerProvisioningResult(
    ScheduledDispatchMutationReceipt Receipt,
    ScheduledDispatchConfiguration Configuration,
    ScheduledDispatchCredentialSourceKind CredentialSourceKind,
    DateTimeOffset? CredentialExpiresAt,
    string PermissionDigest,
    string PolicyVersion);

internal sealed class WorkflowExternalTriggerProvisioningService : IWorkflowExternalTriggerProvisioningPort
{
    private const string WorkflowInvokeEndpointId = "chat";
    private const string ProvisioningBearerCapabilityScope = "proxy";
    private const string ExternalTriggerTeamId = "workflow-external-trigger";

    private readonly IScheduledDispatchApplicationService _scheduleService;
    private readonly IScheduledInvocationAuthorizationPlanner _authorizationPlanner;
    private readonly IScheduledInvocationAuthorizationRevalidator _authorizationRevalidator;
    private readonly IScheduledInvocationWorkflowEvidenceQueryPort _workflowEvidenceQueryPort;
    private readonly IStudioScheduledCredentialMaterializer _credentialMaterializer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkflowExternalTriggerProvisioningService> _logger;

    public WorkflowExternalTriggerProvisioningService(
        IScheduledDispatchApplicationService scheduleService,
        IScheduledInvocationAuthorizationPlanner authorizationPlanner,
        IScheduledInvocationAuthorizationRevalidator authorizationRevalidator,
        IScheduledInvocationWorkflowEvidenceQueryPort workflowEvidenceQueryPort,
        IStudioScheduledCredentialMaterializer credentialMaterializer,
        TimeProvider timeProvider,
        ILogger<WorkflowExternalTriggerProvisioningService>? logger = null)
    {
        _scheduleService = scheduleService ?? throw new ArgumentNullException(nameof(scheduleService));
        _authorizationPlanner = authorizationPlanner ?? throw new ArgumentNullException(nameof(authorizationPlanner));
        _authorizationRevalidator = authorizationRevalidator ?? throw new ArgumentNullException(nameof(authorizationRevalidator));
        _workflowEvidenceQueryPort = workflowEvidenceQueryPort ?? throw new ArgumentNullException(nameof(workflowEvidenceQueryPort));
        _credentialMaterializer = credentialMaterializer ?? throw new ArgumentNullException(nameof(credentialMaterializer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? NullLogger<WorkflowExternalTriggerProvisioningService>.Instance;
    }

    public async Task<WorkflowExternalTriggerProvisioningResult> ProvisionAsync(
        ScopeWorkflowSummary workflow,
        ScheduledDispatchConfiguration configuration,
        ScheduledDispatchMutationContext context,
        StudioMemberAutomationHttpAuthority authority,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(authority);

        var scheduleId = NormalizeRequired(configuration.ScheduleId, nameof(configuration.ScheduleId));
        var serviceInvocation = configuration.Target.ServiceInvocation
            ?? throw new ArgumentException("External trigger target must be a service invocation.", nameof(configuration));
        var workflowEvidence = await _workflowEvidenceQueryPort.GetAsync(
            workflow.ScopeId,
            workflow.PublishedServiceId,
            workflow.ActiveRevisionId,
            ct);
        if (workflowEvidence == null)
            throw new InvalidOperationException("workflow_authorization_evidence_not_found");

        var authorizationRequest = BuildAuthorizationRequest(
            workflow,
            workflowEvidence,
            authority.AuthenticatedOwner);
        var firstPlan = await _authorizationPlanner.PlanAsync(authorizationRequest, ct);
        if (!firstPlan.Success)
            throw new InvalidOperationException(firstPlan.Detail);

        var plan = firstPlan.Plan!;
        EnsureRequiredDisclosures(plan);
        var validation = await _authorizationRevalidator.RevalidateAsync(
            authorizationRequest,
            BuildConfirmation(authorizationRequest, plan.PermissionDigest, plan.CredentialPolicy.PolicyVersion),
            ct);
        if (!validation.Success)
            throw new InvalidOperationException(validation.Detail);

        var validatedPlan = validation.ValidatedPlan!;
        var authorizationFact = ToScheduleAuthorizationFact(validatedPlan.Plan);
        var callerAuthority = BuildScheduleCallerAuthority(authority.AuthenticatedOwner);
        var owner = BuildOwner(workflow);
        var existing = await _scheduleService.GetAsync(scheduleId, ct);
        var operationKind = existing?.Schedule.CredentialSourceKind ==
                            ScheduledDispatchCredentialSourceKind.ScheduledInvocationAgentKey
            ? TeamAutomationOperationKind.Reauthorize
            : TeamAutomationOperationKind.Create;
        var operationId = BuildOperationIdentity(
            operationKind,
            scheduleId,
            plan.PermissionDigest);
        var effectLocator = _credentialMaterializer.CreateEffectLocator(
            scheduleId,
            operationId,
            ToAuthorizationOwner(authority.AuthenticatedOwner));
        var activationDecision = BuildActivationDecision(
            configuration,
            serviceInvocation,
            owner,
            callerAuthority,
            authorizationFact);
        var mutationDigest = BuildTeamAutomationMutationDigest(activationDecision);
        var idempotencyKey = mutationDigest;

        var began = await _scheduleService.BeginTeamAutomationCredentialOperationAsync(
            new TeamAutomationCredentialOperation(
                scheduleId,
                owner,
                operationId,
                idempotencyKey,
                plan.PermissionDigest,
                plan.CredentialPolicy.PolicyVersion,
                operationKind,
                effectLocator,
                activationDecision,
                mutationDigest),
            ct);
        if (!began.Admission.Accepted)
            throw new InvalidOperationException("workflow_external_trigger_credential_begin_rejected");
        if (!began.Outcome.OwnsEffectAttempt)
        {
            return new WorkflowExternalTriggerProvisioningResult(
                began.Admission,
                configuration,
                ScheduledDispatchCredentialSourceKind.ScheduledInvocationAgentKey,
                null,
                plan.PermissionDigest,
                plan.CredentialPolicy.PolicyVersion);
        }

        var effectAttemptId = NormalizeRequired(
            began.Outcome.EffectAttemptId,
            nameof(began.Outcome.EffectAttemptId));
        var committedEffectLocator = began.Outcome.CredentialEffectLocator
            ?? throw new InvalidOperationException("team_automation_credential_effect_locator_missing");
        if (committedEffectLocator != effectLocator)
            throw new InvalidOperationException("team_automation_credential_effect_locator_conflict");

        StudioScheduledCredential? credential = null;
        var candidateCommitted = began.Outcome.CandidateCredential != null;
        var candidateCommitAttempted = candidateCommitted;
        var activationAttempted = false;
        try
        {
            credential = candidateCommitted
                ? ToStudioScheduledCredential(
                    began.Outcome.CandidateCredential!,
                    began.Outcome.CandidateOwner)
                : await _credentialMaterializer.MaterializeAsync(
                    authority.ProvisioningBearerToken,
                    validatedPlan,
                    scheduleId,
                    operationId,
                    effectLocator,
                    began.Outcome.NewOperationCommitted
                        ? StudioScheduledCredentialMaterializationMode.Initial
                        : StudioScheduledCredentialMaterializationMode.Recovery,
                    BuildOwnerScope(authority.AuthenticatedOwner),
                    ct);
            EnsureCredentialMatchesPlan(credential, plan, _timeProvider.GetUtcNow());
            if (!candidateCommitted)
            {
                candidateCommitAttempted = true;
                var candidate = await _scheduleService.RecordTeamAutomationCredentialCandidateAsync(
                    scheduleId,
                    owner,
                    operationId,
                    idempotencyKey,
                    effectAttemptId,
                    BuildScheduleCredential(credential),
                    credential.Owner,
                    ct);
                if (!candidate.Admission.Accepted)
                    throw new InvalidOperationException("team_automation_candidate_rejected");
                candidateCommitted = true;
            }

            var activatedConfiguration = configuration with
            {
                Target = configuration.Target with
                {
                    ServiceInvocation = serviceInvocation with
                    {
                        Auth = BuildScheduleAuth(credential, callerAuthority),
                        AuthorizationFact = CloneScheduleAuthorizationFact(authorizationFact),
                    },
                },
                TeamAutomationOwner = owner,
            };
            activationAttempted = true;
            var activation = await _scheduleService.CompleteTeamAutomationCredentialOperationAsync(
                scheduleId,
                owner,
                operationId,
                idempotencyKey,
                effectAttemptId,
                BuildScheduleCredential(credential),
                activatedConfiguration,
                ct);
            if (!activation.Admission.Accepted)
                throw new InvalidOperationException("team_automation_activation_rejected");
            _ = await ExecutePendingRevocationAsync(
                activation.Outcome,
                authority.ProvisioningBearerToken,
                authority.AuthenticatedOwner,
                owner,
                CancellationToken.None);
            return new WorkflowExternalTriggerProvisioningResult(
                activation.Admission,
                activatedConfiguration,
                ScheduledDispatchCredentialSourceKind.ScheduledInvocationAgentKey,
                credential.ExpiresAtUtc,
                plan.PermissionDigest,
                plan.CredentialPolicy.PolicyVersion);
        }
        catch (Exception ex)
        {
            if (candidateCommitted && !activationAttempted)
            {
                _ = await TryRecordFailureAsync(
                    scheduleId,
                    owner,
                    operationId,
                    idempotencyKey,
                    effectAttemptId,
                    ToStableFailureCode(ex),
                    CancellationToken.None);
            }
            else if (!candidateCommitAttempted && credential != null)
            {
                try
                {
                    _ = await _credentialMaterializer.RevokeAsync(
                        authority.ProvisioningBearerToken,
                        authority.AuthenticatedOwner,
                        credential,
                        revokeNyxId: true,
                        revokeVault: true,
                        CancellationToken.None);
                }
                catch (Exception revokeEx)
                {
                    _logger.LogWarning(
                        revokeEx,
                        "Failed to revoke external trigger credential after provisioning failure for schedule {ScheduleId}.",
                        scheduleId);
                }
            }

            throw;
        }
    }

    private ScheduledInvocationAuthorizationRequest BuildAuthorizationRequest(
        ScopeWorkflowSummary workflow,
        ScheduledInvocationWorkflowEvidence evidence,
        AuthenticatedAuthorizationOwnerContext authenticatedOwner)
    {
        var capabilities = ResolveWorkflowCapabilities(evidence.ExternalCapabilities);
        var evaluatedAtUtc = _timeProvider.GetUtcNow();
        return new ScheduledInvocationAuthorizationRequest(
            new ScheduledInvocationTarget
            {
                ScheduledAgent = new ScheduledAgentInvocationTarget
                {
                    RegistrationScopeId = workflow.ScopeId,
                    ExecutionScopeId = workflow.ScopeId,
                    ScheduledAgentId = workflow.WorkflowId,
                },
            },
            authenticatedOwner,
            capabilities,
            evidence.ServiceGrantRequirement,
            evaluatedAtUtc.AddDays(30),
            evaluatedAtUtc,
            [new AuthorizationSourceStamp
            {
                SourceKind = AuthorizationSourceKind.WorkflowRevision,
                SourceId = workflow.ActiveRevisionId,
                StateVersion = evidence.StateVersion,
            }]);
    }

    private static IReadOnlyList<NyxIdUserServiceCapabilityRef> ResolveWorkflowCapabilities(
        IEnumerable<ExternalWorkflowCapabilityRef> capabilities)
    {
        var services = new SortedDictionary<string, NyxIdUserServiceCapabilityRef>(StringComparer.Ordinal);
        foreach (var capability in capabilities)
        {
            NyxIdUserServiceCapabilityRef? service = capability.CapabilityCase switch
            {
                ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService =>
                    capability.NyxIdUserService.Clone(),
                ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest =>
                    new NyxIdUserServiceCapabilityRef
                    {
                        UserServiceId = capability.NyxIdUserRequest.Request?.UserServiceId ?? string.Empty,
                        ServiceSlugSnapshot = capability.NyxIdUserRequest.ServiceSlugSnapshot,
                    },
                ExternalWorkflowCapabilityRef.CapabilityOneofCase.CodeExecution =>
                    new NyxIdUserServiceCapabilityRef
                    {
                        UserServiceId = capability.CodeExecution.UserServiceId,
                        ServiceSlugSnapshot = capability.CodeExecution.ServiceSlugSnapshot,
                    },
                _ => null,
            };
            if (service == null)
                continue;
            var userServiceId = NormalizeRequired(service.UserServiceId, nameof(service.UserServiceId));
            if (!services.TryGetValue(userServiceId, out var existing))
            {
                services[userServiceId] = service;
                continue;
            }
            if (existing.ServiceSlugSnapshot.Length == 0 && service.ServiceSlugSnapshot.Length > 0)
                existing.ServiceSlugSnapshot = service.ServiceSlugSnapshot;
        }
        return services.Values.ToArray();
    }

    private static TeamMemberAutomationOwner BuildOwner(ScopeWorkflowSummary workflow) =>
        new(
            NormalizeRequired(workflow.ScopeId, nameof(workflow.ScopeId)),
            NormalizeRequired(workflow.WorkflowId, nameof(workflow.WorkflowId)),
            ExternalTriggerTeamId);

    private static ScheduledCallerNyxIdAuthority BuildScheduleCallerAuthority(
        AuthenticatedAuthorizationOwnerContext owner)
    {
        var bindingId = NormalizeRequired(owner.VerifiedBindingId, nameof(owner.VerifiedBindingId));
        return new ScheduledCallerNyxIdAuthority
        {
            Platform = NormalizeRequired(owner.SubjectPlatform, nameof(owner.SubjectPlatform)),
            Tenant = NormalizeOptional(owner.SubjectTenant) ?? string.Empty,
            ExternalUserId = NormalizeRequired(owner.SubjectExternalUserId, nameof(owner.SubjectExternalUserId)),
            Scope = ProvisioningBearerCapabilityScope,
            BindingId = bindingId,
        };
    }

    private static TeamAutomationActivationDecision BuildActivationDecision(
        ScheduledDispatchConfiguration configuration,
        ScheduledServiceInvocationTargetDescriptor serviceInvocation,
        TeamMemberAutomationOwner owner,
        ScheduledCallerNyxIdAuthority callerAuthority,
        ScheduledInvocationAuthorizationFact authorizationFact) =>
        new(
            configuration.ScheduleId,
            configuration.DisplayName,
            owner,
            serviceInvocation.Identity,
            serviceInvocation.EndpointId,
            serviceInvocation.Payload.Clone(),
            callerAuthority.Clone(),
            CloneScheduleAuthorizationFact(authorizationFact),
            configuration.CronExpression,
            configuration.Timezone,
            configuration.Enabled,
            configuration.ScheduleKind,
            configuration.Headers,
            configuration.ScheduleMode,
            configuration.OneShotFireAt,
            configuration.CredentialRequirementTargetKind,
            serviceInvocation.RevisionId ?? string.Empty,
            serviceInvocation.Caller);

    private static ScheduledInvocationAuthorizationConfirmation BuildConfirmation(
        ScheduledInvocationAuthorizationRequest request,
        string permissionDigest,
        string policyVersion) =>
        new()
        {
            InvocationTarget = request.InvocationTarget.Clone(),
            Owner = request.Owner.Clone(),
            SchemaVersion = ScheduledInvocationAuthorizationContractVersions.Schema,
            PolicyVersion = NormalizeRequired(policyVersion, nameof(policyVersion)),
            PermissionDigest = NormalizeRequired(permissionDigest, nameof(permissionDigest)),
        };

    private static ScheduledServiceInvocationAuth BuildScheduleAuth(
        StudioScheduledCredential credential,
        ScheduledCallerNyxIdAuthority callerAuthority) =>
        new(BuildScheduleCredential(credential))
        {
            CallerAuthority = callerAuthority.Clone(),
        };

    private static ScheduledInvocationAgentKeyCredentialReference BuildScheduleCredential(
        StudioScheduledCredential credential) =>
        new(
            credential.SecretReference.Clone(),
            credential.ApiKeyId,
            credential.ExpiresAtUtc.ToUnixTimeMilliseconds(),
            credential.DurableOperationGrants?.Select(static grant => grant.Clone()).ToArray());

    private static StudioScheduledCredential ToStudioScheduledCredential(
        ScheduledInvocationAgentKeyCredentialReference credential,
        ScheduledInvocationAuthorizationOwner? owner)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return new StudioScheduledCredential(
            NormalizeRequired(credential.ApiKeyId, nameof(credential.ApiKeyId)),
            credential.SecretReference?.Clone() ?? throw new InvalidOperationException("revocation_descriptor_missing"),
            DateTimeOffset.FromUnixTimeMilliseconds(credential.KeyExpiresAtUnixMs),
            owner ?? throw new InvalidOperationException("credential_owner_missing"),
            credential.DurableOperationGrants?.Select(static grant => grant.Clone()).ToArray());
    }

    private async Task<bool> ExecutePendingRevocationAsync(
        TeamAutomationOperationCommittedOutcome outcome,
        string bearerToken,
        AuthenticatedAuthorizationOwnerContext authenticatedOwner,
        TeamMemberAutomationOwner owner,
        CancellationToken ct)
    {
        if (!outcome.NyxIdRevocationPending && !outcome.VaultRevocationPending)
            return true;
        if (!outcome.OwnsEffectAttempt)
            return false;

        var result = outcome.PendingRevocationCredential == null || outcome.PendingRevocationOwner == null
            ? new StudioScheduledCredentialRevocationResult(
                !outcome.NyxIdRevocationPending,
                !outcome.VaultRevocationPending,
                "revocation_descriptor_missing")
            : await RevokePendingCredentialAsync(
                bearerToken,
                authenticatedOwner,
                outcome,
                ct);
        var completion = await _scheduleService.CompleteTeamAutomationRevocationAsync(
            outcome.ScheduleId,
            owner,
            outcome.OperationId,
            outcome.IdempotencyKey,
            NormalizeRequired(outcome.EffectAttemptId, nameof(outcome.EffectAttemptId)),
            result.NyxIdRevoked,
            result.VaultRevoked,
            result.ErrorCode,
            ct);
        return completion.Admission.Accepted && result.NyxIdRevoked && result.VaultRevoked;
    }

    private async Task<StudioScheduledCredentialRevocationResult> RevokePendingCredentialAsync(
        string bearerToken,
        AuthenticatedAuthorizationOwnerContext authenticatedOwner,
        TeamAutomationOperationCommittedOutcome outcome,
        CancellationToken ct)
    {
        var pending = outcome.PendingRevocationCredential!;
        var credential = new StudioScheduledCredential(
            pending.ApiKeyId,
            pending.SecretReference.Clone(),
            DateTimeOffset.FromUnixTimeMilliseconds(pending.KeyExpiresAtUnixMs),
            outcome.PendingRevocationOwner!,
            pending.DurableOperationGrants?.Select(static grant => grant.Clone()).ToArray());
        try
        {
            return await _credentialMaterializer.RevokeAsync(
                bearerToken,
                authenticatedOwner,
                credential,
                outcome.NyxIdRevocationPending,
                outcome.VaultRevocationPending,
                ct);
        }
        catch (UnauthorizedAccessException)
        {
            return new StudioScheduledCredentialRevocationResult(
                !outcome.NyxIdRevocationPending,
                !outcome.VaultRevocationPending,
                "credential_owner_mismatch");
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new StudioScheduledCredentialRevocationResult(
                !outcome.NyxIdRevocationPending,
                !outcome.VaultRevocationPending,
                "credential_revocation_transient");
        }
    }

    private async Task<TeamAutomationOperationCommittedOutcome?> TryRecordFailureAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        string effectAttemptId,
        string errorCode,
        CancellationToken ct)
    {
        try
        {
            var failure = await _scheduleService.FailTeamAutomationCredentialOperationAsync(
                scheduleId,
                owner,
                operationId,
                idempotencyKey,
                effectAttemptId,
                errorCode,
                ct);
            return failure.Outcome;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to record external trigger credential operation failure for schedule {ScheduleId} and operation {OperationId}.",
                scheduleId,
                operationId);
            return null;
        }
    }

    private static ScheduledInvocationAuthorizationFact ToScheduleAuthorizationFact(
        ScheduledInvocationAuthorizationPlan plan)
    {
        var policy = plan.CredentialPolicy
            ?? throw new InvalidOperationException("scheduled_authorization_policy_missing");
        var catalog = plan.CatalogAuthority;
        var disclosure = plan.Disclosures.ToHashSet();
        return new ScheduledInvocationAuthorizationFact(
            plan.PermissionDigest,
            policy.PolicyVersion,
            new ScheduledInvocationAuthorizationOwner(
                plan.Owner.Authority,
                plan.Owner.OwnerKind.ToString(),
                plan.Owner.OwnerSubject),
            plan.NyxIdServiceGrants.Select(static grant =>
                new ScheduledInvocationAuthorizationServiceGrant(
                    grant.UserServiceId,
                    grant.NodeIds.ToArray(),
                    grant.NodeGrantRequirement == AuthorizationGrantRequirement.NotRequired)).ToArray(),
            string.Join(' ', policy.Scopes.Select(ToScopeName).Order(StringComparer.Ordinal)),
            policy.ExpiresAt.ToDateTimeOffset(),
            policy.ServiceGrantRequirement == AuthorizationGrantRequirement.NotRequired,
            new ScheduledInvocationAuthorizationDisclosure(
                disclosure.Contains(ScheduledInvocationDisclosure.DedicatedCredential),
                disclosure.Contains(ScheduledInvocationDisclosure.AevatarSecretCustody),
                !disclosure.Contains(ScheduledInvocationDisclosure.BrowserNeverReceivesSecret),
                disclosure.Contains(ScheduledInvocationDisclosure.DeleteRevokesCredential),
                !disclosure.Contains(ScheduledInvocationDisclosure.PauseResumePreservesCredential)),
            new ScheduledInvocationAuthorizationAuthority(
                SourceVersion(plan, AuthorizationSourceKind.StudioMember),
                SourceVersion(plan, AuthorizationSourceKind.WorkflowRevision),
                SourceVersion(plan, AuthorizationSourceKind.ConnectorCatalog),
                SourceVersion(plan, AuthorizationSourceKind.OwnerLlmRoute),
                catalog?.ActorStateVersion ?? 0,
                catalog?.ObservedAt?.ToDateTimeOffset() ?? default,
                catalog?.FreshUntil?.ToDateTimeOffset() ?? default,
                catalog?.ContentDigest ?? string.Empty,
                catalog?.ContractVersion ?? string.Empty,
                catalog?.PolicyVersion ?? string.Empty,
                catalog?.EvaluatedAt?.ToDateTimeOffset() ?? default),
            plan.OwnerLlmSelection?.Clone());
    }

    private static string ToScopeName(NyxIdCredentialScope scope) => scope switch
    {
        NyxIdCredentialScope.Read => "read",
        NyxIdCredentialScope.Proxy => "proxy",
        _ => throw new InvalidOperationException("scheduled_authorization_scope_invalid"),
    };

    private static long SourceVersion(
        ScheduledInvocationAuthorizationPlan plan,
        AuthorizationSourceKind sourceKind) =>
        plan.SourceStamps.FirstOrDefault(stamp => stamp.SourceKind == sourceKind)?.StateVersion ?? 0;

    private static ScheduledInvocationAuthorizationFact CloneScheduleAuthorizationFact(
        ScheduledInvocationAuthorizationFact fact) =>
        new(
            fact.PermissionDigest,
            fact.PolicyVersion,
            new ScheduledInvocationAuthorizationOwner(
                fact.Owner.Authority,
                fact.Owner.OwnerKind,
                fact.Owner.OwnerSubject),
            fact.ServiceGrants.Select(static grant =>
                new ScheduledInvocationAuthorizationServiceGrant(
                    grant.ServiceId,
                    grant.NodeIds.ToArray(),
                    grant.NodeGrantsNotRequired)).ToArray(),
            fact.Scopes,
            fact.ExpiresAt,
            fact.ServiceGrantsNotRequired,
            new ScheduledInvocationAuthorizationDisclosure(
                fact.Disclosure.DedicatedToSchedule,
                fact.Disclosure.SecretManagedByAevatar,
                fact.Disclosure.BrowserReceivesRawKey,
                fact.Disclosure.DeleteRevokesCredential,
                fact.Disclosure.PauseResumeRevokesCredential),
            new ScheduledInvocationAuthorizationAuthority(
                fact.Authority.MemberStateVersion,
                fact.Authority.WorkflowStateVersion,
                fact.Authority.ConnectorStateVersion,
                fact.Authority.OwnerLlmStateVersion,
                fact.Authority.CatalogStateVersion,
                fact.Authority.CatalogObservedAt,
                fact.Authority.CatalogFreshUntil,
                fact.Authority.CatalogContentDigest,
                fact.Authority.CatalogContractVersion,
                fact.Authority.CatalogPolicyVersion,
                fact.Authority.CatalogEvaluatedAt),
            fact.OwnerLLMSelection?.Clone());

    private static string BuildTeamAutomationMutationDigest(TeamAutomationActivationDecision decision)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendDigestValue(hash, "aevatar.workflow-external-trigger-mutation.v1");
        AppendDigestValue(hash, decision.ScheduleId);
        AppendDigestValue(hash, decision.DisplayName);
        AppendDigestValue(hash, decision.Owner.ScopeId);
        AppendDigestValue(hash, decision.Owner.MemberId);
        AppendDigestValue(hash, decision.Owner.TeamId);
        AppendDigestValue(hash, decision.ServiceIdentity.TenantId);
        AppendDigestValue(hash, decision.ServiceIdentity.AppId);
        AppendDigestValue(hash, decision.ServiceIdentity.Namespace);
        AppendDigestValue(hash, decision.ServiceIdentity.ServiceId);
        AppendDigestValue(hash, decision.EndpointId);
        AppendDigestValue(hash, decision.Payload.TypeUrl);
        AppendDigestBytes(hash, decision.Payload.Value.Span);
        AppendDigestValue(hash, decision.CallerAuthority.Platform);
        AppendDigestValue(hash, decision.CallerAuthority.Tenant);
        AppendDigestValue(hash, decision.CallerAuthority.ExternalUserId);
        AppendDigestValue(hash, decision.CallerAuthority.Scope);
        AppendDigestValue(hash, decision.CallerAuthority.BindingId);
        AppendAuthorizationFactDigest(hash, decision.AuthorizationFact);
        AppendDigestValue(hash, decision.CronExpression);
        AppendDigestValue(hash, decision.Timezone);
        AppendDigestBoolean(hash, decision.Enabled);
        AppendDigestInt64(hash, (long)decision.ScheduleKind);
        AppendDigestInt64(hash, decision.Headers.Count);
        foreach (var (key, value) in decision.Headers.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            AppendDigestValue(hash, key);
            AppendDigestValue(hash, value);
        }
        AppendDigestInt64(hash, (long)decision.ScheduleMode);
        AppendDigestBoolean(hash, decision.OneShotFireAt.HasValue);
        if (decision.OneShotFireAt.HasValue)
            AppendDigestInt64(hash, decision.OneShotFireAt.Value.ToUniversalTime().UtcTicks);
        AppendDigestInt64(hash, (long)decision.CredentialRequirementTargetKind);
        AppendDigestValue(hash, decision.RevisionId);
        AppendDigestBoolean(hash, decision.Caller != null);
        if (decision.Caller != null)
        {
            AppendDigestValue(hash, decision.Caller.ServiceKey);
            AppendDigestValue(hash, decision.Caller.TenantId);
            AppendDigestValue(hash, decision.Caller.AppId);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendAuthorizationFactDigest(
        IncrementalHash hash,
        ScheduledInvocationAuthorizationFact fact)
    {
        AppendDigestValue(hash, fact.PermissionDigest);
        AppendDigestValue(hash, fact.PolicyVersion);
        AppendDigestValue(hash, fact.Owner.Authority);
        AppendDigestValue(hash, fact.Owner.OwnerKind);
        AppendDigestValue(hash, fact.Owner.OwnerSubject);
        var grants = fact.ServiceGrants
            .OrderBy(static grant => grant.ServiceId, StringComparer.Ordinal)
            .ThenBy(static grant => grant.NodeGrantsNotRequired)
            .ThenBy(static grant => string.Join('\n', grant.NodeIds.Order(StringComparer.Ordinal)), StringComparer.Ordinal)
            .ToArray();
        AppendDigestInt64(hash, grants.Length);
        foreach (var grant in grants)
        {
            AppendDigestValue(hash, grant.ServiceId);
            AppendDigestBoolean(hash, grant.NodeGrantsNotRequired);
            var nodeIds = grant.NodeIds.Order(StringComparer.Ordinal).ToArray();
            AppendDigestInt64(hash, nodeIds.Length);
            foreach (var nodeId in nodeIds)
                AppendDigestValue(hash, nodeId);
        }
        AppendDigestValue(hash, fact.Scopes);
        AppendDigestInt64(hash, fact.ExpiresAt.ToUniversalTime().UtcTicks);
        AppendDigestBoolean(hash, fact.ServiceGrantsNotRequired);
        AppendDigestBoolean(hash, fact.Disclosure.DedicatedToSchedule);
        AppendDigestBoolean(hash, fact.Disclosure.SecretManagedByAevatar);
        AppendDigestBoolean(hash, fact.Disclosure.BrowserReceivesRawKey);
        AppendDigestBoolean(hash, fact.Disclosure.DeleteRevokesCredential);
        AppendDigestBoolean(hash, fact.Disclosure.PauseResumeRevokesCredential);
        AppendDigestInt64(hash, fact.Authority.MemberStateVersion);
        AppendDigestInt64(hash, fact.Authority.WorkflowStateVersion);
        AppendDigestInt64(hash, fact.Authority.ConnectorStateVersion);
        AppendDigestInt64(hash, fact.Authority.OwnerLlmStateVersion);
        AppendDigestInt64(hash, fact.Authority.CatalogStateVersion);
        AppendDigestInt64(hash, fact.Authority.CatalogObservedAt.ToUniversalTime().UtcTicks);
        AppendDigestInt64(hash, fact.Authority.CatalogFreshUntil.ToUniversalTime().UtcTicks);
        AppendDigestValue(hash, fact.Authority.CatalogContentDigest);
        AppendDigestValue(hash, fact.Authority.CatalogContractVersion);
        AppendDigestValue(hash, fact.Authority.CatalogPolicyVersion);
        AppendDigestInt64(hash, fact.Authority.CatalogEvaluatedAt.ToUniversalTime().UtcTicks);
        AppendDigestBoolean(hash, fact.OwnerLLMSelection != null);
        if (fact.OwnerLLMSelection != null)
        {
            AppendDigestInt64(hash, (long)fact.OwnerLLMSelection.RouteKind);
            AppendDigestValue(hash, fact.OwnerLLMSelection.RouteValue);
            AppendDigestValue(hash, fact.OwnerLLMSelection.NyxIdUserServiceId);
            AppendDigestValue(hash, fact.OwnerLLMSelection.ServiceSlugSnapshot);
            AppendDigestValue(hash, fact.OwnerLLMSelection.Model);
        }
    }

    private static void AppendDigestValue(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void AppendDigestBytes(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void AppendDigestBoolean(IncrementalHash hash, bool value) =>
        hash.AppendData(value ? [1] : [0]);

    private static void AppendDigestInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void EnsureRequiredDisclosures(ScheduledInvocationAuthorizationPlan plan)
    {
        var disclosures = plan.Disclosures.ToHashSet();
        var required = new[]
        {
            ScheduledInvocationDisclosure.DedicatedCredential,
            ScheduledInvocationDisclosure.AevatarSecretCustody,
            ScheduledInvocationDisclosure.BrowserNeverReceivesSecret,
            ScheduledInvocationDisclosure.DeleteRevokesCredential,
            ScheduledInvocationDisclosure.PauseResumePreservesCredential,
        };
        if (required.Any(disclosure => !disclosures.Contains(disclosure)))
            throw new InvalidOperationException("scheduled_authorization_disclosures_missing");
    }

    private static void EnsureCredentialMatchesPlan(
        StudioScheduledCredential credential,
        ScheduledInvocationAuthorizationPlan plan,
        DateTimeOffset now)
    {
        if (credential.ExpiresAtUtc <= now ||
            credential.ExpiresAtUtc > plan.CredentialPolicy.ExpiresAt.ToDateTimeOffset())
            throw new InvalidOperationException("scheduled_credential_expiry_mismatch");
        if (!string.Equals(
                credential.SecretReference.Purpose,
                CredentialSecretPurposes.ScheduledInvocationAgentKey,
                StringComparison.Ordinal))
            throw new InvalidOperationException("scheduled_credential_purpose_mismatch");
        var owner = new ScheduledInvocationAuthorizationOwner(
            plan.Owner.Authority,
            plan.Owner.OwnerKind.ToString(),
            plan.Owner.OwnerSubject);
        if (credential.Owner != owner)
            throw new InvalidOperationException("credential_owner_mismatch");
    }

    private static ScheduledInvocationAuthorizationOwner ToAuthorizationOwner(
        AuthenticatedAuthorizationOwnerContext authenticatedOwner) =>
        new(
            NormalizeRequired(authenticatedOwner.Owner.Authority, nameof(authenticatedOwner.Owner.Authority)),
            authenticatedOwner.Owner.OwnerKind.ToString(),
            NormalizeRequired(authenticatedOwner.Owner.OwnerSubject, nameof(authenticatedOwner.Owner.OwnerSubject)));

    private static OwnerScope BuildOwnerScope(AuthenticatedAuthorizationOwnerContext owner) =>
        string.Equals(owner.SubjectPlatform, OwnerScope.NyxIdPlatform, StringComparison.Ordinal)
            ? OwnerScope.ForNyxIdNative(owner.Owner.OwnerSubject)
            : OwnerScope.ForChannel(
                owner.Owner.OwnerSubject,
                owner.SubjectPlatform.Trim().ToLowerInvariant(),
                NormalizeRequired(owner.SubjectTenant, nameof(owner.SubjectTenant)),
                owner.SubjectExternalUserId);

    private static string BuildOperationIdentity(
        TeamAutomationOperationKind kind,
        string scheduleId,
        string permissionDigest)
    {
        var identity = Encoding.UTF8.GetBytes($"{kind}\n{scheduleId}\n{permissionDigest}");
        return $"workflow-external-trigger:{Convert.ToHexStringLower(SHA256.HashData(identity).AsSpan(0, 16))}";
    }

    private static string ToStableFailureCode(Exception exception) => exception switch
    {
        OperationCanceledException => "operation_cancelled",
        InvalidOperationException { Message: { Length: > 0 } message } when IsStableErrorCode(message) => message,
        _ => "workflow_external_trigger_credential_apply_failed",
    };

    private static bool IsStableErrorCode(string value) =>
        value.Length <= 128 && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    private static string NormalizeRequired(string? value, string paramName)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? throw new ArgumentException($"{paramName} is required.", paramName)
            : normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
