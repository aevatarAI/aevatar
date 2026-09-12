using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelBotRegistrationGAgentTests : IAsyncLifetime
{
    private const long RepairRequestedAtUnixMs = 1784563200000;

    private static Aevatar.Foundation.Abstractions.Credentials.SecretReference TestDeliverySecretReference(
        string registrationId,
        string scopeId = "scope-1") =>
        new()
        {
            Ref = $"sec_delivery_{registrationId}",
            Purpose = Aevatar.Foundation.Abstractions.Credentials.CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey,
            OwnerScopeKey = scopeId,
            Version = 1,
            Fingerprint = $"sha256:{registrationId}",
            CreatedAtUnixMs = 1788912000000,
        };

    private ChannelBotRegistrationGAgent _agent = null!;
    private ServiceProvider _serviceProvider = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventStore, InMemoryEventStore>();
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(
            typeof(IEventSourcingBehaviorFactory<>),
            typeof(DefaultEventSourcingBehaviorFactory<>));
        services.AddSingleton<IActorRuntimeCallbackScheduler, NoopCallbackScheduler>();

        _serviceProvider = services.BuildServiceProvider();

        _agent = new ChannelBotRegistrationGAgent
        {
            Services = _serviceProvider,
            EventSourcingBehaviorFactory =
                _serviceProvider.GetRequiredService<IEventSourcingBehaviorFactory<ChannelBotRegistrationStoreState>>(),
        };
        SetId(_agent, ChannelBotRegistrationGAgent.WellKnownId);

        await _agent.ActivateAsync();
    }

    public Task DisposeAsync()
    {
        _serviceProvider.Dispose();
        return Task.CompletedTask;
    }

    private ChannelBotRegistrationGAgent CreateAgent()
    {
        var agent = new ChannelBotRegistrationGAgent
        {
            Services = _serviceProvider,
            EventSourcingBehaviorFactory =
                _serviceProvider.GetRequiredService<IEventSourcingBehaviorFactory<ChannelBotRegistrationStoreState>>(),
        };
        SetId(agent, ChannelBotRegistrationGAgent.WellKnownId);
        return agent;
    }

    private async Task SeedHistoricalRegistrationAsync(
        string registrationId = "reg-alpha",
        string platform = "lark")
    {
        await AppendCommittedEventAsync(new ChannelBotRegisteredEvent
        {
            Entry = HistoricalRegistration(registrationId, platform),
        });
        _agent = CreateAgent();
        await _agent.ActivateAsync();
    }

    private static ChannelBotRegistrationEntry HistoricalRegistration(
        string registrationId = "reg-alpha",
        string platform = "lark") =>
        new()
        {
            Id = registrationId,
            Platform = platform,
            ScopeId = "scope-alpha",
            NyxProviderSlug = platform == "lark" ? "api-lark-bot" : $"api-{platform}-bot",
            WebhookUrl = $"https://nyx.example/api/v1/webhooks/channel/{platform}/bot-alpha",
            NyxChannelBotId = "bot-alpha",
            NyxAgentApiKeyId = "key-old-alpha",
            NyxConversationRouteId = "route-alpha",
            DefaultSkillName = "team-entry-alpha",
            CreatedAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero)),
        };

    private static ChannelBotRegisterCommand NewRegistration(
        string registrationId = "reg-1",
        string platform = "lark",
        string scopeId = "scope-1",
        string apiKeyId = "key-1",
        string defaultSkillName = "")
    {
        var secretReference = TestDeliverySecretReference(registrationId, scopeId);
        return new ChannelBotRegisterCommand
        {
            RequestedId = registrationId,
            Platform = platform,
            ScopeId = scopeId,
            NyxProviderSlug = platform == "lark" ? "api-lark-bot" : $"api-{platform}-bot",
            WebhookUrl = $"https://nyx.example.com/api/v1/webhooks/channel/{platform}/bot-1",
            NyxChannelBotId = "bot-1",
            NyxAgentApiKeyId = apiKeyId,
            NyxConversationRouteId = "route-1",
            WorkflowResultDeliveryCredential = secretReference.Clone(),
            ChannelAgentKey = new ChannelAgentKeyCredential
            {
                ApiKeyId = apiKeyId,
                SecretReference = secretReference,
                Grant = new ChannelAgentKeyGrantSnapshot
                {
                    AllowAllServices = true,
                    AllowAllNodes = true,
                },
            },
            AuthorizationMode = ChannelRegistrationAuthorizationMode.NyxidDefault,
            DefaultSkillName = defaultSkillName,
        };
    }

    private static ChannelBotRegisterCommand ExplicitRegistration(
        string registrationId,
        bool includeBusinessService)
    {
        var command = NewRegistration(registrationId, apiKeyId: $"key-{registrationId}");
        command.AuthorizationMode = ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist;
        command.RegistrationServiceAllowlist = new ChannelRegistrationServiceAllowlist();
        if (includeBusinessService)
            command.RegistrationServiceAllowlist.ServiceIds.Add("svc-business");
        command.ChannelAgentKey.Grant.AllowAllServices = false;
        command.ChannelAgentKey.Grant.AllowAllNodes = false;
        command.ChannelAgentKey.Grant.AllowedServiceIds.Add("svc-business");
        command.ChannelAgentKey.Grant.AllowedServiceIds.Add("svc-dependency");
        command.ChannelAgentKey.Grant.AllowedNodeIds.Add("node-runtime");
        command.ChannelAgentKey.Grant.ScopePlanDigest =
            "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        return command;
    }

    private static Aevatar.Foundation.Abstractions.Credentials.SecretReference PreparedReference() =>
        new()
        {
            Ref = "sec-repair-alpha",
            Purpose = Aevatar.Foundation.Abstractions.Credentials.CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey,
            OwnerScopeKey = "scope-alpha",
            Version = 1,
        };

    private static ChannelBotWorkflowResultDeliveryRepairRequestCommand RepairRequest(
        string registrationId = "reg-alpha",
        string requestId = "repair-alpha",
        string expectedApiKeyId = "key-old-alpha") =>
        new()
        {
            RegistrationId = registrationId,
            RequestId = requestId,
            ExpectedApiKeyId = expectedApiKeyId,
            ExpectedConversationRouteId = "route-alpha",
            RequestedBySubjectId = "user-alpha",
            RequestedAtUnixMs = RepairRequestedAtUnixMs,
        };

    private static void SetId(GAgentBase agent, string actorId)
    {
        var method = typeof(GAgentBase).GetMethod(
            "SetId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull("tests replay the well-known registration-store event stream");
        method!.Invoke(agent, [actorId]);
    }

    [Fact]
    public async Task HandleRegister_PersistsLarkRelayRegistration()
    {
        await _agent.HandleRegister(NewRegistration());

        _agent.State.Registrations.Should().ContainSingle();
        var entry = _agent.State.Registrations[0];
        entry.Id.Should().Be("reg-1");
        entry.Platform.Should().Be("lark");
        entry.NyxProviderSlug.Should().Be("api-lark-bot");
        entry.ScopeId.Should().Be("scope-1");
        entry.WebhookUrl.Should().Contain("/api/v1/webhooks/channel/lark/");
        entry.NyxChannelBotId.Should().Be("bot-1");
        entry.NyxAgentApiKeyId.Should().Be("key-1");
        entry.NyxConversationRouteId.Should().Be("route-1");
        entry.WorkflowResultDeliveryCredential.Should().Be(TestDeliverySecretReference("reg-1"));
        entry.AuthorizationMode.Should().Be(ChannelRegistrationAuthorizationMode.NyxidDefault);
        entry.ChannelAgentKey.Should().NotBeNull();
        entry.ChannelAgentKey.ApiKeyId.Should().Be("key-1");
        entry.ChannelAgentKey.SecretReference.Should().Be(entry.WorkflowResultDeliveryCredential);
        entry.ChannelAgentKey.Grant.HasAllowAllServices.Should().BeTrue();
        entry.ChannelAgentKey.Grant.HasAllowAllNodes.Should().BeTrue();
        entry.Tombstoned.Should().BeFalse();
        entry.DefaultSkillName.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleRegister_ExplicitAuthorizationContract_SurvivesCommitAndReactivation(
        bool includeBusinessService)
    {
        var registrationId = includeBusinessService
            ? "reg-explicit-nonempty"
            : "reg-explicit-empty";
        var command = ExplicitRegistration(registrationId, includeBusinessService);

        await _agent.HandleRegister(command);

        var committed = await LastCommittedPayloadAsync<ChannelBotRegisteredEvent>();
        committed.Entry.AuthorizationMode.Should().Be(
            ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist);
        committed.Entry.RegistrationServiceAllowlist.Should().NotBeNull();
        committed.Entry.RegistrationServiceAllowlist.ServiceIds.Should().Equal(
            command.RegistrationServiceAllowlist.ServiceIds);
        committed.Entry.ChannelAgentKey.Should().Be(command.ChannelAgentKey);
        committed.Entry.ChannelAgentKey.Should().NotBeSameAs(command.ChannelAgentKey);
        committed.Entry.ChannelAgentKey.Grant.ScopePlanDigest.Should().Be(
            command.ChannelAgentKey.Grant.ScopePlanDigest);
        committed.Entry.ChannelAgentKey.Grant.HasAllowAllServices.Should().BeTrue();
        committed.Entry.ChannelAgentKey.Grant.AllowAllServices.Should().BeFalse();
        committed.Entry.ChannelAgentKey.Grant.HasAllowAllNodes.Should().BeTrue();
        committed.Entry.ChannelAgentKey.Grant.AllowAllNodes.Should().BeFalse();
        committed.Entry.NyxAgentApiKeyId.Should().Be(committed.Entry.ChannelAgentKey.ApiKeyId);
        committed.Entry.WorkflowResultDeliveryCredential.Should().Be(
            committed.Entry.ChannelAgentKey.SecretReference);

        await _agent.DeactivateAsync();
        var reactivated = CreateAgent();
        await reactivated.ActivateAsync();

        reactivated.State.Registrations.Should().ContainSingle();
        reactivated.State.Registrations.Single().Should().Be(committed.Entry);
        reactivated.State.Registrations.Single().RegistrationServiceAllowlist.Should().NotBeSameAs(
            command.RegistrationServiceAllowlist);
    }

    [Fact]
    public async Task HandleRegister_PersistsCanonicalDefaultSkillName()
    {
        await _agent.HandleRegister(NewRegistration(
            registrationId: "reg-bound",
            defaultSkillName: " /WhatsApp-Reply-Draft "));

        _agent.State.Registrations.Single(r => r.Id == "reg-bound")
            .DefaultSkillName.Should().Be("whatsapp-reply-draft");
    }

    [Fact]
    public async Task HandleUpdateRuntimeConfig_UpdatesOnlyRuntimeFacts()
    {
        var command = NewRegistration("reg-runtime", apiKeyId: "key-runtime");
        await _agent.HandleRegister(command);
        var before = _agent.State.Registrations.Single().Clone();
        var runtimeConfig = new ChannelBotRuntimeConfig
        {
            DefaultSkill = new ChannelBotRuntimeDefaultSkillConfig
            {
                Name = " /Booking-Capacity ",
            },
            CredentialSourceMode = ChannelBotRuntimeCredentialSourceMode.RegistrationAgentKey,
            AgentKeyServiceRequirements = new ChannelBotRuntimeAgentKeyServiceRequirements(),
        };
        runtimeConfig.ToolSetRefs.Add("channel.reply.default");
        runtimeConfig.AgentKeyServiceRequirements.AllowedServiceSlugs.Add("API-Google-Workspace");
        runtimeConfig.NyxidServiceSelectors.Add(new ChannelBotRuntimeNyxIdServiceSelector
        {
            ServiceSlug = " API-Google-Workspace ",
        });

        await _agent.HandleUpdateRuntimeConfig(new ChannelBotUpdateRuntimeConfigCommand
        {
            RegistrationId = "reg-runtime",
            RuntimeConfig = runtimeConfig,
            UpdatedAtUnixMs = 1,
        });

        var entry = _agent.State.Registrations.Single();
        entry.NyxAgentApiKeyId.Should().Be(before.NyxAgentApiKeyId);
        entry.NyxChannelBotId.Should().Be(before.NyxChannelBotId);
        entry.NyxConversationRouteId.Should().Be(before.NyxConversationRouteId);
        entry.ChannelAgentKey.Should().Be(before.ChannelAgentKey);
        entry.DefaultSkillName.Should().Be("booking-capacity");
        entry.RuntimeConfig.Should().NotBeNull();
        entry.RuntimeConfig!.NyxidServiceSelectors.Should().ContainSingle();
        entry.RuntimeConfig.NyxidServiceSelectors[0].ServiceSlug.Should().Be("api-google-workspace");
        entry.RuntimeConfig.NyxidServiceSelectors[0].EndpointNames.Should().BeEmpty();
        entry.RuntimeConfig.AgentKeyServiceRequirements.AllowedServiceSlugs.Should().Equal("api-google-workspace");
    }

    [Fact]
    public async Task HandleRegister_RejectsLegacyShapedNewCommand()
    {
        var beforeVersion = _agent.EventSourcing!.CurrentVersion;

        await _agent.HandleRegister(new ChannelBotRegisterCommand
        {
            RequestedId = "reg-legacy-command",
            Platform = "lark",
            ScopeId = "scope-1",
            NyxAgentApiKeyId = "key-legacy",
            WorkflowResultDeliveryCredential = TestDeliverySecretReference("reg-legacy-command"),
        });

        _agent.State.Registrations.Should().BeEmpty();
        _agent.EventSourcing.CurrentVersion.Should().Be(beforeVersion + 1);
        var rejected = await LastCommittedPayloadAsync<ChannelBotRegistrationRejectedEvent>();
        rejected.Reason.Should().Be("channel_authorization_contract_invalid");
    }

    [Fact]
    public async Task HandleRegister_RejectsMalformedNewAuthorizationContract()
    {
        var command = NewRegistration("reg-malformed");
        command.ChannelAgentKey.Grant.ClearAllowAllNodes();

        await _agent.HandleRegister(command);

        _agent.State.Registrations.Should().BeEmpty();
        var rejected = await LastCommittedPayloadAsync<ChannelBotRegistrationRejectedEvent>();
        rejected.Reason.Should().Be("channel_authorization_contract_invalid");
    }

    [Fact]
    public async Task HandleRegister_DuplicateActiveIdPreservesOriginalAuthorizationFact()
    {
        await _agent.HandleRegister(NewRegistration("reg-duplicate", apiKeyId: "key-original"));
        var original = _agent.State.Registrations.Single().Clone();

        await _agent.HandleRegister(NewRegistration("reg-duplicate", apiKeyId: "key-replacement"));

        _agent.State.Registrations.Should().ContainSingle();
        _agent.State.Registrations.Single().Should().Be(original);
        var rejected = await LastCommittedPayloadAsync<ChannelBotRegistrationRejectedEvent>();
        rejected.Reason.Should().Be("registration_id_conflict");
    }

    [Fact]
    public async Task ReplayHistoricalRegisteredEvent_PreservesLegacyShapeWithoutInventingMode()
    {
        await SeedHistoricalRegistrationAsync();

        var entry = _agent.State.Registrations.Should().ContainSingle().Subject;
        entry.AuthorizationMode.Should().Be(ChannelRegistrationAuthorizationMode.Unspecified);
        entry.ChannelAgentKey.Should().BeNull();
        entry.NyxAgentApiKeyId.Should().Be("key-old-alpha");
    }

    [Fact]
    public async Task ReplayRepairCompletedEvent_DoesNotMutateNewAuthorizationFact()
    {
        await _agent.HandleRegister(NewRegistration("reg-new", apiKeyId: "key-new"));
        var original = _agent.State.Registrations.Single().Clone();
        await AppendCommittedEventAsync(new ChannelBotWorkflowResultDeliveryRepairCompletedEvent
        {
            RegistrationId = "reg-new",
            RequestId = "repair-invalid",
            ExpectedApiKeyId = "key-new",
            RotatedApiKeyId = "key-rotated",
            PreparedSecretReference = PreparedReference(),
            CompletedAtUnixMs = RepairRequestedAtUnixMs,
        });

        var replayed = CreateAgent();
        await replayed.ActivateAsync();

        replayed.State.Registrations.Should().ContainSingle();
        replayed.State.Registrations.Single().Should().Be(original);
    }

    [Fact]
    public async Task HandleWorkflowResultDeliveryRepairComplete_RejectsNewAuthorizationModelBeforeIdempotentLegacyShortcut()
    {
        var command = NewRegistration("reg-new-complete", apiKeyId: "key-new-complete");
        await _agent.HandleRegister(command);
        var entry = _agent.State.Registrations.Single();

        await _agent.HandleWorkflowResultDeliveryRepairComplete(new()
        {
            RegistrationId = entry.Id,
            RequestId = "repair-invalid-new",
            ExpectedApiKeyId = entry.NyxAgentApiKeyId,
            RotatedApiKeyId = entry.NyxAgentApiKeyId,
            PreparedSecretReference = entry.WorkflowResultDeliveryCredential.Clone(),
            UpdatedAtUnixMs = RepairRequestedAtUnixMs,
        });

        var rejected = await LastCommittedPayloadAsync<ChannelBotWorkflowResultDeliveryRepairRejectedEvent>();
        rejected.RegistrationId.Should().Be(entry.Id);
        rejected.Reason.Should().Be(ChannelWorkflowResultDeliveryRepairFailureReason.InvalidRequest);
        _agent.State.Registrations.Single().Should().Be(entry);
    }

    [Fact]
    public async Task HandleWorkflowResultDeliveryRepair_RequestPrepareComplete_PromotesCredentialAndPreservesRegistration()
    {
        await SeedHistoricalRegistrationAsync();
        await _agent.HandleRecordInbound(new ChannelBotRecordInboundCommand
        {
            RegistrationId = "reg-alpha",
            ObservedAtUtc = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero)),
        });
        var original = _agent.State.Registrations.Single().Clone();

        await _agent.HandleWorkflowResultDeliveryRepairRequest(RepairRequest());

        var requested = _agent.State.Registrations.Single().WorkflowResultDeliveryRepair;
        requested.Status.Should().Be(ChannelWorkflowResultDeliveryRepairStatus.Requested);
        requested.ExpectedApiKeyId.Should().Be("key-old-alpha");
        requested.ExpectedConversationRouteId.Should().Be("route-alpha");
        requested.RequestedBySubjectId.Should().Be("user-alpha");

        await _agent.HandleWorkflowResultDeliveryRepairPrepare(new()
        {
            RegistrationId = "reg-alpha",
            RequestId = "repair-alpha",
            ExpectedApiKeyId = "key-old-alpha",
            RotatedApiKeyId = "key-new-alpha",
            PreparedSecretReference = PreparedReference(),
            UpdatedAtUnixMs = RepairRequestedAtUnixMs + 1000,
        });

        var prepared = _agent.State.Registrations.Single().WorkflowResultDeliveryRepair;
        prepared.Status.Should().Be(ChannelWorkflowResultDeliveryRepairStatus.CredentialPrepared);
        prepared.RotatedApiKeyId.Should().Be("key-new-alpha");
        prepared.PreparedSecretReference.Should().Be(PreparedReference());

        await _agent.HandleWorkflowResultDeliveryRepairComplete(new()
        {
            RegistrationId = "reg-alpha",
            RequestId = "repair-alpha",
            ExpectedApiKeyId = "key-old-alpha",
            RotatedApiKeyId = "key-new-alpha",
            PreparedSecretReference = PreparedReference(),
            UpdatedAtUnixMs = RepairRequestedAtUnixMs + 2000,
        });

        var completed = _agent.State.Registrations.Single();
        completed.NyxAgentApiKeyId.Should().Be("key-new-alpha");
        completed.WorkflowResultDeliveryCredential.Should().Be(PreparedReference());
        completed.WorkflowResultDeliveryRepair.Should().BeNull();
        completed.Id.Should().Be(original.Id);
        completed.Platform.Should().Be(original.Platform);
        completed.ScopeId.Should().Be(original.ScopeId);
        completed.NyxProviderSlug.Should().Be(original.NyxProviderSlug);
        completed.NyxChannelBotId.Should().Be(original.NyxChannelBotId);
        completed.NyxConversationRouteId.Should().Be(original.NyxConversationRouteId);
        completed.WebhookUrl.Should().Be(original.WebhookUrl);
        completed.DefaultSkillName.Should().Be(original.DefaultSkillName);
        completed.CreatedAt.Should().Be(original.CreatedAt);
        completed.LastInboundAtUtc.Should().Be(original.LastInboundAtUtc);
        completed.Tombstoned.Should().BeFalse();
    }

    [Fact]
    public async Task HandleWorkflowResultDeliveryRepair_DuplicateCommandsRecommitSameBusinessFacts()
    {
        await SeedHistoricalRegistrationAsync();
        var request = RepairRequest();
        await _agent.HandleWorkflowResultDeliveryRepairRequest(request);
        var requested = _agent.State.Registrations.Single().WorkflowResultDeliveryRepair.Clone();
        var requestedVersion = _agent.EventSourcing!.CurrentVersion;

        await _agent.HandleWorkflowResultDeliveryRepairRequest(request.Clone());

        _agent.EventSourcing.CurrentVersion.Should().Be(requestedVersion + 1);
        _agent.State.Registrations.Single().WorkflowResultDeliveryRepair.Should().Be(requested);
        (await LastCommittedPayloadAsync<ChannelBotWorkflowResultDeliveryRepairRequestedEvent>())
            .Repair.Should().Be(requested);

        var prepare = new ChannelBotWorkflowResultDeliveryRepairPrepareCommand
        {
            RegistrationId = "reg-alpha",
            RequestId = "repair-alpha",
            ExpectedApiKeyId = "key-old-alpha",
            RotatedApiKeyId = "key-new-alpha",
            PreparedSecretReference = PreparedReference(),
            UpdatedAtUnixMs = RepairRequestedAtUnixMs + 1000,
        };
        await _agent.HandleWorkflowResultDeliveryRepairPrepare(prepare);
        var prepared = _agent.State.Registrations.Single().WorkflowResultDeliveryRepair.Clone();
        var preparedVersion = _agent.EventSourcing.CurrentVersion;

        await _agent.HandleWorkflowResultDeliveryRepairPrepare(prepare.Clone());

        _agent.EventSourcing.CurrentVersion.Should().Be(preparedVersion + 1);
        _agent.State.Registrations.Single().WorkflowResultDeliveryRepair.Should().Be(prepared);
        (await LastCommittedPayloadAsync<ChannelBotWorkflowResultDeliveryRepairPreparedEvent>())
            .Repair.Should().Be(prepared);
    }

    [Fact]
    public async Task HandleWorkflowResultDeliveryRepair_RejectsStaleAndConflictingCommandsWithoutOverwritingState()
    {
        await SeedHistoricalRegistrationAsync();

        await _agent.HandleWorkflowResultDeliveryRepairRequest(RepairRequest(expectedApiKeyId: "key-stale-alpha"));

        _agent.State.Registrations.Single().WorkflowResultDeliveryRepair.Should().BeNull();
        var staleRequest = await LastCommittedPayloadAsync<ChannelBotWorkflowResultDeliveryRepairRejectedEvent>();
        staleRequest.Reason.Should().Be(ChannelWorkflowResultDeliveryRepairFailureReason.StaleActiveKey);

        await _agent.HandleWorkflowResultDeliveryRepairRequest(RepairRequest());
        var accepted = _agent.State.Registrations.Single().WorkflowResultDeliveryRepair.Clone();

        await _agent.HandleWorkflowResultDeliveryRepairRequest(RepairRequest(requestId: "repair-beta"));

        _agent.State.Registrations.Single().WorkflowResultDeliveryRepair.Should().Be(accepted);
        var conflict = await LastCommittedPayloadAsync<ChannelBotWorkflowResultDeliveryRepairRejectedEvent>();
        conflict.RequestId.Should().Be("repair-beta");
        conflict.Reason.Should().Be(ChannelWorkflowResultDeliveryRepairFailureReason.RequestConflict);

        await _agent.HandleWorkflowResultDeliveryRepairPrepare(new()
        {
            RegistrationId = "reg-alpha",
            RequestId = "repair-alpha",
            ExpectedApiKeyId = "key-stale-alpha",
            RotatedApiKeyId = "key-new-alpha",
            PreparedSecretReference = PreparedReference(),
            UpdatedAtUnixMs = RepairRequestedAtUnixMs + 1000,
        });

        _agent.State.Registrations.Single().WorkflowResultDeliveryRepair.Should().Be(accepted);
        (await LastCommittedPayloadAsync<ChannelBotWorkflowResultDeliveryRepairRejectedEvent>())
            .Reason.Should().Be(ChannelWorkflowResultDeliveryRepairFailureReason.StaleActiveKey);
    }

    [Fact]
    public async Task HandleWorkflowResultDeliveryRepair_FailureRetainsPreparedFactsForForwardOnlyRetry()
    {
        await SeedHistoricalRegistrationAsync();
        await _agent.HandleWorkflowResultDeliveryRepairRequest(RepairRequest());
        await _agent.HandleWorkflowResultDeliveryRepairPrepare(new()
        {
            RegistrationId = "reg-alpha",
            RequestId = "repair-alpha",
            ExpectedApiKeyId = "key-old-alpha",
            RotatedApiKeyId = "key-new-alpha",
            PreparedSecretReference = PreparedReference(),
            UpdatedAtUnixMs = RepairRequestedAtUnixMs + 1000,
        });
        var fail = new ChannelBotWorkflowResultDeliveryRepairFailCommand
        {
            RegistrationId = "reg-alpha",
            RequestId = "repair-alpha",
            ExpectedApiKeyId = "key-old-alpha",
            RotatedApiKeyId = "key-new-alpha",
            PreparedSecretReference = PreparedReference(),
            FailurePhase = ChannelWorkflowResultDeliveryRepairPhase.RouteRebinding,
            FailureReason = ChannelWorkflowResultDeliveryRepairFailureReason.RouteUpdateFailed,
            UpdatedAtUnixMs = RepairRequestedAtUnixMs + 2000,
        };

        await _agent.HandleWorkflowResultDeliveryRepairFail(fail);

        var failed = _agent.State.Registrations.Single().WorkflowResultDeliveryRepair;
        failed.Status.Should().Be(ChannelWorkflowResultDeliveryRepairStatus.Failed);
        failed.RotatedApiKeyId.Should().Be("key-new-alpha");
        failed.PreparedSecretReference.Should().Be(PreparedReference());
        failed.FailurePhase.Should().Be(ChannelWorkflowResultDeliveryRepairPhase.RouteRebinding);
        failed.FailureReason.Should().Be(ChannelWorkflowResultDeliveryRepairFailureReason.RouteUpdateFailed);

        var failedVersion = _agent.EventSourcing!.CurrentVersion;
        await _agent.HandleWorkflowResultDeliveryRepairFail(fail.Clone());
        _agent.EventSourcing.CurrentVersion.Should().Be(failedVersion + 1);
        _agent.State.Registrations.Single().WorkflowResultDeliveryRepair.Should().Be(failed);
    }

    [Fact]
    public async Task HandleWorkflowResultDeliveryRepair_RejectsNonLarkAndTombstonedRegistrations()
    {
        await SeedHistoricalRegistrationAsync("reg-telegram", "telegram");

        await _agent.HandleWorkflowResultDeliveryRepairRequest(RepairRequest("reg-telegram"));

        _agent.State.Registrations.Single().WorkflowResultDeliveryRepair.Should().BeNull();
        (await LastCommittedPayloadAsync<ChannelBotWorkflowResultDeliveryRepairRejectedEvent>())
            .Reason.Should().Be(ChannelWorkflowResultDeliveryRepairFailureReason.UnsupportedPlatform);

        await SeedHistoricalRegistrationAsync();
        await _agent.HandleUnregister(new ChannelBotUnregisterCommand { RegistrationId = "reg-alpha" });

        await _agent.HandleWorkflowResultDeliveryRepairRequest(RepairRequest());

        _agent.State.Registrations.Single(entry => entry.Id == "reg-alpha")
            .WorkflowResultDeliveryRepair.Should().BeNull();
        (await LastCommittedPayloadAsync<ChannelBotWorkflowResultDeliveryRepairRejectedEvent>())
            .Reason.Should().Be(ChannelWorkflowResultDeliveryRepairFailureReason.RegistrationNotFound);
    }

    [Fact]
    public async Task HandleRecordInbound_SetsActivationOnce_AndIsIdempotent()
    {
        await _agent.HandleRegister(NewRegistration());

        var first = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 24, 10, 0, 0, TimeSpan.Zero));
        await _agent.HandleRecordInbound(new ChannelBotRecordInboundCommand { RegistrationId = "reg-1", ObservedAtUtc = first });
        _agent.State.Registrations.Single(r => r.Id == "reg-1").LastInboundAtUtc.Should().Be(first);

        // Activation is set once — a later inbound must NOT overwrite it (bounds the
        // single store actor's event log; we don't persist an event per message).
        var second = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 24, 11, 0, 0, TimeSpan.Zero));
        await _agent.HandleRecordInbound(new ChannelBotRecordInboundCommand { RegistrationId = "reg-1", ObservedAtUtc = second });
        _agent.State.Registrations.Single(r => r.Id == "reg-1").LastInboundAtUtc.Should().Be(first);
    }

    [Fact]
    public async Task HandleRecordInbound_NoopForUnknownRegistration()
    {
        await _agent.HandleRecordInbound(new ChannelBotRecordInboundCommand
        {
            RegistrationId = "does-not-exist",
            ObservedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        _agent.State.Registrations.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleRegister_PersistsTelegramRelayRegistration()
    {
        var command = NewRegistration(
            registrationId: "reg-telegram",
            platform: "telegram",
            apiKeyId: "key-tg-1");
        command.NyxChannelBotId = "bot-tg-1";
        command.NyxConversationRouteId = "route-tg-1";
        command.WebhookUrl = "https://nyx.example.com/api/v1/webhooks/channel/telegram/bot-tg-1";
        await _agent.HandleRegister(command);

        _agent.State.Registrations.Should().ContainSingle();
        var entry = _agent.State.Registrations[0];
        entry.Id.Should().Be("reg-telegram");
        entry.Platform.Should().Be("telegram");
        entry.NyxProviderSlug.Should().Be("api-telegram-bot");
        entry.ScopeId.Should().Be("scope-1");
        entry.WebhookUrl.Should().Contain("/api/v1/webhooks/channel/telegram/");
        entry.NyxChannelBotId.Should().Be("bot-tg-1");
        entry.NyxAgentApiKeyId.Should().Be("key-tg-1");
        entry.NyxConversationRouteId.Should().Be("route-tg-1");
        entry.Tombstoned.Should().BeFalse();
    }

    [Fact]
    public async Task HandleRegister_IgnoresUnsupportedPlatforms()
    {
        await _agent.HandleRegister(new ChannelBotRegisterCommand
        {
            Platform = "discord",
            NyxProviderSlug = "api-discord-bot",
            ScopeId = "scope-1",
            RequestedId = "reg-discord",
        });

        _agent.State.Registrations.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleRegister_RejectsLarkRegistrationWithoutScopeId_AndPersistsRejectionEvent()
    {
        var beforeVersion = _agent.EventSourcing!.CurrentVersion;

        var command = NewRegistration();
        command.ScopeId = string.Empty;
        await _agent.HandleRegister(command);

        // Audit event recorded for the contract break (issue #391); the
        // registration set stays empty because the rejection is a no-op
        // transition.
        _agent.EventSourcing!.CurrentVersion.Should().Be(beforeVersion + 1);
        _agent.State.Registrations.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleUnregister_TombstonesEntry()
    {
        await _agent.HandleRegister(NewRegistration());

        await _agent.HandleUnregister(new ChannelBotUnregisterCommand
        {
            RegistrationId = "reg-1",
        });

        _agent.State.Registrations.Should().ContainSingle();
        _agent.State.Registrations[0].Tombstoned.Should().BeTrue();
        _agent.State.Registrations[0].TombstoneStateVersion.Should().BePositive();
    }

    [Fact]
    public async Task HandleCompactTombstones_RemovesWatermarkPassedEntries()
    {
        await _agent.HandleRegister(NewRegistration());

        await _agent.HandleUnregister(new ChannelBotUnregisterCommand
        {
            RegistrationId = "reg-1",
        });

        var safeStateVersion = _agent.State.Registrations[0].TombstoneStateVersion;
        await _agent.HandleCompactTombstones(new ChannelBotCompactTombstonesCommand
        {
            SafeStateVersion = safeStateVersion,
        });

        _agent.State.Registrations.Should().BeEmpty();
    }

    [Fact]
    public async Task ReplayScopeIdRepairedEvent_PreservesCreatedAt_WhenRewritingScope()
    {
        await _agent.HandleRegister(NewRegistration(scopeId: "scope-original"));

        var originalCreatedAt = _agent.State.Registrations[0].CreatedAt;
        originalCreatedAt.Should().NotBeNull();
        await AppendScopeIdRepairedEventAsync("reg-1", "scope-original", "scope-repaired");

        var replayed = CreateAgent();
        await replayed.ActivateAsync();

        var entry = replayed.State.Registrations.Should().ContainSingle().Subject;
        entry.ScopeId.Should().Be("scope-repaired");
        entry.CreatedAt.Should().Be(originalCreatedAt);
        entry.Tombstoned.Should().BeFalse();
    }

    [Fact]
    public async Task ReplayScopeIdRepairedEvent_IgnoresTombstonedRegistration()
    {
        await _agent.HandleRegister(NewRegistration());
        await _agent.HandleUnregister(new ChannelBotUnregisterCommand
        {
            RegistrationId = "reg-1",
        });
        await AppendScopeIdRepairedEventAsync("reg-1", "scope-1", "scope-2");

        var replayed = CreateAgent();
        await replayed.ActivateAsync();

        replayed.State.Registrations[0].ScopeId.Should().Be("scope-1");
        replayed.State.Registrations[0].Tombstoned.Should().BeTrue();
    }

    [Fact]
    public async Task ReplayScopeIdRepairedEvent_IgnoresMissingRegistration()
    {
        await AppendScopeIdRepairedEventAsync("reg-missing", string.Empty, "scope-1");

        var replayed = CreateAgent();
        await replayed.ActivateAsync();

        replayed.State.Registrations.Should().BeEmpty();
    }

    [Fact]
    public void ScopeIdRepairedEvent_DoesNotExposeLiveRepairCommandHandler()
    {
        typeof(ChannelBotRegistrationGAgent)
            .GetMethods()
            .Select(static method => method.Name)
            .Should()
            .NotContain("HandleRepairScopeId");
    }

    private async Task AppendCommittedEventAsync(IMessage payload)
    {
        var eventStore = _serviceProvider.GetRequiredService<IEventStore>();
        var currentVersion = _agent.EventSourcing?.CurrentVersion ?? 0;
        await eventStore.AppendAsync(
            ChannelBotRegistrationGAgent.WellKnownId,
            [
                new StateEvent
                {
                    AgentId = ChannelBotRegistrationGAgent.WellKnownId,
                    EventId = Guid.NewGuid().ToString("N"),
                    EventType = payload.Descriptor.FullName,
                    EventData = Any.Pack(payload),
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    Version = currentVersion + 1,
                },
            ],
            currentVersion,
            CancellationToken.None);
    }

    private async Task AppendScopeIdRepairedEventAsync(
        string registrationId,
        string previousScopeId,
        string scopeId)
    {
        var eventStore = _serviceProvider.GetRequiredService<IEventStore>();
        await eventStore.AppendAsync(
            ChannelBotRegistrationGAgent.WellKnownId,
            [
                new StateEvent
                {
                    AgentId = ChannelBotRegistrationGAgent.WellKnownId,
                    EventId = Guid.NewGuid().ToString("N"),
                    EventType = ChannelBotScopeIdRepairedEvent.Descriptor.FullName,
                    EventData = Any.Pack(new ChannelBotScopeIdRepairedEvent
                    {
                        RegistrationId = registrationId,
                        PreviousScopeId = previousScopeId,
                        ScopeId = scopeId,
                        RepairedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    }),
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    Version = _agent.EventSourcing!.CurrentVersion + 1,
                },
            ],
            _agent.EventSourcing!.CurrentVersion,
            CancellationToken.None);
    }

    private async Task<T> LastCommittedPayloadAsync<T>() where T : IMessage<T>, new()
    {
        var eventStore = _serviceProvider.GetRequiredService<IEventStore>();
        var events = await eventStore.GetEventsAsync(ChannelBotRegistrationGAgent.WellKnownId);
        events.Should().NotBeEmpty();
        return events[^1].EventData.Unpack<T>();
    }

    private sealed class NoopCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                0,
                RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                0,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) => Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class InMemoryEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _events = new(StringComparer.Ordinal);

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (!_events.TryGetValue(agentId, out var stream))
            {
                stream = [];
                _events[agentId] = stream;
            }

            var currentVersion = stream.Count == 0 ? 0 : stream[^1].Version;
            if (currentVersion != expectedVersion)
                throw new InvalidOperationException(
                    $"Optimistic concurrency conflict: expected {expectedVersion}, actual {currentVersion}");

            var appended = events.Select(x => x.Clone()).ToList();
            stream.AddRange(appended);
            var latest = stream.Count == 0 ? 0 : stream[^1].Version;
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = latest,
                CommittedEvents = { appended.Select(x => x.Clone()) },
            });
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
                return Task.FromResult<IReadOnlyList<StateEvent>>([]);

            IReadOnlyList<StateEvent> result = fromVersion.HasValue
                ? stream.Where(x => x.Version > fromVersion.Value).Select(x => x.Clone()).ToList()
                : stream.Select(x => x.Clone()).ToList();
            return Task.FromResult(result);
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream) || stream.Count == 0)
                return Task.FromResult(0L);

            return Task.FromResult(stream[^1].Version);
        }

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (toVersion <= 0 || !_events.TryGetValue(agentId, out var stream))
                return Task.FromResult(0L);

            var before = stream.Count;
            stream.RemoveAll(x => x.Version <= toVersion);
            return Task.FromResult((long)(before - stream.Count));
        }
    }
}
