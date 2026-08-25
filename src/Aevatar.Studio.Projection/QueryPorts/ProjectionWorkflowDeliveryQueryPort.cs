using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.WorkflowDelivery;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;
using ApplicationConnectionStatus = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryConnectionStatus;
using ApplicationAcceptanceRunStatus = Aevatar.Studio.Application.Studio.Abstractions.WorkflowAcceptanceRunStatus;
using ApplicationArtifactKind = Aevatar.Studio.Application.Studio.Abstractions.WorkflowInstallationArtifactKind;
using ApplicationArtifactVerificationStatus = Aevatar.Studio.Application.Studio.Abstractions.WorkflowInstallationArtifactVerificationStatus;
using ApplicationLifecycleStatus = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryLifecycleStatus;
using ApplicationScheduleReadinessStatus = Aevatar.Studio.Application.Studio.Abstractions.WorkflowScheduleReadinessStatus;
using ApplicationTriggerKind = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryTriggerKind;
using ApplicationVariableKind = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryVariableKind;
using ApplicationInstallationStatus = Aevatar.Studio.Application.Studio.Abstractions.WorkflowInstallationStatus;
using ApplicationConnectionSlotDefinition = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryConnectionSlotDefinition;
using ApplicationAcceptanceMode = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryAcceptanceMode;
using ApplicationAcceptancePolicy = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryAcceptancePolicy;
using ApplicationAcceptanceDateProjection = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryAcceptanceDateProjection;
using ApplicationAcceptanceInputBinding = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryAcceptanceInputBinding;
using ApplicationAcceptanceInputRecipe = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryAcceptanceInputRecipe;
using ApplicationInstallationCreatedAtUtcInput = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryInstallationCreatedAtUtcInput;
using ApplicationAuthenticatedOwnerExternalUserIdInput = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryAuthenticatedOwnerExternalUserIdInput;
using ApplicationConfirmationReference = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryConfirmationReference;
using ApplicationAcceptanceRunEvidence = Aevatar.Studio.Application.Studio.Abstractions.WorkflowAcceptanceRunReadinessEvidence;
using ApplicationArtifactEvidence = Aevatar.Studio.Application.Studio.Abstractions.WorkflowInstallationArtifactEvidence;
using ApplicationBoundRevisionEvidence = Aevatar.Studio.Application.Studio.Abstractions.WorkflowBoundRevisionReadinessEvidence;
using ApplicationInstallationReadinessEvidence = Aevatar.Studio.Application.Studio.Abstractions.WorkflowInstallationReadinessEvidence;
using ApplicationNoTriggerEvidence = Aevatar.Studio.Application.Studio.Abstractions.WorkflowNoTriggerReadinessEvidence;
using ApplicationPublishedServiceEvidence = Aevatar.Studio.Application.Studio.Abstractions.WorkflowPublishedServiceReadinessEvidence;
using ApplicationScheduleEvidence = Aevatar.Studio.Application.Studio.Abstractions.WorkflowScheduleReadinessEvidence;
using ApplicationTriggerIntent = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryTriggerIntent;
using ApplicationTriggerReadinessEvidence = Aevatar.Studio.Application.Studio.Abstractions.WorkflowTriggerReadinessEvidence;
using ApplicationVariableDefinition = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryVariableDefinition;
using ProtoConnectionStatus = Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryConnectionStatus;
using ProtoAcceptanceRunStatus = Aevatar.GAgents.WorkflowDelivery.WorkflowAcceptanceRunStatus;
using ProtoArtifactKind = Aevatar.GAgents.WorkflowDelivery.WorkflowInstallationArtifactKind;
using ProtoArtifactVerificationStatus = Aevatar.GAgents.WorkflowDelivery.WorkflowInstallationArtifactVerificationStatus;
using ProtoLifecycleStatus = Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryLifecycleStatus;
using ProtoScheduleReadinessStatus = Aevatar.GAgents.WorkflowDelivery.WorkflowScheduleReadinessStatus;
using ProtoTriggerKind = Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryTriggerKind;
using ProtoVariableKind = Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryVariableKind;
using ProtoInstallationStatus = Aevatar.GAgents.WorkflowDelivery.WorkflowInstallationStatus;
using ProtoAcceptanceMode = Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryAcceptanceMode;
using ProtoAcceptanceDateProjection = Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryAcceptanceDateProjection;
using ProtoAcceptanceInputBinding = Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryAcceptanceInputBinding;
using ProtoAcceptanceInputRecipe = Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryAcceptanceInputRecipe;

