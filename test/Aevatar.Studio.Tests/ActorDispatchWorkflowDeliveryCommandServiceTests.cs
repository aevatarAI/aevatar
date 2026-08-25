using Aevatar.CQRS.Core.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.WorkflowDelivery;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.CommandServices;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using ApplicationConfirmationReference = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryConfirmationReference;
using ApplicationConnectionSlotDefinition = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryConnectionSlotDefinition;
using ApplicationTriggerIntent = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryTriggerIntent;
using ApplicationTriggerKind = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryTriggerKind;
using ApplicationVariableDefinition = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryVariableDefinition;
using ApplicationVariableKind = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryVariableKind;
using DeliveryApplication = global::Aevatar.Studio.Application.Studio.Abstractions;
using ProtoAcceptanceDateProjection = Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryAcceptanceDateProjection;
using ProtoAcceptanceInputBinding = Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryAcceptanceInputBinding;
using ProtobufValue = Google.Protobuf.WellKnownTypes.Value;

namespace Aevatar.Studio.Tests;

public sealed class ActorDispatchWorkflowDeliveryCommandServiceTests
{
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse("2026-08-16T01:00:00Z");

    [Fact]
    public async Task CreateAsync_ShouldDispatchTypedImmutablePackageAndHonestAcceptedReceipt()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchWorkflowDeliveryCommandService(
            bootstrap,
            CreateCommandDispatch(dispatch));

        var receipt = await service.CreateAsync(new CreateWorkflowDeliveryMutation(
            "delivery-alpha",
            Package(),
            "scope-alpha",
            CreatedAt.AddHours(8),
            false,
            "review before install",
            "admin-alpha",
            CreatedAt));

