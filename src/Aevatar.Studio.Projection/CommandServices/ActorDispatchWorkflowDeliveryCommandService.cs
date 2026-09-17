using System.Security.Cryptography;
using Aevatar.GAgents.WorkflowDelivery;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using DeliveryApplication = global::Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Projection.CommandServices;

internal sealed class ActorDispatchWorkflowDeliveryCommandService
    : DeliveryApplication.IWorkflowDeliveryCommandPort
{
    private const string PublisherId = "aevatar.studio.projection.workflow-delivery";

    private readonly DeliveryApplication.IStudioActorBootstrap _bootstrap;
    private readonly StudioProjectionActorCommandDispatch _commandDispatch;

    public ActorDispatchWorkflowDeliveryCommandService(
        DeliveryApplication.IStudioActorBootstrap bootstrap,
        StudioProjectionActorCommandDispatch commandDispatch)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _commandDispatch = commandDispatch ?? throw new ArgumentNullException(nameof(commandDispatch));
    }

    public Task<DeliveryApplication.WorkflowDeliveryCommandReceipt> CreateAsync(
        DeliveryApplication.CreateWorkflowDeliveryMutation mutation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var command = new CreateWorkflowDeliveryCommand
        {
            DeliveryId = mutation.DeliveryId,
            Package = MapPackage(mutation.Package),
            TargetScopeId = mutation.TargetScopeId,
            ExpiresAtUtc = Timestamp.FromDateTimeOffset(mutation.ExpiresAtUtc),
            ExpiresAtDefaulted = mutation.ExpiresAtDefaulted,
            Note = mutation.Note ?? string.Empty,
            CreatedBy = mutation.CreatedBy,
            CreatedAtUtc = Timestamp.FromDateTimeOffset(mutation.CreatedAtUtc),
        };
        return DispatchAsync(mutation.DeliveryId, command, "create", StableCreateCommand(command), ct);
    }

    public Task<DeliveryApplication.WorkflowDeliveryCommandReceipt> RecordAccessAsync(
        DeliveryApplication.RecordWorkflowDeliveryAccessMutation mutation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var command = new RecordWorkflowDeliveryAccessCommand
        {
            DeliveryId = mutation.DeliveryId,
            TargetScopeId = mutation.TargetScopeId,
            AccessedAtUtc = Timestamp.FromDateTimeOffset(mutation.AccessedAtUtc),
        };
        return DispatchAsync(mutation.DeliveryId, command, "access", command.ToByteArray(), ct);
    }

    public Task<DeliveryApplication.WorkflowDeliveryCommandReceipt> RevokeAsync(
        DeliveryApplication.RevokeWorkflowDeliveryMutation mutation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var command = new RevokeWorkflowDeliveryCommand
        {
            DeliveryId = mutation.DeliveryId,
            RevokedBy = mutation.RevokedBy,
            RevokedAtUtc = Timestamp.FromDateTimeOffset(mutation.RevokedAtUtc),
        };
        return DispatchAsync(mutation.DeliveryId, command, "revoke", command.ToByteArray(), ct);
    }

    public Task<DeliveryApplication.WorkflowDeliveryCommandReceipt> BeginConnectionAsync(
        DeliveryApplication.BeginWorkflowDeliveryConnectionMutation mutation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var command = new BeginWorkflowDeliveryConnectionCommand
        {
            DeliveryId = mutation.DeliveryId,
            TargetScopeId = mutation.TargetScopeId,
            SlotKey = mutation.SlotKey,
            ServiceSlug = mutation.ServiceSlug,
            LinkId = mutation.LinkId,
            RequestedAtUtc = Timestamp.FromDateTimeOffset(mutation.RequestedAtUtc),
        };
        return DispatchAsync(mutation.DeliveryId, command, "begin-connection", command.ToByteArray(), ct);
    }

    public Task<DeliveryApplication.WorkflowDeliveryCommandReceipt> UpdateConnectionAsync(
        DeliveryApplication.UpdateWorkflowDeliveryConnectionMutation mutation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var command = new UpdateWorkflowDeliveryConnectionCommand
        {
            DeliveryId = mutation.DeliveryId,
            TargetScopeId = mutation.TargetScopeId,
            SlotKey = mutation.SlotKey,
            LinkId = mutation.LinkId,
            Status = MapConnectionStatus(mutation.Status),
            UserServiceId = mutation.UserServiceId ?? string.Empty,
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(mutation.UpdatedAtUtc),
        };
        return DispatchAsync(mutation.DeliveryId, command, "update-connection", command.ToByteArray(), ct);
    }

    public Task<DeliveryApplication.WorkflowDeliveryCommandReceipt> AttachConnectionAsync(
        DeliveryApplication.AttachWorkflowDeliveryConnectionMutation mutation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var command = new AttachWorkflowDeliveryConnectionCommand
        {
            DeliveryId = mutation.DeliveryId,
            TargetScopeId = mutation.TargetScopeId,
            SlotKey = mutation.SlotKey,
            ServiceSlug = mutation.ServiceSlug,
            UserServiceId = mutation.UserServiceId,
            AttachedAtUtc = Timestamp.FromDateTimeOffset(mutation.AttachedAtUtc),
            ExpectedStateVersion = mutation.ExpectedStateVersion,
        };
        var stableCommand = command.Clone();
        stableCommand.AttachedAtUtc = null;
        return DispatchAsync(
            mutation.DeliveryId,
            command,
            "attach-connection",
            stableCommand.ToByteArray(),
            ct);
    }

    public Task<DeliveryApplication.WorkflowDeliveryCommandReceipt> StartInstallationAsync(
        DeliveryApplication.StartWorkflowInstallationMutation mutation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var command = new StartWorkflowInstallationCommand
        {
            DeliveryId = mutation.DeliveryId,
            InstallationId = mutation.InstallationId,
            IdempotencyKey = mutation.IdempotencyKey,
            ScopeId = mutation.ScopeId,
            TeamId = mutation.TeamId,
            TriggerIntent = MapTrigger(mutation.TriggerIntent),
            SourceHash = mutation.SourceHash,
            ResolvedHash = mutation.ResolvedHash,
            ResolvedYaml = mutation.ResolvedYaml,
            OperationId = mutation.OperationId,
            CapabilityAdmissionPlan = (mutation.CapabilityAdmissionPlan ??
                throw new ArgumentNullException(nameof(mutation.CapabilityAdmissionPlan))).Clone(),
            AuthenticatedOwner = MapAuthorizationOwner(mutation.AuthenticatedOwner),
            RequestedAtUtc = Timestamp.FromDateTimeOffset(mutation.RequestedAtUtc),
        };
        foreach (var pair in mutation.ConfigurationValues ??
                     throw new ArgumentNullException(nameof(mutation.ConfigurationValues)))
        {
            command.ConfigurationValues.Add(pair.Key, pair.Value);
        }
        foreach (var pair in mutation.ConnectionReferences ??
                     throw new ArgumentNullException(nameof(mutation.ConnectionReferences)))
        {
            command.ConnectionReferences.Add(pair.Key, pair.Value);
        }
        command.Confirmations.Add((mutation.Confirmations ??
                throw new ArgumentNullException(nameof(mutation.Confirmations)))
            .Select(MapConfirmation));

        return DispatchAsync(
            mutation.DeliveryId,
            command,
            "start-installation",
            StableStartCommand(command),
            ct);
    }

    public Task<DeliveryApplication.WorkflowDeliveryCommandReceipt> RetryInstallationAsync(
        DeliveryApplication.RetryWorkflowInstallationMutation mutation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var command = new RetryWorkflowInstallationCommand
        {
            DeliveryId = mutation.DeliveryId,
            InstallationId = mutation.InstallationId,
            IdempotencyKey = mutation.IdempotencyKey,
            OperationId = mutation.OperationId,
            RequestedAtUtc = Timestamp.FromDateTimeOffset(mutation.RequestedAtUtc),
        };
        var stableCommand = command.Clone();
        stableCommand.RequestedAtUtc = null;
        return DispatchAsync(
            mutation.DeliveryId,
            command,
            "retry-installation",
            stableCommand.ToByteArray(),
            ct);
    }

    public Task<DeliveryApplication.WorkflowDeliveryCommandReceipt> ClaimInstallationContinuationAsync(
        DeliveryApplication.ClaimWorkflowInstallationContinuationMutation mutation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var command = new ClaimWorkflowInstallationContinuationCommand
        {
            DeliveryId = mutation.DeliveryId,
            InstallationId = mutation.InstallationId,
            ExpectedStatus = (WorkflowInstallationStatus)(int)mutation.ExpectedStatus,
            Attempt = mutation.Attempt,
            OperationId = mutation.OperationId,
            ClaimId = mutation.ClaimId,
            ClaimantId = mutation.ClaimantId,
            RequestedDuration = Duration.FromTimeSpan(mutation.RequestedDuration),
        };
        return DispatchAsync(
            mutation.DeliveryId,
            command,
            "claim-installation-continuation",
            command.ToByteArray(),
            ct);
    }

    public Task<DeliveryApplication.WorkflowDeliveryCommandReceipt> RecordProvisioningAcceptedAsync(
        DeliveryApplication.RecordWorkflowProvisioningAcceptedMutation mutation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var command = new RecordWorkflowProvisioningAcceptedCommand
        {
            DeliveryId = mutation.DeliveryId,
            InstallationId = mutation.InstallationId,
            MemberId = mutation.MemberId,
            WorkflowId = mutation.WorkflowId ?? string.Empty,
            PublishedServiceId = mutation.PublishedServiceId ?? string.Empty,
            RevisionId = mutation.RevisionId ?? string.Empty,
            BindingRunId = mutation.BindingRunId ?? string.Empty,
            ScheduleId = mutation.ScheduleId ?? string.Empty,
            ScheduleProvisioningId = mutation.ScheduleProvisioningId ?? string.Empty,
            ScheduleProvisioningStatus = mutation.ScheduleProvisioningStatus ?? string.Empty,
            Attempt = mutation.Attempt,
            OperationId = mutation.OperationId,
            AcceptedAtUtc = Timestamp.FromDateTimeOffset(mutation.AcceptedAtUtc),
            ContinuationClaimId = mutation.ContinuationClaimId,
            ContinuationClaimantId = mutation.ContinuationClaimantId,
        };
        var stableCommand = command.Clone();
        stableCommand.AcceptedAtUtc = null;
        return DispatchAsync(
            mutation.DeliveryId,
            command,
            "provisioning-accepted",
            stableCommand.ToByteArray(),
            ct);
    }

    public Task<DeliveryApplication.WorkflowDeliveryCommandReceipt> RecordInstallationReadyAsync(
        DeliveryApplication.RecordWorkflowInstallationReadyMutation mutation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var command = new RecordWorkflowInstallationReadyCommand
        {
            DeliveryId = mutation.DeliveryId,
            InstallationId = mutation.InstallationId,
            Evidence = MapReadinessEvidence(mutation.Evidence),
            Attempt = mutation.Attempt,
            OperationId = mutation.OperationId,
            ReadyAtUtc = Timestamp.FromDateTimeOffset(mutation.ReadyAtUtc),
            ContinuationClaimId = mutation.ContinuationClaimId,
            ContinuationClaimantId = mutation.ContinuationClaimantId,
        };
        var stableCommand = command.Clone();
        stableCommand.ReadyAtUtc = null;
        return DispatchAsync(
            mutation.DeliveryId,
            command,
            "installation-ready",
            stableCommand.ToByteArray(),
            ct);
    }

    public Task<DeliveryApplication.WorkflowDeliveryCommandReceipt> RecordInstallationFailedAsync(
        DeliveryApplication.RecordWorkflowInstallationFailedMutation mutation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var command = new RecordWorkflowInstallationFailedCommand
        {
            DeliveryId = mutation.DeliveryId,
            InstallationId = mutation.InstallationId,
            ErrorCode = mutation.ErrorCode,
            ErrorMessage = mutation.ErrorMessage ?? string.Empty,
            ExpectedStatus = (WorkflowInstallationStatus)(int)mutation.ExpectedStatus,
            Attempt = mutation.Attempt,
            OperationId = mutation.OperationId,
            FailedAtUtc = Timestamp.FromDateTimeOffset(mutation.FailedAtUtc),
            ContinuationClaimId = mutation.ContinuationClaimId,
            ContinuationClaimantId = mutation.ContinuationClaimantId,
        };
        var stableCommand = command.Clone();
        stableCommand.FailedAtUtc = null;
        return DispatchAsync(
            mutation.DeliveryId,
            command,
            "installation-failed",
            stableCommand.ToByteArray(),
            ct);
    }

    private static byte[] StableCreateCommand(CreateWorkflowDeliveryCommand source)
    {
        var stable = source.Clone();
        stable.CreatedAtUtc = null;
        stable.Package.CreatedAtUtc = null;
        if (stable.ExpiresAtDefaulted)
            stable.ExpiresAtUtc = null;
        return stable.ToByteArray();
    }

    private static byte[] StableStartCommand(StartWorkflowInstallationCommand source)
    {
        var stable = source.Clone();
        stable.RequestedAtUtc = null;
        var configuration = stable.ConfigurationValues.OrderBy(static value => value.Key, StringComparer.Ordinal).ToArray();
        stable.ConfigurationValues.Clear();
        foreach (var value in configuration)
            stable.ConfigurationValues.Add(value.Key, value.Value);
        var connections = stable.ConnectionReferences.OrderBy(static value => value.Key, StringComparer.Ordinal).ToArray();
        stable.ConnectionReferences.Clear();
        foreach (var value in connections)
            stable.ConnectionReferences.Add(value.Key, value.Value);
        var confirmations = stable.Confirmations
            .OrderBy(static value => value.CallSiteId, StringComparer.Ordinal)
            .ThenBy(static value => value.RequestContractDigest, StringComparer.Ordinal)
            .Select(static value => value.Clone())
            .ToArray();
        stable.Confirmations.Clear();
        stable.Confirmations.Add(confirmations);
        return stable.ToByteArray();
    }

    private static WorkflowDeliveryAuthorizationOwnerContext? MapAuthorizationOwner(
        Aevatar.GAgentService.Abstractions.Schedules.Authorization.AuthenticatedAuthorizationOwnerContext? source) =>
        source == null
            ? null
            : new WorkflowDeliveryAuthorizationOwnerContext
            {
                Owner = source.Owner.Clone(),
                SubjectPlatform = source.SubjectPlatform,
                SubjectTenant = source.SubjectTenant,
                SubjectExternalUserId = source.SubjectExternalUserId,
                VerifiedBindingId = source.VerifiedBindingId,
                AuthenticatedActor = source.AuthenticatedActor?.Clone(),
            };

    private async Task<DeliveryApplication.WorkflowDeliveryCommandReceipt> DispatchAsync(
        string deliveryId,
        IMessage payload,
        string operation,
        ReadOnlyMemory<byte> stableCommandInput,
        CancellationToken ct)
    {
        var actorId = WorkflowDeliveryConventions.BuildActorId(deliveryId);
        var commandId = BuildCommandId(operation, stableCommandInput.Span);
        var actor = await _bootstrap.EnsureAsync<WorkflowDeliveryGAgent>(actorId, ct);
        var receipt = await _commandDispatch.DispatchAsync(
            actor,
            payload,
            PublisherId,
            commandId,
            commandId,
            commandId,
            ct);
        return new DeliveryApplication.WorkflowDeliveryCommandReceipt(
            deliveryId.Trim(),
            receipt.ActorId,
            receipt.CommandId,
            receipt.CorrelationId,
            DeliveryApplication.WorkflowDeliveryCommandAckStage.AcceptedForDispatch,
            receipt.AckedAt);
    }

    private static WorkflowPackageVersionSnapshot MapPackage(
        DeliveryApplication.WorkflowDeliveryPackageSnapshot package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var snapshot = new WorkflowPackageVersionSnapshot
        {
            PackageId = package.PackageId,
            PackageVersionId = package.PackageVersionId,
            WorkflowName = package.WorkflowName,
            Version = package.Version,
            DisplayName = package.DisplayName,
            Description = package.Description,
            SourceYaml = package.SourceYaml,
            SourceHash = package.SourceHash,
            PackageHash = package.PackageHash,
            RiskSummary = package.RiskSummary,
            AcceptancePolicy = MapAcceptancePolicy(package.AcceptancePolicy),
            CreatedBy = package.CreatedBy,
            CreatedAtUtc = Timestamp.FromDateTimeOffset(package.CreatedAtUtc),
        };
        snapshot.VariableSchema.Add((package.VariableSchema ??
                throw new ArgumentNullException(nameof(package.VariableSchema)))
            .Select(MapVariable));
        snapshot.ConnectionSlots.Add((package.ConnectionSlots ??
                throw new ArgumentNullException(nameof(package.ConnectionSlots)))
            .Select(MapConnectionSlot));
        snapshot.Capabilities.Add(package.Capabilities ??
            throw new ArgumentNullException(nameof(package.Capabilities)));
        snapshot.ParserDiagnostics.Add(package.ParserDiagnostics ??
            throw new ArgumentNullException(nameof(package.ParserDiagnostics)));
        return snapshot;
    }

    private static WorkflowDeliveryAcceptancePolicy MapAcceptancePolicy(
        DeliveryApplication.WorkflowDeliveryAcceptancePolicy value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var policy = new WorkflowDeliveryAcceptancePolicy
        {
            Mode = value.Mode switch
            {
                DeliveryApplication.WorkflowDeliveryAcceptanceMode.AutomaticPreview =>
                    WorkflowDeliveryAcceptanceMode.AutomaticPreview,
                DeliveryApplication.WorkflowDeliveryAcceptanceMode.Manual =>
                    WorkflowDeliveryAcceptanceMode.Manual,
                _ => WorkflowDeliveryAcceptanceMode.Unspecified,
            },
            Limitation = value.Limitation ?? string.Empty,
        };
        if (value.InputDeclared)
            policy.Input = MapAcceptanceInput(value.Input);
        return policy;
    }

    private static WorkflowDeliveryAcceptanceInputRecipe MapAcceptanceInput(
        DeliveryApplication.WorkflowDeliveryAcceptanceInputRecipe value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(value.Literals);
        ArgumentNullException.ThrowIfNull(value.Bindings);
        var recipe = new WorkflowDeliveryAcceptanceInputRecipe
        {
            Literals = value.Literals.Clone(),
        };
        recipe.Bindings.Add(value.Bindings.Select(MapAcceptanceInputBinding));
        WorkflowDeliveryConventions.ValidateAcceptanceInput(recipe);
        return recipe;
    }

    private static WorkflowDeliveryAcceptanceInputBinding MapAcceptanceInputBinding(
        DeliveryApplication.WorkflowDeliveryAcceptanceInputBinding value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var binding = new WorkflowDeliveryAcceptanceInputBinding
        {
            Key = value.Key,
            Prefix = value.Prefix,
            Suffix = value.Suffix,
        };
        switch (value.Source)
        {
            case DeliveryApplication.WorkflowDeliveryInstallationCreatedAtUtcInput source:
                binding.InstallationCreatedAtUtc = new WorkflowDeliveryInstallationCreatedAtUtcInput
                {
                    DateProjection = source.DateProjection switch
                    {
                        DeliveryApplication.WorkflowDeliveryAcceptanceDateProjection.UtcDate =>
                            WorkflowDeliveryAcceptanceDateProjection.UtcDate,
                        DeliveryApplication.WorkflowDeliveryAcceptanceDateProjection.UtcYearMonth =>
                            WorkflowDeliveryAcceptanceDateProjection.UtcYearMonth,
                        DeliveryApplication.WorkflowDeliveryAcceptanceDateProjection.UtcIsoWeek =>
                            WorkflowDeliveryAcceptanceDateProjection.UtcIsoWeek,
                        DeliveryApplication.WorkflowDeliveryAcceptanceDateProjection.UtcCompactDate =>
                            WorkflowDeliveryAcceptanceDateProjection.UtcCompactDate,
                        _ => throw new InvalidOperationException(
                            "Workflow delivery acceptance input date projection is unsupported."),
                    },
                    DayOffset = source.DayOffset,
                };
                break;
            case DeliveryApplication.WorkflowDeliveryAuthenticatedOwnerExternalUserIdInput:
                binding.AuthenticatedOwnerExternalUserId =
                    new WorkflowDeliveryAuthenticatedOwnerExternalUserIdInput();
                break;
            default:
                throw new InvalidOperationException(
                    "Workflow delivery acceptance input binding source is unsupported.");
        }
        return binding;
    }

    private static WorkflowDeliveryVariableDefinition MapVariable(
        DeliveryApplication.WorkflowDeliveryVariableDefinition value) =>
        new()
        {
            Key = value.Key,
            Label = value.Label,
            Description = value.Description,
            Kind = value.Kind switch
            {
                DeliveryApplication.WorkflowDeliveryVariableKind.String => WorkflowDeliveryVariableKind.String,
                DeliveryApplication.WorkflowDeliveryVariableKind.Integer => WorkflowDeliveryVariableKind.Integer,
                DeliveryApplication.WorkflowDeliveryVariableKind.Number => WorkflowDeliveryVariableKind.Number,
                DeliveryApplication.WorkflowDeliveryVariableKind.Boolean => WorkflowDeliveryVariableKind.Boolean,
                _ => throw new ArgumentOutOfRangeException(nameof(value.Kind)),
            },
            Required = value.Required,
            YamlPointer = value.YamlPointer,
            JsonPointer = value.JsonPointer ?? string.Empty,
            DefaultValue = value.DefaultValue ?? string.Empty,
        };

    private static WorkflowDeliveryConnectionSlotDefinition MapConnectionSlot(
        DeliveryApplication.WorkflowDeliveryConnectionSlotDefinition value) =>
        new()
        {
            Key = value.Key,
            Label = value.Label,
            ServiceSlug = value.ServiceSlug,
            Required = value.Required,
        };

    private static WorkflowDeliveryTriggerIntent MapTrigger(DeliveryApplication.WorkflowDeliveryTriggerIntent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new WorkflowDeliveryTriggerIntent
        {
            Kind = value.Kind switch
            {
                DeliveryApplication.WorkflowDeliveryTriggerKind.None => WorkflowDeliveryTriggerKind.None,
                DeliveryApplication.WorkflowDeliveryTriggerKind.OneShot => WorkflowDeliveryTriggerKind.OneShot,
                DeliveryApplication.WorkflowDeliveryTriggerKind.Cron => WorkflowDeliveryTriggerKind.Cron,
                _ => throw new ArgumentOutOfRangeException(nameof(value.Kind)),
            },
            Cron = value.Cron ?? string.Empty,
            TimeZone = value.TimeZone ?? string.Empty,
            RunImmediately = value.RunImmediately,
        };
    }

    private static WorkflowDeliveryConfirmationReference MapConfirmation(
        DeliveryApplication.WorkflowDeliveryConfirmationReference value) =>
        new()
        {
            CallSiteId = value.CallSiteId,
            RequestContractDigest = value.RequestContractDigest,
        };

    private static WorkflowDeliveryConnectionStatus MapConnectionStatus(
        DeliveryApplication.WorkflowDeliveryConnectionStatus value) => value switch
        {
            DeliveryApplication.WorkflowDeliveryConnectionStatus.Pending => WorkflowDeliveryConnectionStatus.Pending,
            DeliveryApplication.WorkflowDeliveryConnectionStatus.Completed => WorkflowDeliveryConnectionStatus.Completed,
            DeliveryApplication.WorkflowDeliveryConnectionStatus.Failed => WorkflowDeliveryConnectionStatus.Failed,
            DeliveryApplication.WorkflowDeliveryConnectionStatus.Expired => WorkflowDeliveryConnectionStatus.Expired,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static WorkflowInstallationReadinessEvidence MapReadinessEvidence(
        DeliveryApplication.WorkflowInstallationReadinessEvidence value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = new WorkflowInstallationReadinessEvidence
        {
            PublishedService = new WorkflowPublishedServiceReadinessEvidence
            {
                PublishedServiceId = value.PublishedService.PublishedServiceId,
                Committed = value.PublishedService.Committed,
                Runnable = value.PublishedService.Runnable,
                CommittedStateVersion = value.PublishedService.CommittedStateVersion,
            },
            BoundRevision = new WorkflowBoundRevisionReadinessEvidence
            {
                RevisionId = value.BoundRevision.RevisionId,
                BindingRunId = value.BoundRevision.BindingRunId ?? string.Empty,
                Bound = value.BoundRevision.Bound,
                CommittedStateVersion = value.BoundRevision.CommittedStateVersion,
            },
            Trigger = MapTriggerReadinessEvidence(value.Trigger),
        };
        if (value.AcceptanceRun is not null)
        {
            result.AcceptanceRun = new WorkflowAcceptanceRunReadinessEvidence
            {
                AcceptanceRunId = value.AcceptanceRun.AcceptanceRunId,
                Status = value.AcceptanceRun.Status switch
                {
                    DeliveryApplication.WorkflowAcceptanceRunStatus.TerminalSuccess =>
                        WorkflowAcceptanceRunStatus.TerminalSuccess,
                    DeliveryApplication.WorkflowAcceptanceRunStatus.TerminalFailure =>
                        WorkflowAcceptanceRunStatus.TerminalFailure,
                    _ => throw new ArgumentOutOfRangeException(nameof(value.AcceptanceRun.Status)),
                },
                CommittedStateVersion = value.AcceptanceRun.CommittedStateVersion,
            };
        }
        result.Artifacts.Add((value.Artifacts ?? throw new ArgumentNullException(nameof(value.Artifacts)))
            .Select(MapArtifactEvidence));
        return result;
    }

    private static WorkflowTriggerReadinessEvidence MapTriggerReadinessEvidence(
        DeliveryApplication.WorkflowTriggerReadinessEvidence value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = new WorkflowTriggerReadinessEvidence
        {
            Intent = MapTrigger(value.Intent),
        };
        switch (value.NoTrigger, value.Schedule)
        {
            case (not null, null):
                result.NoTrigger = new WorkflowNoTriggerReadinessEvidence
                {
                    Ready = value.NoTrigger.Ready,
                };
                break;
            case (null, not null):
                result.Schedule = new WorkflowScheduleReadinessEvidence
                {
                    ScheduleId = value.Schedule.ScheduleId,
                    ScheduleProvisioningId = value.Schedule.ScheduleProvisioningId,
                    Status = value.Schedule.Status switch
                    {
                        DeliveryApplication.WorkflowScheduleReadinessStatus.Ready =>
                            WorkflowScheduleReadinessStatus.Ready,
                        _ => throw new ArgumentOutOfRangeException(nameof(value.Schedule.Status)),
                    },
                    CommittedStateVersion = value.Schedule.CommittedStateVersion,
                };
                break;
            default:
                throw new InvalidOperationException(
                    "Workflow installation trigger readiness must select exactly one evidence type.");
        }
        return result;
    }

    private static WorkflowInstallationArtifactEvidence MapArtifactEvidence(
        DeliveryApplication.WorkflowInstallationArtifactEvidence value) =>
        new()
        {
            Kind = value.Kind switch
            {
                DeliveryApplication.WorkflowInstallationArtifactKind.RunOutput =>
                    WorkflowInstallationArtifactKind.RunOutput,
                DeliveryApplication.WorkflowInstallationArtifactKind.SideEffectReceipt =>
                    WorkflowInstallationArtifactKind.SideEffectReceipt,
                DeliveryApplication.WorkflowInstallationArtifactKind.ApprovalResult =>
                    WorkflowInstallationArtifactKind.ApprovalResult,
                DeliveryApplication.WorkflowInstallationArtifactKind.ExternalResource =>
                    WorkflowInstallationArtifactKind.ExternalResource,
                _ => throw new ArgumentOutOfRangeException(nameof(value.Kind)),
            },
            ArtifactId = value.ArtifactId,
            VerificationStatus = value.VerificationStatus switch
            {
                DeliveryApplication.WorkflowInstallationArtifactVerificationStatus.Verified =>
                    WorkflowInstallationArtifactVerificationStatus.Verified,
                DeliveryApplication.WorkflowInstallationArtifactVerificationStatus.Rejected =>
                    WorkflowInstallationArtifactVerificationStatus.Rejected,
                _ => throw new ArgumentOutOfRangeException(nameof(value.VerificationStatus)),
            },
            VerificationReference = value.VerificationReference ?? string.Empty,
            ContentDigest = value.ContentDigest ?? string.Empty,
        };

    private static string BuildCommandId(string operation, ReadOnlySpan<byte> stableCommandInput)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(stableCommandInput));
        return $"workflow-delivery-{operation}-{digest}";
    }
}