namespace Aevatar.Studio.Projection.QueryPorts;

public sealed class ProjectionWorkflowDeliveryQueryPort : IWorkflowDeliveryQueryPort
{
    public const int MaxPageSize = 200;

    private readonly IProjectionDocumentReader<WorkflowDeliveryCurrentStateDocument, string> _documentReader;

    public ProjectionWorkflowDeliveryQueryPort(
        IProjectionDocumentReader<WorkflowDeliveryCurrentStateDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<WorkflowDeliverySnapshot?> GetAsync(
        string deliveryId,
        CancellationToken ct = default)
    {
        var normalizedDeliveryId = NormalizeRequired(deliveryId, nameof(deliveryId));
        var actorId = WorkflowDeliveryConventions.BuildActorId(normalizedDeliveryId);
        var document = await _documentReader.GetAsync(actorId, ct);
        if (document == null)
            return null;

        EnsureDocumentIdentity(document, actorId, normalizedDeliveryId);
        return Map(document);
    }

    public async Task<WorkflowDeliverySnapshot?> GetForScopeAsync(
        string deliveryId,
        string targetScopeId,
        CancellationToken ct = default)
    {
        var normalizedDeliveryId = NormalizeRequired(deliveryId, nameof(deliveryId));
        var normalizedScopeId = NormalizeRequired(targetScopeId, nameof(targetScopeId));
        var actorId = WorkflowDeliveryConventions.BuildActorId(normalizedDeliveryId);
        var result = await _documentReader.QueryAsync(
            new ProjectionDocumentQuery
            {
                Filters =
                [
                    Equal("id", actorId),
                    Equal("delivery_id", normalizedDeliveryId),
                    Equal("target_scope_id", normalizedScopeId),
                ],
                Take = 2,
            },
            ct);

        var matches = result.Items.Where(document =>
                string.Equals(document.Id, actorId, StringComparison.Ordinal) &&
                string.Equals(document.DeliveryId, normalizedDeliveryId, StringComparison.Ordinal) &&
                string.Equals(document.TargetScopeId, normalizedScopeId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
            return null;
        if (matches.Length != 1)
            throw new InvalidOperationException("Projected workflow delivery identity is not unique.");

        EnsureDocumentIdentity(matches[0], actorId, normalizedDeliveryId);
        return Map(matches[0]);
    }

    public async Task<WorkflowDeliveryListResult> ListAsync(
        WorkflowDeliveryListQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var targetScopeId = NormalizeOptional(query.TargetScopeId);
        ProtoInstallationStatus? installationStatus = query.InstallationStatus == null
            ? null
            : MapInstallationStatusFilter(query.InstallationStatus.Value);
        var filters = new List<ProjectionDocumentFilter>();
        if (targetScopeId != null)
            filters.Add(Equal("target_scope_id", targetScopeId));
        if (installationStatus != null)
            filters.Add(Equal("installation.status.keyword", MapInstallationStatusStorageValue(installationStatus.Value)));
        var pageSize = query.PageSize is > 0 and <= MaxPageSize
            ? query.PageSize.Value
            : MaxPageSize;
        var result = await _documentReader.QueryAsync(
            new ProjectionDocumentQuery
            {
                Filters = filters,
                Take = pageSize,
                Cursor = NormalizeOptional(query.PageToken),
            },
            ct);

        var items = result.Items
            .Where(document => targetScopeId == null ||
                string.Equals(document.TargetScopeId, targetScopeId, StringComparison.Ordinal))
            .Where(document => installationStatus == null ||
                document.Installation?.Status == installationStatus)
            .Select(document =>
            {
                var deliveryId = NormalizeRequired(document.DeliveryId, "projected delivery_id");
                EnsureDocumentIdentity(
                    document,
                    WorkflowDeliveryConventions.BuildActorId(deliveryId),
                    deliveryId);
                return Map(document);
            })
            .ToArray();
        return new WorkflowDeliveryListResult(items, NormalizeOptional(result.NextCursor));
    }

    public async Task<WorkflowDeliverySnapshot?> FindByInstallationAsync(
        string scopeId,
        string installationId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedInstallationId = NormalizeRequired(installationId, nameof(installationId));
        var result = await _documentReader.QueryAsync(
            new ProjectionDocumentQuery
            {
                Filters =
                [
                    Equal("target_scope_id", normalizedScopeId),
                    Equal("installation.scope_id", normalizedScopeId),
                    Equal("installation.installation_id", normalizedInstallationId),
                ],
                Take = 2,
            },
            ct);

        var matches = result.Items.Where(document =>
                string.Equals(document.TargetScopeId, normalizedScopeId, StringComparison.Ordinal) &&
                string.Equals(document.Installation?.ScopeId, normalizedScopeId, StringComparison.Ordinal) &&
                string.Equals(document.Installation?.InstallationId, normalizedInstallationId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
            return null;
        if (matches.Length != 1)
            throw new InvalidOperationException("Projected workflow installation identity is not unique.");

        var deliveryId = NormalizeRequired(matches[0].DeliveryId, "projected delivery_id");
        EnsureDocumentIdentity(
            matches[0],
            WorkflowDeliveryConventions.BuildActorId(deliveryId),
            deliveryId);
        return Map(matches[0]);
    }

    private static WorkflowDeliverySnapshot Map(WorkflowDeliveryCurrentStateDocument document) =>
        new(
            document.DeliveryId,
            MapPackage(document.Package ?? throw InvalidDocument("package is missing")),
            NormalizeRequired(document.TargetScopeId, "projected target_scope_id"),
            RequiredDateTime(document.ExpiresAtUtc, "expires_at_utc"),
            NormalizeOptional(document.Note),
            MapLifecycleStatus(document.LifecycleStatus),
            NormalizeRequired(document.CreatedBy, "projected created_by"),
            RequiredDateTime(document.CreatedAtUtc, "created_at_utc"),
            OptionalDateTime(document.AccessedAtUtc),
            NormalizeOptional(document.RevokedBy),
            OptionalDateTime(document.RevokedAtUtc),
            document.Connections.Select(MapConnection).ToArray(),
            MapInstallation(document.Installation),
            document.StateVersion,
            RequiredDateTime(document.UpdatedAt, "projected updated_at"));

    private static WorkflowDeliveryPackageSnapshot MapPackage(WorkflowPackageVersionSnapshot package) =>
        new(
            NormalizeRequired(package.PackageId, "projected package_id"),
            NormalizeRequired(package.PackageVersionId, "projected package_version_id"),
            NormalizeRequired(package.WorkflowName, "projected workflow_name"),
            NormalizeRequired(package.Version, "projected package version"),
            package.DisplayName,
            package.Description,
            RequireOpaqueContent(package.SourceYaml, "projected source_yaml"),
            NormalizeRequired(package.SourceHash, "projected source_hash"),
            NormalizeRequired(package.PackageHash, "projected package_hash"),
            package.VariableSchema.Select(MapVariable).ToArray(),
            package.ConnectionSlots.Select(MapConnectionSlot).ToArray(),
            package.Capabilities.ToArray(),
            package.RiskSummary,
            package.ParserDiagnostics.ToArray(),
            MapAcceptancePolicy(package.AcceptancePolicy),
            NormalizeRequired(package.CreatedBy, "projected package created_by"),
            RequiredDateTime(package.CreatedAtUtc, "package.created_at_utc"));

    private static ApplicationAcceptancePolicy MapAcceptancePolicy(
        Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryAcceptancePolicy? value)
    {
        if (value == null)
            throw InvalidDocument("package acceptance_policy is missing");
        var mode = value.Mode switch
        {
            ProtoAcceptanceMode.AutomaticPreview => ApplicationAcceptanceMode.AutomaticPreview,
            ProtoAcceptanceMode.Manual => ApplicationAcceptanceMode.Manual,
            _ => ApplicationAcceptanceMode.Unspecified,
        };
        if (mode == ApplicationAcceptanceMode.Unspecified)
            throw InvalidDocument("package acceptance_policy mode is unspecified");
        return new ApplicationAcceptancePolicy(
            mode,
            NormalizeOptional(value.Limitation),
            value.Input == null
                ? new ApplicationAcceptanceInputRecipe(new Struct(), [])
                : MapAcceptanceInput(value.Input),
            InputDeclared: value.Input != null);
    }

    private static ApplicationAcceptanceInputRecipe MapAcceptanceInput(
        ProtoAcceptanceInputRecipe value)
    {
        try
        {
            WorkflowDeliveryConventions.ValidateAcceptanceInput(value);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                "Projected workflow delivery document package acceptance_policy input is invalid.",
                exception);
        }

        return new ApplicationAcceptanceInputRecipe(
            value.Literals.Clone(),
            value.Bindings.Select(MapAcceptanceInputBinding).ToArray());
    }

    private static ApplicationAcceptanceInputBinding MapAcceptanceInputBinding(
        ProtoAcceptanceInputBinding value) =>
        new(
            value.Key,
            value.Prefix,
            value.Suffix,
            value.SourceCase switch
            {
                ProtoAcceptanceInputBinding.SourceOneofCase.InstallationCreatedAtUtc =>
                    new ApplicationInstallationCreatedAtUtcInput(
                        value.InstallationCreatedAtUtc.DateProjection switch
                        {
                            ProtoAcceptanceDateProjection.UtcDate =>
                                ApplicationAcceptanceDateProjection.UtcDate,
                            ProtoAcceptanceDateProjection.UtcYearMonth =>
                                ApplicationAcceptanceDateProjection.UtcYearMonth,
                            ProtoAcceptanceDateProjection.UtcIsoWeek =>
                                ApplicationAcceptanceDateProjection.UtcIsoWeek,
                            ProtoAcceptanceDateProjection.UtcCompactDate =>
                                ApplicationAcceptanceDateProjection.UtcCompactDate,
                            _ => throw InvalidDocument(
                                "package acceptance_policy input date projection is unsupported"),
                        },
                        value.InstallationCreatedAtUtc.DayOffset),
                ProtoAcceptanceInputBinding.SourceOneofCase.AuthenticatedOwnerExternalUserId =>
                    new ApplicationAuthenticatedOwnerExternalUserIdInput(),
                _ => throw InvalidDocument(
                    "package acceptance_policy input binding source is unsupported"),
            });

    private static ApplicationVariableDefinition MapVariable(
        Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryVariableDefinition value) =>
        new(
            value.Key,
            value.Label,
            value.Description,
            MapVariableKind(value.Kind),
            value.Required,
            value.YamlPointer,
            NormalizeOptional(value.JsonPointer),
            NormalizeOptional(value.DefaultValue));

    private static ApplicationConnectionSlotDefinition MapConnectionSlot(
        Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryConnectionSlotDefinition value) =>
        new(value.Key, value.Label, value.ServiceSlug, value.Required);

    private static WorkflowDeliveryConnectionSnapshot MapConnection(
        WorkflowDeliveryConnectionState value) =>
        new(
            value.SlotKey,
            value.ServiceSlug,
            value.LinkId,
            MapConnectionStatus(value.Status),
            NormalizeOptional(value.UserServiceId),
            RequiredDateTime(value.UpdatedAtUtc, "connection.updated_at_utc"));

    private static WorkflowInstallationSnapshot? MapInstallation(WorkflowInstallationState? value)
    {
        if (value == null || string.IsNullOrWhiteSpace(value.InstallationId))
            return null;

        return new WorkflowInstallationSnapshot(
            value.InstallationId,
            value.IdempotencyKey,
            value.ScopeId,
            value.TeamId,
            new Dictionary<string, string>(value.ConfigurationValues, StringComparer.Ordinal),
            new Dictionary<string, string>(value.ConnectionReferences, StringComparer.Ordinal),
            MapTrigger(value.TriggerIntent ?? throw InvalidDocument("installation trigger_intent is missing")),
            value.SourceHash,
            value.ResolvedHash,
            value.ResolvedYaml,
            value.Confirmations.Select(static reference =>
                    new ApplicationConfirmationReference(
                        reference.CallSiteId,
                        reference.RequestContractDigest))
                .ToArray(),
            value.CapabilityAdmissionPlan?.Clone() ??
                throw InvalidDocument("installation capability_admission_plan is missing"),
            MapAuthenticatedOwner(value.AuthenticatedOwner),
            value.AcceptanceInput?.Clone(),
            NormalizeRequired(value.OperationId, "projected installation operation_id"),
            MapInstallationStatus(value.Status),
            value.Stage,
            NormalizeOptional(value.ErrorCode),
            NormalizeOptional(value.ErrorMessage),
            NormalizeOptional(value.WorkflowId),
            NormalizeOptional(value.MemberId),
            NormalizeOptional(value.PublishedServiceId),
            NormalizeOptional(value.RevisionId),
            NormalizeOptional(value.BindingRunId),
            NormalizeOptional(value.ScheduleId),
            NormalizeOptional(value.ScheduleProvisioningId),
            NormalizeOptional(value.ScheduleProvisioningStatus),
            MapReadinessEvidence(value.ReadinessEvidence),
            value.Attempt,
            RequiredDateTime(value.CreatedAtUtc, "installation.created_at_utc"),
            RequiredDateTime(value.UpdatedAtUtc, "installation.updated_at_utc"))
        {
            ContinuationClaim = MapContinuationClaim(value.ContinuationClaim),
        };
    }

    private static WorkflowInstallationContinuationClaimSnapshot? MapContinuationClaim(
        WorkflowInstallationContinuationClaim? value) =>
        value == null
            ? null
            : new WorkflowInstallationContinuationClaimSnapshot(
                NormalizeRequired(value.ClaimId, "projected continuation claim_id"),
                NormalizeRequired(value.ClaimantId, "projected continuation claimant_id"),
                MapInstallationStatus(value.ExpectedStatus),
                value.Attempt,
                NormalizeRequired(value.OperationId, "projected continuation operation_id"),
                RequiredDateTime(value.ClaimedAtUtc, "continuation.claimed_at_utc"),
                RequiredDateTime(value.ExpiresAtUtc, "continuation.expires_at_utc"));

    private static ApplicationInstallationReadinessEvidence? MapReadinessEvidence(
        Aevatar.GAgents.WorkflowDelivery.WorkflowInstallationReadinessEvidence? value)
    {
        if (value == null)
            return null;

        var publishedService = value.PublishedService ??
            throw InvalidDocument("installation readiness published_service is missing");
        var boundRevision = value.BoundRevision ??
            throw InvalidDocument("installation readiness bound_revision is missing");
        var acceptanceRun = value.AcceptanceRun;

        return new ApplicationInstallationReadinessEvidence(
            new ApplicationPublishedServiceEvidence(
                NormalizeRequired(
                    publishedService.PublishedServiceId,
                    "projected readiness published_service_id"),
                publishedService.Committed,
                publishedService.Runnable,
                publishedService.CommittedStateVersion),
            new ApplicationBoundRevisionEvidence(
                NormalizeRequired(boundRevision.RevisionId, "projected readiness revision_id"),
                NormalizeOptional(boundRevision.BindingRunId),
                boundRevision.Bound,
                boundRevision.CommittedStateVersion),
            MapTriggerReadiness(
                value.Trigger ?? throw InvalidDocument("installation readiness trigger is missing")),
            acceptanceRun == null
                ? null
                : new ApplicationAcceptanceRunEvidence(
                    NormalizeRequired(
                        acceptanceRun.AcceptanceRunId,
                        "projected readiness acceptance_run_id"),
                    MapAcceptanceRunStatus(acceptanceRun.Status),
                    acceptanceRun.CommittedStateVersion),
            value.Artifacts.Select(MapArtifactEvidence).ToArray());
    }

    private static AuthenticatedAuthorizationOwnerContext? MapAuthenticatedOwner(
        WorkflowDeliveryAuthorizationOwnerContext? value)
    {
        if (value == null)
            return null;
        var subjectPlatform = NormalizeRequired(
            value.SubjectPlatform,
            "projected authenticated owner subject_platform");
        var subjectTenant = string.Equals(subjectPlatform, OwnerScope.NyxIdPlatform, StringComparison.Ordinal)
            ? NormalizeOptional(value.SubjectTenant) ?? string.Empty
            : NormalizeRequired(value.SubjectTenant, "projected authenticated owner subject_tenant");
        return new AuthenticatedAuthorizationOwnerContext(
            value.Owner?.Clone() ?? throw InvalidDocument("installation authenticated_owner.owner is missing"),
            subjectPlatform,
            subjectTenant,
            NormalizeRequired(
                value.SubjectExternalUserId,
                "projected authenticated owner subject_external_user_id"),
            NormalizeRequired(value.VerifiedBindingId, "projected authenticated owner verified_binding_id"),
            value.AuthenticatedActor?.Clone());
    }

    private static ApplicationTriggerReadinessEvidence MapTriggerReadiness(
        Aevatar.GAgents.WorkflowDelivery.WorkflowTriggerReadinessEvidence value)
    {
        var intent = MapTrigger(
            value.Intent ?? throw InvalidDocument("installation readiness trigger intent is missing"));
        return value.ReadinessCase switch
        {
            Aevatar.GAgents.WorkflowDelivery.WorkflowTriggerReadinessEvidence.ReadinessOneofCase.NoTrigger =>
                new ApplicationTriggerReadinessEvidence(
                    intent,
                    new ApplicationNoTriggerEvidence(value.NoTrigger.Ready),
                    null),
            Aevatar.GAgents.WorkflowDelivery.WorkflowTriggerReadinessEvidence.ReadinessOneofCase.Schedule =>
                new ApplicationTriggerReadinessEvidence(
                    intent,
                    null,
                    new ApplicationScheduleEvidence(
                        NormalizeRequired(value.Schedule.ScheduleId, "projected readiness schedule_id"),
                        NormalizeRequired(
                            value.Schedule.ScheduleProvisioningId,
                            "projected readiness schedule_provisioning_id"),
                        MapScheduleReadinessStatus(value.Schedule.Status),
                        value.Schedule.CommittedStateVersion)),
            _ => throw InvalidDocument("installation readiness trigger evidence is missing"),
        };
    }

    private static ApplicationArtifactEvidence MapArtifactEvidence(
        Aevatar.GAgents.WorkflowDelivery.WorkflowInstallationArtifactEvidence value) =>
        new(
            MapArtifactKind(value.Kind),
            NormalizeRequired(value.ArtifactId, "projected readiness artifact_id"),
            MapArtifactVerificationStatus(value.VerificationStatus),
            NormalizeOptional(value.VerificationReference),
            NormalizeOptional(value.ContentDigest));

    private static ApplicationTriggerIntent MapTrigger(
        Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryTriggerIntent value) =>
        new(MapTriggerKind(value.Kind), NormalizeOptional(value.Cron), NormalizeOptional(value.TimeZone), value.RunImmediately);

    private static ApplicationLifecycleStatus MapLifecycleStatus(ProtoLifecycleStatus value) => value switch
    {
        ProtoLifecycleStatus.Active => ApplicationLifecycleStatus.Active,
        ProtoLifecycleStatus.Revoked => ApplicationLifecycleStatus.Revoked,
        _ => throw InvalidDocument("lifecycle_status is invalid"),
    };

    private static ApplicationVariableKind MapVariableKind(ProtoVariableKind value) => value switch
    {
        ProtoVariableKind.String => ApplicationVariableKind.String,
        ProtoVariableKind.Integer => ApplicationVariableKind.Integer,
        ProtoVariableKind.Number => ApplicationVariableKind.Number,
        ProtoVariableKind.Boolean => ApplicationVariableKind.Boolean,
        _ => throw InvalidDocument("variable kind is invalid"),
    };

    private static ApplicationTriggerKind MapTriggerKind(ProtoTriggerKind value) => value switch
    {
        ProtoTriggerKind.None => ApplicationTriggerKind.None,
        ProtoTriggerKind.OneShot => ApplicationTriggerKind.OneShot,
        ProtoTriggerKind.Cron => ApplicationTriggerKind.Cron,
        _ => throw InvalidDocument("trigger kind is invalid"),
    };

    private static ApplicationConnectionStatus MapConnectionStatus(ProtoConnectionStatus value) => value switch
    {
        ProtoConnectionStatus.Pending => ApplicationConnectionStatus.Pending,
        ProtoConnectionStatus.Completed => ApplicationConnectionStatus.Completed,
        ProtoConnectionStatus.Failed => ApplicationConnectionStatus.Failed,
        ProtoConnectionStatus.Expired => ApplicationConnectionStatus.Expired,
        _ => throw InvalidDocument("connection status is invalid"),
    };

    private static ApplicationInstallationStatus MapInstallationStatus(ProtoInstallationStatus value) => value switch
    {
        ProtoInstallationStatus.Accepted => ApplicationInstallationStatus.Accepted,
        ProtoInstallationStatus.ProvisioningAccepted => ApplicationInstallationStatus.ProvisioningAccepted,
        ProtoInstallationStatus.Failed => ApplicationInstallationStatus.Failed,
        ProtoInstallationStatus.Ready => ApplicationInstallationStatus.Ready,
        _ => throw InvalidDocument("installation status is invalid"),
    };

    private static ApplicationScheduleReadinessStatus MapScheduleReadinessStatus(
        ProtoScheduleReadinessStatus value) => value switch
        {
            ProtoScheduleReadinessStatus.Ready => ApplicationScheduleReadinessStatus.Ready,
            _ => throw InvalidDocument("installation schedule readiness status is invalid"),
        };

    private static ProtoInstallationStatus MapInstallationStatusFilter(ApplicationInstallationStatus value) => value switch
    {
        ApplicationInstallationStatus.Accepted => ProtoInstallationStatus.Accepted,
        ApplicationInstallationStatus.ProvisioningAccepted => ProtoInstallationStatus.ProvisioningAccepted,
        ApplicationInstallationStatus.Failed => ProtoInstallationStatus.Failed,
        ApplicationInstallationStatus.Ready => ProtoInstallationStatus.Ready,
        _ => throw new ArgumentOutOfRangeException(nameof(value), "installation status filter is invalid"),
    };

    private static string MapInstallationStatusStorageValue(ProtoInstallationStatus value)
    {
        if (value == ProtoInstallationStatus.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(value), "installation status storage value is invalid");

        var valueDescriptor = WorkflowInstallationState.Descriptor
            .FindFieldByNumber(WorkflowInstallationState.StatusFieldNumber)
            .EnumType
            .FindValueByNumber((int)value);
        return valueDescriptor?.Name ??
            throw new ArgumentOutOfRangeException(nameof(value), "installation status storage value is invalid");
    }

    private static ApplicationAcceptanceRunStatus MapAcceptanceRunStatus(
        ProtoAcceptanceRunStatus value) => value switch
        {
            ProtoAcceptanceRunStatus.TerminalSuccess => ApplicationAcceptanceRunStatus.TerminalSuccess,
            ProtoAcceptanceRunStatus.TerminalFailure => ApplicationAcceptanceRunStatus.TerminalFailure,
            _ => throw InvalidDocument("installation acceptance run status is invalid"),
        };

    private static ApplicationArtifactKind MapArtifactKind(ProtoArtifactKind value) => value switch
    {
        ProtoArtifactKind.RunOutput => ApplicationArtifactKind.RunOutput,
        ProtoArtifactKind.SideEffectReceipt => ApplicationArtifactKind.SideEffectReceipt,
        ProtoArtifactKind.ApprovalResult => ApplicationArtifactKind.ApprovalResult,
        ProtoArtifactKind.ExternalResource => ApplicationArtifactKind.ExternalResource,
        _ => throw InvalidDocument("installation artifact kind is invalid"),
    };

    private static ApplicationArtifactVerificationStatus MapArtifactVerificationStatus(
        ProtoArtifactVerificationStatus value) => value switch
        {
            ProtoArtifactVerificationStatus.Verified => ApplicationArtifactVerificationStatus.Verified,
            ProtoArtifactVerificationStatus.Rejected => ApplicationArtifactVerificationStatus.Rejected,
            _ => throw InvalidDocument("installation artifact verification status is invalid"),
        };

    private static void EnsureDocumentIdentity(
        WorkflowDeliveryCurrentStateDocument document,
        string actorId,
        string deliveryId)
    {
        if (!string.Equals(document.Id, actorId, StringComparison.Ordinal) ||
            !string.Equals(document.ActorId, actorId, StringComparison.Ordinal) ||
            !string.Equals(document.DeliveryId, deliveryId, StringComparison.Ordinal) ||
            document.StateVersion <= 0 ||
            string.IsNullOrWhiteSpace(document.LastEventId) ||
            document.UpdatedAt == null)
        {
            throw InvalidDocument("committed identity evidence is invalid");
        }
    }

    private static ProjectionDocumentFilter Equal(string fieldPath, string value) =>
        new()
        {
            FieldPath = fieldPath,
            Operator = ProjectionDocumentFilterOperator.Eq,
            Value = ProjectionDocumentValue.FromString(value),
        };

    private static string NormalizeRequired(string? value, string name)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Any(char.IsControl))
            throw new InvalidOperationException($"{name} is invalid.");
        return normalized;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string RequireOpaqueContent(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{name} is invalid.")
            : value;

    private static DateTimeOffset RequiredDateTime(Timestamp? value, string name) =>
        value?.ToDateTimeOffset() ?? throw InvalidDocument($"{name} is missing");

    private static DateTimeOffset? OptionalDateTime(Timestamp? value) => value?.ToDateTimeOffset();

    private static InvalidOperationException InvalidDocument(string detail) =>
        new($"Projected workflow delivery document {detail}.");
}
