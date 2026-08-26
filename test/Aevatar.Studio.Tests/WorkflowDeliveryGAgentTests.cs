using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgents.WorkflowDelivery;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using ProtobufValue = Google.Protobuf.WellKnownTypes.Value;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowDeliveryGAgentTests
{
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse("2026-08-16T01:00:00Z");
    private static readonly MethodInfo SetIdMethod = typeof(GAgentBase)
        .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GAgentBase.SetId was not found.");

    [Fact]
    public async Task Create_ShouldCommitImmutablePackageSourceUnderCanonicalDeliveryActor()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        var command = CreateCommand("delivery-alpha");

        await agent.HandleCreateAsync(command);
        command.Package.SourceYaml = "mutated: true";

        agent.EventSourcing!.CurrentVersion.Should().Be(1);
        agent.State.DeliveryId.Should().Be("delivery-alpha");
        agent.State.TargetScopeId.Should().Be("scope-alpha");
        agent.State.Package.SourceYaml.Should().Be(MultiLineSourceYaml);
        agent.State.Package.SourceHash.Should().Be("sha256-alpha");
        agent.State.LifecycleStatus.Should().Be(WorkflowDeliveryLifecycleStatus.Active);
    }

    [Fact]
    public async Task Create_WhenPackageSemanticsDriftWithoutNewIdentity_ShouldReject()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        var command = CreateCommand("delivery-alpha");
        command.Package.RiskSummary = "changed after package identity was sealed";

        var action = () => agent.HandleCreateAsync(command);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*package hash does not match*");
        agent.EventSourcing!.CurrentVersion.Should().Be(0);
    }

    [Fact]
    public async Task Create_WhenAcceptanceModeIsUnknown_ShouldRejectBeforeCommit()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        var command = CreateCommand("delivery-alpha");
        command.Package.AcceptancePolicy.Mode = (WorkflowDeliveryAcceptanceMode)99;
        ResealPackage(command.Package);

        var action = () => agent.HandleCreateAsync(command);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*acceptance policy is required*");
        agent.EventSourcing!.CurrentVersion.Should().Be(0);
    }

    [Fact]
    public async Task Create_WhenAcceptanceBindingHasNoTypedSource_ShouldRejectBeforeCommit()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        var command = CreateCommand("delivery-alpha");
        command.Package.AcceptancePolicy.Input.Bindings.Add(
            new WorkflowDeliveryAcceptanceInputBinding { Key = "period" });
        ResealPackage(command.Package);

        var action = () => agent.HandleCreateAsync(command);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*binding source is unsupported*");
        agent.EventSourcing!.CurrentVersion.Should().Be(0);
    }

    [Fact]
    public async Task Create_WhenAcceptanceBindingsAreNotCanonicallyOrdered_ShouldRejectBeforeCommit()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        var command = CreateCommand("delivery-alpha");
        command.Package.AcceptancePolicy.Input.Bindings.Add(new WorkflowDeliveryAcceptanceInputBinding
        {
            Key = "zulu",
            AuthenticatedOwnerExternalUserId =
                new WorkflowDeliveryAuthenticatedOwnerExternalUserIdInput(),
        });
        command.Package.AcceptancePolicy.Input.Bindings.Add(new WorkflowDeliveryAcceptanceInputBinding
        {
            Key = "alpha",
            AuthenticatedOwnerExternalUserId =
                new WorkflowDeliveryAuthenticatedOwnerExternalUserIdInput(),
        });
        ResealPackage(command.Package);

        var action = () => agent.HandleCreateAsync(command);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*stable key ordering*");
        agent.EventSourcing!.CurrentVersion.Should().Be(0);
    }

    [Fact]
    public async Task DuplicateCreate_WithDefaultExpiryClockDrift_ShouldKeepFirstExpiryWhileExplicitDriftConflicts()
    {
        var defaultedAgent = await CreateAgentAsync("delivery-defaulted");
        var firstDefaulted = CreateCommand("delivery-defaulted");
        firstDefaulted.ExpiresAtDefaulted = true;
        await defaultedAgent.HandleCreateAsync(firstDefaulted);
        var defaultedRetry = firstDefaulted.Clone();
        defaultedRetry.CreatedAtUtc = Timestamp.FromDateTimeOffset(CreatedAt.AddMinutes(2));
        defaultedRetry.ExpiresAtUtc = Timestamp.FromDateTimeOffset(CreatedAt.AddHours(4).AddMinutes(2));

        await defaultedAgent.HandleCreateAsync(defaultedRetry);

        defaultedAgent.EventSourcing!.CurrentVersion.Should().Be(1);
        defaultedAgent.State.ExpiresAtDefaulted.Should().BeTrue();
        defaultedAgent.State.ExpiresAtUtc.Should().Be(firstDefaulted.ExpiresAtUtc);

        var explicitAgent = await CreateAgentAsync("delivery-explicit");
        var firstExplicit = CreateCommand("delivery-explicit");
        await explicitAgent.HandleCreateAsync(firstExplicit);
        var explicitRetry = firstExplicit.Clone();
        explicitRetry.ExpiresAtUtc = Timestamp.FromDateTimeOffset(CreatedAt.AddHours(5));

        var conflict = () => explicitAgent.HandleCreateAsync(explicitRetry);

        await conflict.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*different immutable content*");
        explicitAgent.EventSourcing!.CurrentVersion.Should().Be(1);
        explicitAgent.State.ExpiresAtDefaulted.Should().BeFalse();
    }

    [Fact]
    public async Task DuplicateInstallationStart_ShouldConvergeToOneAcceptedInstallation()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        await agent.HandleCreateAsync(CreateCommand("delivery-alpha"));
        var start = StartInstallationCommand();

        await agent.HandleStartInstallationAsync(start);
        await agent.HandleStartInstallationAsync(start.Clone());

        agent.EventSourcing!.CurrentVersion.Should().Be(2);
        agent.State.Installation.InstallationId.Should().Be("installation-alpha");
        agent.State.Installation.IdempotencyKey.Should().Be("publish-alpha");
        agent.State.Installation.Status.Should().Be(WorkflowInstallationStatus.Accepted);
        agent.State.Installation.Stage.Should().Be("accepted");
        agent.State.Installation.Attempt.Should().Be(1);
        agent.State.Installation.OperationId.Should().Be("installation-alpha:provision:a1");
        agent.State.Installation.CapabilityAdmissionPlan.Should().NotBeNull();
        agent.State.Installation.AcceptanceInput.Should().NotBeNull();
        agent.State.Installation.AcceptanceInput.Fields.Should().BeEmpty();
    }

    [Fact]
    public async Task DuplicateInstallationStart_WithClockDrift_ShouldKeepFirstResolvedInput()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        var create = CreateCommand("delivery-alpha");
        create.ExpiresAtUtc = Timestamp.FromDateTimeOffset(
            DateTimeOffset.Parse("2026-08-18T00:00:00Z"));
        create.Package.AcceptancePolicy.Input.Bindings.Add(new WorkflowDeliveryAcceptanceInputBinding
        {
            Key = "installation_date",
            InstallationCreatedAtUtc = new WorkflowDeliveryInstallationCreatedAtUtcInput
            {
                DateProjection = WorkflowDeliveryAcceptanceDateProjection.UtcDate,
            },
        });
        ResealPackage(create.Package);
        await agent.HandleCreateAsync(create);
        var first = StartInstallationCommand();
        first.RequestedAtUtc = Timestamp.FromDateTimeOffset(
            DateTimeOffset.Parse("2026-08-16T23:59:00Z"));
        var duplicate = first.Clone();
        duplicate.RequestedAtUtc = Timestamp.FromDateTimeOffset(
            DateTimeOffset.Parse("2026-08-17T00:01:00Z"));

        await agent.HandleStartInstallationAsync(first);
        await agent.HandleStartInstallationAsync(duplicate);

        agent.EventSourcing!.CurrentVersion.Should().Be(2);
        agent.State.Installation.CreatedAtUtc.Should().Be(first.RequestedAtUtc);
        agent.State.Installation.AcceptanceInput.Fields["installation_date"].StringValue
            .Should().Be("2026-08-16");
    }

    [Fact]
    public async Task StartInstallation_ShouldResolveAndPersistGenericAcceptanceInputFromCommittedContext()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        var create = CreateCommand("delivery-alpha");
        create.ExpiresAtUtc = Timestamp.FromDateTimeOffset(
            DateTimeOffset.Parse("2027-01-02T00:00:00Z"));
        create.Package.AcceptancePolicy.Input.Literals.Fields.Add(
            "dry_run",
            ProtobufValue.ForBool(true));
        create.Package.AcceptancePolicy.Input.Bindings.Add(new WorkflowDeliveryAcceptanceInputBinding
        {
            Key = "compact_reference",
            Prefix = "run-",
            InstallationCreatedAtUtc = new WorkflowDeliveryInstallationCreatedAtUtcInput
            {
                DateProjection = WorkflowDeliveryAcceptanceDateProjection.UtcCompactDate,
                DayOffset = 1,
            },
        });
        create.Package.AcceptancePolicy.Input.Bindings.Add(new WorkflowDeliveryAcceptanceInputBinding
        {
            Key = "iso_period",
            InstallationCreatedAtUtc = new WorkflowDeliveryInstallationCreatedAtUtcInput
            {
                DateProjection = WorkflowDeliveryAcceptanceDateProjection.UtcIsoWeek,
            },
        });
        create.Package.AcceptancePolicy.Input.Bindings.Add(new WorkflowDeliveryAcceptanceInputBinding
        {
            Key = "owner_reference",
            Prefix = "owner-",
            AuthenticatedOwnerExternalUserId =
                new WorkflowDeliveryAuthenticatedOwnerExternalUserIdInput(),
        });
        ResealPackage(create.Package);
        await agent.HandleCreateAsync(create);
        var start = StartInstallationCommand();
        start.RequestedAtUtc = Timestamp.FromDateTimeOffset(
            DateTimeOffset.Parse("2027-01-01T00:30:00+02:00"));
        start.AuthenticatedOwner = AuthorizationOwnerContext();

        await agent.HandleStartInstallationAsync(start);

        var input = agent.State.Installation.AcceptanceInput;
        input.Should().NotBeNull();
        input.Fields["dry_run"].BoolValue.Should().BeTrue();
        input.Fields["compact_reference"].StringValue.Should().Be("run-20270101");
        input.Fields["iso_period"].StringValue.Should().Be("2026-W53");
        input.Fields["owner_reference"].StringValue.Should().Be("owner-user-alpha");
        agent.State.Installation.CreatedAtUtc.Should().Be(start.RequestedAtUtc);
    }

    [Fact]
    public async Task StartInstallation_WhenResolvedDynamicStringExceedsLimit_ShouldRejectBeforeCommit()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        var create = CreateCommand("delivery-alpha");
        create.Package.AcceptancePolicy.Input.Bindings.Add(new WorkflowDeliveryAcceptanceInputBinding
        {
            Key = "owner_reference",
            Prefix = "x",
            AuthenticatedOwnerExternalUserId =
                new WorkflowDeliveryAuthenticatedOwnerExternalUserIdInput(),
        });
        ResealPackage(create.Package);
        await agent.HandleCreateAsync(create);
        var start = StartInstallationCommand();
        start.AuthenticatedOwner = AuthorizationOwnerContext();
        start.AuthenticatedOwner.SubjectExternalUserId = new string('u', 4096);

        var action = () => agent.HandleStartInstallationAsync(start);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*resolved acceptance input string is too long*");
        agent.EventSourcing!.CurrentVersion.Should().Be(1);
        agent.State.Installation.Should().BeNull();
    }

    [Fact]
    public void AcceptanceInputProtoPresence_ShouldDistinguishLegacyFromExplicitEmptyRecipe()
    {
        WorkflowDeliveryAcceptancePolicy.InputFieldNumber.Should().Be(3);
        WorkflowInstallationState.AcceptanceInputFieldNumber.Should().Be(34);
        var declared = new WorkflowDeliveryAcceptancePolicy
        {
            Mode = WorkflowDeliveryAcceptanceMode.AutomaticPreview,
            Input = new WorkflowDeliveryAcceptanceInputRecipe
            {
                Literals = new Struct(),
            },
        };

        var roundTrip = WorkflowDeliveryAcceptancePolicy.Parser.ParseFrom(declared.ToByteArray());
        var legacy = WorkflowDeliveryAcceptancePolicy.Parser.ParseFrom(
            new WorkflowDeliveryAcceptancePolicy
            {
                Mode = WorkflowDeliveryAcceptanceMode.AutomaticPreview,
            }.ToByteArray());

        roundTrip.Input.Should().NotBeNull();
        roundTrip.Input.Literals.Should().NotBeNull();
        roundTrip.Input.Literals.Fields.Should().BeEmpty();
        legacy.Input.Should().BeNull();
    }

    [Fact]
    public async Task StartInstallation_WhenConnectionChangedAfterProjectionRead_ShouldRejectStaleReference()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        await agent.HandleCreateAsync(CreateCommandWithConnectionSlot());
        await agent.HandleBeginConnectionAsync(BeginConnectionCommand("link-a"));
        await agent.HandleUpdateConnectionAsync(UpdateConnectionCommand(
            "link-a",
            WorkflowDeliveryConnectionStatus.Completed,
            "user-service-a"));
        await agent.HandleBeginConnectionAsync(BeginConnectionCommand("link-b"));
        var start = StartInstallationCommand();
        start.ConnectionReferences.Add("lark", "user-service-a");

        var action = () => agent.HandleStartInstallationAsync(start);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*connection references do not match completed delivery connections*");
        agent.EventSourcing!.CurrentVersion.Should().Be(4);
        agent.State.Installation.Should().BeNull();
        agent.State.Connections.Should().ContainSingle();
        agent.State.Connections[0].LinkId.Should().Be("link-b");
        agent.State.Connections[0].Status.Should().Be(WorkflowDeliveryConnectionStatus.Pending);
    }

    [Fact]
    public async Task AttachConnection_ShouldCommitCompletedConnectionWithoutFabricatingLinkIdentity()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        await agent.HandleCreateAsync(CreateCommandWithConnectionSlot());

        await agent.HandleAttachConnectionAsync(AttachConnectionCommand("user-service-a"));

        agent.EventSourcing!.CurrentVersion.Should().Be(2);
        agent.State.Connections.Should().ContainSingle();
        var connection = agent.State.Connections[0];
        connection.SlotKey.Should().Be("lark");
        connection.ServiceSlug.Should().Be("api-lark");
        connection.Status.Should().Be(WorkflowDeliveryConnectionStatus.Completed);
        connection.UserServiceId.Should().Be("user-service-a");
        connection.LinkId.Should().BeEmpty();
        connection.UpdatedAtUtc.Should().Be(AttachConnectionCommand("user-service-a").AttachedAtUtc);
    }

    [Fact]
    public async Task AttachConnection_WhenConnectLinkIsPending_ShouldRejectWithoutChangingConnection()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        await agent.HandleCreateAsync(CreateCommandWithConnectionSlot());
        await agent.HandleBeginConnectionAsync(BeginConnectionCommand("link-a"));

        var action = () => agent.HandleAttachConnectionAsync(AttachConnectionCommand("user-service-a"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*connection link is already pending*");
        agent.EventSourcing!.CurrentVersion.Should().Be(2);
        agent.State.Connections.Should().ContainSingle();
        agent.State.Connections[0].LinkId.Should().Be("link-a");
        agent.State.Connections[0].Status.Should().Be(WorkflowDeliveryConnectionStatus.Pending);
    }

    [Fact]
    public async Task AttachConnection_AfterInstallation_ShouldAllowExactReplayAndRejectReplacement()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        await agent.HandleCreateAsync(CreateCommandWithConnectionSlot());
        var attached = AttachConnectionCommand("user-service-a");
        await agent.HandleAttachConnectionAsync(attached);
        var start = StartInstallationCommand();
        start.ConnectionReferences.Add("lark", "user-service-a");
        await agent.HandleStartInstallationAsync(start);

        var exactReplay = attached.Clone();
        exactReplay.ExpectedStateVersion = 0;
        await agent.HandleAttachConnectionAsync(exactReplay);
        var replace = () => agent.HandleAttachConnectionAsync(AttachConnectionCommand("user-service-b"));

        await replace.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*connections cannot be changed after installation has started*");
        agent.EventSourcing!.CurrentVersion.Should().Be(3);
        agent.State.Connections.Should().ContainSingle();
        agent.State.Connections[0].UserServiceId.Should().Be("user-service-a");
        agent.State.Installation.ConnectionReferences.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, string>("lark", "user-service-a"));
    }

    [Fact]
    public async Task AttachConnection_WhenExpectedStateVersionIsStale_ShouldRejectReplacement()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        await agent.HandleCreateAsync(CreateCommandWithConnectionSlot());
        await agent.HandleAttachConnectionAsync(AttachConnectionCommand("user-service-a"));

        var replace = () => agent.HandleAttachConnectionAsync(
            AttachConnectionCommand("user-service-b", expectedStateVersion: 1));

        await replace.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(
                "workflow delivery attach expected_state_version 1 does not match committed state version 2.");
        agent.EventSourcing!.CurrentVersion.Should().Be(2);
        agent.State.Connections.Should().ContainSingle();
        agent.State.Connections[0].UserServiceId.Should().Be("user-service-a");
    }

    [Fact]
    public async Task AttachConnection_WhenExpectedStateVersionIsNotPositive_ShouldRejectMutation()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        await agent.HandleCreateAsync(CreateCommandWithConnectionSlot());

        var attach = () => agent.HandleAttachConnectionAsync(
            AttachConnectionCommand("user-service-a", expectedStateVersion: 0));

        await attach.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("workflow delivery attach expected_state_version must be positive.");
        agent.EventSourcing!.CurrentVersion.Should().Be(1);
        agent.State.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task Connections_AfterInstallation_ShouldAllowExactReplayAndRejectMutation()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        await agent.HandleCreateAsync(CreateCommandWithConnectionSlot());
        var begin = BeginConnectionCommand("link-a");
        var completed = UpdateConnectionCommand(
            "link-a",
            WorkflowDeliveryConnectionStatus.Completed,
            "user-service-a");
        await agent.HandleBeginConnectionAsync(begin);
        await agent.HandleUpdateConnectionAsync(completed);
        var start = StartInstallationCommand();
        start.ConnectionReferences.Add("lark", "user-service-a");
        await agent.HandleStartInstallationAsync(start);

        await agent.HandleBeginConnectionAsync(begin.Clone());
        await agent.HandleUpdateConnectionAsync(completed.Clone());
        var replaceLink = () => agent.HandleBeginConnectionAsync(BeginConnectionCommand("link-b"));
        var replaceReference = () => agent.HandleUpdateConnectionAsync(UpdateConnectionCommand(
            "link-a",
            WorkflowDeliveryConnectionStatus.Completed,
            "user-service-b"));

        await replaceLink.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*connections cannot be changed after installation has started*");
        await replaceReference.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*connections cannot be changed after installation has started*");
        agent.EventSourcing!.CurrentVersion.Should().Be(4);
        agent.State.Connections.Should().ContainSingle();
        agent.State.Connections[0].LinkId.Should().Be("link-a");
        agent.State.Connections[0].Status.Should().Be(WorkflowDeliveryConnectionStatus.Completed);
        agent.State.Connections[0].UserServiceId.Should().Be("user-service-a");
        agent.State.Installation.ConnectionReferences.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, string>("lark", "user-service-a"));
    }

    [Fact]
    public async Task ScheduledInstallation_WhenOwnerIsNyxIdNative_ShouldAllowEmptySubjectTenant()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        await agent.HandleCreateAsync(CreateCommand("delivery-alpha"));
        var trigger = new WorkflowDeliveryTriggerIntent
        {
            Kind = WorkflowDeliveryTriggerKind.OneShot,
            RunImmediately = true,
            TimeZone = "UTC",
        };
        var start = StartInstallationCommand(trigger);
        start.AuthenticatedOwner.SubjectTenant = string.Empty;

        await agent.HandleStartInstallationAsync(start);

        agent.State.Installation.AuthenticatedOwner.Should().NotBeNull();
        agent.State.Installation.AuthenticatedOwner.SubjectPlatform.Should().Be("nyxid");
        agent.State.Installation.AuthenticatedOwner.SubjectTenant.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduledInstallation_WhenOwnerIsChannelNative_ShouldRequireSubjectTenant()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        await agent.HandleCreateAsync(CreateCommand("delivery-alpha"));
        var trigger = new WorkflowDeliveryTriggerIntent
        {
            Kind = WorkflowDeliveryTriggerKind.OneShot,
            RunImmediately = true,
            TimeZone = "UTC",
        };
        var start = StartInstallationCommand(trigger);
        start.AuthenticatedOwner.SubjectPlatform = "lark";
        start.AuthenticatedOwner.SubjectTenant = string.Empty;

        var act = () => agent.HandleStartInstallationAsync(start);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*authenticated_owner.subject_tenant*");
        agent.State.Installation.Should().BeNull();
    }

    [Fact]
    public async Task DuplicateInstallationStart_WithChangedSemanticInput_ShouldRejectConflict()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        await agent.HandleCreateAsync(CreateCommand("delivery-alpha"));
        var start = StartInstallationCommand();
        start.Confirmations.Add(new WorkflowDeliveryConfirmationReference
        {
            CallSiteId = "call-alpha",
            RequestContractDigest = "digest-alpha",
        });
        await agent.HandleStartInstallationAsync(start);

        var changedTrigger = start.Clone();
        changedTrigger.TriggerIntent.RunImmediately = true;
        var changedYaml = start.Clone();
        changedYaml.ResolvedYaml = "name: workflow-beta\n";
        var changedConfirmation = start.Clone();
        changedConfirmation.Confirmations[0].RequestContractDigest = "digest-beta";

        foreach (var conflicting in new[] { changedTrigger, changedYaml, changedConfirmation })
        {
            var duplicate = () => agent.HandleStartInstallationAsync(conflicting);
            await duplicate.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*different installation*");
        }

        agent.EventSourcing!.CurrentVersion.Should().Be(2);
        agent.State.Installation.TriggerIntent.Kind.Should().Be(WorkflowDeliveryTriggerKind.None);
        agent.State.Installation.ResolvedYaml.Should().Be("name: workflow-alpha\n");
        agent.State.Installation.Confirmations.Should().ContainSingle()
            .Which.RequestContractDigest.Should().Be("digest-alpha");
    }

    [Fact]
    public async Task ContinuationClaim_ShouldRemainWithFirstClaimantUntilItsActorOwnedLeaseExpires()
    {
        var clock = new MutableTimeProvider(CreatedAt.AddMinutes(2));
        var agent = await CreateAgentAsync("delivery-alpha", clock);
        await agent.HandleCreateAsync(CreateCommand("delivery-alpha"));
        await agent.HandleStartInstallationAsync(StartInstallationCommand());
        var first = ContinuationClaimCommand(WorkflowInstallationStatus.Accepted);

        await agent.HandleClaimInstallationContinuationAsync(first);
        var competing = first.Clone();
        competing.ClaimId = "claim-competing";
        competing.ClaimantId = "worker-beta";
        await agent.HandleClaimInstallationContinuationAsync(competing);

        agent.EventSourcing!.CurrentVersion.Should().Be(3);
        agent.State.Installation.ContinuationClaim.ClaimantId.Should().Be("worker-alpha");
        agent.State.Installation.ContinuationClaim.ClaimedAtUtc.ToDateTimeOffset()
            .Should().Be(clock.GetUtcNow());
        agent.State.Installation.ContinuationClaim.ExpiresAtUtc.ToDateTimeOffset()
            .Should().Be(clock.GetUtcNow().AddMinutes(5));

        clock.SetUtcNow(clock.GetUtcNow().AddMinutes(5));
        await agent.HandleClaimInstallationContinuationAsync(competing);

        agent.EventSourcing.CurrentVersion.Should().Be(4);
        agent.State.Installation.ContinuationClaim.ClaimId.Should().Be("claim-competing");
        agent.State.Installation.ContinuationClaim.ClaimantId.Should().Be("worker-beta");
        agent.State.Installation.ContinuationClaim.ClaimedAtUtc.ToDateTimeOffset()
            .Should().Be(clock.GetUtcNow());
    }

    [Fact]
    public async Task ContinuationClaim_WhenDeliveryWasRevoked_ShouldTerminalizeAndNearExpiryShouldCapLease()
    {
        var revoked = await CreateAgentAsync("delivery-alpha");
        await revoked.HandleCreateAsync(CreateCommand("delivery-alpha"));
        await revoked.HandleStartInstallationAsync(StartInstallationCommand());
        await revoked.HandleRevokeAsync(new RevokeWorkflowDeliveryCommand
        {
            DeliveryId = "delivery-alpha",
            RevokedBy = "admin-alpha",
            RevokedAtUtc = Timestamp.FromDateTimeOffset(CreatedAt.AddMinutes(2)),
        });

        await revoked.HandleClaimInstallationContinuationAsync(
            ContinuationClaimCommand(WorkflowInstallationStatus.Accepted));

        revoked.State.Installation.Status.Should().Be(WorkflowInstallationStatus.Failed);
        revoked.State.Installation.ErrorCode.Should().Be("delivery_revoked");
        revoked.State.Installation.FailureOriginStatus.Should().Be(WorkflowInstallationStatus.Accepted);
        revoked.State.Installation.ContinuationClaim.Should().BeNull();

        var nearExpiry = CreatedAt.AddHours(4).AddMinutes(-2);
        var expiring = await CreateAgentAsync(
            "delivery-alpha",
            new MutableTimeProvider(nearExpiry));
        await expiring.HandleCreateAsync(CreateCommand("delivery-alpha"));
        await expiring.HandleStartInstallationAsync(StartInstallationCommand());
        var outlivingClaim = ContinuationClaimCommand(WorkflowInstallationStatus.Accepted);

        await expiring.HandleClaimInstallationContinuationAsync(outlivingClaim);

        expiring.State.Installation.ContinuationClaim.ClaimedAtUtc.ToDateTimeOffset()
            .Should().Be(nearExpiry);
        expiring.State.Installation.ContinuationClaim.ExpiresAtUtc
            .Should().Be(expiring.State.ExpiresAtUtc);
    }

    [Fact]
    public async Task ContinuationClaim_WhenDeliveryExpired_ShouldTerminalizeInstallation()
    {
        var clock = new MutableTimeProvider(CreatedAt.AddHours(4));
        var agent = await CreateAgentAsync("delivery-alpha", clock);
        await agent.HandleCreateAsync(CreateCommand("delivery-alpha"));
        await agent.HandleStartInstallationAsync(StartInstallationCommand());

        await agent.HandleClaimInstallationContinuationAsync(
            ContinuationClaimCommand(WorkflowInstallationStatus.Accepted));

        agent.State.Installation.Status.Should().Be(WorkflowInstallationStatus.Failed);
        agent.State.Installation.ErrorCode.Should().Be("delivery_expired");
        agent.State.Installation.FailureOriginStatus.Should().Be(WorkflowInstallationStatus.Accepted);
        agent.State.Installation.ContinuationClaim.Should().BeNull();
    }

    [Fact]
    public async Task ContinuationClaim_AfterActorClockRollback_ShouldRemainExclusiveAndAcceptOwnedOutcome()
    {
        var clock = new MutableTimeProvider(CreatedAt.AddMinutes(3));
        var agent = await CreateAgentAsync("delivery-alpha", clock);
        await agent.HandleCreateAsync(CreateCommand("delivery-alpha"));
        await agent.HandleStartInstallationAsync(StartInstallationCommand());
        await agent.HandleClaimInstallationContinuationAsync(
            ContinuationClaimCommand(WorkflowInstallationStatus.Accepted));
        clock.SetUtcNow(CreatedAt.AddMinutes(2));
        var replacement = ContinuationClaimCommand(
            WorkflowInstallationStatus.Accepted,
            claimantId: "worker-beta");
        replacement.ClaimId = "claim-replacement";

        await agent.HandleClaimInstallationContinuationAsync(replacement);

        agent.EventSourcing!.CurrentVersion.Should().Be(3);
        agent.State.Installation.ContinuationClaim.ClaimId.Should().Be("claim-accepted-a1");
        agent.State.Installation.ContinuationClaim.ClaimedAtUtc.ToDateTimeOffset()
            .Should().Be(CreatedAt.AddMinutes(3));

        await agent.HandleProvisioningAcceptedAsync(ProvisioningAcceptedCommand());

        agent.EventSourcing.CurrentVersion.Should().Be(4);
        agent.State.Installation.Status.Should().Be(WorkflowInstallationStatus.ProvisioningAccepted);
    }

    [Fact]
    public async Task ProvisioningOutcome_AfterActorOwnedLeaseExpiry_ShouldRejectBackdatedOutcome()
    {
        var clock = new MutableTimeProvider(CreatedAt.AddMinutes(2));
        var agent = await CreateAgentAsync("delivery-alpha", clock);
        await agent.HandleCreateAsync(CreateCommand("delivery-alpha"));
        await agent.HandleStartInstallationAsync(StartInstallationCommand());
        await agent.HandleClaimInstallationContinuationAsync(
            ContinuationClaimCommand(
                WorkflowInstallationStatus.Accepted,
                requestedDuration: TimeSpan.FromMinutes(2)));
        clock.SetUtcNow(CreatedAt.AddMinutes(4));
        var outcome = ProvisioningAcceptedCommand();
        outcome.AcceptedAtUtc = Timestamp.FromDateTimeOffset(CreatedAt.AddMinutes(3));

        var record = () => agent.HandleProvisioningAcceptedAsync(outcome);

        await record.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not active*");
        agent.State.Installation.Status.Should().Be(WorkflowInstallationStatus.Accepted);
    }

    [Fact]
    public async Task Revoke_AfterContinuationClaim_ShouldWithdrawBeforeOwnedProvisioningOutcome()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        await agent.HandleCreateAsync(CreateCommand("delivery-alpha"));
        await agent.HandleStartInstallationAsync(StartInstallationCommand());
        await ClaimContinuationAsync(agent, WorkflowInstallationStatus.Accepted);
        await agent.HandleRevokeAsync(new RevokeWorkflowDeliveryCommand
        {
            DeliveryId = "delivery-alpha",
            RevokedBy = "admin-alpha",
            RevokedAtUtc = Timestamp.FromDateTimeOffset(CreatedAt.AddMinutes(2).AddSeconds(30)),
        });

        await agent.HandleProvisioningAcceptedAsync(ProvisioningAcceptedCommand());

        agent.EventSourcing!.CurrentVersion.Should().Be(5);
        agent.State.LifecycleStatus.Should().Be(WorkflowDeliveryLifecycleStatus.Revoked);
        agent.State.Installation.Status.Should().Be(WorkflowInstallationStatus.Failed);
        agent.State.Installation.ErrorCode.Should().Be("delivery_revoked");
        agent.State.Installation.ContinuationClaim.Should().BeNull();
    }

    [Fact]
    public async Task ReadyOutcome_AfterRevokeWithActiveClaim_ShouldRemainWithdrawnWithoutReadyEvent()
    {
        var eventStore = new InMemoryEventStore();
        var agent = await CreateAgentAsync("delivery-alpha", eventStore: eventStore);
        await agent.HandleCreateAsync(CreateCommand("delivery-alpha"));
        await agent.HandleStartInstallationAsync(StartInstallationCommand());
        await ClaimContinuationAsync(agent, WorkflowInstallationStatus.Accepted);
        await agent.HandleProvisioningAcceptedAsync(ProvisioningAcceptedCommand());
        await ClaimContinuationAsync(agent, WorkflowInstallationStatus.ProvisioningAccepted);

        await agent.HandleRevokeAsync(new RevokeWorkflowDeliveryCommand
        {
            DeliveryId = "delivery-alpha",
            RevokedBy = "admin-alpha",
            RevokedAtUtc = Timestamp.FromDateTimeOffset(CreatedAt.AddMinutes(3).AddSeconds(30)),
        });
        await agent.HandleInstallationReadyAsync(ReadyCommand());

        agent.State.Installation.Status.Should().Be(WorkflowInstallationStatus.Failed);
        agent.State.Installation.ErrorCode.Should().Be("delivery_revoked");
        agent.State.Installation.FailureOriginStatus.Should()
            .Be(WorkflowInstallationStatus.ProvisioningAccepted);
        agent.State.Installation.ContinuationClaim.Should().BeNull();
        var events = await eventStore.GetEventsAsync(agent.Id);
        events.Should().NotContain(evt => evt.EventData.Is(WorkflowInstallationReadyEvent.Descriptor));
    }

    [Fact]
    public async Task ReadyOutcome_AfterExpiryWithActiveClaim_ShouldRemainWithdrawnWithoutReadyEvent()
    {
        var clock = new MutableTimeProvider(CreatedAt.AddMinutes(2));
        var eventStore = new InMemoryEventStore();
        var agent = await CreateAgentAsync("delivery-alpha", clock, eventStore);
        await agent.HandleCreateAsync(CreateCommand("delivery-alpha"));
        await agent.HandleStartInstallationAsync(StartInstallationCommand());
        await ClaimContinuationAsync(agent, WorkflowInstallationStatus.Accepted);
        await agent.HandleProvisioningAcceptedAsync(ProvisioningAcceptedCommand());
        await ClaimContinuationAsync(agent, WorkflowInstallationStatus.ProvisioningAccepted);
        clock.SetUtcNow(CreatedAt.AddHours(4));

        await agent.HandleInstallationReadyAsync(ReadyCommand());

        agent.State.Installation.Status.Should().Be(WorkflowInstallationStatus.Failed);
        agent.State.Installation.ErrorCode.Should().Be("delivery_expired");
        agent.State.Installation.FailureOriginStatus.Should()
            .Be(WorkflowInstallationStatus.ProvisioningAccepted);
        agent.State.Installation.ContinuationClaim.Should().BeNull();
        var events = await eventStore.GetEventsAsync(agent.Id);
        events.Should().NotContain(evt => evt.EventData.Is(WorkflowInstallationReadyEvent.Descriptor));
    }

    [Fact]
    public async Task Claim_AfterLegacyRevokedReplay_ShouldPersistWithdrawalWithoutClaiming()
    {
        var eventStore = new InMemoryEventStore();
        var first = await CreateAgentAsync("delivery-alpha", eventStore: eventStore);
        await first.HandleCreateAsync(CreateCommand("delivery-alpha"));
        await first.HandleStartInstallationAsync(StartInstallationCommand());
        await first.DeactivateAsync();
        await AppendLegacyRevokedEventAsync(eventStore, first.Id, expectedVersion: 2);

        var replayed = await CreateAgentAsync(
            "delivery-alpha",
            new MutableTimeProvider(CreatedAt.AddMinutes(4)),
            eventStore);

        replayed.State.LifecycleStatus.Should().Be(WorkflowDeliveryLifecycleStatus.Revoked);
        replayed.State.Installation.Status.Should().Be(WorkflowInstallationStatus.Accepted);
        await replayed.HandleClaimInstallationContinuationAsync(
            ContinuationClaimCommand(WorkflowInstallationStatus.Accepted));

        replayed.State.Installation.Status.Should().Be(WorkflowInstallationStatus.Failed);
        replayed.State.Installation.ErrorCode.Should().Be("delivery_revoked");
        replayed.State.Installation.ContinuationClaim.Should().BeNull();
        var events = await eventStore.GetEventsAsync(replayed.Id);
        events.Should().ContainSingle(evt =>
            evt.EventData.Is(WorkflowInstallationWithdrawnEvent.Descriptor));
        events.Should().NotContain(evt =>
            evt.EventData.Is(WorkflowInstallationContinuationClaimedEvent.Descriptor));
    }

    [Fact]
    public async Task ReadyOutcome_AfterLegacyRevokedReplay_ShouldWithdrawWithoutReadyEvent()
    {
        var eventStore = new InMemoryEventStore();
        var first = await CreateAgentAsync("delivery-alpha", eventStore: eventStore);
        await first.HandleCreateAsync(CreateCommand("delivery-alpha"));
        await first.HandleStartInstallationAsync(StartInstallationCommand());
        await ClaimContinuationAsync(first, WorkflowInstallationStatus.Accepted);
        await first.HandleProvisioningAcceptedAsync(ProvisioningAcceptedCommand());
        await ClaimContinuationAsync(first, WorkflowInstallationStatus.ProvisioningAccepted);
        await first.DeactivateAsync();
        await AppendLegacyRevokedEventAsync(eventStore, first.Id, expectedVersion: 5);

        var replayed = await CreateAgentAsync(
            "delivery-alpha",
            new MutableTimeProvider(CreatedAt.AddMinutes(4)),
            eventStore);

        replayed.State.LifecycleStatus.Should().Be(WorkflowDeliveryLifecycleStatus.Revoked);
        replayed.State.Installation.Status.Should().Be(WorkflowInstallationStatus.ProvisioningAccepted);
        replayed.State.Installation.ContinuationClaim.Should().NotBeNull();
        await replayed.HandleInstallationReadyAsync(ReadyCommand());

        replayed.State.Installation.Status.Should().Be(WorkflowInstallationStatus.Failed);
        replayed.State.Installation.ErrorCode.Should().Be("delivery_revoked");
        replayed.State.Installation.ContinuationClaim.Should().BeNull();
        var events = await eventStore.GetEventsAsync(replayed.Id);
        events.Should().ContainSingle(evt =>
            evt.EventData.Is(WorkflowInstallationWithdrawnEvent.Descriptor));
        events.Should().NotContain(evt => evt.EventData.Is(WorkflowInstallationReadyEvent.Descriptor));
    }

    [Fact]
    public async Task ProvisioningOutcome_WithoutMatchingContinuationClaim_ShouldNotAdvanceInstallation()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        await agent.HandleCreateAsync(CreateCommand("delivery-alpha"));
        await agent.HandleStartInstallationAsync(StartInstallationCommand());
        await ClaimContinuationAsync(agent, WorkflowInstallationStatus.Accepted);
        var wrongOwner = ProvisioningAcceptedCommand();
        wrongOwner.ContinuationClaimantId = "worker-beta";

        var record = () => agent.HandleProvisioningAcceptedAsync(wrongOwner);

        await record.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not own*");
        agent.EventSourcing!.CurrentVersion.Should().Be(3);
        agent.State.Installation.Status.Should().Be(WorkflowInstallationStatus.Accepted);
    }

    [Fact]
    public async Task ProvisioningOutcome_ShouldFenceStaleAttemptsAndPersistTerminalFailureForActiveAttempt()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        await agent.HandleCreateAsync(CreateCommand("delivery-alpha"));
        await agent.HandleStartInstallationAsync(StartInstallationCommand());
        await ClaimContinuationAsync(agent, WorkflowInstallationStatus.Accepted);
        await agent.HandleInstallationFailedAsync(FailedCommand(1, "installation-alpha:provision:a1"));
        await agent.HandleRetryInstallationAsync(new RetryWorkflowInstallationCommand
        {
            DeliveryId = "delivery-alpha",
            InstallationId = "installation-alpha",
            IdempotencyKey = "publish-alpha",
            OperationId = "installation-alpha:provision:a2",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(CreatedAt.AddMinutes(4)),
        });
        await ClaimContinuationAsync(
            agent,
            WorkflowInstallationStatus.Accepted,
            2,
            "installation-alpha:provision:a2");
        await agent.HandleProvisioningAcceptedAsync(ProvisioningAcceptedCommand(
            attempt: 2,
            operationId: "installation-alpha:provision:a2"));
        await ClaimContinuationAsync(
            agent,
            WorkflowInstallationStatus.ProvisioningAccepted,
            2,
            "installation-alpha:provision:a2");

        await agent.HandleInstallationFailedAsync(FailedCommand(1, "installation-alpha:provision:a1"));
        await agent.HandleInstallationFailedAsync(FailedCommand(
            2,
            "installation-alpha:provision:a2",
            WorkflowInstallationStatus.ProvisioningAccepted));

        agent.EventSourcing!.CurrentVersion.Should().Be(9);
        agent.State.Installation.Attempt.Should().Be(2);
        agent.State.Installation.OperationId.Should().Be("installation-alpha:provision:a2");
        agent.State.Installation.Status.Should().Be(WorkflowInstallationStatus.Failed);
        agent.State.Installation.AcceptanceInput.Should().NotBeNull();

        var wrongOperation = () => agent.HandleInstallationFailedAsync(
            FailedCommand(
                2,
                "installation-alpha:provision:other",
                WorkflowInstallationStatus.ProvisioningAccepted));
        await wrongOperation.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*operation identity*");
        agent.EventSourcing.CurrentVersion.Should().Be(9);
    }

    [Theory]
    [InlineData(WorkflowDeliveryTriggerKind.OneShot)]
    [InlineData(WorkflowDeliveryTriggerKind.Cron)]
    public async Task ScheduledInstallationRetry_ShouldReplaceAttemptScopedProvisioningIntent(
        WorkflowDeliveryTriggerKind triggerKind)
    {
        var trigger = triggerKind == WorkflowDeliveryTriggerKind.Cron
            ? new WorkflowDeliveryTriggerIntent
            {
                Kind = triggerKind,
                Cron = "0 9 * * 1-5",
                TimeZone = "Asia/Singapore",
            }
            : new WorkflowDeliveryTriggerIntent
            {
                Kind = triggerKind,
                RunImmediately = true,
                TimeZone = "UTC",
            };
        var agent = await CreateProvisioningAcceptedAgentAsync(trigger, includeSchedule: true);
        await agent.HandleInstallationFailedAsync(
            FailedCommand(
                1,
                "installation-alpha:provision:a1",
                WorkflowInstallationStatus.ProvisioningAccepted));

        await agent.HandleRetryInstallationAsync(new RetryWorkflowInstallationCommand
        {
            DeliveryId = "delivery-alpha",
            InstallationId = "installation-alpha",
            IdempotencyKey = "publish-alpha",
            OperationId = "installation-alpha:provision:a2",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(CreatedAt.AddMinutes(4)),
        });
        await ClaimContinuationAsync(
            agent,
            WorkflowInstallationStatus.Accepted,
            2,
            "installation-alpha:provision:a2");

        var retried = agent.State.Installation;
        retried.Status.Should().Be(WorkflowInstallationStatus.Accepted);
        retried.FailureOriginStatus.Should().Be(WorkflowInstallationStatus.Unspecified);
        retried.Attempt.Should().Be(2);
        retried.OperationId.Should().Be("installation-alpha:provision:a2");
        retried.MemberId.Should().Be("m-alpha");
        retried.WorkflowId.Should().Be("wf-alpha");
        retried.PublishedServiceId.Should().Be("svc-alpha");
        retried.RevisionId.Should().Be("rev-alpha");
        retried.ScheduleId.Should().Be("schedule-alpha");
        retried.ScheduleProvisioningId.Should().BeEmpty();
        retried.ScheduleProvisioningStatus.Should().BeEmpty();
        retried.ReadinessEvidence.Should().BeNull();

        var attemptTwo = ProvisioningAcceptedCommand(
            includeSchedule: true,
            attempt: 2,
            operationId: "installation-alpha:provision:a2");
        attemptTwo.ScheduleProvisioningId = "schedule-provisioning-attempt-2";
        attemptTwo.ScheduleProvisioningStatus = "pending_binding";
        await agent.HandleProvisioningAcceptedAsync(attemptTwo);

        agent.State.Installation.Status.Should().Be(WorkflowInstallationStatus.ProvisioningAccepted);
        agent.State.Installation.ScheduleProvisioningId.Should().Be("schedule-provisioning-attempt-2");
        agent.State.Installation.ScheduleProvisioningStatus.Should().Be("pending_binding");
        agent.State.Installation.Attempt.Should().Be(2);
        agent.State.Installation.OperationId.Should().Be("installation-alpha:provision:a2");
    }

    [Fact]
    public async Task ProvisioningAccepted_ShouldOverrideSameClaimProvisioningFailure()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        await agent.HandleCreateAsync(CreateCommand("delivery-alpha"));
        await agent.HandleStartInstallationAsync(StartInstallationCommand());
        await ClaimContinuationAsync(agent, WorkflowInstallationStatus.Accepted);
        await agent.HandleInstallationFailedAsync(FailedCommand(1, "installation-alpha:provision:a1"));
        agent.State.Installation.Status.Should().Be(WorkflowInstallationStatus.Failed);

        await agent.HandleProvisioningAcceptedAsync(ProvisioningAcceptedCommand());

        agent.EventSourcing!.CurrentVersion.Should().Be(5);
        agent.State.Installation.Status.Should().Be(WorkflowInstallationStatus.ProvisioningAccepted);
        agent.State.Installation.FailureOriginStatus.Should().Be(WorkflowInstallationStatus.Unspecified);
        agent.State.Installation.ErrorCode.Should().BeEmpty();
        agent.State.Installation.PublishedServiceId.Should().Be("svc-alpha");
    }

    [Fact]
    public async Task InstallationFailed_ShouldFenceFailureOriginStageAcrossProvisioningRace()
    {
        var agent = await CreateProvisioningAcceptedAgentAsync();
        var versionAfterAccepted = agent.EventSourcing!.CurrentVersion;

        await agent.HandleInstallationFailedAsync(FailedCommand(
            1,
            "installation-alpha:provision:a1",
            WorkflowInstallationStatus.Accepted));

        agent.EventSourcing.CurrentVersion.Should().Be(versionAfterAccepted);
        agent.State.Installation.Status.Should().Be(WorkflowInstallationStatus.ProvisioningAccepted);
        agent.State.Installation.PublishedServiceId.Should().Be("svc-alpha");

        await agent.HandleInstallationFailedAsync(FailedCommand(
            1,
            "installation-alpha:provision:a1",
            WorkflowInstallationStatus.ProvisioningAccepted,
            "READINESS_FAILED"));

        agent.EventSourcing.CurrentVersion.Should().Be(versionAfterAccepted + 1);
        agent.State.Installation.Status.Should().Be(WorkflowInstallationStatus.Failed);
        agent.State.Installation.FailureOriginStatus.Should()
            .Be(WorkflowInstallationStatus.ProvisioningAccepted);
        agent.State.Installation.ErrorCode.Should().Be("READINESS_FAILED");

        await agent.HandleProvisioningAcceptedAsync(ProvisioningAcceptedCommand());

        agent.EventSourcing.CurrentVersion.Should().Be(versionAfterAccepted + 1);
        agent.State.Installation.Status.Should().Be(WorkflowInstallationStatus.Failed);
    }

    [Fact]
    public async Task InstallationFailed_ShouldRejectReadinessFailureBeforeProvisioningIsAccepted()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        await agent.HandleCreateAsync(CreateCommand("delivery-alpha"));
        await agent.HandleStartInstallationAsync(StartInstallationCommand());

        var fail = () => agent.HandleInstallationFailedAsync(FailedCommand(
            1,
            "installation-alpha:provision:a1",
            WorkflowInstallationStatus.ProvisioningAccepted,
            "READINESS_FAILED"));

        await fail.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*expected status*");
        agent.EventSourcing!.CurrentVersion.Should().Be(2);
        agent.State.Installation.Status.Should().Be(WorkflowInstallationStatus.Accepted);
    }

    [Fact]
    public async Task InstallationFailed_AfterReady_ShouldNotRegressReadyState()
    {
        var agent = await CreateProvisioningAcceptedAgentAsync();
        await agent.HandleInstallationReadyAsync(ReadyCommand());

        await agent.HandleInstallationFailedAsync(
            FailedCommand(
                1,
                "installation-alpha:provision:a1",
                WorkflowInstallationStatus.ProvisioningAccepted));

        agent.EventSourcing!.CurrentVersion.Should().Be(6);
        agent.State.Installation.Status.Should().Be(WorkflowInstallationStatus.Ready);
        agent.State.Installation.ReadinessEvidence.Should().NotBeNull();
    }

    [Fact]
    public async Task ProvisioningAccepted_ShouldMonotonicallyEnrichMissingOperationIdentities()
    {
        var agent = await CreateProvisioningAcceptedAgentAsync(includeBindingRun: false);
        var original = ProvisioningAcceptedCommand(
            includeBindingRun: false,
            claimStatus: WorkflowInstallationStatus.ProvisioningAccepted);
        var enriched = original.Clone();
        enriched.BindingRunId = "binding-run-alpha";
        enriched.ScheduleId = "schedule-alpha";
        enriched.ScheduleProvisioningId = "schedule-provisioning-alpha";
        enriched.ScheduleProvisioningStatus = "accepted";

        await agent.HandleProvisioningAcceptedAsync(enriched);
        await agent.HandleProvisioningAcceptedAsync(original);
        await agent.HandleProvisioningAcceptedAsync(enriched.Clone());

        agent.EventSourcing!.CurrentVersion.Should().Be(6);
        agent.State.Installation.BindingRunId.Should().Be("binding-run-alpha");
        agent.State.Installation.ScheduleId.Should().Be("schedule-alpha");
        agent.State.Installation.ScheduleProvisioningId.Should().Be("schedule-provisioning-alpha");

        var conflict = enriched.Clone();
        conflict.ScheduleId = "schedule-beta";
        var conflictingOutcome = () => agent.HandleProvisioningAcceptedAsync(conflict);
        await conflictingOutcome.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*different provisioning identities*");
        agent.EventSourcing.CurrentVersion.Should().Be(6);
    }

    [Fact]
    public async Task AccessFromAnotherScope_ShouldRejectWithoutCommittingOrLeakingStateChanges()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        await agent.HandleCreateAsync(CreateCommand("delivery-alpha"));

        var access = () => agent.HandleAccessAsync(new RecordWorkflowDeliveryAccessCommand
        {
            DeliveryId = "delivery-alpha",
            TargetScopeId = "scope-beta",
            AccessedAtUtc = Timestamp.FromDateTimeOffset(CreatedAt.AddMinutes(1)),
        });

        await access.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*target scope*");
        agent.EventSourcing!.CurrentVersion.Should().Be(1);
        agent.State.AccessedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task InstallationReady_ShouldPersistTypedEvidenceAndConvergeOnExactReplay()
    {
        var triggerIntent = OneShotTriggerIntent();
        var agent = await CreateProvisioningAcceptedAgentAsync(triggerIntent, includeSchedule: true);
        var command = ReadyCommand(triggerIntent, includeSchedule: true);

        await agent.HandleInstallationReadyAsync(command);
        await agent.HandleInstallationReadyAsync(command.Clone());

        agent.EventSourcing!.CurrentVersion.Should().Be(6);
        agent.State.Installation.Status.Should().Be(WorkflowInstallationStatus.Ready);
        agent.State.Installation.Stage.Should().Be("ready");
        agent.State.Installation.ReadinessEvidence.Should().Be(command.Evidence);
        agent.State.Installation.ReadinessEvidence.AcceptanceRun.AcceptanceRunId
            .Should().Be("acceptance-run-alpha");
        agent.State.Installation.ReadinessEvidence.Artifacts.Should().ContainSingle()
            .Which.Kind.Should().Be(WorkflowInstallationArtifactKind.RunOutput);

        var conflicting = command.Clone();
        conflicting.Evidence.Artifacts[0].ContentDigest = "sha256-artifact-beta";

        var conflict = () => agent.HandleInstallationReadyAsync(conflicting);

        await conflict.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*different readiness evidence*");
        agent.EventSourcing.CurrentVersion.Should().Be(6);
        agent.State.Installation.ReadinessEvidence.Artifacts[0].ContentDigest
            .Should().Be("sha256-artifact-alpha");
    }

    [Fact]
    public async Task InstallationReady_ShouldFenceStaleAttemptAndRejectMismatchedActiveOperation()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        await agent.HandleCreateAsync(CreateCommand("delivery-alpha"));
        await agent.HandleStartInstallationAsync(StartInstallationCommand());
        await ClaimContinuationAsync(agent, WorkflowInstallationStatus.Accepted);
        await agent.HandleInstallationFailedAsync(FailedCommand(1, "installation-alpha:provision:a1"));
        await agent.HandleRetryInstallationAsync(new RetryWorkflowInstallationCommand
        {
            DeliveryId = "delivery-alpha",
            InstallationId = "installation-alpha",
            IdempotencyKey = "publish-alpha",
            OperationId = "installation-alpha:provision:a2",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(CreatedAt.AddMinutes(4)),
        });
        await ClaimContinuationAsync(
            agent,
            WorkflowInstallationStatus.Accepted,
            2,
            "installation-alpha:provision:a2");
        await agent.HandleProvisioningAcceptedAsync(ProvisioningAcceptedCommand(
            attempt: 2,
            operationId: "installation-alpha:provision:a2"));
        await ClaimContinuationAsync(
            agent,
            WorkflowInstallationStatus.ProvisioningAccepted,
            2,
            "installation-alpha:provision:a2");

        await agent.HandleInstallationReadyAsync(ReadyCommand(
            attempt: 1,
            operationId: "installation-alpha:provision:a1"));

        agent.EventSourcing!.CurrentVersion.Should().Be(8);
        agent.State.Installation.Status.Should().Be(WorkflowInstallationStatus.ProvisioningAccepted);

        var mismatchedOperation = () => agent.HandleInstallationReadyAsync(ReadyCommand(
            attempt: 2,
            operationId: "installation-alpha:provision:other"));
        await mismatchedOperation.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*operation identity*");
        agent.EventSourcing.CurrentVersion.Should().Be(8);

        await agent.HandleInstallationReadyAsync(ReadyCommand(
            attempt: 2,
            operationId: "installation-alpha:provision:a2"));
        agent.EventSourcing.CurrentVersion.Should().Be(9);
        agent.State.Installation.Status.Should().Be(WorkflowInstallationStatus.Ready);
    }

    [Fact]
    public async Task InstallationReady_ShouldFailClosedWhenRequiredEvidenceIsMissingOrMismatched()
    {
        var triggerIntent = OneShotTriggerIntent();
        var agent = await CreateProvisioningAcceptedAgentAsync(triggerIntent, includeSchedule: true);
        var invalidCommands = new List<RecordWorkflowInstallationReadyCommand>();

        var publishedServiceNotCommitted = ReadyCommand(triggerIntent, includeSchedule: true);
        publishedServiceNotCommitted.Evidence.PublishedService.Committed = false;
        invalidCommands.Add(publishedServiceNotCommitted);

        var publishedServiceNotRunnable = ReadyCommand(triggerIntent, includeSchedule: true);
        publishedServiceNotRunnable.Evidence.PublishedService.Runnable = false;
        invalidCommands.Add(publishedServiceNotRunnable);

        var revisionMismatch = ReadyCommand(triggerIntent, includeSchedule: true);
        revisionMismatch.Evidence.BoundRevision.RevisionId = "rev-beta";
        invalidCommands.Add(revisionMismatch);

        var missingTriggerEvidence = ReadyCommand(triggerIntent, includeSchedule: true);
        missingTriggerEvidence.Evidence.Trigger.ClearReadiness();
        invalidCommands.Add(missingTriggerEvidence);

        var nonTerminalAcceptance = ReadyCommand(triggerIntent, includeSchedule: true);
        nonTerminalAcceptance.Evidence.AcceptanceRun.Status = WorkflowAcceptanceRunStatus.TerminalFailure;
        invalidCommands.Add(nonTerminalAcceptance);

        var missingArtifacts = ReadyCommand(triggerIntent, includeSchedule: true);
        missingArtifacts.Evidence.Artifacts.Clear();
        invalidCommands.Add(missingArtifacts);

        var unverifiedArtifact = ReadyCommand(triggerIntent, includeSchedule: true);
        unverifiedArtifact.Evidence.Artifacts[0].VerificationStatus =
            WorkflowInstallationArtifactVerificationStatus.Rejected;
        invalidCommands.Add(unverifiedArtifact);

        foreach (var invalid in invalidCommands)
        {
            var ready = () => agent.HandleInstallationReadyAsync(invalid);

            await ready.Should().ThrowAsync<InvalidOperationException>();
            agent.EventSourcing!.CurrentVersion.Should().Be(5);
            agent.State.Installation.Status.Should().Be(WorkflowInstallationStatus.ProvisioningAccepted);
            agent.State.Installation.ReadinessEvidence.Should().BeNull();
        }
    }

    [Fact]
    public async Task InstallationReady_ShouldRequireProvisioningAcceptedState()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        await agent.HandleCreateAsync(CreateCommand("delivery-alpha"));
        await agent.HandleStartInstallationAsync(StartInstallationCommand());

        var ready = () => agent.HandleInstallationReadyAsync(ReadyCommand());

        await ready.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*provisioning-accepted*");
        agent.EventSourcing!.CurrentVersion.Should().Be(2);
        agent.State.Installation.Status.Should().Be(WorkflowInstallationStatus.Accepted);
    }

    [Fact]
    public async Task InstallationReady_ShouldRequireScheduleEvidenceMatchingCronIntent()
    {
        var triggerIntent = new WorkflowDeliveryTriggerIntent
        {
            Kind = WorkflowDeliveryTriggerKind.Cron,
            Cron = "0 9 * * 1-5",
            TimeZone = "Asia/Singapore",
        };
        var agent = await CreateProvisioningAcceptedAgentAsync(triggerIntent, includeSchedule: true);
        agent.State.Installation.AuthenticatedOwner.Owner.OwnerSubject.Should().Be("scope-alpha");
        agent.State.Installation.AuthenticatedOwner.VerifiedBindingId.Should().Be("binding-alpha");

        var mismatchedIntent = ReadyCommand(triggerIntent, includeSchedule: true);
        mismatchedIntent.Evidence.Trigger.Intent.Cron = "0 10 * * 1-5";
        var intentMismatch = () => agent.HandleInstallationReadyAsync(mismatchedIntent);
        await intentMismatch.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*trigger intent*");

        var mismatchedSchedule = ReadyCommand(triggerIntent, includeSchedule: true);
        mismatchedSchedule.Evidence.Trigger.Schedule.ScheduleId = "schedule-beta";
        var scheduleMismatch = () => agent.HandleInstallationReadyAsync(mismatchedSchedule);
        await scheduleMismatch.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*installation schedule*");

        await agent.HandleInstallationReadyAsync(ReadyCommand(triggerIntent, includeSchedule: true));

        agent.EventSourcing!.CurrentVersion.Should().Be(6);
        agent.State.Installation.Status.Should().Be(WorkflowInstallationStatus.Ready);
        agent.State.Installation.ReadinessEvidence.Trigger.ReadinessCase
            .Should().Be(WorkflowTriggerReadinessEvidence.ReadinessOneofCase.Schedule);
    }

    [Fact]
    public async Task InstallationReady_ShouldUseCommittedRevisionWhenBindingRunIdentityDoesNotExist()
    {
        var agent = await CreateProvisioningAcceptedAgentAsync(includeBindingRun: false);
        var ready = ReadyCommand(includeBindingRun: false);

        await agent.HandleInstallationReadyAsync(ready);

        agent.State.Installation.Status.Should().Be(WorkflowInstallationStatus.Ready);
        agent.State.Installation.ReadinessEvidence.BoundRevision.RevisionId.Should().Be("rev-alpha");
        agent.State.Installation.ReadinessEvidence.BoundRevision.BindingRunId.Should().BeEmpty();

        var otherAgent = await CreateProvisioningAcceptedAgentAsync(includeBindingRun: false);
        var unknownRun = ReadyCommand(includeBindingRun: false);
        unknownRun.Evidence.BoundRevision.BindingRunId = "binding-run-unknown";
        var readyWithUnknownRun = () => otherAgent.HandleInstallationReadyAsync(unknownRun);
        await readyWithUnknownRun.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unknown binding run*");
    }

    // A real workflow document is multi-line. A single-line fixture hides identifier-shaped
    // validation of YAML fields, because Trim() removes the only line break it has.
    private const string MultiLineSourceYaml =
        "name: workflow-alpha\ndescription: |\n  first line\n  second line\nsteps:\n  - id: config\n    parameters:\n      value: '{\"a\":1}'\n";

    [Fact]
    public async Task Create_WhenPackageYamlIsMultiLine_ShouldCommitItVerbatim()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        var command = CommandWithSourceYaml("delivery-alpha", MultiLineSourceYaml);

        await agent.HandleCreateAsync(command);

        agent.State.Package.SourceYaml.Should().Be(MultiLineSourceYaml);
        agent.State.Package.SourceYaml.Should().Contain("\n  second line");
    }

    [Fact]
    public async Task StartInstallation_WhenResolvedYamlIsMultiLine_ShouldAcceptIt()
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        await agent.HandleCreateAsync(CreateCommand("delivery-alpha"));
        var command = StartInstallationCommand();
        command.ResolvedYaml = MultiLineSourceYaml;

        await agent.HandleStartInstallationAsync(command);

        agent.State.Installation.ResolvedYaml.Should().Be(MultiLineSourceYaml);
    }

    [Theory]
    [InlineData("name: a\u0000b\nsteps: []\n")]
    [InlineData("name: a\u0007b\nsteps: []\n")]
    public async Task Create_WhenPackageYamlCarriesNonTextControlCharacters_ShouldReject(string yaml)
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        var command = CommandWithSourceYaml("delivery-alpha", yaml);

        var action = () => agent.HandleCreateAsync(command);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*source_yaml*control characters*");
        agent.EventSourcing!.CurrentVersion.Should().Be(0);
    }

    private static CreateWorkflowDeliveryCommand CommandWithSourceYaml(string deliveryId, string sourceYaml)
    {
        var command = CreateCommand(deliveryId);
        command.Package.SourceYaml = sourceYaml;
        command.Package.PackageHash = WorkflowDeliveryConventions.ComputePackageHash(command.Package);
        command.Package.Version = command.Package.PackageHash[..16];
        command.Package.PackageVersionId = WorkflowDeliveryConventions.BuildPackageVersionId(
            command.Package.WorkflowName,
            command.Package.PackageHash);
        return command;
    }

    private static CreateWorkflowDeliveryCommand CreateCommand(string deliveryId)
    {
        var package = new WorkflowPackageVersionSnapshot
        {
            PackageId = "package-alpha",
            WorkflowName = "workflow-alpha",
            DisplayName = "Workflow Alpha",
            SourceYaml = MultiLineSourceYaml,
            SourceHash = "sha256-alpha",
            AcceptancePolicy = new WorkflowDeliveryAcceptancePolicy
            {
                Mode = WorkflowDeliveryAcceptanceMode.AutomaticPreview,
                Input = new WorkflowDeliveryAcceptanceInputRecipe
                {
                    Literals = new Struct(),
                },
            },
            CreatedBy = "admin-alpha",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(CreatedAt),
        };
        package.PackageHash = WorkflowDeliveryConventions.ComputePackageHash(package);
        package.Version = package.PackageHash[..16];
        package.PackageVersionId = WorkflowDeliveryConventions.BuildPackageVersionId(
            package.WorkflowName,
            package.PackageHash);
        return new CreateWorkflowDeliveryCommand
        {
            DeliveryId = deliveryId,
            Package = package,
            TargetScopeId = "scope-alpha",
            ExpiresAtUtc = Timestamp.FromDateTimeOffset(CreatedAt.AddHours(4)),
            CreatedBy = "admin-alpha",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(CreatedAt),
        };
    }

    private static void ResealPackage(WorkflowPackageVersionSnapshot package)
    {
        package.PackageHash = WorkflowDeliveryConventions.ComputePackageHash(package);
        package.Version = package.PackageHash[..16];
        package.PackageVersionId = WorkflowDeliveryConventions.BuildPackageVersionId(
            package.WorkflowName,
            package.PackageHash);
    }

    private static CreateWorkflowDeliveryCommand CreateCommandWithConnectionSlot()
    {
        var command = CreateCommand("delivery-alpha");
        command.Package.ConnectionSlots.Add(new WorkflowDeliveryConnectionSlotDefinition
        {
            Key = "lark",
            Label = "Lark",
            ServiceSlug = "api-lark",
            Required = true,
        });
        command.Package.PackageHash = WorkflowDeliveryConventions.ComputePackageHash(command.Package);
        command.Package.Version = command.Package.PackageHash[..16];
        command.Package.PackageVersionId = WorkflowDeliveryConventions.BuildPackageVersionId(
            command.Package.WorkflowName,
            command.Package.PackageHash);
        return command;
    }

    private static BeginWorkflowDeliveryConnectionCommand BeginConnectionCommand(string linkId) =>
        new()
        {
            DeliveryId = "delivery-alpha",
            TargetScopeId = "scope-alpha",
            SlotKey = "lark",
            ServiceSlug = "api-lark",
            LinkId = linkId,
            RequestedAtUtc = Timestamp.FromDateTimeOffset(CreatedAt.AddMinutes(1)),
        };

    private static UpdateWorkflowDeliveryConnectionCommand UpdateConnectionCommand(
        string linkId,
        WorkflowDeliveryConnectionStatus status,
        string userServiceId) =>
        new()
        {
            DeliveryId = "delivery-alpha",
            TargetScopeId = "scope-alpha",
            SlotKey = "lark",
            LinkId = linkId,
            Status = status,
            UserServiceId = userServiceId,
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(CreatedAt.AddMinutes(1)),
        };

    private static AttachWorkflowDeliveryConnectionCommand AttachConnectionCommand(
        string userServiceId,
        long expectedStateVersion = 1) =>
        new()
        {
            DeliveryId = "delivery-alpha",
            TargetScopeId = "scope-alpha",
            SlotKey = "lark",
            ServiceSlug = "api-lark",
            UserServiceId = userServiceId,
            AttachedAtUtc = Timestamp.FromDateTimeOffset(CreatedAt.AddMinutes(1)),
            ExpectedStateVersion = expectedStateVersion,
        };

    private static StartWorkflowInstallationCommand StartInstallationCommand(
        WorkflowDeliveryTriggerIntent? triggerIntent = null) =>
        new()
        {
            DeliveryId = "delivery-alpha",
            InstallationId = "installation-alpha",
            IdempotencyKey = "publish-alpha",
            ScopeId = "scope-alpha",
            TeamId = "team-alpha",
            TriggerIntent = triggerIntent?.Clone() ?? new WorkflowDeliveryTriggerIntent
            {
                Kind = WorkflowDeliveryTriggerKind.None,
            },
            SourceHash = "sha256-alpha",
            ResolvedHash = "resolved-alpha",
            ResolvedYaml = "name: workflow-alpha\n",
            OperationId = "installation-alpha:provision:a1",
            CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan(),
            AuthenticatedOwner = triggerIntent?.Kind is WorkflowDeliveryTriggerKind.OneShot or
                WorkflowDeliveryTriggerKind.Cron
                ? AuthorizationOwnerContext()
                : null,
            RequestedAtUtc = Timestamp.FromDateTimeOffset(CreatedAt.AddMinutes(2)),
        };

    private static WorkflowDeliveryAuthorizationOwnerContext AuthorizationOwnerContext() =>
        new()
        {
            Owner = new AuthorizationOwnerIdentity
            {
                Authority = "nyxid",
                OwnerKind = AuthorizationOwnerKind.Personal,
                OwnerSubject = "scope-alpha",
            },
            SubjectPlatform = "nyxid",
            SubjectTenant = "tenant-alpha",
            SubjectExternalUserId = "user-alpha",
            VerifiedBindingId = "binding-alpha",
        };

    private static WorkflowDeliveryTriggerIntent OneShotTriggerIntent() =>
        new()
        {
            Kind = WorkflowDeliveryTriggerKind.OneShot,
            RunImmediately = true,
            TimeZone = "UTC",
        };

    private static RecordWorkflowProvisioningAcceptedCommand ProvisioningAcceptedCommand(
        bool includeSchedule = false,
        bool includeBindingRun = true,
        int attempt = 1,
        string operationId = "installation-alpha:provision:a1",
        WorkflowInstallationStatus claimStatus = WorkflowInstallationStatus.Accepted) =>
        new()
        {
            DeliveryId = "delivery-alpha",
            InstallationId = "installation-alpha",
            MemberId = "m-alpha",
            WorkflowId = "wf-alpha",
            PublishedServiceId = "svc-alpha",
            RevisionId = "rev-alpha",
            BindingRunId = includeBindingRun ? "binding-run-alpha" : string.Empty,
            ScheduleId = includeSchedule ? "schedule-alpha" : string.Empty,
            ScheduleProvisioningId = includeSchedule ? "schedule-provisioning-alpha" : string.Empty,
            ScheduleProvisioningStatus = includeSchedule ? "accepted" : string.Empty,
            Attempt = attempt,
            OperationId = operationId,
            AcceptedAtUtc = Timestamp.FromDateTimeOffset(OutcomeAt(attempt, 3)),
            ContinuationClaimId = ClaimId(claimStatus, attempt),
            ContinuationClaimantId = "worker-alpha",
        };

    private static RecordWorkflowInstallationFailedCommand FailedCommand(
        int attempt,
        string operationId,
        WorkflowInstallationStatus expectedStatus = WorkflowInstallationStatus.Accepted,
        string errorCode = "PROVISIONING_FAILED") =>
        new()
        {
            DeliveryId = "delivery-alpha",
            InstallationId = "installation-alpha",
            ErrorCode = errorCode,
            ErrorMessage = "provisioning failed",
            ExpectedStatus = expectedStatus,
            Attempt = attempt,
            OperationId = operationId,
            FailedAtUtc = Timestamp.FromDateTimeOffset(OutcomeAt(
                attempt,
                expectedStatus == WorkflowInstallationStatus.Accepted ? 3 : 4)),
            ContinuationClaimId = ClaimId(expectedStatus, attempt),
            ContinuationClaimantId = "worker-alpha",
        };

    private static RecordWorkflowInstallationReadyCommand ReadyCommand(
        WorkflowDeliveryTriggerIntent? triggerIntent = null,
        bool includeSchedule = false,
        bool includeBindingRun = true,
        int attempt = 1,
        string operationId = "installation-alpha:provision:a1")
    {
        var intent = triggerIntent?.Clone() ?? new WorkflowDeliveryTriggerIntent
        {
            Kind = WorkflowDeliveryTriggerKind.None,
        };
        var trigger = new WorkflowTriggerReadinessEvidence { Intent = intent };
        if (includeSchedule)
        {
            trigger.Schedule = new WorkflowScheduleReadinessEvidence
            {
                ScheduleId = "schedule-alpha",
                ScheduleProvisioningId = "schedule-provisioning-alpha",
                Status = WorkflowScheduleReadinessStatus.Ready,
                CommittedStateVersion = 7,
            };
        }
        else
        {
            trigger.NoTrigger = new WorkflowNoTriggerReadinessEvidence { Ready = true };
        }

        var evidence = new WorkflowInstallationReadinessEvidence
        {
            PublishedService = new WorkflowPublishedServiceReadinessEvidence
            {
                PublishedServiceId = "svc-alpha",
                Committed = true,
                Runnable = true,
                CommittedStateVersion = 11,
            },
            BoundRevision = new WorkflowBoundRevisionReadinessEvidence
            {
                RevisionId = "rev-alpha",
                BindingRunId = includeBindingRun ? "binding-run-alpha" : string.Empty,
                Bound = true,
                CommittedStateVersion = 9,
            },
            Trigger = trigger,
        };
        if (intent.Kind != WorkflowDeliveryTriggerKind.None)
        {
            evidence.AcceptanceRun = new WorkflowAcceptanceRunReadinessEvidence
            {
                AcceptanceRunId = "acceptance-run-alpha",
                Status = WorkflowAcceptanceRunStatus.TerminalSuccess,
                CommittedStateVersion = 13,
            };
            evidence.Artifacts.Add(new WorkflowInstallationArtifactEvidence
            {
                Kind = WorkflowInstallationArtifactKind.RunOutput,
                ArtifactId = "artifact-alpha",
                VerificationStatus = WorkflowInstallationArtifactVerificationStatus.Verified,
                VerificationReference = "run-output:artifact-alpha",
                ContentDigest = "sha256-artifact-alpha",
            });
        }
        return new RecordWorkflowInstallationReadyCommand
        {
            DeliveryId = "delivery-alpha",
            InstallationId = "installation-alpha",
            Evidence = evidence,
            Attempt = attempt,
            OperationId = operationId,
            ReadyAtUtc = Timestamp.FromDateTimeOffset(OutcomeAt(attempt, 4)),
            ContinuationClaimId = ClaimId(WorkflowInstallationStatus.ProvisioningAccepted, attempt),
            ContinuationClaimantId = "worker-alpha",
        };
    }

    private static ClaimWorkflowInstallationContinuationCommand ContinuationClaimCommand(
        WorkflowInstallationStatus status,
        int attempt = 1,
        string operationId = "installation-alpha:provision:a1",
        string claimantId = "worker-alpha",
        TimeSpan? requestedDuration = null) =>
        new()
        {
            DeliveryId = "delivery-alpha",
            InstallationId = "installation-alpha",
            ExpectedStatus = status,
            Attempt = attempt,
            OperationId = operationId,
            ClaimId = ClaimId(status, attempt),
            ClaimantId = claimantId,
            RequestedDuration = Duration.FromTimeSpan(
                requestedDuration ?? TimeSpan.FromMinutes(5)),
        };

    private static string ClaimId(WorkflowInstallationStatus status, int attempt) =>
        $"claim-{(status == WorkflowInstallationStatus.Accepted ? "accepted" : "readiness")}-a{attempt}";

    private static DateTimeOffset OutcomeAt(int attempt, int firstAttemptMinute) =>
        CreatedAt.AddMinutes(firstAttemptMinute + ((attempt - 1) * 3));

    private static Task ClaimContinuationAsync(
        WorkflowDeliveryGAgent agent,
        WorkflowInstallationStatus status,
        int attempt = 1,
        string operationId = "installation-alpha:provision:a1") =>
        agent.HandleClaimInstallationContinuationAsync(
            ContinuationClaimCommand(status, attempt, operationId));

    private static async Task<WorkflowDeliveryGAgent> CreateProvisioningAcceptedAgentAsync(
        WorkflowDeliveryTriggerIntent? triggerIntent = null,
        bool includeSchedule = false,
        bool includeBindingRun = true)
    {
        var agent = await CreateAgentAsync("delivery-alpha");
        await agent.HandleCreateAsync(CreateCommand("delivery-alpha"));
        await agent.HandleStartInstallationAsync(StartInstallationCommand(triggerIntent));
        await ClaimContinuationAsync(agent, WorkflowInstallationStatus.Accepted);
        await agent.HandleProvisioningAcceptedAsync(ProvisioningAcceptedCommand(includeSchedule, includeBindingRun));
        await ClaimContinuationAsync(agent, WorkflowInstallationStatus.ProvisioningAccepted);
        return agent;
    }

    private static async Task<WorkflowDeliveryGAgent> CreateAgentAsync(
        string deliveryId,
        TimeProvider? timeProvider = null,
        InMemoryEventStore? eventStore = null)
    {
        var agent = new WorkflowDeliveryGAgent(
            timeProvider ?? new MutableTimeProvider(CreatedAt.AddMinutes(2)))
        {
            EventSourcingBehaviorFactory =
                new DefaultEventSourcingBehaviorFactory<WorkflowDeliveryState>(
                    eventStore ?? new InMemoryEventStore()),
            Services = new ServiceCollection()
                .AddSingleton<IEnumerable<IGAgentExecutionHook>>([])
                .BuildServiceProvider(),
        };
        SetIdMethod.Invoke(agent, [WorkflowDeliveryConventions.BuildActorId(deliveryId)]);
        await agent.ActivateAsync();
        return agent;
    }

    private static async Task AppendLegacyRevokedEventAsync(
        InMemoryEventStore eventStore,
        string actorId,
        long expectedVersion)
    {
        var revoked = new WorkflowDeliveryRevokedEvent
        {
            RevokedBy = "legacy-admin",
            RevokedAtUtc = Timestamp.FromDateTimeOffset(CreatedAt.AddMinutes(3)),
        };
        await eventStore.AppendAsync(
            actorId,
            [new StateEvent
            {
                AgentId = actorId,
                EventId = "legacy-revoked",
                EventType = WorkflowDeliveryRevokedEvent.Descriptor.FullName,
                EventData = Any.Pack(revoked),
                Timestamp = revoked.RevokedAtUtc.Clone(),
                Version = expectedVersion + 1,
            }],
            expectedVersion);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void SetUtcNow(DateTimeOffset value) => _utcNow = value;
    }
}
