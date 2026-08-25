using System.Text.Json;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Studio.Application.Delivery;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Options;
using ActorPackage = Aevatar.GAgents.WorkflowDelivery.WorkflowPackageVersionSnapshot;
using ApplicationAcceptanceDateProjection = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryAcceptanceDateProjection;
using ProtobufValue = Google.Protobuf.WellKnownTypes.Value;
using ProtoAcceptanceDateProjection = Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryAcceptanceDateProjection;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowDeliveryServiceTests
{
    [Fact]
    public async Task CreateAsync_ShouldCarryGenericAcceptanceRecipeIntoCommandContract()
    {
        var package = DeliveryActorPackage();
        var context = new TestContext(packageCatalog: new StubPackageCatalog(package));

        await context.Service.CreateAsync(
            "admin-alpha",
            new WorkflowDeliveryCreateRequest(
                package.WorkflowName,
                package.PackageVersionId,
                "scope-alpha",
                "create-alpha"));

        var policy = context.Commands.Created.Should().ContainSingle().Subject.Package.AcceptancePolicy;
        policy.InputDeclared.Should().BeTrue();
        policy.Input.Literals.Fields.Should().ContainKey("dry_run")
            .WhoseValue.BoolValue.Should().BeTrue();
        policy.Input.Bindings.Select(static value => value.Key)
            .Should().Equal("created_month", "owner_id");
        policy.Input.Bindings[0].Source.Should().BeEquivalentTo(
            new WorkflowDeliveryInstallationCreatedAtUtcInput(
                ApplicationAcceptanceDateProjection.UtcYearMonth,
                -2));
        policy.Input.Bindings[1].Source.Should().BeOfType<
            WorkflowDeliveryAuthenticatedOwnerExternalUserIdInput>();
    }

    [Theory]
    [InlineData("stale-digest", "write")]
    [InlineData("digest-alpha", "destructive")]
    public async Task PublishAsync_WhenConfirmationDigestOrRiskDrifts_ShouldFailClosedBeforeInstallation(
        string suppliedDigest,
        string suppliedRisk)
    {
        var context = new TestContext();
        var request = PublishRequest(suppliedDigest, suppliedRisk);

        var action = () => context.Service.PublishAsync(
            "delivery-alpha",
            "scope-alpha",
            request,
            Caller());

        var exception = (await action.Should().ThrowAsync<WorkflowDeliveryException>()).Which;
        exception.Code.Should().Be("CONFIRMATION_DIGEST_MISMATCH");
        context.Commands.Started.Should().BeEmpty();
        context.Commands.ProvisioningAccepted.Should().BeEmpty();
        context.Commands.Ready.Should().BeEmpty();
        context.Commands.Failed.Should().BeEmpty();
        context.Provisioning.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishAsync_WhenRepeatedWithSameIdempotencyKey_ShouldPersistAcceptedWithoutProvisioning()
    {
        var context = new TestContext();
        var request = PublishRequest("digest-alpha", "write");

        var first = await context.Service.PublishAsync(
            "delivery-alpha",
            "scope-alpha",
            request,
            Caller());
        var second = await context.Service.PublishAsync(
            "delivery-alpha",
            "scope-alpha",
            request,
            Caller());

        var expectedInstallationId =
            Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryConventions.BuildInstallationId(
                "delivery-alpha",
                "scope-alpha");
        first.InstallationId.Should().Be(expectedInstallationId);
        second.InstallationId.Should().Be(expectedInstallationId);
        first.Status.Should().Be("accepted");
        second.Status.Should().Be("accepted");
        first.Status.Should().NotBe("ready");
        second.Status.Should().NotBe("ready");

        context.Commands.Started.Should().ContainSingle();
        context.Commands.Started.Should().OnlyContain(item =>
            item.InstallationId == expectedInstallationId &&
            item.IdempotencyKey == "publish-alpha");
        context.Commands.Started.Should().OnlyContain(item =>
            item.OperationId == $"{expectedInstallationId}:provision:a1" &&
            item.CapabilityAdmissionPlan.AdmissionDigest.Length > 0 &&
            item.AuthenticatedOwner != null &&
            item.AuthenticatedOwner.VerifiedBindingId == "binding-alpha");
        context.Commands.ProvisioningAccepted.Should().BeEmpty();
        context.Commands.Ready.Should().BeEmpty();

        context.Provisioning.PreparationRequests.Should().ContainSingle();
        context.Provisioning.PreparationRequests.Should().OnlyContain(item =>
            item.ScheduleOperationId == $"{expectedInstallationId}:provision:a1" &&
            item.ScheduleIdempotencyKey == "publish-alpha");
        context.Provisioning.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishAsync_WhenInstallationAlreadyExistsWithDifferentRequest_ShouldConflictBeforePreparation()
    {
        var context = new TestContext();
        var request = PublishRequest("digest-alpha", "write");
        await context.Service.PublishAsync(
            "delivery-alpha",
            "scope-alpha",
            request,
            Caller());

        var action = () => context.Service.PublishAsync(
            "delivery-alpha",
            "scope-alpha",
            request with { IdempotencyKey = "publish-beta" },
            Caller());

        var exception = (await action.Should().ThrowAsync<WorkflowDeliveryException>()).Which;
        exception.Code.Should().Be("DELIVERY_CONFLICT");
        context.Commands.Started.Should().ContainSingle();
        context.Provisioning.PreparationRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task PublishAsync_WhenAnotherInstallationWinsAfterDispatch_ShouldReturnConflict()
    {
        var context = new TestContext(
            startProjection: (mutation, queries) =>
                queries.ProjectStart(mutation with { IdempotencyKey = "publish-winner" }));

        var action = () => context.Service.PublishAsync(
            "delivery-alpha",
            "scope-alpha",
            PublishRequest("digest-alpha", "write"),
            Caller());

        var exception = (await action.Should().ThrowAsync<WorkflowDeliveryException>()).Which;
        exception.Code.Should().Be("DELIVERY_CONFLICT");
        context.Commands.Started.Should().ContainSingle();
        context.Provisioning.PreparationRequests.Should().ContainSingle();
        context.Queries.Snapshot.Installation!.IdempotencyKey.Should().Be("publish-winner");
    }

    [Fact]
    public async Task PublishAsync_WhenProjectionBrieflyLags_ShouldWaitForExactInstallation()
    {
        var context = new TestContext(
            startProjection: (mutation, queries) =>
                queries.ProjectStartAfterReads(mutation, staleReads: 1));

        var result = await context.Service.PublishAsync(
            "delivery-alpha",
            "scope-alpha",
            PublishRequest("digest-alpha", "write"),
            Caller());

        result.Status.Should().Be("accepted");
        context.Queries.ForScopeReads.Should().Be(3);
        context.Queries.Snapshot.Installation!.IdempotencyKey.Should().Be("publish-alpha");
    }

    [Fact]
    public async Task ListCustomerAsync_ShouldExposeAvailableTriggerIntentsInPreferredOrder()
    {
        var context = new TestContext();

        var result = await context.Service.ListCustomerAsync("scope-alpha");

        var delivery = result.Items.Should().ContainSingle().Subject;
        delivery.AvailableTriggerIntents.Select(static option => option.Kind)
            .Should().Equal("one_shot", "cron", "none");
        delivery.AvailableTriggerIntents.Single(static option => option.Kind == "one_shot").Label
            .Should().Contain("verify");
        delivery.AvailableTriggerIntents.Single(static option => option.Kind == "none").Label
            .Should().Be("Publish only (automatic acceptance unavailable)");

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            delivery,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        json.RootElement.TryGetProperty("triggerIntents", out _).Should().BeFalse();
        json.RootElement.GetProperty("availableTriggerIntents")
            .EnumerateArray()
            .Select(static option => option.GetProperty("kind").GetString())
            .Should().Equal("one_shot", "cron", "none");
        json.RootElement.GetProperty("package").GetProperty("acceptancePolicy")
            .GetProperty("automaticAcceptanceSupported").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ListCustomerAsync_ShouldForwardPageSizeAndToken()
    {
        var context = new TestContext();

        await context.Service.ListCustomerAsync(
            "scope-alpha",
            new WorkflowDeliveryPageRequest(37, "cursor-alpha"));

        var query = context.Queries.ListQueries.Should().ContainSingle().Subject;
        query.TargetScopeId.Should().Be("scope-alpha");
        query.PageSize.Should().Be(37);
        query.PageToken.Should().Be("cursor-alpha");
    }

    [Fact]
    public async Task ListCustomerAsync_WhenPackageRequiresManualAcceptance_ShouldExposeManualOnlyPolicy()
    {
        var context = new TestContext(DeliverySnapshot(
            acceptanceMode: WorkflowDeliveryAcceptanceMode.Manual,
            acceptanceLimitation: "An external acceptance run is required."));

        var result = await context.Service.ListCustomerAsync("scope-alpha");

        var delivery = result.Items.Should().ContainSingle().Subject;
        delivery.AvailableTriggerIntents.Select(static option => option.Kind).Should().Equal("none");
        delivery.Package.AcceptancePolicy.AutomaticAcceptanceSupported.Should().BeFalse();
        delivery.Package.AcceptancePolicy.Limitation.Should().Be("An external acceptance run is required.");
    }

    [Fact]
    public async Task ListCustomerAsync_WhenAcceptanceInputWasNotDeclared_ShouldExposeMigrationWithoutTriggers()
    {
        var context = new TestContext(DeliverySnapshot(inputDeclared: false));

        var result = await context.Service.ListCustomerAsync("scope-alpha");

        var delivery = result.Items.Should().ContainSingle().Subject;
        delivery.AvailableTriggerIntents.Should().BeEmpty();
        delivery.Package.AcceptancePolicy.AutomaticAcceptanceSupported.Should().BeFalse();
        delivery.Package.AcceptancePolicy.InputDeclared.Should().BeFalse();
        delivery.Package.AcceptancePolicy.Limitation.Should().Contain("revoked");
        delivery.Package.AcceptancePolicy.Limitation.Should().Contain("recreated");
        delivery.Package.AcceptancePolicy.Limitation.Should().Contain("reinstall");

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            delivery,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        json.RootElement.GetProperty("availableTriggerIntents").GetArrayLength().Should().Be(0);
        json.RootElement.GetProperty("package").GetProperty("acceptancePolicy")
            .GetProperty("inputDeclared").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData("one_shot")]
    [InlineData("cron")]
    public async Task ValidateConfigurationAsync_WhenPackageRequiresManualAcceptance_ShouldRejectAutomaticTrigger(
        string triggerKind)
    {
        var context = new TestContext(DeliverySnapshot(
            acceptanceMode: WorkflowDeliveryAcceptanceMode.Manual,
            acceptanceLimitation: "An external acceptance run is required."));
        var request = new WorkflowDeliveryValidateConfigurationRequest(
            "team-alpha",
            TriggerIntent: AutomaticTrigger(triggerKind));

        var action = () => context.Service.ValidateConfigurationAsync(
            "delivery-alpha",
            "scope-alpha",
            request,
            new WorkflowCapabilityAdmissionContext("caller-alpha"));

        var exception = (await action.Should().ThrowAsync<WorkflowDeliveryException>()).Which;
        exception.Code.Should().Be("AUTOMATIC_ACCEPTANCE_UNSUPPORTED");
        exception.Message.Should().Be("An external acceptance run is required.");
        context.Commands.Started.Should().BeEmpty();
        context.Provisioning.PreparationRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("one_shot")]
    [InlineData("cron")]
    public async Task PublishAsync_WhenPackageRequiresManualAcceptance_ShouldRejectAutomaticTrigger(
        string triggerKind)
    {
        var context = new TestContext(DeliverySnapshot(
            acceptanceMode: WorkflowDeliveryAcceptanceMode.Manual,
            acceptanceLimitation: "An external acceptance run is required."));
        var request = new WorkflowDeliveryPublishRequest(
            "team-alpha",
            "publish-alpha",
            TriggerIntent: AutomaticTrigger(triggerKind));

        var action = () => context.Service.PublishAsync(
            "delivery-alpha",
            "scope-alpha",
            request,
            Caller());

        var exception = (await action.Should().ThrowAsync<WorkflowDeliveryException>()).Which;
        exception.Code.Should().Be("AUTOMATIC_ACCEPTANCE_UNSUPPORTED");
        exception.Message.Should().Be("An external acceptance run is required.");
        context.Commands.Started.Should().BeEmpty();
        context.Provisioning.PreparationRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateConnectLinkAsync_ShouldUseBrowserBearerWithoutCallback()
    {
        var connectLinks = new RecordingCreateConnectLinkPort();
        var context = new TestContext(DeliveryWithLarkSlot(), connectLinks);

        var result = await context.Service.CreateConnectLinkAsync(
            "delivery-alpha",
            "scope-alpha",
            "lark",
            "caller-bearer");

        result.Status.Should().Be("begin_accepted");
        result.ConnectLinkId.Should().Be("link-created");
        result.StatusUrl.Should().Be(
            "/api/scopes/scope-alpha/delivery-requests/delivery-alpha/connections/lark");
        connectLinks.BearerToken.Should().Be("caller-bearer");
        connectLinks.Request.Should().NotBeNull();
        connectLinks.Request!.ServiceSlug.Should().Be("api-lark");
        connectLinks.Request.RequestedBy.Should().Be("scope-alpha");
        connectLinks.Request.CallbackUrl.Should().BeNull();
        context.Commands.BegunConnections.Should().ContainSingle()
            .Which.LinkId.Should().Be("link-created");
        context.Queries.ForScopeReads.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task CreateConnectLinkAsync_WhenAnotherLinkWinsAfterDispatch_ShouldNotReturnOrphanedLink()
    {
        var connectLinks = new RecordingCreateConnectLinkPort();
        var context = new TestContext(
            DeliveryWithLarkSlot(),
            connectLinks,
            beginProjection: (mutation, queries) =>
                queries.ProjectBegin(mutation with { LinkId = "link-winner" }));

        var action = () => context.Service.CreateConnectLinkAsync(
            "delivery-alpha",
            "scope-alpha",
            "lark",
            "caller-bearer");

        var exception = (await action.Should().ThrowAsync<WorkflowDeliveryException>()).Which;
        exception.Code.Should().Be("CONNECTION_ALREADY_PENDING");
        connectLinks.Request.Should().NotBeNull();
        context.Commands.BegunConnections.Should().ContainSingle()
            .Which.LinkId.Should().Be("link-created");
        context.Queries.Snapshot.Connections.Should().ContainSingle()
            .Which.LinkId.Should().Be("link-winner");
    }

    [Fact]
    public async Task CreateConnectLinkAsync_WhenProjectionLagsAfterDispatch_ShouldReturnLinkWithoutPolling()
    {
        var connectLinks = new RecordingCreateConnectLinkPort();
        var context = new TestContext(
            DeliveryWithLarkSlot(),
            connectLinks,
            beginProjection: (mutation, queries) =>
                queries.ProjectBeginAfterReads(mutation, staleReads: 1));

        var result = await context.Service.CreateConnectLinkAsync(
            "delivery-alpha",
            "scope-alpha",
            "lark",
            "caller-bearer");

        result.Status.Should().Be("begin_accepted");
        result.ConnectLinkId.Should().Be("link-created");
        result.ConnectUrl.Should().Be("https://nyx.example/connect/redacted");
        context.Queries.ForScopeReads.Should().Be(2);
        context.Queries.Snapshot.Connections.Should().BeEmpty(
            "the read model may legitimately lag an accepted actor command");
    }

    [Fact]
    public async Task CreateConnectLinkAsync_WhenInstallationExists_ShouldRejectBeforeCallingNyxId()
    {
        var connectLinks = new RecordingCreateConnectLinkPort();
        var now = DateTimeOffset.Parse("2026-08-16T01:30:00Z");
        var context = new TestContext(
            DeliveryWithLarkSlot() with { Installation = ProvisionedInstallation(now) },
            connectLinks);

        var action = () => context.Service.CreateConnectLinkAsync(
            "delivery-alpha",
            "scope-alpha",
            "lark",
            "caller-bearer");

        var exception = (await action.Should().ThrowAsync<WorkflowDeliveryException>()).Which;
        exception.Code.Should().Be("CONNECTIONS_LOCKED");
        connectLinks.Request.Should().BeNull();
        context.Commands.BegunConnections.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateConnectLinkAsync_WhenSlotIsPending_ShouldRejectBeforeCallingNyxId()
    {
        var connectLinks = new RecordingCreateConnectLinkPort();
        var now = DateTimeOffset.Parse("2026-08-16T01:30:00Z");
        var pending = new WorkflowDeliveryConnectionSnapshot(
            "lark",
            "api-lark",
            "link-alpha",
            WorkflowDeliveryConnectionStatus.Pending,
            null,
            now);
        var context = new TestContext(
            DeliveryWithLarkSlot() with { Connections = [pending] },
            connectLinks);

        var action = () => context.Service.CreateConnectLinkAsync(
            "delivery-alpha",
            "scope-alpha",
            "lark",
            "caller-bearer");

        var exception = (await action.Should().ThrowAsync<WorkflowDeliveryException>()).Which;
        exception.Code.Should().Be("CONNECTION_ALREADY_PENDING");
        connectLinks.Request.Should().BeNull();
        context.Commands.BegunConnections.Should().BeEmpty();
    }

    [Fact]
    public async Task ListExistingConnectionsAsync_ShouldExposeOnlyExactReadyPersonalConnections()
    {
        var inventory = new RecordingUserServiceInventoryPort(
        [
            UserService("user-service-ready", label: "Ready Lark"),
            UserService("user-service-wrong-slug", catalogServiceSlug: "api-lark-2"),
            UserService(
                "user-service-spoofed-slug",
                instanceSlug: "api-lark",
                catalogServiceSlug: "custom-service"),
            UserService("user-service-inactive", isActive: false),
            UserService(
                "user-service-org",
                credentialSource: NyxIdInventoryCredentialSourceKind.Organization),
            UserService("user-service-forbidden", allowed: false),
            UserService(
                "user-service-expired",
                credentialStatus: NyxIdInventoryCredentialStatus.Expired),
            UserService("user-service-disconnected", connected: false),
            UserService(
                "user-service-offline-node",
                credentialStatus: NyxIdInventoryCredentialStatus.PendingAuthorization,
                nodeId: "node-offline",
                nodeStatus: NyxIdInventoryNodeStatus.Offline),
        ]);
        var context = new TestContext(
            DeliveryWithLarkSlot(),
            connectionInventory: inventory);

        var result = await context.Service.ListExistingConnectionsAsync(
            "delivery-alpha",
            "scope-alpha",
            "lark",
            "caller-bearer");

        result.SlotKey.Should().Be("lark");
        result.Items.Should().ContainSingle().Which.Should().Be(
            new WorkflowDeliveryExistingConnectionView(
                "user-service-ready",
                "api-lark",
                "Ready Lark"));
        inventory.BearerToken.Should().Be("caller-bearer");
        inventory.ListCalls.Should().Be(1);
    }

    [Fact]
    public async Task AttachExistingConnectionAsync_ShouldRevalidateExactInventoryItemBeforeDispatch()
    {
        var inventory = new RecordingUserServiceInventoryPort(
        [
            UserService("user-service-selected", label: "Selected Lark"),
            UserService("user-service-other"),
        ]);
        var context = new TestContext(
            DeliveryWithLarkSlot(),
            connectionInventory: inventory);

        var result = await context.Service.AttachExistingConnectionAsync(
            "delivery-alpha",
            "scope-alpha",
            "lark",
            new WorkflowDeliveryAttachConnectionRequest("user-service-selected"),
            "caller-bearer");

        result.Should().Be(new WorkflowDeliveryAttachedConnectionResponse(
            "lark",
            "completed",
            "user-service-selected"));
        inventory.BearerToken.Should().Be("caller-bearer");
        context.Commands.AttachedConnections.Should().ContainSingle().Which.Should().Match<AttachWorkflowDeliveryConnectionMutation>(
            mutation => mutation.DeliveryId == "delivery-alpha" &&
                        mutation.TargetScopeId == "scope-alpha" &&
                        mutation.SlotKey == "lark" &&
                        mutation.ServiceSlug == "api-lark" &&
                        mutation.UserServiceId == "user-service-selected");
    }

    [Fact]
    public async Task AttachExistingConnectionAsync_WhenSelectedIdHasDifferentCatalogSlug_ShouldFailClosed()
    {
        var inventory = new RecordingUserServiceInventoryPort(
        [
            UserService("user-service-selected", catalogServiceSlug: "api-lark-2"),
            UserService("user-service-other"),
        ]);
        var context = new TestContext(
            DeliveryWithLarkSlot(),
            connectionInventory: inventory);

        var action = () => context.Service.AttachExistingConnectionAsync(
            "delivery-alpha",
            "scope-alpha",
            "lark",
            new WorkflowDeliveryAttachConnectionRequest("user-service-selected"),
            "caller-bearer");

        var exception = (await action.Should().ThrowAsync<WorkflowDeliveryException>()).Which;
        exception.Code.Should().Be("EXISTING_CONNECTION_NOT_AVAILABLE");
        context.Commands.AttachedConnections.Should().BeEmpty();
    }

    [Fact]
    public async Task AttachExistingConnectionAsync_ShouldWaitForHigherCommittedStateVersion()
    {
        var inventory = new RecordingUserServiceInventoryPort(
            [UserService("user-service-selected")]);
        var context = new TestContext(
            DeliveryWithLarkSlot(),
            connectionInventory: inventory,
            attachProjection: (mutation, queries) =>
                queries.ProjectAttachAtCurrentVersionThenAdvanceAfterReads(mutation, staleReads: 1));

        var result = await context.Service.AttachExistingConnectionAsync(
            "delivery-alpha",
            "scope-alpha",
            "lark",
            new WorkflowDeliveryAttachConnectionRequest("user-service-selected"),
            "caller-bearer");

        result.Status.Should().Be("completed");
        context.Queries.ForScopeReads.Should().BeGreaterThanOrEqualTo(3);
        context.Queries.Snapshot.StateVersion.Should().Be(2);
    }

    [Fact]
    public async Task AttachExistingConnectionAsync_WhenOnlyUnrelatedStateAdvances_ShouldRejectWithoutTimingOut()
    {
        var inventory = new RecordingUserServiceInventoryPort(
            [UserService("user-service-selected")]);
        var context = new TestContext(
            DeliveryWithLarkSlot(),
            connectionInventory: inventory,
            attachProjection: (_, queries) => queries.AdvanceStateVersion());

        var action = () => context.Service.AttachExistingConnectionAsync(
            "delivery-alpha",
            "scope-alpha",
            "lark",
            new WorkflowDeliveryAttachConnectionRequest("user-service-selected"),
            "caller-bearer");

        var exception = (await action.Should().ThrowAsync<WorkflowDeliveryException>()).Which;
        exception.Code.Should().Be("CONNECTION_CHANGED");
        context.Queries.ForScopeReads.Should().Be(2);
    }

    [Fact]
    public async Task ListExistingConnectionsAsync_ShouldAcceptOnlyOnlineNodeBackedService()
    {
        var inventory = new RecordingUserServiceInventoryPort(
        [
            UserService(
                "user-service-online",
                credentialStatus: NyxIdInventoryCredentialStatus.PendingAuthorization,
                nodeId: "node-online",
                nodeStatus: NyxIdInventoryNodeStatus.Online),
            UserService(
                "user-service-offline",
                credentialStatus: NyxIdInventoryCredentialStatus.Active,
                nodeId: "node-offline",
                nodeStatus: NyxIdInventoryNodeStatus.Offline),
        ]);
        var context = new TestContext(
            DeliveryWithLarkSlot(),
            connectionInventory: inventory);

        var result = await context.Service.ListExistingConnectionsAsync(
            "delivery-alpha",
            "scope-alpha",
            "lark",
            "caller-bearer");

        result.Items.Should().ContainSingle()
            .Which.UserServiceId.Should().Be("user-service-online");
    }

    [Fact]
    public async Task AttachExistingConnectionAsync_WhenAnotherAttachmentWins_ShouldRejectObservedConflict()
    {
        var inventory = new RecordingUserServiceInventoryPort(
            [UserService("user-service-selected")]);
        var context = new TestContext(
            DeliveryWithLarkSlot(),
            connectionInventory: inventory,
            attachProjection: (mutation, queries) => queries.ProjectAttach(mutation with
            {
                UserServiceId = "user-service-winner",
            }));

        var action = () => context.Service.AttachExistingConnectionAsync(
            "delivery-alpha",
            "scope-alpha",
            "lark",
            new WorkflowDeliveryAttachConnectionRequest("user-service-selected"),
            "caller-bearer");

        var exception = (await action.Should().ThrowAsync<WorkflowDeliveryException>()).Which;
        exception.Code.Should().Be("CONNECTION_CHANGED");
        context.Commands.AttachedConnections.Should().ContainSingle()
            .Which.UserServiceId.Should().Be("user-service-selected");
        context.Queries.Snapshot.Connections.Should().ContainSingle()
            .Which.UserServiceId.Should().Be("user-service-winner");
    }

    [Fact]
    public async Task AttachExistingConnectionAsync_WhenInstallationExists_ShouldAllowOnlyExactReplay()
    {
        var now = DateTimeOffset.Parse("2026-08-16T01:30:00Z");
        var attached = new WorkflowDeliveryConnectionSnapshot(
            "lark",
            "api-lark",
            string.Empty,
            WorkflowDeliveryConnectionStatus.Completed,
            "user-service-attached",
            now);
        var installed = DeliveryWithLarkSlot() with
        {
            Connections = [attached],
            Installation = ProvisionedInstallation(now),
        };
        var replayInventory = new RecordingUserServiceInventoryPort([]);
        var replayContext = new TestContext(
            installed,
            connectionInventory: replayInventory);

        var replay = await replayContext.Service.AttachExistingConnectionAsync(
            "delivery-alpha",
            "scope-alpha",
            "lark",
            new WorkflowDeliveryAttachConnectionRequest("user-service-attached"),
            "caller-bearer");

        replay.Status.Should().Be("completed");
        replayInventory.ListCalls.Should().Be(0);
        replayContext.Commands.AttachedConnections.Should().BeEmpty();

        var changedInventory = new RecordingUserServiceInventoryPort(
            [UserService("user-service-changed")]);
        var changedContext = new TestContext(
            installed,
            connectionInventory: changedInventory);
        var action = () => changedContext.Service.AttachExistingConnectionAsync(
            "delivery-alpha",
            "scope-alpha",
            "lark",
            new WorkflowDeliveryAttachConnectionRequest("user-service-changed"),
            "caller-bearer");

        var exception = (await action.Should().ThrowAsync<WorkflowDeliveryException>()).Which;
        exception.Code.Should().Be("CONNECTIONS_LOCKED");
        changedInventory.ListCalls.Should().Be(0);
        changedContext.Commands.AttachedConnections.Should().BeEmpty();
    }

    [Fact]
    public async Task GetConnectStatusAsync_ShouldReadProjectionWithoutNyxIdOrCommandSideEffects()
    {
        var updatedAt = DateTimeOffset.Parse("2026-08-16T01:30:00Z");
        var connection = new WorkflowDeliveryConnectionSnapshot(
            "lark",
            "api-lark",
            "link-alpha",
            WorkflowDeliveryConnectionStatus.Pending,
            null,
            updatedAt);
        var context = new TestContext(DeliverySnapshot() with { Connections = [connection] });

        var result = await context.Service.GetConnectStatusAsync(
            "delivery-alpha",
            "scope-alpha",
            "lark");

        result.Should().Be(new WorkflowDeliveryConnectStatusResponse(
            "lark",
            "pending",
            "link-alpha",
            null,
            updatedAt));
        context.Commands.UpdatedConnections.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshConnectStatusAsync_ShouldDispatchUpdateWithoutClaimingProjectedCompletion()
    {
        var updatedAt = DateTimeOffset.Parse("2026-08-16T01:30:00Z");
        var connection = new WorkflowDeliveryConnectionSnapshot(
            "lark",
            "api-lark",
            "link-alpha",
            WorkflowDeliveryConnectionStatus.Pending,
            null,
            updatedAt);
        var connectLinks = new RecordingConnectLinkPort(new NyxIdConnectLinkSnapshot(
            "link-alpha",
            NyxIdConnectLinkStatus.Completed,
            "api-lark",
            DateTimeOffset.Parse("2026-08-16T03:00:00Z"),
            DateTimeOffset.Parse("2026-08-16T01:45:00Z"),
            "user-service-alpha"));
        var context = new TestContext(
            DeliverySnapshot() with { Connections = [connection] },
            connectLinks);

        var accepted = await context.Service.RefreshConnectStatusAsync(
            "delivery-alpha",
            "scope-alpha",
            "lark",
            "caller-bearer");
        var projected = await context.Service.GetConnectStatusAsync(
            "delivery-alpha",
            "scope-alpha",
            "lark");

        accepted.Status.Should().Be("refresh_accepted");
        accepted.StatusUrl.Should().Be(
            "/api/scopes/scope-alpha/delivery-requests/delivery-alpha/connections/lark");
        connectLinks.GetCalls.Should().Be(1);
        context.Commands.UpdatedConnections.Should().ContainSingle()
            .Which.Status.Should().Be(WorkflowDeliveryConnectionStatus.Completed);
        projected.Status.Should().Be("pending");
        projected.UserServiceId.Should().BeNull();
        projected.UpdatedAt.Should().Be(updatedAt);
    }

    [Fact]
    public async Task RefreshConnectStatusAsync_WhenInstallationExists_ShouldRejectBeforeCallingNyxId()
    {
        var now = DateTimeOffset.Parse("2026-08-16T01:30:00Z");
        var connection = new WorkflowDeliveryConnectionSnapshot(
            "lark",
            "api-lark",
            "link-alpha",
            WorkflowDeliveryConnectionStatus.Pending,
            null,
            now);
        var connectLinks = new RecordingConnectLinkPort(new NyxIdConnectLinkSnapshot(
            "link-alpha",
            NyxIdConnectLinkStatus.Completed,
            "api-lark",
            now.AddHours(1),
            now.AddMinutes(15),
            "user-service-alpha"));
        var context = new TestContext(
            DeliverySnapshot() with
            {
                Connections = [connection],
                Installation = ProvisionedInstallation(now),
            },
            connectLinks);

        var action = () => context.Service.RefreshConnectStatusAsync(
            "delivery-alpha",
            "scope-alpha",
            "lark",
            "caller-bearer");

        var exception = (await action.Should().ThrowAsync<WorkflowDeliveryException>()).Which;
        exception.Code.Should().Be("CONNECTIONS_LOCKED");
        connectLinks.GetCalls.Should().Be(0);
        context.Commands.UpdatedConnections.Should().BeEmpty();
    }

    [Fact]
    public async Task GetInstallationAsync_WhenMemberIsKnown_ShouldReturnAbsoluteConsoleUrlAndChannelRunCommand()
    {
        var now = DateTimeOffset.Parse("2026-08-16T01:00:00Z");
        var installation = new WorkflowInstallationSnapshot(
            "installation-alpha",
            "publish-alpha",
            "scope-alpha",
            "team-alpha",
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            new WorkflowDeliveryTriggerIntent(WorkflowDeliveryTriggerKind.None, null, null, false),
            "source-alpha",
            "resolved-alpha",
            "name: workflow-alpha\nsteps: []\n",
            [],
            new WorkflowCapabilityAdmissionPlan(),
            null,
            new Struct(),
            "installation-alpha:provision:a1",
            WorkflowInstallationStatus.ProvisioningAccepted,
            "provisioning_accepted",
            null,
            null,
            "wf-alpha",
            "m-alpha",
            "svc-alpha",
            "rev-alpha",
            "binding-alpha",
            null,
            null,
            null,
            null,
            1,
            now,
            now);
        var context = new TestContext(
            DeliverySnapshot() with { Installation = installation },
            options: new WorkflowDeliveryOptions
            {
                ConsoleWebBaseUrl = "https://aevatar-console.aevatar.ai",
            });

        var result = await context.Service.GetInstallationAsync("scope-alpha", "installation-alpha");

        result.Should().NotBeNull();
        result!.ConsoleUrl.Should().Be(
            "https://aevatar-console.aevatar.ai/scopes/scope-alpha/teams/team-alpha/members/m-alpha/invoke");
        result.ChannelRunCommand.Should().Be("/workflow run wf-alpha");
        result.DeliveryStateVersion.Should().Be(1);
    }

    [Fact]
    public async Task GetInstallationAsync_WhenConsoleWebBaseUrlIsUnconfigured_ShouldOmitConsoleUrl()
    {
        var now = DateTimeOffset.Parse("2026-08-16T01:00:00Z");
        var installation = ProvisionedInstallation(now);
        var context = new TestContext(DeliverySnapshot() with { Installation = installation });

        var result = await context.Service.GetInstallationAsync("scope-alpha", "installation-alpha");

        result.Should().NotBeNull();
        result!.ConsoleUrl.Should().BeNull();
        result.ChannelRunCommand.Should().Be("/workflow run wf-alpha");
    }

    [Fact]
    public void Constructor_WhenConsoleWebBaseUrlIsNotAnAbsoluteHttpsOrigin_ShouldFailFast()
    {
        var action = () => new TestContext(
            options: new WorkflowDeliveryOptions
            {
                ConsoleWebBaseUrl = "/scopes",
            });

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConsoleWebBaseUrl*");
    }

    private static WorkflowInstallationSnapshot ProvisionedInstallation(DateTimeOffset now) =>
        new(
            "installation-alpha",
            "publish-alpha",
            "scope-alpha",
            "team-alpha",
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            new WorkflowDeliveryTriggerIntent(WorkflowDeliveryTriggerKind.None, null, null, false),
            "source-alpha",
            "resolved-alpha",
            "name: workflow-alpha\nsteps: []\n",
            [],
            new WorkflowCapabilityAdmissionPlan(),
            null,
            new Struct(),
            "installation-alpha:provision:a1",
            WorkflowInstallationStatus.ProvisioningAccepted,
            "provisioning_accepted",
            null,
            null,
            "wf-alpha",
            "m-alpha",
            "svc-alpha",
            "rev-alpha",
            "binding-alpha",
            null,
            null,
            null,
            null,
            1,
            now,
            now);

    private static WorkflowDeliveryPublishRequest PublishRequest(string digest, string risk) =>
        new(
            "team-alpha",
            "publish-alpha",
            Confirmations:
            [
                new WorkflowDeliveryConfirmationInput(
                    "call-alpha",
                    digest,
                    risk),
            ]);

    private static WorkflowDeliveryTriggerRequest AutomaticTrigger(string kind) =>
        string.Equals(kind, "cron", StringComparison.Ordinal)
            ? new WorkflowDeliveryTriggerRequest(kind, "0 9 * * 1-5", "UTC")
            : new WorkflowDeliveryTriggerRequest(kind, TimeZone: "UTC");

    private static WorkflowDeliveryCallerContext Caller() =>
        new(
            new ProvisionWorkflowCallerCredential(
                "nyxid",
                "user-alpha",
                ProvisionWorkflowCallerCredential.DefaultScope),
            new WorkflowCapabilityAdmissionContext("caller-alpha"),
            new AuthenticatedAuthorizationOwnerContext(
                new AuthorizationOwnerIdentity
                {
                    Authority = NyxIdAuthorizationAuthorities.NyxId,
                    OwnerKind = AuthorizationOwnerKind.Personal,
                    OwnerSubject = "caller-alpha",
                },
                "nyxid",
                string.Empty,
                "user-alpha",
                "binding-alpha"),
            null);

    private static WorkflowDeliverySnapshot DeliverySnapshot(
        string workflowName = "workflow-alpha",
        WorkflowDeliveryAcceptanceMode acceptanceMode = WorkflowDeliveryAcceptanceMode.AutomaticPreview,
        string? acceptanceLimitation = null,
        bool inputDeclared = true)
    {
        var now = DateTimeOffset.Parse("2026-08-16T01:00:00Z");
        return new WorkflowDeliverySnapshot(
            "delivery-alpha",
            new WorkflowDeliveryPackageSnapshot(
                "package-alpha",
                "package-alpha@source-alpha",
                workflowName,
                "1",
                "Workflow Alpha",
                "Description",
                $"name: {workflowName}\nsteps: []\n",
                "source-alpha",
                "package-alpha",
                [],
                [],
                ["network.write"],
                "Writes an external resource",
                [],
                new WorkflowDeliveryAcceptancePolicy(
                    acceptanceMode,
                    acceptanceLimitation,
                    new WorkflowDeliveryAcceptanceInputRecipe(
                        new Struct
                        {
                            Fields =
                            {
                                ["dry_run"] = ProtobufValue.ForBool(true),
                            },
                        },
                        []),
                    inputDeclared),
                "admin-alpha",
                now),
            "scope-alpha",
            now.AddDays(7),
            null,
            WorkflowDeliveryLifecycleStatus.Active,
            "admin-alpha",
            now,
            null,
            null,
            null,
            [],
            null,
            1,
            now);
    }

    private static ActorPackage DeliveryActorPackage()
    {
        var recipe = new Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryAcceptanceInputRecipe
        {
            Literals = new Struct
            {
                Fields =
                {
                    ["dry_run"] = ProtobufValue.ForBool(true),
                },
            },
        };
        recipe.Bindings.Add(new Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryAcceptanceInputBinding
        {
            Key = "created_month",
            Prefix = "period:",
            Suffix = ":utc",
            InstallationCreatedAtUtc =
                new Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryInstallationCreatedAtUtcInput
                {
                    DateProjection = ProtoAcceptanceDateProjection.UtcYearMonth,
                    DayOffset = -2,
                },
        });
        recipe.Bindings.Add(new Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryAcceptanceInputBinding
        {
            Key = "owner_id",
            AuthenticatedOwnerExternalUserId =
                new Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryAuthenticatedOwnerExternalUserIdInput(),
        });
        return new ActorPackage
        {
            PackageId = "package-alpha",
            PackageVersionId = "package-alpha@package-hash-alpha",
            WorkflowName = "workflow-alpha",
            Version = "package-hash-alpha",
            DisplayName = "Workflow Alpha",
            Description = "Description",
            SourceYaml = "name: workflow-alpha\nsteps: []\n",
            SourceHash = "source-alpha",
            PackageHash = "package-hash-alpha",
            RiskSummary = "Writes an external resource",
            AcceptancePolicy = new Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryAcceptancePolicy
            {
                Mode = Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryAcceptanceMode.AutomaticPreview,
                Input = recipe,
            },
            CreatedBy = "admin-alpha",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(
                DateTimeOffset.Parse("2026-08-16T01:00:00Z")),
        };
    }

    private static WorkflowDeliverySnapshot DeliveryWithLarkSlot()
    {
        var snapshot = DeliverySnapshot();
        return snapshot with
        {
            Package = snapshot.Package with
            {
                ConnectionSlots =
                [
                    new WorkflowDeliveryConnectionSlotDefinition(
                        "lark",
                        "Lark",
                        "api-lark",
                        true),
                ],
            },
        };
    }

    private static NyxIdUserServiceInventoryItem UserService(
        string userServiceId,
        string instanceSlug = "customer-lark",
        string? catalogServiceSlug = "api-lark",
        string? label = null,
        bool isActive = true,
        NyxIdInventoryCredentialSourceKind credentialSource =
            NyxIdInventoryCredentialSourceKind.Personal,
        bool allowed = true,
        NyxIdInventoryCredentialStatus credentialStatus =
            NyxIdInventoryCredentialStatus.Active,
        string? nodeId = null,
        NyxIdInventoryNodeStatus nodeStatus = NyxIdInventoryNodeStatus.NotBound,
        bool connected = true) =>
        new(
            userServiceId,
            instanceSlug,
            catalogServiceSlug,
            label,
            isActive,
            credentialSource,
            allowed,
            credentialStatus,
            nodeId,
            nodeStatus,
            connected);

    private sealed class TestContext
    {
        public TestContext(
            WorkflowDeliverySnapshot? snapshot = null,
            INyxIdConnectLinkPort? connectLinks = null,
            INyxIdUserServiceInventoryPort? connectionInventory = null,
            WorkflowDeliveryOptions? options = null,
            Action<BeginWorkflowDeliveryConnectionMutation, StubQueryPort>? beginProjection = null,
            Action<AttachWorkflowDeliveryConnectionMutation, StubQueryPort>? attachProjection = null,
            Action<StartWorkflowInstallationMutation, StubQueryPort>? startProjection = null,
            IWorkflowDeliveryPackageCatalog? packageCatalog = null)
        {
            Queries = new StubQueryPort(snapshot ?? DeliverySnapshot());
            var projector = beginProjection ??
                ((BeginWorkflowDeliveryConnectionMutation value, StubQueryPort queries) => queries.ProjectBegin(value));
            var attachmentProjector = attachProjection ??
                ((AttachWorkflowDeliveryConnectionMutation value, StubQueryPort queries) => queries.ProjectAttach(value));
            var installationProjector = startProjection ??
                ((StartWorkflowInstallationMutation value, StubQueryPort queries) => queries.ProjectStart(value));
            Commands = new RecordingCommandPort(
                mutation => projector(mutation, Queries),
                mutation => attachmentProjector(mutation, Queries),
                mutation => installationProjector(mutation, Queries));
            Preview = new StubPreviewService();
            Provisioning = new RecordingProvisioningService();
            Service = new WorkflowDeliveryService(
                packageCatalog ?? new UnusedPackageCatalog(),
                new StubRenderer(),
                Commands,
                Queries,
                connectLinks ?? new UnusedConnectLinkPort(),
                connectionInventory ?? new UnusedUserServiceInventoryPort(),
                Preview,
                Provisioning,
                Options.Create(options ?? new WorkflowDeliveryOptions()),
                new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T02:00:00Z")));
        }

        public WorkflowDeliveryService Service { get; }

        public RecordingCommandPort Commands { get; }

        public StubQueryPort Queries { get; }

        public StubPreviewService Preview { get; }

        public RecordingProvisioningService Provisioning { get; }
    }

    private sealed class StubRenderer : IWorkflowDeliveryConfigurationRenderer
    {
        public WorkflowDeliveryRenderResult Render(
            ActorPackage package,
            IReadOnlyDictionary<string, System.Text.Json.JsonElement>? customerConfiguration,
            IReadOnlyDictionary<string, string>? connectionReferences) =>
            new(
                package.SourceHash,
                "resolved-alpha",
                "name: workflow-alpha\nsteps: []\n",
                new Dictionary<string, string>(),
                new Dictionary<string, string>());
    }

    private sealed class StubPreviewService : IWorkflowExplicitRequestPreviewService
    {
        public Task<WorkflowExplicitRequestPreviewResult> PreviewAsync(
            WorkflowExplicitRequestPreviewRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkflowExplicitRequestPreviewResult(
                request.WorkflowId ?? string.Empty,
                request.RevisionId ?? string.Empty,
                [
                    new WorkflowExplicitRequestPreviewItem(
                        "call-alpha",
                        "digest-alpha",
                        "user-service-alpha",
                        NyxIdRequestMethod.Post,
                        "/open-apis/resource",
                        NyxIdRequestBodyMode.Json,
                        true,
                        NyxIdRequestResponseMode.Text,
                        NyxIdOperationRisk.Write,
                        true,
                        WorkflowExplicitRequestApprovalEnforcement.BindTimeConfirmationAndRunTimeToolApproval,
                        [ExternalCapabilityExecutionMode.Interactive]),
                ]));
    }

    private sealed class RecordingProvisioningService : IStudioWorkflowProvisioningService
    {
        public List<ProvisionWorkflowRequest> PreparationRequests { get; } = [];

        public List<ProvisionWorkflowRequest> Requests { get; } = [];

        public Task<ProvisionWorkflowPreparation> PrepareAsync(
            string scopeId,
            ProvisionWorkflowCallerCredential callerCredential,
            ProvisionWorkflowRequest request,
            CancellationToken ct = default)
        {
            PreparationRequests.Add(request);
            var mode = request.RunImmediately || !string.IsNullOrWhiteSpace(request.Cron)
                ? ExternalCapabilityExecutionMode.Durable
                : ExternalCapabilityExecutionMode.Interactive;
            var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
                request.WorkflowYaml,
                request.InlineWorkflowYamls,
                mode,
                [],
                []);
            return Task.FromResult(new ProvisionWorkflowPreparation(
                "wf-alpha",
                "revision-alpha",
                plan));
        }

        public Task<ProvisionWorkflowResponse> ProvisionAsync(
            string scopeId,
            ProvisionWorkflowCallerCredential callerCredential,
            ProvisionWorkflowRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ProvisionWorkflowResponse(
                "m-alpha",
                scopeId,
                request.TeamId ?? string.Empty,
                ProvisionWorkflowBindingStatusNames.Accepted,
                "/admin#/observatory")
            {
                WorkflowId = "wf-alpha",
                PublishedServiceId = "svc-alpha",
                RevisionId = "revision-alpha",
                BindingRunId = "binding-run-alpha",
                StudioUrl = "/admin#/studio/scope-alpha/team-alpha/m-alpha",
            });
        }
    }

    private sealed class RecordingCommandPort(
        Action<BeginWorkflowDeliveryConnectionMutation>? projectBegin = null,
        Action<AttachWorkflowDeliveryConnectionMutation>? projectAttach = null,
        Action<StartWorkflowInstallationMutation>? projectStart = null) : IWorkflowDeliveryCommandPort
    {
        public List<CreateWorkflowDeliveryMutation> Created { get; } = [];

        public List<StartWorkflowInstallationMutation> Started { get; } = [];

        public List<RecordWorkflowProvisioningAcceptedMutation> ProvisioningAccepted { get; } = [];

        public List<RecordWorkflowInstallationReadyMutation> Ready { get; } = [];

        public List<RecordWorkflowInstallationFailedMutation> Failed { get; } = [];

        public List<BeginWorkflowDeliveryConnectionMutation> BegunConnections { get; } = [];

        public List<UpdateWorkflowDeliveryConnectionMutation> UpdatedConnections { get; } = [];

        public List<AttachWorkflowDeliveryConnectionMutation> AttachedConnections { get; } = [];

        public Task<WorkflowDeliveryCommandReceipt> CreateAsync(
            CreateWorkflowDeliveryMutation mutation,
            CancellationToken ct = default)
        {
            Created.Add(mutation);
            return Accepted(mutation.DeliveryId);
        }

        public Task<WorkflowDeliveryCommandReceipt> RecordAccessAsync(
            RecordWorkflowDeliveryAccessMutation mutation,
            CancellationToken ct = default) => Accepted(mutation.DeliveryId);

        public Task<WorkflowDeliveryCommandReceipt> RevokeAsync(
            RevokeWorkflowDeliveryMutation mutation,
            CancellationToken ct = default) => Accepted(mutation.DeliveryId);

        public Task<WorkflowDeliveryCommandReceipt> BeginConnectionAsync(
            BeginWorkflowDeliveryConnectionMutation mutation,
            CancellationToken ct = default)
        {
            BegunConnections.Add(mutation);
            projectBegin?.Invoke(mutation);
            return Accepted(mutation.DeliveryId);
        }

        public Task<WorkflowDeliveryCommandReceipt> UpdateConnectionAsync(
            UpdateWorkflowDeliveryConnectionMutation mutation,
            CancellationToken ct = default)
        {
            UpdatedConnections.Add(mutation);
            return Accepted(mutation.DeliveryId);
        }

        public Task<WorkflowDeliveryCommandReceipt> AttachConnectionAsync(
            AttachWorkflowDeliveryConnectionMutation mutation,
            CancellationToken ct = default)
        {
            AttachedConnections.Add(mutation);
            projectAttach?.Invoke(mutation);
            return Accepted(mutation.DeliveryId);
        }

        public Task<WorkflowDeliveryCommandReceipt> StartInstallationAsync(
            StartWorkflowInstallationMutation mutation,
            CancellationToken ct = default)
        {
            Started.Add(mutation);
            projectStart?.Invoke(mutation);
            return Accepted(mutation.DeliveryId);
        }

        public Task<WorkflowDeliveryCommandReceipt> RetryInstallationAsync(
            RetryWorkflowInstallationMutation mutation,
            CancellationToken ct = default) => Accepted(mutation.DeliveryId);

        public Task<WorkflowDeliveryCommandReceipt> ClaimInstallationContinuationAsync(
            ClaimWorkflowInstallationContinuationMutation mutation,
            CancellationToken ct = default) => Accepted(mutation.DeliveryId);

        public Task<WorkflowDeliveryCommandReceipt> RecordProvisioningAcceptedAsync(
            RecordWorkflowProvisioningAcceptedMutation mutation,
            CancellationToken ct = default)
        {
            ProvisioningAccepted.Add(mutation);
            return Accepted(mutation.DeliveryId);
        }

        public Task<WorkflowDeliveryCommandReceipt> RecordInstallationReadyAsync(
            RecordWorkflowInstallationReadyMutation mutation,
            CancellationToken ct = default)
        {
            Ready.Add(mutation);
            return Accepted(mutation.DeliveryId);
        }

        public Task<WorkflowDeliveryCommandReceipt> RecordInstallationFailedAsync(
            RecordWorkflowInstallationFailedMutation mutation,
            CancellationToken ct = default)
        {
            Failed.Add(mutation);
            return Accepted(mutation.DeliveryId);
        }

        private static Task<WorkflowDeliveryCommandReceipt> Accepted(string deliveryId) =>
            Task.FromResult(new WorkflowDeliveryCommandReceipt(
                deliveryId,
                $"actor-{deliveryId}",
                $"command-{deliveryId}",
                $"correlation-{deliveryId}",
                WorkflowDeliveryCommandAckStage.AcceptedForDispatch,
                null));
    }

    private sealed class StubQueryPort(WorkflowDeliverySnapshot snapshot) : IWorkflowDeliveryQueryPort
    {
        private BeginWorkflowDeliveryConnectionMutation? _scheduledBegin;
        private StartWorkflowInstallationMutation? _scheduledStart;
        private int _projectBeginAtRead = int.MaxValue;
        private int _projectStartAtRead = int.MaxValue;
        private int _advanceStateVersionAtRead = int.MaxValue;

        public WorkflowDeliverySnapshot Snapshot { get; private set; } = snapshot;

        public int ForScopeReads { get; private set; }

        public List<WorkflowDeliveryListQuery> ListQueries { get; } = [];

        public void ProjectBegin(BeginWorkflowDeliveryConnectionMutation mutation)
        {
            var projected = new WorkflowDeliveryConnectionSnapshot(
                mutation.SlotKey,
                mutation.ServiceSlug,
                mutation.LinkId,
                WorkflowDeliveryConnectionStatus.Pending,
                null,
                mutation.RequestedAtUtc);
            Snapshot = Snapshot with
            {
                Connections = Snapshot.Connections
                    .Where(connection => !string.Equals(
                        connection.SlotKey,
                        mutation.SlotKey,
                        StringComparison.Ordinal))
                    .Append(projected)
                    .ToArray(),
                StateVersion = Snapshot.StateVersion + 1,
                ProjectedAtUtc = mutation.RequestedAtUtc,
            };
        }

        public void ProjectBeginAfterReads(
            BeginWorkflowDeliveryConnectionMutation mutation,
            int staleReads)
        {
            _scheduledBegin = mutation;
            _projectBeginAtRead = ForScopeReads + staleReads + 1;
        }

        public void ProjectAttach(AttachWorkflowDeliveryConnectionMutation mutation)
        {
            var projected = new WorkflowDeliveryConnectionSnapshot(
                mutation.SlotKey,
                mutation.ServiceSlug,
                string.Empty,
                WorkflowDeliveryConnectionStatus.Completed,
                mutation.UserServiceId,
                mutation.AttachedAtUtc);
            Snapshot = Snapshot with
            {
                Connections = Snapshot.Connections
                    .Where(connection => !string.Equals(
                        connection.SlotKey,
                        mutation.SlotKey,
                        StringComparison.Ordinal))
                    .Append(projected)
                    .ToArray(),
                StateVersion = Snapshot.StateVersion + 1,
                ProjectedAtUtc = mutation.AttachedAtUtc,
            };
        }

        public void ProjectStart(StartWorkflowInstallationMutation mutation)
        {
            Snapshot = Snapshot with
            {
                Installation = new WorkflowInstallationSnapshot(
                    mutation.InstallationId,
                    mutation.IdempotencyKey,
                    mutation.ScopeId,
                    mutation.TeamId,
                    mutation.ConfigurationValues,
                    mutation.ConnectionReferences,
                    mutation.TriggerIntent,
                    mutation.SourceHash,
                    mutation.ResolvedHash,
                    mutation.ResolvedYaml,
                    mutation.Confirmations,
                    mutation.CapabilityAdmissionPlan,
                    mutation.AuthenticatedOwner,
                    AcceptanceInput: null,
                    mutation.OperationId,
                    WorkflowInstallationStatus.Accepted,
                    "accepted",
                    ErrorCode: null,
                    ErrorMessage: null,
                    WorkflowId: null,
                    MemberId: null,
                    PublishedServiceId: null,
                    RevisionId: null,
                    BindingRunId: null,
                    ScheduleId: null,
                    ScheduleProvisioningId: null,
                    ScheduleProvisioningStatus: null,
                    ReadinessEvidence: null,
                    Attempt: 1,
                    mutation.RequestedAtUtc,
                    mutation.RequestedAtUtc),
                StateVersion = Snapshot.StateVersion + 1,
                ProjectedAtUtc = mutation.RequestedAtUtc,
            };
        }

        public void ProjectStartAfterReads(
            StartWorkflowInstallationMutation mutation,
            int staleReads)
        {
            _scheduledStart = mutation;
            _projectStartAtRead = ForScopeReads + staleReads + 1;
        }

        public void ProjectAttachAtCurrentVersionThenAdvanceAfterReads(
            AttachWorkflowDeliveryConnectionMutation mutation,
            int staleReads)
        {
            var baselineStateVersion = Snapshot.StateVersion;
            ProjectAttach(mutation);
            Snapshot = Snapshot with { StateVersion = baselineStateVersion };
            _advanceStateVersionAtRead = ForScopeReads + staleReads + 1;
        }

        public void AdvanceStateVersion() =>
            Snapshot = Snapshot with { StateVersion = Snapshot.StateVersion + 1 };

        public Task<WorkflowDeliverySnapshot?> GetAsync(
            string deliveryId,
            CancellationToken ct = default) =>
            Task.FromResult<WorkflowDeliverySnapshot?>(Snapshot);

        public Task<WorkflowDeliverySnapshot?> GetForScopeAsync(
            string deliveryId,
            string targetScopeId,
            CancellationToken ct = default)
        {
            ForScopeReads++;
            if (_scheduledBegin != null && ForScopeReads >= _projectBeginAtRead)
            {
                var mutation = _scheduledBegin;
                _scheduledBegin = null;
                _projectBeginAtRead = int.MaxValue;
                ProjectBegin(mutation);
            }
            if (_scheduledStart != null && ForScopeReads >= _projectStartAtRead)
            {
                var mutation = _scheduledStart;
                _scheduledStart = null;
                _projectStartAtRead = int.MaxValue;
                ProjectStart(mutation);
            }
            if (ForScopeReads >= _advanceStateVersionAtRead)
            {
                Snapshot = Snapshot with { StateVersion = Snapshot.StateVersion + 1 };
                _advanceStateVersionAtRead = int.MaxValue;
            }
            return Task.FromResult<WorkflowDeliverySnapshot?>(Snapshot);
        }

        public Task<WorkflowDeliveryListResult> ListAsync(
            WorkflowDeliveryListQuery query,
            CancellationToken ct = default)
        {
            ListQueries.Add(query);
            return Task.FromResult(new WorkflowDeliveryListResult([Snapshot], null));
        }

        public Task<WorkflowDeliverySnapshot?> FindByInstallationAsync(
            string scopeId,
            string installationId,
            CancellationToken ct = default) =>
            Task.FromResult<WorkflowDeliverySnapshot?>(Snapshot);
    }

    private sealed class UnusedPackageCatalog : IWorkflowDeliveryPackageCatalog
    {
        public Task<IReadOnlyList<ActorPackage>> ListAsync(
            string createdBy,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ActorPackage> GetAsync(
            string workflowName,
            string createdBy,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubPackageCatalog(ActorPackage package) : IWorkflowDeliveryPackageCatalog
    {
        public Task<IReadOnlyList<ActorPackage>> ListAsync(
            string createdBy,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ActorPackage>>([package]);

        public Task<ActorPackage> GetAsync(
            string workflowName,
            string createdBy,
            CancellationToken ct = default) =>
            Task.FromResult(package);
    }

    private sealed class UnusedConnectLinkPort : INyxIdConnectLinkPort
    {
        public Task<NyxIdConnectLinkCreated> CreateAsync(
            string bearerToken,
            NyxIdConnectLinkCreateRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<NyxIdConnectLinkSnapshot> GetAsync(
            string bearerToken,
            string connectLinkId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedUserServiceInventoryPort : INyxIdUserServiceInventoryPort
    {
        public Task<IReadOnlyList<NyxIdUserServiceInventoryItem>> ListAsync(
            string bearerToken,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingUserServiceInventoryPort(
        IReadOnlyList<NyxIdUserServiceInventoryItem> items) : INyxIdUserServiceInventoryPort
    {
        public int ListCalls { get; private set; }

        public string? BearerToken { get; private set; }

        public Task<IReadOnlyList<NyxIdUserServiceInventoryItem>> ListAsync(
            string bearerToken,
            CancellationToken ct = default)
        {
            ListCalls++;
            BearerToken = bearerToken;
            return Task.FromResult(items);
        }
    }

    private sealed class RecordingCreateConnectLinkPort : INyxIdConnectLinkPort
    {
        public string? BearerToken { get; private set; }

        public NyxIdConnectLinkCreateRequest? Request { get; private set; }

        public Task<NyxIdConnectLinkCreated> CreateAsync(
            string bearerToken,
            NyxIdConnectLinkCreateRequest request,
            CancellationToken ct = default)
        {
            BearerToken = bearerToken;
            Request = request;
            return Task.FromResult(new NyxIdConnectLinkCreated(
                "link-created",
                "https://nyx.example/connect/redacted",
                DateTimeOffset.Parse("2026-08-16T03:00:00Z")));
        }

        public Task<NyxIdConnectLinkSnapshot> GetAsync(
            string bearerToken,
            string connectLinkId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingConnectLinkPort(NyxIdConnectLinkSnapshot snapshot) : INyxIdConnectLinkPort
    {
        public int GetCalls { get; private set; }

        public Task<NyxIdConnectLinkCreated> CreateAsync(
            string bearerToken,
            NyxIdConnectLinkCreateRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<NyxIdConnectLinkSnapshot> GetAsync(
            string bearerToken,
            string connectLinkId,
            CancellationToken ct = default)
        {
            GetCalls++;
            bearerToken.Should().Be("caller-bearer");
            connectLinkId.Should().Be(snapshot.ConnectLinkId);
            return Task.FromResult(snapshot);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
