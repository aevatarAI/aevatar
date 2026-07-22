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

    private static Aevatar.Foundation.Abstractions.Credentials.SecretReference TestDeliverySecretReference(string registrationId) =>
        new()
        {
            Ref = $"sec_delivery_{registrationId}",
            Purpose = Aevatar.Foundation.Abstractions.Credentials.CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey,
            OwnerScopeKey = "scope-x",
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

    private static ChannelBotRegisterCommand HistoricalRegistration(
        string registrationId = "reg-alpha",
        string platform = "lark") =>
        new()
        {
            RequestedId = registrationId,
            Platform = platform,
            ScopeId = "scope-alpha",
            NyxProviderSlug = platform == "lark" ? "api-lark-bot" : $"api-{platform}-bot",
            WebhookUrl = $"https://nyx.example/api/v1/webhooks/channel/{platform}/bot-alpha",
            NyxChannelBotId = "bot-alpha",
            NyxAgentApiKeyId = "key-old-alpha",
            NyxConversationRouteId = "route-alpha",
            DefaultSkillName = "team-entry-alpha",
        };

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
        await _agent.HandleRegister(new ChannelBotRegisterCommand
        {
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            ScopeId = "scope-1",
            WebhookUrl = "https://nyx.example.com/api/v1/webhooks/channel/lark/bot-1",
            RequestedId = "reg-1",
            NyxChannelBotId = "bot-1",
            NyxAgentApiKeyId = "key-1",
            NyxConversationRouteId = "route-1",
            WorkflowResultDeliveryCredential = TestDeliverySecretReference("reg-1"),
        });

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
        entry.Tombstoned.Should().BeFalse();
        entry.DefaultSkillName.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleRegister_PersistsCanonicalDefaultSkillName()
    {
        await _agent.HandleRegister(new ChannelBotRegisterCommand
        {
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            ScopeId = "scope-1",
            RequestedId = "reg-bound",
            NyxChannelBotId = "bot-1",
            NyxAgentApiKeyId = "key-1",
            // Leading trigger token and mixed case must normalize to the parser's
            // canonical skill-name form so inbound routing compares 1:1.
            DefaultSkillName = " /WhatsApp-Reply-Draft ",
        });

        _agent.State.Registrations.Single(r => r.Id == "reg-bound")
            .DefaultSkillName.Should().Be("whatsapp-reply-draft");
    }

    [Fact]
    public async Task HandleWorkflowResultDeliveryRepair_RequestPrepareComplete_PromotesCredentialAndPreservesRegistration()
    {
        await _agent.HandleRegister(HistoricalRegistration());
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
        await _agent.HandleRegister(HistoricalRegistration());
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
        await _agent.HandleRegister(HistoricalRegistration());

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
        await _agent.HandleRegister(HistoricalRegistration());
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
        await _agent.HandleRegister(HistoricalRegistration("reg-telegram", "telegram"));

        await _agent.HandleWorkflowResultDeliveryRepairRequest(RepairRequest("reg-telegram"));

        _agent.State.Registrations.Single().WorkflowResultDeliveryRepair.Should().BeNull();
        (await LastCommittedPayloadAsync<ChannelBotWorkflowResultDeliveryRepairRejectedEvent>())
            .Reason.Should().Be(ChannelWorkflowResultDeliveryRepairFailureReason.UnsupportedPlatform);

        await _agent.HandleRegister(HistoricalRegistration());
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
        await _agent.HandleRegister(new ChannelBotRegisterCommand
        {
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            ScopeId = "scope-1",
            RequestedId = "reg-1",
            NyxChannelBotId = "bot-1",
            NyxAgentApiKeyId = "key-1",
        });

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
        await _agent.HandleRegister(new ChannelBotRegisterCommand
        {
            Platform = "telegram",
            NyxProviderSlug = "api-telegram-bot",
            ScopeId = "scope-1",
            WebhookUrl = "https://nyx.example.com/api/v1/webhooks/channel/telegram/bot-tg-1",
            RequestedId = "reg-telegram",
            NyxChannelBotId = "bot-tg-1",
            NyxAgentApiKeyId = "key-tg-1",
            NyxConversationRouteId = "route-tg-1",
        });

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

        await _agent.HandleRegister(new ChannelBotRegisterCommand
        {
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            RequestedId = "reg-1",
            NyxAgentApiKeyId = "key-1",
        });

        // Audit event recorded for the contract break (issue #391); the
        // registration set stays empty because the rejection is a no-op
        // transition.
        _agent.EventSourcing!.CurrentVersion.Should().Be(beforeVersion + 1);
        _agent.State.Registrations.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleUnregister_TombstonesEntry()
    {
        await _agent.HandleRegister(new ChannelBotRegisterCommand
        {
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            ScopeId = "scope-1",
            RequestedId = "reg-1",
        });

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
        await _agent.HandleRegister(new ChannelBotRegisterCommand
        {
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            ScopeId = "scope-1",
            RequestedId = "reg-1",
        });

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
        await _agent.HandleRegister(new ChannelBotRegisterCommand
        {
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            ScopeId = "scope-original",
            RequestedId = "reg-1",
            NyxAgentApiKeyId = "key-1",
        });

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
        await _agent.HandleRegister(new ChannelBotRegisterCommand
        {
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            ScopeId = "scope-1",
            RequestedId = "reg-1",
        });
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