        bootstrap.ActorIds.Should().ContainSingle()
            .Which.Should().Be(WorkflowDeliveryConventions.BuildActorId("delivery-alpha"));
        var envelope = dispatch.Envelopes.Should().ContainSingle().Subject;
        envelope.Route.PublisherActorId.Should().Be("aevatar.studio.projection.workflow-delivery");
        var command = envelope.Payload.Unpack<CreateWorkflowDeliveryCommand>();
        command.DeliveryId.Should().Be("delivery-alpha");
        command.Package.SourceYaml.Should().Be("name: workflow-alpha\n");
        command.Package.SourceHash.Should().Be("sha256-alpha");
        command.Package.PackageHash.Should().Be("package-hash-alpha");
        command.Package.AcceptancePolicy.Mode.Should().Be(
            Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryAcceptanceMode.AutomaticPreview);
        command.Package.AcceptancePolicy.Input.Literals.Fields.Should().ContainKey("dry_run")
            .WhoseValue.BoolValue.Should().BeTrue();
        command.Package.AcceptancePolicy.Input.Bindings.Select(static value => value.Key)
            .Should().Equal("created_month", "owner_id");
        var dateBinding = command.Package.AcceptancePolicy.Input.Bindings[0];
        dateBinding.SourceCase.Should().Be(
            ProtoAcceptanceInputBinding.SourceOneofCase.InstallationCreatedAtUtc);
        dateBinding.InstallationCreatedAtUtc.DateProjection.Should().Be(
            ProtoAcceptanceDateProjection.UtcYearMonth);
        dateBinding.InstallationCreatedAtUtc.DayOffset.Should().Be(-2);
        command.Package.AcceptancePolicy.Input.Bindings[1].SourceCase.Should().Be(
            ProtoAcceptanceInputBinding.SourceOneofCase.AuthenticatedOwnerExternalUserId);
        command.ExpiresAtDefaulted.Should().BeFalse();
        receipt.DeliveryId.Should().Be("delivery-alpha");
        receipt.ActorId.Should().Be(WorkflowDeliveryConventions.BuildActorId("delivery-alpha"));
        receipt.AckStage.Should().Be(WorkflowDeliveryCommandAckStage.AcceptedForDispatch);
        receipt.CommandId.Should().Be(envelope.Id);
        receipt.CorrelationId.Should().Be(envelope.Id);
    }

    [Fact]
    public async Task CreateAsync_WhenAcceptanceInputIsLegacyMissing_ShouldPreserveProtoAbsence()
    {
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchWorkflowDeliveryCommandService(
            new RecordingBootstrap(),
            CreateCommandDispatch(dispatch));

        await service.CreateAsync(new CreateWorkflowDeliveryMutation(
            "delivery-alpha",
            Package(inputDeclared: false),
            "scope-alpha",
            CreatedAt.AddHours(8),
            false,
            null,
            "admin-alpha",
            CreatedAt));

        var command = dispatch.Envelopes.Should().ContainSingle().Subject.Payload
            .Unpack<CreateWorkflowDeliveryCommand>();
        command.Package.AcceptancePolicy.Input.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_DefaultExpiryRetryClockDrift_ShouldKeepStableDispatchIdentity()
    {
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchWorkflowDeliveryCommandService(
            new RecordingBootstrap(),
            CreateCommandDispatch(dispatch));
        var first = new CreateWorkflowDeliveryMutation(
            "delivery-alpha",
            Package(),
            "scope-alpha",
            CreatedAt.AddHours(8),
            true,
            "review before install",
            "admin-alpha",
            CreatedAt);
        var retry = first with
        {
            Package = first.Package with { CreatedAtUtc = CreatedAt.AddMinutes(2) },
            ExpiresAtUtc = CreatedAt.AddHours(8).AddMinutes(2),
            CreatedAtUtc = CreatedAt.AddMinutes(2),
        };

        await service.CreateAsync(first);
        await service.CreateAsync(retry);

        dispatch.Envelopes.Should().HaveCount(2);
        dispatch.Envelopes.Select(static envelope => envelope.Id).Distinct().Should().ContainSingle();
        dispatch.Envelopes.Select(static envelope =>
                envelope.EnsureRuntime().EnsureDeliveryIdentity().OperationId)
            .Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task StartInstallationAsync_RetryingSameIdempotencyKey_ShouldKeepStableDispatchIdentity()
    {
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchWorkflowDeliveryCommandService(
            new RecordingBootstrap(),
            CreateCommandDispatch(dispatch));
        var original = StartMutation(CreatedAt.AddMinutes(1));
        var uncertainRetry = original with { RequestedAtUtc = CreatedAt.AddMinutes(2) };

        await service.StartInstallationAsync(original);
        await service.StartInstallationAsync(uncertainRetry);

        dispatch.Envelopes.Should().HaveCount(2);
        dispatch.Envelopes.Select(static envelope => envelope.Id).Distinct().Should().ContainSingle();
        dispatch.Envelopes.Select(static envelope =>
                envelope.EnsureRuntime().EnsureDeliveryIdentity().OperationId)
            .Distinct().Should().ContainSingle();
        var command = dispatch.Envelopes[0].Payload.Unpack<StartWorkflowInstallationCommand>();
        command.IdempotencyKey.Should().Be("publish-alpha");
        command.ConfigurationValues.Should().Contain("threshold", "20");
        command.ConnectionReferences.Should().Contain("mail", "user-service-alpha");
        command.ResolvedYaml.Should().Be("name: workflow-alpha\nthreshold: 20\n");
        command.Confirmations.Should().ContainSingle();
        command.OperationId.Should().Be("installation-alpha:provision:a1");
    }

    [Fact]
    public async Task AttachConnectionAsync_RetryClockDrift_ShouldMapExactIdentityAndKeepStableDispatchIdentity()
    {
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchWorkflowDeliveryCommandService(
            new RecordingBootstrap(),
            CreateCommandDispatch(dispatch));
        var original = new AttachWorkflowDeliveryConnectionMutation(
            "delivery-alpha",
            "scope-alpha",
            "mail",
            "api-lark-bot",
            "user-service-alpha",
            CreatedAt.AddMinutes(1),
            17);
        var uncertainRetry = original with { AttachedAtUtc = CreatedAt.AddMinutes(2) };

        await service.AttachConnectionAsync(original);
        await service.AttachConnectionAsync(uncertainRetry);

        dispatch.Envelopes.Should().HaveCount(2);
        dispatch.Envelopes.Select(static envelope => envelope.Id).Distinct().Should().ContainSingle();
        var command = dispatch.Envelopes[0].Payload.Unpack<AttachWorkflowDeliveryConnectionCommand>();
        command.DeliveryId.Should().Be("delivery-alpha");
        command.TargetScopeId.Should().Be("scope-alpha");
        command.SlotKey.Should().Be("mail");
        command.ServiceSlug.Should().Be("api-lark-bot");
        command.UserServiceId.Should().Be("user-service-alpha");
        command.AttachedAtUtc.ToDateTimeOffset().Should().Be(original.AttachedAtUtc);
        command.ExpectedStateVersion.Should().Be(17);
    }

    [Fact]
    public async Task StartInstallationAsync_ChangedSemanticInput_ShouldChangeDispatchIdentity()
    {
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchWorkflowDeliveryCommandService(
            new RecordingBootstrap(),
            CreateCommandDispatch(dispatch));
        var original = StartMutation(CreatedAt.AddMinutes(1));
        var changed = original with
        {
            TriggerIntent = new ApplicationTriggerIntent(
                ApplicationTriggerKind.Cron,
                "0 9 * * 1-5",
                "Asia/Singapore",
                false),
        };

        await service.StartInstallationAsync(original);
        await service.StartInstallationAsync(changed);

        dispatch.Envelopes.Select(static envelope => envelope.Id).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task ClaimInstallationContinuationAsync_ShouldDispatchTypedActorOwnedFence()
    {
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchWorkflowDeliveryCommandService(
            new RecordingBootstrap(),
            CreateCommandDispatch(dispatch));
        var mutation = new ClaimWorkflowInstallationContinuationMutation(
            "delivery-alpha",
            "installation-alpha",
            DeliveryApplication.WorkflowInstallationStatus.Accepted,
            2,
            "installation-alpha:provision:a2",
            "claim-alpha",
            "worker-alpha",
            TimeSpan.FromMinutes(2));

        await service.ClaimInstallationContinuationAsync(mutation);

        var envelope = dispatch.Envelopes.Should().ContainSingle().Subject;
        var command = envelope.Payload.Unpack<ClaimWorkflowInstallationContinuationCommand>();
        command.InstallationId.Should().Be("installation-alpha");
        command.ExpectedStatus.Should().Be(
            Aevatar.GAgents.WorkflowDelivery.WorkflowInstallationStatus.Accepted);
        command.Attempt.Should().Be(2);
        command.OperationId.Should().Be("installation-alpha:provision:a2");
        command.ClaimId.Should().Be("claim-alpha");
        command.ClaimantId.Should().Be("worker-alpha");
        command.RequestedDuration.ToTimeSpan().Should().Be(mutation.RequestedDuration);
    }

    [Fact]
    public async Task RecordInstallationReadyAsync_RetryingSameEvidence_ShouldKeepStableDispatchIdentity()
    {
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchWorkflowDeliveryCommandService(
            new RecordingBootstrap(),
            CreateCommandDispatch(dispatch));
        var original = ReadyMutation(CreatedAt.AddMinutes(3));
        var uncertainRetry = original with { ReadyAtUtc = CreatedAt.AddMinutes(4) };

        await service.RecordInstallationReadyAsync(original);
        await service.RecordInstallationReadyAsync(uncertainRetry);

        dispatch.Envelopes.Should().HaveCount(2);
        dispatch.Envelopes.Select(static envelope => envelope.Id).Distinct().Should().ContainSingle();
        var command = dispatch.Envelopes[0].Payload.Unpack<RecordWorkflowInstallationReadyCommand>();
        command.ReadyAtUtc.ToDateTimeOffset().Should().Be(original.ReadyAtUtc);
        command.Attempt.Should().Be(1);
        command.OperationId.Should().Be("installation-alpha:provision:a1");
        command.Evidence.PublishedService.PublishedServiceId.Should().Be("service-alpha");
        command.Evidence.PublishedService.CommittedStateVersion.Should().Be(20);
        command.Evidence.BoundRevision.BindingRunId.Should().Be("binding-run-alpha");
        command.Evidence.Trigger.ReadinessCase.Should().Be(
            Aevatar.GAgents.WorkflowDelivery.WorkflowTriggerReadinessEvidence.ReadinessOneofCase.NoTrigger);
        command.Evidence.Trigger.NoTrigger.Ready.Should().BeTrue();
        command.Evidence.AcceptanceRun.Status.Should().Be(
            Aevatar.GAgents.WorkflowDelivery.WorkflowAcceptanceRunStatus.TerminalSuccess);
        command.Evidence.Artifacts.Should().ContainSingle().Which.ContentDigest.Should()
            .Be("sha256-artifact-alpha");
    }

    [Fact]
    public async Task RecordInstallationReadyAsync_DifferentOutcomeFence_ShouldUseDifferentDispatchIdentity()
    {
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchWorkflowDeliveryCommandService(
            new RecordingBootstrap(),
            CreateCommandDispatch(dispatch));
        var original = ReadyMutation(CreatedAt.AddMinutes(3));
        var nextAttempt = original with
        {
            Attempt = 2,
            OperationId = "installation-alpha:provision:a2",
        };

        await service.RecordInstallationReadyAsync(original);
        await service.RecordInstallationReadyAsync(nextAttempt);

        dispatch.Envelopes.Select(static envelope => envelope.Id).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task RecordInstallationFailedAsync_ShouldMapExpectedStageIntoOutcomeFence()
    {
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchWorkflowDeliveryCommandService(
            new RecordingBootstrap(),
            CreateCommandDispatch(dispatch));
        var provisioningFailure = new RecordWorkflowInstallationFailedMutation(
            "delivery-alpha",
            "installation-alpha",
            "PROVISIONING_FAILED",
            "Workflow provisioning failed.",
            DeliveryApplication.WorkflowInstallationStatus.Accepted,
            1,
            "installation-alpha:provision:a1",
            CreatedAt.AddMinutes(3),
            "claim-accepted-a1",
            "worker-alpha");
        var uncertainRetry = provisioningFailure with { FailedAtUtc = CreatedAt.AddMinutes(4) };
        var readinessFailure = provisioningFailure with
        {
            ExpectedStatus = DeliveryApplication.WorkflowInstallationStatus.ProvisioningAccepted,
        };

        await service.RecordInstallationFailedAsync(provisioningFailure);
        await service.RecordInstallationFailedAsync(uncertainRetry);
        await service.RecordInstallationFailedAsync(readinessFailure);

        dispatch.Envelopes.Should().HaveCount(3);
        dispatch.Envelopes[0].Id.Should().Be(dispatch.Envelopes[1].Id);
        dispatch.Envelopes[2].Id.Should().NotBe(dispatch.Envelopes[0].Id);
        var commands = dispatch.Envelopes.Select(static envelope =>
            envelope.Payload.Unpack<RecordWorkflowInstallationFailedCommand>()).ToArray();
        commands[0].ExpectedStatus.Should().Be(
            Aevatar.GAgents.WorkflowDelivery.WorkflowInstallationStatus.Accepted);
        commands[2].ExpectedStatus.Should().Be(
            Aevatar.GAgents.WorkflowDelivery.WorkflowInstallationStatus.ProvisioningAccepted);
    }

    private static WorkflowDeliveryPackageSnapshot Package(bool inputDeclared = true) =>
        new(
            "package-alpha",
            "package-alpha@sha256-alpha",
            "workflow-alpha",
            "1",
            "Workflow Alpha",
            "Description",
            "name: workflow-alpha\n",
            "sha256-alpha",
            "package-hash-alpha",
            [
                new ApplicationVariableDefinition(
                    "threshold",
                    "Threshold",
                    "Approval threshold",
                    ApplicationVariableKind.Integer,
                    true,
                    "/threshold",
                    null,
                    "10"),
            ],
            [new ApplicationConnectionSlotDefinition("mail", "Mail", "lark", true)],
            ["network.write"],
            "Writes a notification",
            [],
            new DeliveryApplication.WorkflowDeliveryAcceptancePolicy(
                DeliveryApplication.WorkflowDeliveryAcceptanceMode.AutomaticPreview,
                null,
                AcceptanceInput(),
                inputDeclared),
            "admin-alpha",
            CreatedAt);

    private static DeliveryApplication.WorkflowDeliveryAcceptanceInputRecipe AcceptanceInput() =>
        new(
            new Struct
            {
                Fields =
                {
                    ["dry_run"] = ProtobufValue.ForBool(true),
                },
            },
            [
                new DeliveryApplication.WorkflowDeliveryAcceptanceInputBinding(
                    "created_month",
                    "period:",
                    ":utc",
                    new DeliveryApplication.WorkflowDeliveryInstallationCreatedAtUtcInput(
                        DeliveryApplication.WorkflowDeliveryAcceptanceDateProjection.UtcYearMonth,
                        -2)),
                new DeliveryApplication.WorkflowDeliveryAcceptanceInputBinding(
                    "owner_id",
                    "owner:",
                    string.Empty,
                    new DeliveryApplication.WorkflowDeliveryAuthenticatedOwnerExternalUserIdInput()),
            ]);

    private static StartWorkflowInstallationMutation StartMutation(DateTimeOffset requestedAt) =>
        new(
            "delivery-alpha",
            "installation-alpha",
            "publish-alpha",
            "scope-alpha",
            "team-alpha",
            new Dictionary<string, string> { ["threshold"] = "20" },
            new Dictionary<string, string> { ["mail"] = "user-service-alpha" },
            new ApplicationTriggerIntent(ApplicationTriggerKind.None, null, null, false),
            "sha256-alpha",
            "resolved-alpha",
            "name: workflow-alpha\nthreshold: 20\n",
            [new ApplicationConfirmationReference("call-alpha", "digest-alpha")],
            new WorkflowCapabilityAdmissionPlan(),
            null,
            "installation-alpha:provision:a1",
            requestedAt);

    private static RecordWorkflowInstallationReadyMutation ReadyMutation(DateTimeOffset readyAt) =>
        new(
            "delivery-alpha",
            "installation-alpha",
            new DeliveryApplication.WorkflowInstallationReadinessEvidence(
                new DeliveryApplication.WorkflowPublishedServiceReadinessEvidence(
                    "service-alpha",
                    true,
                    true,
                    20),
                new DeliveryApplication.WorkflowBoundRevisionReadinessEvidence(
                    "revision-alpha",
                    "binding-run-alpha",
                    true,
                    21),
                new DeliveryApplication.WorkflowTriggerReadinessEvidence(
                    new ApplicationTriggerIntent(ApplicationTriggerKind.None, null, null, false),
                    new DeliveryApplication.WorkflowNoTriggerReadinessEvidence(true),
                    null),
                new DeliveryApplication.WorkflowAcceptanceRunReadinessEvidence(
                    "acceptance-run-alpha",
                    DeliveryApplication.WorkflowAcceptanceRunStatus.TerminalSuccess,
                    22),
                [
                    new DeliveryApplication.WorkflowInstallationArtifactEvidence(
                        DeliveryApplication.WorkflowInstallationArtifactKind.RunOutput,
                        "artifact-alpha",
                        DeliveryApplication.WorkflowInstallationArtifactVerificationStatus.Verified,
                        "verification-alpha",
                        "sha256-artifact-alpha"),
                ]),
            1,
            "installation-alpha:provision:a1",
            readyAt,
            "claim-readiness-a1",
            "worker-alpha");

    private static StudioProjectionActorCommandDispatch CreateCommandDispatch(
        IActorDispatchPort dispatchPort) =>
        new(new DefaultCommandDispatchService<
            StudioProjectionActorCommand,
            StudioProjectionActorCommandTarget,
            StudioProjectionActorCommandReceipt,
            StudioProjectionActorCommandStartError>(
            new DefaultCommandDispatchPipeline<
                StudioProjectionActorCommand,
                StudioProjectionActorCommandTarget,
                StudioProjectionActorCommandReceipt,
                StudioProjectionActorCommandStartError>(
                new StudioProjectionActorCommandTargetResolver(),
                new DefaultCommandContextPolicy(),
                new StudioProjectionActorCommandEnvelopeFactory(),
                new ActorCommandTargetDispatcher<StudioProjectionActorCommandTarget>(dispatchPort),
                new StudioProjectionActorCommandReceiptFactory())));

    private sealed class RecordingBootstrap : IStudioActorBootstrap
    {
        public List<string> ActorIds { get; } = [];

        public Task<IActor> EnsureAsync<TAgent>(string actorId, CancellationToken ct = default)
            where TAgent : IAgent, IProjectedActor
        {
            ActorIds.Add(actorId);
            return Task.FromResult<IActor>(new StubActor(actorId));
        }
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<EventEnvelope> Envelopes { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            Envelopes.Add(envelope);
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class StubActor(string id) : IActor
    {
        public string Id { get; } = id;

        public IAgent Agent => throw new NotSupportedException();

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
