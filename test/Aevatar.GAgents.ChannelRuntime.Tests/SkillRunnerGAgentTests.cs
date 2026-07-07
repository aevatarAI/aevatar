using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Scheduled;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class SkillRunnerGAgentTests : IAsyncLifetime
{
    private InMemoryEventStore _store = null!;
    private ServiceProvider _serviceProvider = null!;
    private SkillRunnerGAgent _agent = null!;

    public async Task InitializeAsync()
    {
        _store = new InMemoryEventStore();
        _serviceProvider = BuildServiceProvider(_store);
        _agent = CreateAgent("skill-runner-test");
        await _agent.ActivateAsync();
    }

    public Task DisposeAsync()
    {
        _serviceProvider.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task HandleInitializeAsync_WhenSamplingFieldsAreOmitted_ShouldKeepThemUnset()
    {
        await _agent.HandleInitializeAsync(CreateInitializeCommand());

        var persisted = await _store.GetEventsAsync("skill-runner-test");
        var initialized = persisted.Should().ContainSingle().Subject.EventData.Unpack<SkillRunnerInitializedEvent>();
        initialized.HasTemperature.Should().BeFalse();
        initialized.HasMaxTokens.Should().BeFalse();

        _agent.State.HasTemperature.Should().BeFalse();
        _agent.State.HasMaxTokens.Should().BeFalse();
        _agent.State.MaxToolRounds.Should().Be(SkillRunnerDefaults.DefaultMaxToolRounds);
        _agent.State.MaxHistoryMessages.Should().Be(SkillRunnerDefaults.DefaultMaxHistoryMessages);
        _agent.EffectiveConfig.Temperature.Should().BeNull();
        _agent.EffectiveConfig.MaxTokens.Should().BeNull();
    }

    [Fact]
    public async Task HandleInitializeAsync_WhenTemperatureIsExplicitZero_ShouldPreserveIt()
    {
        var command = CreateInitializeCommand();
        command.Temperature = 0;

        await _agent.HandleInitializeAsync(command);

        var persisted = await _store.GetEventsAsync("skill-runner-test");
        var initialized = persisted.Should().ContainSingle().Subject.EventData.Unpack<SkillRunnerInitializedEvent>();
        initialized.HasTemperature.Should().BeTrue();
        initialized.Temperature.Should().Be(0);

        _agent.State.HasTemperature.Should().BeTrue();
        _agent.State.Temperature.Should().Be(0);
        _agent.EffectiveConfig.Temperature.Should().Be(0);
    }

    [Fact]
    public async Task HandleInitializeAsync_WhenMaxTokensIsExplicitZero_ShouldPreserveStateAndSuppressEffectiveConfig()
    {
        var command = CreateInitializeCommand();
        command.MaxTokens = 0;

        await _agent.HandleInitializeAsync(command);

        var persisted = await _store.GetEventsAsync("skill-runner-test");
        var initialized = persisted.Should().ContainSingle().Subject.EventData.Unpack<SkillRunnerInitializedEvent>();
        initialized.HasMaxTokens.Should().BeTrue();
        initialized.MaxTokens.Should().Be(0);

        _agent.State.HasMaxTokens.Should().BeTrue();
        _agent.State.MaxTokens.Should().Be(0);
        _agent.EffectiveConfig.MaxTokens.Should().BeNull();
    }

    [Fact]
    public async Task HandleInitializeAsync_ShouldPersistOutputFormatIntoOutboundConfig()
    {
        var command = CreateInitializeCommand();
        command.OutputFormat = SkillRunnerOutputFormat.FeishuDoc;

        await _agent.HandleInitializeAsync(command);

        var persisted = await _store.GetEventsAsync("skill-runner-test");
        var initialized = persisted.Should().ContainSingle().Subject.EventData.Unpack<SkillRunnerInitializedEvent>();
        initialized.OutboundConfig.OutputFormat.Should().Be(SkillRunnerOutputFormat.FeishuDoc);
        _agent.State.OutboundConfig.OutputFormat.Should().Be(SkillRunnerOutputFormat.FeishuDoc);
    }

    [Fact]
    public async Task HandleInitializeAsync_WithSkillRefOnly_ShouldPersistTypedReferenceAndNoInlineContent()
    {
        var command = CreateInitializeCommand();
        command.SkillContent = string.Empty;
        command.SkillRef = new SkillRunnerSkillReference
        {
            Name = "daily-report",
            Source = SkillRunnerSkillSource.Ornn,
        };

        await _agent.HandleInitializeAsync(command);

        var persisted = await _store.GetEventsAsync("skill-runner-test");
        var initialized = persisted.Should().ContainSingle().Subject.EventData.Unpack<SkillRunnerInitializedEvent>();
        initialized.SkillRef.Should().NotBeNull();
        initialized.SkillRef.Name.Should().Be("daily-report");
        initialized.SkillRef.Source.Should().Be(SkillRunnerSkillSource.Ornn);
        initialized.SkillContent.Should().BeEmpty();
        _agent.State.SkillRef.Name.Should().Be("daily-report");
        _agent.State.SkillContent.Should().BeEmpty();
        _agent.EffectiveConfig.SystemPrompt.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleInitializeAsync_WithLegacyInlineOnly_ShouldStillInitialize()
    {
        await _agent.HandleInitializeAsync(CreateInitializeCommand());

        _agent.State.SkillRef.Should().BeNull();
        _agent.State.SkillContent.Should().Be("You are a summary report runner.");
        _agent.EffectiveConfig.SystemPrompt.Should().Be("You are a summary report runner.");
    }

    [Fact]
    public async Task HandleInitializeAsync_WithSkillRefAndInlineContentWithoutFallback_ShouldReject()
    {
        var command = CreateInitializeCommand();
        command.SkillRef = new SkillRunnerSkillReference
        {
            Name = "daily-report",
            Source = SkillRunnerSkillSource.Ornn,
        };

        await _agent.HandleInitializeAsync(command);

        var persisted = await _store.GetEventsAsync("skill-runner-test");
        persisted.Should().BeEmpty();
        _agent.State.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task HandleInitializeAsync_WithSkillRefAndInlineFallback_ShouldPersistBoth()
    {
        var command = CreateInitializeCommand();
        command.SkillRef = new SkillRunnerSkillReference
        {
            Name = "daily-report",
            Source = SkillRunnerSkillSource.Ornn,
            AllowInlineFallback = true,
        };

        await _agent.HandleInitializeAsync(command);

        _agent.State.SkillRef.Name.Should().Be("daily-report");
        _agent.State.SkillRef.AllowInlineFallback.Should().BeTrue();
        _agent.State.SkillContent.Should().Be("You are a summary report runner.");
        _agent.EffectiveConfig.SystemPrompt.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleInitializeAsync_NonFetchTemplate_DoesNotDeriveProxySuccessFromTemplate()
    {
        // The legacy default applies only to known fetch-and-summarize templates. Skills
        // that don't depend on tool data (future pure-LLM transformations) must not be
        // falsely failed when they legitimately fan out zero nyxid_proxy calls.
        var command = CreateInitializeCommand();
        command.RequiresNyxidProxySuccess = false;
        command.TemplateName = "future_pure_llm_template";

        await _agent.HandleInitializeAsync(command);

        _agent.State.RequiresNyxidProxySuccess.Should().BeFalse();
    }

    [Fact]
    public async Task TryCreateStreamingSink_WhenRequiresNyxidProxySuccess_ReturnsNull()
    {
        // PR #569 review (codex P1 on SkillRunnerGAgent.cs:351): when the run is gated by
        // EnsureToolStatusAllowsCompletion, streaming each delta to Lark would post the
        // hallucinated text live before the guard ran, then repost it on each retry.
        // TryCreateStreamingSink must short-circuit so chunked dispatch (which only fires
        // AFTER the guard) is the only path that reaches Lark for fanout-gated runs.
        AttachNyxIdApiClient(_agent, new RecordingHandler("""{"code":0,"msg":"success","data":{"message_id":"om_success"}}"""));
        var command = CreateInitializeCommand();
        command.RequiresNyxidProxySuccess = true;
        await _agent.HandleInitializeAsync(command);
        _agent.State.RequiresNyxidProxySuccess.Should().BeTrue();

        var method = typeof(SkillRunnerGAgent).GetMethod(
            "TryCreateStreamingSinkAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(CancellationToken)],
            modifiers: null);
        method.Should().NotBeNull();
        var task = (Task<SkillRunnerStreamingReplySink?>)method!.Invoke(_agent, [CancellationToken.None])!;
        var sink = await task;

        sink.Should().BeNull();
    }

    [Fact]
    public async Task HandleInitializeAsync_DispatchesMembershipOnly_AndRunnerCommittedStateFeedsExecutionProjection()
    {
        // Refactor (iter1/cluster-001):
        //   Old pattern: the runner dispatched UserAgentCatalogExecutionUpdateCommand after membership upsert.
        //   New principle: runner committed state is sufficient for the catalog projector to materialize execution fields.
        var captured = new List<EventEnvelope>();
        var scheduler = new RecordingCallbackScheduler();
        var runtime = Substitute.For<IActorRuntime>();
        runtime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        var dispatch = Substitute.For<IActorDispatchPort>();
        dispatch.DispatchAsync(
                UserAgentCatalogGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(captured.Add),
                Arg.Any<CancellationToken>())
            .Returns(ActorDispatchPortTestSupport.AcceptAsync);

        using var provider = BuildServiceProvider(
            new InMemoryEventStore(),
            services =>
            {
                services.AddSingleton(runtime);
                services.AddSingleton(dispatch);
                services.AddSingleton(scheduler);
            });
        var agent = CreateAgent("skill-runner-projection-regression", provider);
        await agent.ActivateAsync();
        var init = CreateInitializeCommand();
        init.ScheduleCron = "0 9 * * *";
        init.ScheduleTimezone = "UTC";

        await agent.HandleInitializeAsync(init);

        captured.Should().ContainSingle();
        captured[0].Payload.Is(UserAgentCatalogUpsertCommand.Descriptor).Should().BeTrue();
        var persisted = await provider.GetRequiredService<IEventStore>().GetEventsAsync("skill-runner-projection-regression");
        persisted.Should().ContainSingle();
        scheduler.Timeouts.Should().BeEmpty();

        var runnerState = agent.State.Clone();
        var writeDispatcher = new RecordingExecutionWriteDispatcher();
        var projector = new SkillRunnerExecutionProjector(
            writeDispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 4, 14, 10, 0, 0, TimeSpan.Zero)));

        await projector.ProjectAsync(
            new UserAgentCatalogMaterializationContext
            {
                RootActorId = "skill-runner-projection-regression",
                ProjectionKind = UserAgentCatalogProjectionBootstrapActivator.ProjectionKind,
            },
            new EventEnvelope
            {
                Id = "runner-state-2",
                Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 4, 14, 10, 0, 0, TimeSpan.Zero)),
                Route = EnvelopeRouteSemantics.CreateObserverPublication("skill-runner-projection-regression"),
                Payload = Google.Protobuf.WellKnownTypes.Any.Pack(new CommittedStateEventPublished
                {
                    StateEvent = persisted[^1],
                    StateRoot = Google.Protobuf.WellKnownTypes.Any.Pack(runnerState),
                }),
            },
            CancellationToken.None);

        writeDispatcher.Upserts.Should().ContainSingle();
        var doc = writeDispatcher.Upserts[0];
        doc.Id.Should().Be("skill-runner-projection-regression");
        doc.ActorId.Should().Be("skill-runner-projection-regression");
        doc.Status.Should().Be(SkillRunnerDefaults.StatusRunning);
        doc.NextRunAtUtc.Should().BeNull();
        doc.StateVersion.Should().Be(1);
    }

    [Fact]
    public async Task HandleInitializeAsync_WhenOneShotReminder_ShouldScheduleFixedRunAt()
    {
        var scheduler = new RecordingCallbackScheduler();
        using var provider = BuildServiceProvider(
            new InMemoryEventStore(),
            services => services.AddSingleton<IActorRuntimeCallbackScheduler>(scheduler));
        var agent = CreateAgent("skill-runner-one-shot-schedule", provider);
        await agent.ActivateAsync();
        var runAt = DateTimeOffset.UtcNow.AddMinutes(30);
        var command = CreateOneShotInitializeCommand(runAt);

        await agent.HandleInitializeAsync(command);

        scheduler.Timeouts.Should().ContainSingle();
        scheduler.Timeouts[0].CallbackId.Should().Be(SkillRunnerDefaults.TriggerCallbackId);
        scheduler.Timeouts[0].TriggerEnvelope.Payload.Unpack<TriggerSkillRunnerExecutionCommand>().Reason
            .Should().Be(SkillRunnerDefaults.OneShotTriggerReason);
        agent.State.ScheduleMode.Should().Be(SkillRunnerScheduleMode.OneShot);
        agent.State.OneShotRunAt.ToDateTimeOffset().Should().Be(runAt);
        agent.State.NextRunAt.ToDateTimeOffset().Should().Be(runAt);
    }

    [Fact]
    public async Task HandleTriggerAsync_WhenOneShotReminderCompletes_ShouldDeliverAndRetire()
    {
        var dispatcher = new RecordingLarkOutboundDispatcher();
        using var provider = BuildServiceProvider(
            new InMemoryEventStore(),
            services =>
            {
                services.AddSingleton<ILarkOutboundDispatcher>(dispatcher);
                services.AddSingleton<IActorRuntimeCallbackScheduler>(new RecordingCallbackScheduler());
            });
        var agent = CreateAgent("skill-runner-one-shot-fire", provider);
        await agent.ActivateAsync();
        AttachNyxIdApiClient(agent, new RecordingHandler("""{"code":0,"msg":"success","data":{"message_id":"om_one_shot"}}"""));
        await agent.HandleInitializeAsync(CreateOneShotInitializeCommand(DateTimeOffset.UtcNow.AddMinutes(30)));

        await agent.HandleTriggerAsync(new TriggerSkillRunnerExecutionCommand
        {
            Reason = SkillRunnerDefaults.OneShotTriggerReason,
        });

        dispatcher.Requests.Should().ContainSingle();
        dispatcher.Requests[0].ContentJson.Should().Contain("Submit the report");
        agent.State.Enabled.Should().BeFalse();
        agent.State.NextRunAt.Should().BeNull();
        agent.State.RetiredAt.Should().NotBeNull();
        agent.State.RetirementReason.Should().Be(SkillRunnerDefaults.OneShotRetirementReasonCompleted);

        var persisted = await provider.GetRequiredService<IEventStore>().GetEventsAsync("skill-runner-one-shot-fire");
        persisted.Should().Contain(e => e.EventData.Is(SkillRunnerOneShotRetiredEvent.Descriptor));
    }

    [Fact]
    public async Task ProjectAsync_WhenOneShotRetired_ShouldExposeCompletedReadModel()
    {
        var state = new SkillRunnerState
        {
            SkillName = SkillRunnerDefaults.OneShotSkillName,
            TemplateName = "Reminder",
            ScopeId = "scope-1",
            ScheduleMode = SkillRunnerScheduleMode.OneShot,
            OneShotRunAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 11, 10, 30, 0, TimeSpan.Zero)),
            LastRunAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 11, 10, 30, 0, TimeSpan.Zero)),
            RetiredAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 11, 10, 31, 0, TimeSpan.Zero)),
            RetirementReason = SkillRunnerDefaults.OneShotRetirementReasonCompleted,
            Enabled = false,
        };
        var stateEvent = new StateEvent
        {
            Version = 4,
            EventId = "evt-retired",
            EventData = Any.Pack(new SkillRunnerOneShotRetiredEvent
            {
                RetiredAt = state.RetiredAt,
                Reason = state.RetirementReason,
            }),
        };
        var writeDispatcher = new RecordingExecutionWriteDispatcher();
        var projector = new SkillRunnerExecutionProjector(
            writeDispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 6, 11, 10, 31, 0, TimeSpan.Zero)));

        await projector.ProjectAsync(
            new UserAgentCatalogMaterializationContext
            {
                RootActorId = "skill-runner-one-shot-readmodel",
                ProjectionKind = UserAgentCatalogProjectionBootstrapActivator.ProjectionKind,
            },
            new EventEnvelope
            {
                Id = "runner-state-4",
                Timestamp = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 11, 10, 31, 0, TimeSpan.Zero)),
                Route = EnvelopeRouteSemantics.CreateObserverPublication("skill-runner-one-shot-readmodel"),
                Payload = Any.Pack(new CommittedStateEventPublished
                {
                    StateEvent = stateEvent,
                    StateRoot = Any.Pack(state),
                }),
            },
            CancellationToken.None);

        var doc = writeDispatcher.Upserts.Should().ContainSingle().Subject;
        doc.Status.Should().Be(SkillRunnerDefaults.StatusCompleted);
        doc.ScheduleMode.Should().Be(SkillRunnerScheduleMode.OneShot);
        doc.RunAtUtc.Should().Be(state.OneShotRunAt);
        doc.RetiredAtUtc.Should().Be(state.RetiredAt);
        doc.RetirementReason.Should().Be(SkillRunnerDefaults.OneShotRetirementReasonCompleted);
    }

    [Fact]
    public async Task HandleTriggerAsync_WhenDisabled_PersistsRunnerOwnedRejectedEvent()
    {
        await _agent.HandleInitializeAsync(CreateInitializeCommand());
        await _agent.HandleDisableAsync(new DisableSkillRunnerCommand { Reason = "test" });

        await _agent.HandleTriggerAsync(new TriggerSkillRunnerExecutionCommand { Reason = "run_agent" });

        var persisted = await _store.GetEventsAsync("skill-runner-test");
        var rejected = persisted
            .Select(x => x.EventData)
            .Where(x => x.Is(SkillRunnerExecutionRejectedEvent.Descriptor))
            .Select(x => x.Unpack<SkillRunnerExecutionRejectedEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        rejected.Reason.Should().Be(SkillRunnerDefaults.RejectionReasonRunnerDisabled);

        _agent.State.Enabled.Should().BeFalse();
        _agent.State.LastError.Should().Be(SkillRunnerDefaults.RejectionReasonRunnerDisabled);
        _agent.State.ErrorCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleTriggerAsync_SameCronOccurrenceDeliveredTwice_ShouldExecuteAndSendOnlyOnce()
    {
        var provider = new StubStreamingProviderFactory("first output", "second output");
        var agent = CreateAgent("skill-runner-cron-duplicate", providerFactory: provider);
        await agent.ActivateAsync();
        await agent.HandleInitializeAsync(CreateTextOutputInitializeCommand());
        var handler = new RecordingHandler("""{"code":0,"msg":"success","data":{"message_id":"om_cron"}}""");
        AttachNyxIdApiClient(agent, handler);
        var occurrenceKey = "schedule:runner-1:fire:2026-06-16T01:00:00.0000000+00:00";

        await DispatchCronFireAsync(agent, "schedule-envelope-1", occurrenceKey);
        var outboundRequestsAfterFirstDelivery = handler.Requests.Count;

        await DispatchCronFireAsync(agent, "schedule-envelope-redelivery", occurrenceKey);

        provider.Requests.Should().ContainSingle();
        handler.Requests.Should().HaveCount(outboundRequestsAfterFirstDelivery);
        var persisted = await _store.GetEventsAsync("skill-runner-cron-duplicate");
        persisted.Select(x => x.EventData)
            .Count(x => x.Is(SkillRunnerExecutionCompletedEvent.Descriptor))
            .Should()
            .Be(1);
        var duplicate = persisted.Select(x => x.EventData)
            .Where(x => x.Is(SkillRunnerCronOccurrenceDuplicateIgnoredEvent.Descriptor))
            .Select(x => x.Unpack<SkillRunnerCronOccurrenceDuplicateIgnoredEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        duplicate.CronOccurrenceKey.Should().Be(occurrenceKey);
        agent.State.IsCronOccurrenceTerminal(occurrenceKey).Should().BeTrue();
    }

    [Fact]
    public async Task HandleTriggerAsync_CronOccurrenceWithoutBaggage_ShouldUseEnvelopeIdForRedeliveryDedup()
    {
        var provider = new StubStreamingProviderFactory("first output", "second output");
        var agent = CreateAgent("skill-runner-cron-envelope-id", providerFactory: provider);
        await agent.ActivateAsync();
        await agent.HandleInitializeAsync(CreateTextOutputInitializeCommand());
        AttachNyxIdApiClient(agent, new RecordingHandler("""{"code":0,"msg":"success","data":{"message_id":"om_cron"}}"""));
        var envelopeId = "schedule:runner-1:fire:2026-06-16T02:00:00.0000000+00:00";

        await DispatchCronFireAsync(agent, envelopeId, cronOccurrenceKey: null);
        await DispatchCronFireAsync(agent, envelopeId, cronOccurrenceKey: null);

        provider.Requests.Should().ContainSingle();
        var completed = await ReadSingleCompletedEventAsync(_store, "skill-runner-cron-envelope-id");
        completed.CronOccurrenceKey.Should().Be(envelopeId);
        agent.State.IsCronOccurrenceTerminal(envelopeId).Should().BeTrue();
        var persisted = await _store.GetEventsAsync("skill-runner-cron-envelope-id");
        persisted.Select(x => x.EventData)
            .Count(x => x.Is(SkillRunnerCronOccurrenceDuplicateIgnoredEvent.Descriptor))
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task HandleTriggerAsync_CronRetryAttempt_ShouldBypassDuplicateSkipAndUseSameOccurrenceKey()
    {
        var scheduler = new RecordingCallbackScheduler();
        var provider = new StubStreamingProviderFactory(
            new StubStreamingTurn(new InvalidOperationException("transient")),
            new StubStreamingTurn(["retry output"]));
        using var serviceProvider = BuildServiceProvider(
            new InMemoryEventStore(),
            services => services.AddSingleton<IActorRuntimeCallbackScheduler>(scheduler));
        var agent = CreateAgent(
            "skill-runner-cron-retry",
            serviceProvider,
            providerFactory: provider);
        await agent.ActivateAsync();
        await agent.HandleInitializeAsync(CreateTextOutputInitializeCommand());
        AttachNyxIdApiClient(agent, new RecordingHandler("""{"code":0,"msg":"success","data":{"message_id":"om_retry"}}"""));
        var occurrenceKey = "schedule:runner-1:fire:2026-06-16T03:00:00.0000000+00:00";

        await DispatchCronFireAsync(agent, "first-attempt-envelope", occurrenceKey);

        scheduler.Timeouts.Should().ContainSingle();
        var retryEnvelope = scheduler.Timeouts[0].TriggerEnvelope;
        retryEnvelope.Propagation.Baggage[ScheduledDispatchMetadataKeys.IdempotencyKey].Should().Be(occurrenceKey);

        await agent.HandleEventAsync(retryEnvelope);

        provider.Requests.Should().HaveCount(2);
        var store = serviceProvider.GetRequiredService<IEventStore>() as InMemoryEventStore
            ?? throw new InvalidOperationException("test store missing");
        var completed = await ReadSingleCompletedEventAsync(store, "skill-runner-cron-retry");
        completed.CronOccurrenceKey.Should().Be(occurrenceKey);
        agent.State.IsCronOccurrenceTerminal(occurrenceKey).Should().BeTrue();
    }

    [Fact]
    public async Task HandleTriggerAsync_ExternalTriggerWithScheduleReason_ShouldNotUseCronOccurrenceDedup()
    {
        var provider = new StubStreamingProviderFactory("external output");
        var agent = CreateAgent("skill-runner-external-schedule-reason", providerFactory: provider);
        await agent.ActivateAsync();
        var initialize = CreateInitializeCommandWithExternalSource();
        initialize.OutputFormat = SkillRunnerOutputFormat.Text;
        initialize.OutboundConfig.OutputFormat = SkillRunnerOutputFormat.Text;
        await agent.HandleInitializeAsync(initialize);
        AttachNyxIdApiClient(agent, new RecordingHandler("""{"code":0,"msg":"success","data":{"message_id":"om_external"}}"""));
        var identity = CreateExternalIdentity("delivery-schedule-reason");
        await agent.HandleAdmitExternalTriggerAsync(new AdmitSkillRunnerExternalTriggerCommand { Identity = identity });

        await DispatchCronFireAsync(
            agent,
            "schedule-envelope-external",
            "schedule:runner-1:fire:2026-06-16T04:00:00.0000000+00:00",
            new TriggerSkillRunnerExecutionCommand
            {
                Reason = SkillRunnerDefaults.ScheduleTriggerReason,
                ExternalTriggerIdentity = identity.Clone(),
            });

        provider.Requests.Should().ContainSingle();
        var completed = await ReadSingleCompletedEventAsync(_store, "skill-runner-external-schedule-reason");
        completed.ExternalTriggerIdentity.DeliveryId.Should().Be(identity.DeliveryId);
        completed.CronOccurrenceKey.Should().BeEmpty();
        agent.State.RecentCronOccurrenceTerminals.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleTriggerAsync_OneShotReminder_ShouldNotUseCronOccurrenceDedup()
    {
        using var provider = BuildServiceProvider(
            new InMemoryEventStore(),
            services =>
            {
                services.AddSingleton<IActorRuntimeCallbackScheduler>(new RecordingCallbackScheduler());
            });
        var agent = CreateAgent("skill-runner-one-shot-not-cron", provider);
        await agent.ActivateAsync();
        AttachNyxIdApiClient(agent, new RecordingHandler("""{"code":0,"msg":"success","data":{"message_id":"om_one_shot"}}"""));
        await agent.HandleInitializeAsync(CreateOneShotInitializeCommand(DateTimeOffset.UtcNow.AddMinutes(30)));

        await DispatchCronFireAsync(
            agent,
            "one-shot-envelope",
            "schedule:runner-1:fire:2026-06-16T05:00:00.0000000+00:00",
            new TriggerSkillRunnerExecutionCommand
            {
                Reason = SkillRunnerDefaults.OneShotTriggerReason,
            });

        var completed = await ReadSingleCompletedEventAsync(
            provider.GetRequiredService<IEventStore>() as InMemoryEventStore
            ?? throw new InvalidOperationException("test store missing"),
            "skill-runner-one-shot-not-cron");
        completed.CronOccurrenceKey.Should().BeEmpty();
        agent.State.RecentCronOccurrenceTerminals.Should().BeEmpty();
        agent.State.RetiredAt.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleInitializeAsync_WithExternalTriggerSource_ShouldPersistSourceDeclaration()
    {
        var command = CreateInitializeCommand();
        command.ExternalTriggerSources.Add(new ExternalTriggerSource
        {
            SourceId = " webhook-main ",
            Kind = ExternalTriggerSourceKind.Webhook,
            Enabled = true,
            DisplayName = " Main webhook ",
        });

        await _agent.HandleInitializeAsync(command);

        var persisted = await _store.GetEventsAsync("skill-runner-test");
        var initialized = persisted
            .Select(x => x.EventData)
            .Where(x => x.Is(SkillRunnerInitializedEvent.Descriptor))
            .Select(x => x.Unpack<SkillRunnerInitializedEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        initialized.ExternalTriggerSources.Should().ContainSingle()
            .Which.SourceId.Should().Be("webhook-main");
        _agent.State.ExternalTriggerSources.Should().ContainSingle()
            .Which.DisplayName.Should().Be("Main webhook");
    }

    [Fact]
    public async Task HandleAdmitExternalTriggerAsync_FirstDelivery_ShouldPersistAdmittedThenDispatchRequested()
    {
        await _agent.HandleInitializeAsync(CreateInitializeCommandWithExternalSource());

        await _agent.HandleAdmitExternalTriggerAsync(new AdmitSkillRunnerExternalTriggerCommand
        {
            Identity = CreateExternalIdentity("delivery-1"),
        });

        var persisted = await _store.GetEventsAsync("skill-runner-test");
        var externalEvents = persisted
            .Select(x => x.EventData)
            .Where(x =>
                x.Is(SkillRunnerExternalTriggerAdmittedEvent.Descriptor) ||
                x.Is(SkillRunnerExternalTriggerDispatchRequestedEvent.Descriptor))
            .ToArray();
        externalEvents.Should().HaveCount(2);
        externalEvents[0].Is(SkillRunnerExternalTriggerAdmittedEvent.Descriptor).Should().BeTrue();
        externalEvents[1].Is(SkillRunnerExternalTriggerDispatchRequestedEvent.Descriptor).Should().BeTrue();
        var dispatchRequested = externalEvents[1].Unpack<SkillRunnerExternalTriggerDispatchRequestedEvent>();
        dispatchRequested.DispatchAttempt.Should().Be(1);
        dispatchRequested.Identity.DeliveryId.Should().Be("delivery-1");

        var record = _agent.State.FindExternalTriggerDelivery(CreateExternalIdentity("delivery-1"));
        record.Should().NotBeNull();
        record!.Status.Should().Be(SkillRunnerExternalTriggerDeliveryStatus.DispatchRequested);
    }

    [Theory]
    [InlineData(" ", "delivery-malformed-source")]
    [InlineData("webhook-main", " ")]
    public async Task HandleAdmitExternalTriggerAsync_MalformedIdentity_ShouldCommitRejectedWithoutDispatch(
        string sourceId,
        string deliveryId)
    {
        await _agent.HandleInitializeAsync(CreateInitializeCommandWithExternalSource());

        await _agent.HandleAdmitExternalTriggerAsync(new AdmitSkillRunnerExternalTriggerCommand
        {
            Identity = new SkillRunnerExternalTriggerIdentity
            {
                SourceId = sourceId,
                DeliveryId = deliveryId,
                AdmissionId = "admission-malformed",
                Kind = ExternalTriggerSourceKind.Webhook,
            },
        });

        var persisted = await _store.GetEventsAsync("skill-runner-test");
        var rejected = persisted
            .Select(x => x.EventData)
            .Where(x => x.Is(SkillRunnerExternalTriggerRejectedEvent.Descriptor))
            .Select(x => x.Unpack<SkillRunnerExternalTriggerRejectedEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        rejected.Reason.Should().Be(SkillRunnerDefaults.ExternalTriggerRejectedReasonMalformedDelivery);
        rejected.Identity.SourceId.Should().Be(sourceId.Trim());
        rejected.Identity.DeliveryId.Should().Be(deliveryId.Trim());
        persisted.Select(x => x.EventData)
            .Should()
            .NotContain(x => x.Is(SkillRunnerExternalTriggerDispatchRequestedEvent.Descriptor));
    }

    [Theory]
    [InlineData("missing-source", SkillRunnerDefaults.ExternalTriggerRejectedReasonUnknownSource)]
    [InlineData("disabled-source", SkillRunnerDefaults.ExternalTriggerRejectedReasonDisabledSource)]
    public async Task HandleAdmitExternalTriggerAsync_UnknownOrDisabledSource_ShouldCommitRejectedWithoutDispatch(
        string sourceId,
        string expectedReason)
    {
        var command = CreateInitializeCommandWithExternalSource();
        command.ExternalTriggerSources.Add(new ExternalTriggerSource
        {
            SourceId = "disabled-source",
            Kind = ExternalTriggerSourceKind.Webhook,
            Enabled = false,
        });
        await _agent.HandleInitializeAsync(command);

        await _agent.HandleAdmitExternalTriggerAsync(new AdmitSkillRunnerExternalTriggerCommand
        {
            Identity = CreateExternalIdentity("delivery-rejected", sourceId),
        });

        var persisted = await _store.GetEventsAsync("skill-runner-test");
        var rejected = persisted
            .Select(x => x.EventData)
            .Where(x => x.Is(SkillRunnerExternalTriggerRejectedEvent.Descriptor))
            .Select(x => x.Unpack<SkillRunnerExternalTriggerRejectedEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        rejected.Reason.Should().Be(expectedReason);
        persisted.Select(x => x.EventData)
            .Should()
            .NotContain(x => x.Is(SkillRunnerExternalTriggerDispatchRequestedEvent.Descriptor));
    }

    [Fact]
    public async Task HandleAdmitExternalTriggerAsync_DuplicateDelivery_ShouldCommitDuplicateIgnoredWithoutSecondDispatch()
    {
        await _agent.HandleInitializeAsync(CreateInitializeCommandWithExternalSource());
        var command = new AdmitSkillRunnerExternalTriggerCommand
        {
            Identity = CreateExternalIdentity("delivery-dup"),
        };

        await _agent.HandleAdmitExternalTriggerAsync(command);
        await _agent.HandleAdmitExternalTriggerAsync(command.Clone());

        var persisted = await _store.GetEventsAsync("skill-runner-test");
        persisted.Select(x => x.EventData)
            .Count(x => x.Is(SkillRunnerExternalTriggerDispatchRequestedEvent.Descriptor))
            .Should()
            .Be(1);
        persisted.Select(x => x.EventData)
            .Count(x => x.Is(SkillRunnerExternalTriggerDuplicateIgnoredEvent.Descriptor))
            .Should()
            .Be(1);
    }

    [Theory]
    [InlineData(SkillRunnerExternalTriggerDeliveryStatus.Completed)]
    [InlineData(SkillRunnerExternalTriggerDeliveryStatus.Failed)]
    [InlineData(SkillRunnerExternalTriggerDeliveryStatus.Rejected)]
    public async Task HandleTriggerAsync_TerminalExternalDelivery_ShouldCommitDuplicateIgnoredWithoutExecutingAgain(
        SkillRunnerExternalTriggerDeliveryStatus terminalStatus)
    {
        var actorId = $"skill-runner-external-terminal-{terminalStatus.ToString().ToLowerInvariant()}";
        var provider = terminalStatus == SkillRunnerExternalTriggerDeliveryStatus.Failed
            ? new StubStreamingProviderFactory(
                new StubStreamingTurn(new InvalidOperationException("terminal failure")),
                new StubStreamingTurn(["should-not-run"]))
            : new StubStreamingProviderFactory("terminal output", "should-not-run");
        var agent = CreateAgent(actorId, providerFactory: provider);
        await agent.ActivateAsync();
        await agent.HandleInitializeAsync(CreateInitializeCommandWithExternalSource());
        var identity = CreateExternalIdentity($"delivery-terminal-{terminalStatus.ToString().ToLowerInvariant()}");
        await agent.HandleAdmitExternalTriggerAsync(new AdmitSkillRunnerExternalTriggerCommand { Identity = identity });

        if (terminalStatus == SkillRunnerExternalTriggerDeliveryStatus.Rejected)
        {
            await agent.HandleDisableAsync(new DisableSkillRunnerCommand { Reason = "operator" });
        }

        await agent.HandleTriggerAsync(new TriggerSkillRunnerExecutionCommand
        {
            Reason = SkillRunnerDefaults.ExternalTriggerReason,
            RetryAttempt = terminalStatus == SkillRunnerExternalTriggerDeliveryStatus.Failed
                ? SkillRunnerDefaults.MaxRetryAttempts
                : 0,
            ExternalTriggerIdentity = identity.Clone(),
        });
        var executionRequestsBeforeDuplicate = provider.Requests.Count;
        var persistedBeforeDuplicate = await _store.GetEventsAsync(actorId);
        persistedBeforeDuplicate
            .Select(x => x.EventData)
            .Count(IsExternalExecutionTerminalEvent)
            .Should()
            .Be(1);
        agent.State.FindExternalTriggerDelivery(identity)!.Status.Should().Be(terminalStatus);

        await agent.HandleTriggerAsync(new TriggerSkillRunnerExecutionCommand
        {
            Reason = SkillRunnerDefaults.ExternalTriggerReason,
            ExternalTriggerIdentity = identity.Clone(),
        });

        var persisted = await _store.GetEventsAsync(actorId);
        provider.Requests.Should().HaveCount(executionRequestsBeforeDuplicate);
        persisted
            .Select(x => x.EventData)
            .Count(IsExternalExecutionTerminalEvent)
            .Should()
            .Be(1);
        var duplicate = persisted
            .Select(x => x.EventData)
            .Where(x => x.Is(SkillRunnerExternalTriggerDuplicateIgnoredEvent.Descriptor))
            .Select(x => x.Unpack<SkillRunnerExternalTriggerDuplicateIgnoredEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        duplicate.Identity.DeliveryId.Should().Be(identity.DeliveryId);
        duplicate.Reason.Should().Be(SkillRunnerDefaults.ExternalTriggerDuplicateReasonAlreadyAdmitted);
        agent.State.FindExternalTriggerDelivery(identity)!.Status.Should().Be(terminalStatus);
    }

    [Fact]
    public async Task HandleTriggerAsync_ExternalCompleted_ShouldPreserveIdentityAndMarkTerminal()
    {
        var provider = new StubStreamingProviderFactory("external output");
        var agent = CreateAgent("skill-runner-external-complete", providerFactory: provider);
        await agent.ActivateAsync();
        await agent.HandleInitializeAsync(CreateInitializeCommandWithExternalSource());
        var identity = CreateExternalIdentity("delivery-complete");
        await agent.HandleAdmitExternalTriggerAsync(new AdmitSkillRunnerExternalTriggerCommand { Identity = identity });

        await agent.HandleTriggerAsync(new TriggerSkillRunnerExecutionCommand
        {
            Reason = SkillRunnerDefaults.ExternalTriggerReason,
            ExternalTriggerIdentity = identity.Clone(),
        });

        var completed = await ReadSingleCompletedEventAsync(_store, "skill-runner-external-complete");
        completed.ExternalTriggerIdentity.Should().NotBeNull();
        completed.ExternalTriggerIdentity.DeliveryId.Should().Be("delivery-complete");
        agent.State.FindExternalTriggerDelivery(identity)!.Status
            .Should()
            .Be(SkillRunnerExternalTriggerDeliveryStatus.Completed);
    }

    [Fact]
    public async Task HandleTriggerAsync_DisabledExternalDelivery_ShouldPreserveIdentityInExecutionRejected()
    {
        await _agent.HandleInitializeAsync(CreateInitializeCommandWithExternalSource());
        var identity = CreateExternalIdentity("delivery-disabled-runner");
        await _agent.HandleAdmitExternalTriggerAsync(new AdmitSkillRunnerExternalTriggerCommand { Identity = identity });
        await _agent.HandleDisableAsync(new DisableSkillRunnerCommand { Reason = "operator" });

        await _agent.HandleTriggerAsync(new TriggerSkillRunnerExecutionCommand
        {
            Reason = SkillRunnerDefaults.ExternalTriggerReason,
            ExternalTriggerIdentity = identity.Clone(),
        });

        var persisted = await _store.GetEventsAsync("skill-runner-test");
        var rejected = persisted
            .Select(x => x.EventData)
            .Where(x => x.Is(SkillRunnerExecutionRejectedEvent.Descriptor))
            .Select(x => x.Unpack<SkillRunnerExecutionRejectedEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        rejected.ExternalTriggerIdentity.DeliveryId.Should().Be("delivery-disabled-runner");
        _agent.State.FindExternalTriggerDelivery(identity)!.Status
            .Should()
            .Be(SkillRunnerExternalTriggerDeliveryStatus.Rejected);
    }

    [Fact]
    public async Task HandleTriggerAsync_ExternalRetry_ShouldPreserveIdentityInRetryCommand()
    {
        var scheduler = new RecordingCallbackScheduler();
        using var provider = BuildServiceProvider(
            new InMemoryEventStore(),
            services => services.AddSingleton<Foundation.Abstractions.Runtime.Callbacks.IActorRuntimeCallbackScheduler>(scheduler));
        var agent = CreateAgent(
            "skill-runner-external-retry",
            provider,
            providerFactory: new StubStreamingProviderFactory(new StubStreamingTurn(new InvalidOperationException("fail once"))));
        await agent.ActivateAsync();
        await agent.HandleInitializeAsync(CreateInitializeCommandWithExternalSource());
        var identity = CreateExternalIdentity("delivery-retry");
        await agent.HandleAdmitExternalTriggerAsync(new AdmitSkillRunnerExternalTriggerCommand { Identity = identity });

        await agent.HandleTriggerAsync(new TriggerSkillRunnerExecutionCommand
        {
            Reason = SkillRunnerDefaults.ExternalTriggerReason,
            ExternalTriggerIdentity = identity.Clone(),
        });

        scheduler.Timeouts.Should().ContainSingle();
        var retry = scheduler.Timeouts[0].TriggerEnvelope.Payload.Unpack<TriggerSkillRunnerExecutionCommand>();
        retry.RetryAttempt.Should().Be(1);
        retry.ExternalTriggerIdentity.DeliveryId.Should().Be("delivery-retry");
    }

    [Fact]
    public async Task HandleTriggerAsync_ExternalFailureAtExhaustedRetry_ShouldPreserveIdentityAndMarkFailed()
    {
        var provider = new StubStreamingProviderFactory(new StubStreamingTurn(
            new InvalidOperationException("terminal external failure")));
        var agent = CreateAgent("skill-runner-external-failed", providerFactory: provider);
        await agent.ActivateAsync();
        await agent.HandleInitializeAsync(CreateInitializeCommandWithExternalSource());
        var identity = CreateExternalIdentity("delivery-failed");
        await agent.HandleAdmitExternalTriggerAsync(new AdmitSkillRunnerExternalTriggerCommand { Identity = identity });

        await agent.HandleTriggerAsync(new TriggerSkillRunnerExecutionCommand
        {
            Reason = SkillRunnerDefaults.ExternalTriggerReason,
            RetryAttempt = SkillRunnerDefaults.MaxRetryAttempts,
            ExternalTriggerIdentity = identity.Clone(),
        });

        var failed = await ReadSingleFailedEventAsync(_store, "skill-runner-external-failed");
        failed.Error.Should().Be("terminal external failure");
        failed.ExternalTriggerIdentity.Should().NotBeNull();
        failed.ExternalTriggerIdentity.DeliveryId.Should().Be("delivery-failed");
        var record = agent.State.FindExternalTriggerDelivery(identity);
        record.Should().NotBeNull();
        record!.Status.Should().Be(SkillRunnerExternalTriggerDeliveryStatus.Failed);
        record.Reason.Should().Be("terminal external failure");
    }

    [Fact]
    public async Task OnActivateAsync_RecoverableExternalDelivery_ShouldRedispatchWithBoundedAttempt()
    {
        var store = new InMemoryEventStore();
        using var provider = BuildServiceProvider(store);
        var first = CreateAgent("skill-runner-external-recover", provider);
        await first.ActivateAsync();
        await first.HandleInitializeAsync(CreateInitializeCommandWithExternalSource());
        await first.HandleAdmitExternalTriggerAsync(new AdmitSkillRunnerExternalTriggerCommand
        {
            Identity = CreateExternalIdentity("delivery-recover"),
        });

        var recovered = CreateAgent("skill-runner-external-recover", provider);
        await recovered.ActivateAsync();

        var persisted = await store.GetEventsAsync("skill-runner-external-recover");
        var dispatches = persisted
            .Select(x => x.EventData)
            .Where(x => x.Is(SkillRunnerExternalTriggerDispatchRequestedEvent.Descriptor))
            .Select(x => x.Unpack<SkillRunnerExternalTriggerDispatchRequestedEvent>())
            .ToArray();
        dispatches.Should().HaveCount(2);
        dispatches[^1].DispatchAttempt.Should().Be(2);
    }

    [Fact]
    public async Task OnActivateAsync_RecoverableExternalDelivery_WhenAttemptsExhausted_ShouldCommitTerminalRejectedWithoutRedispatch()
    {
        var store = new InMemoryEventStore();
        using var provider = BuildServiceProvider(store);
        var first = CreateAgent("skill-runner-external-exhausted", provider);
        await first.ActivateAsync();
        await first.HandleInitializeAsync(CreateInitializeCommandWithExternalSource());
        var identity = CreateExternalIdentity("delivery-exhausted");
        await first.HandleAdmitExternalTriggerAsync(new AdmitSkillRunnerExternalTriggerCommand
        {
            Identity = identity,
        });

        for (var attempt = 2; attempt <= SkillRunnerDefaults.ExternalTriggerMaxDispatchAttempts; attempt++)
        {
            var recovered = CreateAgent("skill-runner-external-exhausted", provider);
            await recovered.ActivateAsync();
        }

        var exhausted = CreateAgent("skill-runner-external-exhausted", provider);
        await exhausted.ActivateAsync();

        var persisted = await store.GetEventsAsync("skill-runner-external-exhausted");
        persisted
            .Select(x => x.EventData)
            .Count(x => x.Is(SkillRunnerExternalTriggerDispatchRequestedEvent.Descriptor))
            .Should()
            .Be(SkillRunnerDefaults.ExternalTriggerMaxDispatchAttempts);
        var rejected = persisted
            .Select(x => x.EventData)
            .Where(x => x.Is(SkillRunnerExternalTriggerRejectedEvent.Descriptor))
            .Select(x => x.Unpack<SkillRunnerExternalTriggerRejectedEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        rejected.Reason.Should().Be(SkillRunnerDefaults.ExternalTriggerRejectedReasonDispatchAttemptsExhausted);
        rejected.Identity.DeliveryId.Should().Be("delivery-exhausted");
        var record = exhausted.State.FindExternalTriggerDelivery(identity);
        record.Should().NotBeNull();
        record!.Status.Should().Be(SkillRunnerExternalTriggerDeliveryStatus.Rejected);
        record.Reason.Should().Be(SkillRunnerDefaults.ExternalTriggerRejectedReasonDispatchAttemptsExhausted);
    }

    [Fact]
    public void TrimExternalTriggerDeliveries_ShouldTrimTerminalByCountAndAgeWhilePreservingNonTerminal()
    {
        var state = new SkillRunnerState();
        var now = new DateTimeOffset(2026, 6, 6, 8, 0, 0, TimeSpan.Zero);
        Func<DateTimeOffset, Google.Protobuf.WellKnownTypes.Timestamp> timestamp =
            Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset;

        for (var i = 0; i <= SkillRunnerDefaults.ExternalTriggerTerminalDeliveryRetention; i++)
        {
            state.UpsertExternalTriggerDelivery(
                CreateExternalIdentity($"recent-terminal-{i:0000}"),
                SkillRunnerExternalTriggerDeliveryStatus.Completed,
                timestamp(now - TimeSpan.FromMinutes(i)));
        }

        var oldTerminal = CreateExternalIdentity("old-terminal");
        state.UpsertExternalTriggerDelivery(
            oldTerminal,
            SkillRunnerExternalTriggerDeliveryStatus.Completed,
            timestamp(now - SkillRunnerDefaults.ExternalTriggerTerminalDeliveryRetentionAge - TimeSpan.FromDays(1)));
        var oldNonTerminal = CreateExternalIdentity("old-nonterminal");
        state.UpsertExternalTriggerDelivery(
            oldNonTerminal,
            SkillRunnerExternalTriggerDeliveryStatus.DispatchRequested,
            timestamp(now - SkillRunnerDefaults.ExternalTriggerTerminalDeliveryRetentionAge - TimeSpan.FromDays(1)),
            dispatchAttempt: 1);

        state.TrimExternalTriggerDeliveries(now);

        state.RecentExternalTriggerDeliveries
            .Count(record => record.Status == SkillRunnerExternalTriggerDeliveryStatus.Completed)
            .Should()
            .Be(SkillRunnerDefaults.ExternalTriggerTerminalDeliveryRetention);
        state.FindExternalTriggerDelivery(oldTerminal).Should().BeNull();
        state.FindExternalTriggerDelivery(CreateExternalIdentity("recent-terminal-1000")).Should().BeNull();
        state.FindExternalTriggerDelivery(CreateExternalIdentity("recent-terminal-0000")).Should().NotBeNull();
        var nonTerminal = state.FindExternalTriggerDelivery(oldNonTerminal);
        nonTerminal.Should().NotBeNull();
        nonTerminal!.Status.Should().Be(SkillRunnerExternalTriggerDeliveryStatus.DispatchRequested);
    }

    [Fact]
    public void TrimCronOccurrenceTerminals_ShouldTrimByCountAndAge()
    {
        var state = new SkillRunnerState();
        var now = new DateTimeOffset(2026, 6, 16, 8, 0, 0, TimeSpan.Zero);

        for (var i = 0; i <= SkillRunnerDefaults.CronOccurrenceTerminalRetention; i++)
        {
            state.UpsertCronOccurrenceTerminal(
                $"schedule:runner-1:fire:recent-{i:0000}",
                Timestamp.FromDateTimeOffset(now - TimeSpan.FromMinutes(i)));
        }

        var oldKey = "schedule:runner-1:fire:old";
        state.UpsertCronOccurrenceTerminal(
            oldKey,
            Timestamp.FromDateTimeOffset(now - SkillRunnerDefaults.CronOccurrenceTerminalRetentionAge - TimeSpan.FromDays(1)));

        state.TrimCronOccurrenceTerminals(now);

        state.RecentCronOccurrenceTerminals.Should().HaveCount(SkillRunnerDefaults.CronOccurrenceTerminalRetention);
        state.IsCronOccurrenceTerminal(oldKey).Should().BeFalse();
        state.IsCronOccurrenceTerminal("schedule:runner-1:fire:recent-1000").Should().BeFalse();
        state.IsCronOccurrenceTerminal("schedule:runner-1:fire:recent-0000").Should().BeTrue();
    }

    [Fact]
    public void SkillRunnerCronOccurrenceProtoContract_ShouldUseAppendOnlyFieldNumbersAndRoundTrip()
    {
        SkillRunnerState.Descriptor.FindFieldByName("recent_cron_occurrence_terminals")!.FieldNumber
            .Should()
            .Be(30);
        SkillRunnerExecutionCompletedEvent.Descriptor.FindFieldByName("cron_occurrence_key")!.FieldNumber
            .Should()
            .Be(12);
        SkillRunnerExecutionFailedEvent.Descriptor.FindFieldByName("cron_occurrence_key")!.FieldNumber
            .Should()
            .Be(9);
        SkillRunnerExecutionRejectedEvent.Descriptor.FindFieldByName("cron_occurrence_key")!.FieldNumber
            .Should()
            .Be(4);
        SkillRunnerCronOccurrenceDuplicateIgnoredEvent.Descriptor.FindFieldByName("cron_occurrence_key")!.FieldNumber
            .Should()
            .Be(1);
        SkillRunnerCronOccurrenceDuplicateIgnoredEvent.Descriptor.FindFieldByName("ignored_at")!.FieldNumber
            .Should()
            .Be(2);

        var state = new SkillRunnerState();
        state.RecentCronOccurrenceTerminals.Add(new SkillRunnerCronOccurrenceTerminalRecord
        {
            CronOccurrenceKey = "schedule:runner-1:fire:2026-06-16T06:00:00.0000000+00:00",
            TerminalAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 16, 6, 0, 0, TimeSpan.Zero)),
        });

        var parsedState = SkillRunnerState.Parser.ParseFrom(state.ToByteArray());
        parsedState.RecentCronOccurrenceTerminals.Should().ContainSingle()
            .Which.CronOccurrenceKey.Should().Be(state.RecentCronOccurrenceTerminals[0].CronOccurrenceKey);

        var completed = new SkillRunnerExecutionCompletedEvent
        {
            CompletedAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 16, 6, 1, 0, TimeSpan.Zero)),
            Output = "done",
            CronOccurrenceKey = state.RecentCronOccurrenceTerminals[0].CronOccurrenceKey,
        };
        SkillRunnerExecutionCompletedEvent.Parser.ParseFrom(completed.ToByteArray())
            .CronOccurrenceKey.Should().Be(completed.CronOccurrenceKey);

        var duplicate = new SkillRunnerCronOccurrenceDuplicateIgnoredEvent
        {
            CronOccurrenceKey = completed.CronOccurrenceKey,
            IgnoredAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 16, 6, 2, 0, TimeSpan.Zero)),
        };
        SkillRunnerCronOccurrenceDuplicateIgnoredEvent.Parser.ParseFrom(duplicate.ToByteArray())
            .CronOccurrenceKey.Should().Be(duplicate.CronOccurrenceKey);
    }

    [Fact]
    public async Task HandleInitializeAsync_ShouldDispatchCatalogCommandsThroughDispatchPort()
    {
        var catalogActor = Substitute.For<IActor>();
        var runtime = Substitute.For<IActorRuntime>();
        runtime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(catalogActor));

        var dispatch = Substitute.For<IActorDispatchPort>();
        var captured = new List<EventEnvelope>();
        dispatch.DispatchAsync(
                UserAgentCatalogGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(captured.Add),
                Arg.Any<CancellationToken>())
            .Returns(ActorDispatchPortTestSupport.AcceptAsync);

        using var provider = BuildServiceProvider(
            new InMemoryEventStore(),
            services =>
            {
                services.AddSingleton(runtime);
                services.AddSingleton(dispatch);
            });
        var agent = CreateAgent("skill-runner-dispatch-test", provider);
        await agent.ActivateAsync();

        await agent.HandleInitializeAsync(CreateInitializeCommand());

        captured.Should().ContainSingle();
        captured[0].Payload.Is(UserAgentCatalogUpsertCommand.Descriptor).Should().BeTrue();
        captured.Should().OnlyContain(envelope =>
            envelope.Route.PublisherActorId == "skill-runner-dispatch-test" &&
            envelope.Route.Direct.TargetActorId == UserAgentCatalogGAgent.WellKnownId);
        await catalogActor.DidNotReceive()
            .HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleInitializeAsync_WithOwnerScope_DispatchesOwnerScopeOnlyCatalogCommand()
    {
        var catalogActor = Substitute.For<IActor>();
        var runtime = Substitute.For<IActorRuntime>();
        runtime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(catalogActor));

        var dispatch = Substitute.For<IActorDispatchPort>();
        var captured = new List<EventEnvelope>();
        dispatch.DispatchAsync(
                UserAgentCatalogGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(captured.Add),
                Arg.Any<CancellationToken>())
            .Returns(ActorDispatchPortTestSupport.AcceptAsync);

        using var provider = BuildServiceProvider(
            new InMemoryEventStore(),
            services =>
            {
                services.AddSingleton(runtime);
                services.AddSingleton(dispatch);
            });
        var agent = CreateAgent("skill-runner-owner-scope-only", provider);
        await agent.ActivateAsync();

        var ownerScope = OwnerScope.ForNyxIdNative("user-1");
        var initialize = CreateInitializeCommand();
        initialize.OutputFormat = SkillRunnerOutputFormat.Text;
        initialize.OutboundConfig.OwnerScope = ownerScope;
#pragma warning disable CS0612 // stale legacy fields must not be emitted when owner_scope exists
        initialize.OutboundConfig.Platform = "nyxid";
        initialize.OutboundConfig.OwnerNyxUserId = "user-1";
#pragma warning restore CS0612

        await agent.HandleInitializeAsync(initialize);

        captured.Should().ContainSingle();
        captured[0].Payload.Is(UserAgentCatalogUpsertCommand.Descriptor).Should().BeTrue();
        var command = captured[0].Payload.Unpack<UserAgentCatalogUpsertCommand>();
        command.OwnerScope.Should().NotBeNull();
        command.OwnerScope!.MatchesStrictly(ownerScope).Should().BeTrue();
        command.OutputFormat.Should().Be(SkillRunnerOutputFormat.Text);
#pragma warning disable CS0612
        command.Platform.Should().BeEmpty();
        command.OwnerNyxUserId.Should().BeEmpty();
#pragma warning restore CS0612
    }

    [Fact]
    public async Task HandleInitializeAsync_WithRawOutboundNyxApiKey_DoesNotPersistOrReemitRawKey()
    {
        var catalogActor = Substitute.For<IActor>();
        var runtime = Substitute.For<IActorRuntime>();
        runtime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(catalogActor));

        var dispatch = Substitute.For<IActorDispatchPort>();
        var captured = new List<EventEnvelope>();
        dispatch.DispatchAsync(
                UserAgentCatalogGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(captured.Add),
                Arg.Any<CancellationToken>())
            .Returns(ActorDispatchPortTestSupport.AcceptAsync);

        var store = new InMemoryEventStore();
        using var provider = BuildServiceProvider(
            store,
            services =>
            {
                services.AddSingleton(runtime);
                services.AddSingleton(dispatch);
            });
        var agent = CreateAgent("skill-runner-raw-outbound-scrub", provider);
        await agent.ActivateAsync();

        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig.NyxApiKey = "raw-outbound-secret";
        initialize.OutboundConfig.NyxApiKeyReference = null;

        await agent.HandleInitializeAsync(initialize);

        var persisted = await store.GetEventsAsync("skill-runner-raw-outbound-scrub");
        var initialized = persisted.Should().ContainSingle().Subject.EventData.Unpack<SkillRunnerInitializedEvent>();
#pragma warning disable CS0612 // asserting deprecated field stays empty on new writes
        initialized.OutboundConfig.NyxApiKey.Should().BeEmpty();
        agent.State.OutboundConfig.NyxApiKey.Should().BeEmpty();
#pragma warning restore CS0612
        agent.State.OutboundConfig.NyxApiKeyReference.Should().BeNull();

        captured.Should().ContainSingle();
        captured[0].Payload.Is(UserAgentCatalogUpsertCommand.Descriptor).Should().BeTrue();
        var command = captured[0].Payload.Unpack<UserAgentCatalogUpsertCommand>();
#pragma warning disable CS0612 // asserting registry command no longer re-emits raw keys
        command.NyxApiKey.Should().BeEmpty();
#pragma warning restore CS0612
        command.NyxApiKeyReference.Should().BeNull();
    }

    [Fact]
    public async Task HandleInitializeAsync_WithOutboundReferenceAndRawKey_PersistsAndReemitsReferenceOnly()
    {
        var catalogActor = Substitute.For<IActor>();
        var runtime = Substitute.For<IActorRuntime>();
        runtime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(catalogActor));

        var dispatch = Substitute.For<IActorDispatchPort>();
        var captured = new List<EventEnvelope>();
        dispatch.DispatchAsync(
                UserAgentCatalogGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(captured.Add),
                Arg.Any<CancellationToken>())
            .Returns(ActorDispatchPortTestSupport.AcceptAsync);

        var store = new InMemoryEventStore();
        using var provider = BuildServiceProvider(
            store,
            services =>
            {
                services.AddSingleton(runtime);
                services.AddSingleton(dispatch);
            });
        var agent = CreateAgent("skill-runner-reference-outbound", provider);
        await agent.ActivateAsync();

        var reference = new SecretReference
        {
            Ref = "sec-scheduled-runner",
            Purpose = CredentialSecretPurposes.ScheduledNyxApiKey,
            OwnerScopeKey = "scope-key-runner",
        };
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig.NyxApiKey = "raw-outbound-secret";
        initialize.OutboundConfig.NyxApiKeyReference = reference;

        await agent.HandleInitializeAsync(initialize);

        var persisted = await store.GetEventsAsync("skill-runner-reference-outbound");
        var initialized = persisted.Should().ContainSingle().Subject.EventData.Unpack<SkillRunnerInitializedEvent>();
#pragma warning disable CS0612 // asserting deprecated field stays empty on new writes
        initialized.OutboundConfig.NyxApiKey.Should().BeEmpty();
        agent.State.OutboundConfig.NyxApiKey.Should().BeEmpty();
#pragma warning restore CS0612
        agent.State.OutboundConfig.NyxApiKeyReference.Should().NotBeNull();
        agent.State.OutboundConfig.NyxApiKeyReference!.Ref.Should().Be("sec-scheduled-runner");

        captured.Should().ContainSingle();
        captured[0].Payload.Is(UserAgentCatalogUpsertCommand.Descriptor).Should().BeTrue();
        var command = captured[0].Payload.Unpack<UserAgentCatalogUpsertCommand>();
#pragma warning disable CS0612 // asserting registry command no longer re-emits raw keys
        command.NyxApiKey.Should().BeEmpty();
#pragma warning restore CS0612
        command.NyxApiKeyReference.Should().NotBeNull();
        command.NyxApiKeyReference!.Ref.Should().Be("sec-scheduled-runner");
    }

    [Fact]
    public async Task HandleInitializeAsync_WithLegacyOwnershipFields_DerivesOwnerScopeAndPreservesLegacyFields()
    {
        var catalogActor = Substitute.For<IActor>();
        var runtime = Substitute.For<IActorRuntime>();
        runtime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(catalogActor));

        var dispatch = Substitute.For<IActorDispatchPort>();
        var captured = new List<EventEnvelope>();
        dispatch.DispatchAsync(
                UserAgentCatalogGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(captured.Add),
                Arg.Any<CancellationToken>())
            .Returns(ActorDispatchPortTestSupport.AcceptAsync);

        using var provider = BuildServiceProvider(
            new InMemoryEventStore(),
            services =>
            {
                services.AddSingleton(runtime);
                services.AddSingleton(dispatch);
            });
        var agent = CreateAgent("skill-runner-legacy-owner-fallback", provider);
        await agent.ActivateAsync();

        var initialize = CreateInitializeCommand();
#pragma warning disable CS0612 // legacy fallback branch must keep backwards-compatible writes
        initialize.OutboundConfig.OwnerNyxUserId = "legacy-user-1";
        initialize.OutboundConfig.Platform = "nyxid";
#pragma warning restore CS0612

        await agent.HandleInitializeAsync(initialize);

        captured.Should().ContainSingle();
        captured[0].Payload.Is(UserAgentCatalogUpsertCommand.Descriptor).Should().BeTrue();
        var command = captured[0].Payload.Unpack<UserAgentCatalogUpsertCommand>();
        command.OwnerScope.Should().NotBeNull();
        command.OwnerScope!.MatchesStrictly(OwnerScope.ForNyxIdNative("legacy-user-1")).Should().BeTrue();
#pragma warning disable CS0612
        command.Platform.Should().Be("nyxid");
        command.OwnerNyxUserId.Should().Be("legacy-user-1");
#pragma warning restore CS0612
    }

    [Fact]
    public async Task SendOutputAsync_ShouldUseTypedReceiveTarget_WhenLarkReceiveIdIsPopulated()
    {
        // Initialize with typed fields set (the shape AgentBuilderTool now writes for p2p flows).
        // Even though the legacy ConversationId is an `oc_*` chat id (which Lark would also accept
        // with chat_id), the typed open_id target should be sent verbatim — this is what fixes the
        // production 400 where the relay's ConversationId fell through to ou_*.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig.ConversationId = "oc_chat_legacy";
        initialize.OutboundConfig.LarkReceiveId = "ou_user_1";
        initialize.OutboundConfig.LarkReceiveIdType = "open_id";
        await _agent.HandleInitializeAsync(initialize);

        var handler = new RecordingHandler("""{"code":0,"msg":"success","data":{"message_id":"om_1"}}""");
        AttachNyxIdApiClient(_agent, handler);

        await InvokeSendOutputAsync(_agent, "scheduled report body");

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages?receive_id_type=open_id");
        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("receive_id").GetString().Should().Be("ou_user_1");
        body.RootElement.GetProperty("msg_type").GetString().Should().Be("text");
        var delivery = await ReadSingleDeliveryProducedEventAsync(_store, "skill-runner-test");
        delivery.DeliveryKind.Should().Be(DeliveryKind.TextMessage);
        delivery.Status.Should().Be(DeliveryStatus.Succeeded);
        delivery.LarkMessageId.Should().Be("om_1");
        delivery.Target.ReceiveId.Should().Be("ou_user_1");
        _agent.State.LastSuccessfulDelivery.Should().NotBeNull();
        _agent.State.LastSuccessfulDelivery!.LarkMessageId.Should().Be("om_1");
    }

    [Fact]
    public async Task SendOutputAsync_ShouldFallBackToConversationIdPrefixInference_ForLegacyState()
    {
        // Backward compatibility: state persisted before the typed lark_receive_id fields existed
        // still resolves through the prefix heuristic on ConversationId. The send still succeeds
        // (no exception); the sender emits a Debug breadcrumb that is not visible to xUnit.
        var store = new InMemoryEventStore();
        using var provider = BuildServiceProvider(store);
        const string actorId = "skill-runner-legacy-raw-state";
        await AppendLegacyInitializedEventAsync(
            store,
            actorId,
            new SkillRunnerOutboundConfig
            {
                ConversationId = "ou_legacy_user",
                NyxProviderSlug = "api-lark-bot",
                NyxApiKey = "nyx-api-key",
            });
        var agent = CreateAgent(actorId, provider);
        await agent.ActivateAsync();

        var handler = new RecordingHandler("""{"code":0,"msg":"success","data":{"message_id":"om_success"}}""");
        AttachNyxIdApiClient(agent, handler);

        await InvokeSendOutputAsync(agent, "legacy report body");

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages?receive_id_type=open_id");
        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("receive_id").GetString().Should().Be("ou_legacy_user");
    }

    [Fact]
    public async Task SendOutputAsync_ShouldThrow_WhenLarkBusinessCodeIsNonZero()
    {
        // Lark reports business errors as HTTP 200 with `code != 0`. Ignoring the response would
        // let HandleTriggerAsync persist SkillRunnerExecutionCompletedEvent on a silent failure.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig.LarkReceiveId = "ou_user_1";
        initialize.OutboundConfig.LarkReceiveIdType = "open_id";
        await _agent.HandleInitializeAsync(initialize);

        var handler = new RecordingHandler("""{"code":230002,"msg":"invalid receive_id"}""");
        AttachNyxIdApiClient(_agent, handler);

        Func<Task> act = () => InvokeSendOutputAsync(_agent, "report");

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.WithMessage("*code=230002*");
        assertion.WithMessage("*invalid receive_id*");
    }

    [Fact]
    public async Task SendOutputAsync_ShouldThrow_WhenNyxProxyEnvelopeReportsError()
    {
        // HTTP non-2xx from NyxID gets packaged into a Nyx envelope that ProxyRequestAsync returns
        // verbatim. Ignoring it would mask transport / auth failures.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig.LarkReceiveId = "ou_user_1";
        initialize.OutboundConfig.LarkReceiveIdType = "open_id";
        await _agent.HandleInitializeAsync(initialize);

        var handler = new RecordingHandler("""{"error":true,"message":"upstream timeout"}""");
        AttachNyxIdApiClient(_agent, handler);

        Func<Task> act = () => InvokeSendOutputAsync(_agent, "report");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*upstream timeout*");
    }

    [Fact]
    public async Task SendOutputAsync_ShouldIncludeRecreateHint_When_LarkRejectsAsCrossAppOpenId()
    {
        // PR #409 review (pulls/409#review-4175198266): after this fix new agents capture
        // union_id, but agents created before the fix still have `LarkReceiveIdType=open_id`
        // pinned to a relay-app-scoped `ou_*`. Their next scheduled run hits Lark
        // `99992361 open_id cross app` and the user sees the bare error in `/agent-status`'s
        // `last_error` with no clue what to do. Surface explicit "delete and recreate" guidance
        // so the failure becomes self-documenting.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig.LarkReceiveId = "ou_relay_app_user_1";
        initialize.OutboundConfig.LarkReceiveIdType = "open_id";
        await _agent.HandleInitializeAsync(initialize);

        var handler = new RecordingHandler(
            """{"code":99992361,"msg":"open_id cross app","error":{"message":"Refer to the documentation"}}""");
        AttachNyxIdApiClient(_agent, handler);

        Func<Task> act = () => InvokeSendOutputAsync(_agent, "report");

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.WithMessage("*code=99992361*");
        assertion.WithMessage("*open_id cross app*");
        // The hint must be actionable enough that the user can recover without reading source.
        assertion.WithMessage("*before cross-app union_id ingress existed*");
        assertion.WithMessage("*/agents*");
        assertion.WithMessage("*Delete*");
    }

    [Fact]
    public async Task SendOutputAsync_ShouldRetryWithFallback_When_PrimaryRejectedAsBotNotInChat_ViaHttp400Envelope()
    {
        // Reviewer (PR #412 r3141700469): production failures arrive through
        // `NyxIdApiClient.SendAsync` as an HTTP-400 Nyx envelope:
        // `{"error": true, "status": 400, "body": "{\"code\":230002,...}"}`. The previous
        // `LarkProxyResponse.TryGetError` returned true for that shape but left
        // `larkCode=null` because it didn't parse the nested `body`, so the BotNotInChat
        // retry branch never fired in the actual production path. Pin the wrapped envelope
        // shape end-to-end.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig.ConversationId = "oc_dm_chat_1";
        initialize.OutboundConfig.LarkReceiveId = "oc_dm_chat_1";
        initialize.OutboundConfig.LarkReceiveIdType = "chat_id";
        initialize.OutboundConfig.LarkReceiveIdFallback = "on_user_1";
        initialize.OutboundConfig.LarkReceiveIdTypeFallback = "union_id";
        await _agent.HandleInitializeAsync(initialize);

        // First (primary) attempt: NyxIdApiClient.SendAsync HTTP-400 envelope wrapping Lark
        // 230002. Second (fallback) attempt: clean success.
        var handler = new SequencedHandler(
            """{"error": true, "status": 400, "body": "{\"code\":230002,\"msg\":\"Bot is not in the chat\"}"}""",
            """{"code":0,"msg":"success","data":{"message_id":"om_success"}}""");
        AttachNyxIdApiClient(_agent, handler);

        await InvokeSendOutputAsync(_agent, "report");

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].RequestUri!.Query.Should().Contain("receive_id_type=chat_id");
        handler.Requests[1].RequestUri!.Query.Should().Contain("receive_id_type=union_id");
        handler.Bodies[1].Should().Contain("\"receive_id\":\"on_user_1\"");
    }

    [Fact]
    public async Task SendOutputAsync_ShouldThrowCrossTenantHint_When_LarkCodeNestedInHttp400Body()
    {
        // Same envelope shape as the production /summary failure log: NyxID wraps the Lark
        // 99992364 as a string body inside an HTTP-400 Nyx envelope. The cross-tenant
        // recreate-the-agent hint (PR #412) only fires when the parser surfaces the nested
        // Lark code; previously it never did. Pin both the recovery hint and the nested-body
        // unwrap together.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig.ConversationId = "oc_dm_chat_1";
        initialize.OutboundConfig.LarkReceiveId = "on_relay_tenant_user_1";
        initialize.OutboundConfig.LarkReceiveIdType = "union_id";
        await _agent.HandleInitializeAsync(initialize);

        var handler = new RecordingHandler(
            """{"error": true, "status": 400, "body": "{\"code\":99992364,\"msg\":\"user id cross tenant\"}"}""");
        AttachNyxIdApiClient(_agent, handler);

        Func<Task> act = () => InvokeSendOutputAsync(_agent, "report");

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.WithMessage("*99992364*");
        assertion.WithMessage("*different tenant*");
        assertion.WithMessage("*/agents*");
        assertion.WithMessage("*Delete*");
    }

    [Fact]
    public async Task SendOutputAsync_ShouldRetryWithFallback_When_PrimaryRejectedAsBotNotInChat()
    {
        // Reviewer concern (codex-bot, P1, PR #412): chat_id-first regresses cross-app
        // same-tenant deployments where the outbound app is not a member of the inbound DM
        // chat — Lark returns `230002 bot not in chat` for chat_id-typed sends. Captured the
        // union_id at create time as a fallback; assert the runtime retries once with the
        // fallback typed pair when the primary attempt fails with 230002, and that the retry
        // body uses the fallback `receive_id` / `receive_id_type`.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig.ConversationId = "oc_dm_chat_1";
        initialize.OutboundConfig.LarkReceiveId = "oc_dm_chat_1";
        initialize.OutboundConfig.LarkReceiveIdType = "chat_id";
        initialize.OutboundConfig.LarkReceiveIdFallback = "on_user_1";
        initialize.OutboundConfig.LarkReceiveIdTypeFallback = "union_id";
        await _agent.HandleInitializeAsync(initialize);

        var handler = new SequencedHandler(
            """{"code":230002,"msg":"Bot is not in the chat"}""",
            """{"code":0,"msg":"success","data":{"message_id":"om_success"}}""");
        AttachNyxIdApiClient(_agent, handler);

        await InvokeSendOutputAsync(_agent, "report");

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].RequestUri!.Query.Should().Contain("receive_id_type=chat_id");
        handler.Bodies[0].Should().Contain("\"receive_id\":\"oc_dm_chat_1\"");
        handler.Requests[1].RequestUri!.Query.Should().Contain("receive_id_type=union_id");
        handler.Bodies[1].Should().Contain("\"receive_id\":\"on_user_1\"");
    }

    [Fact]
    public async Task SendOutputAsync_ShouldNotRetry_When_PrimaryRejectedWithDifferentLarkCode()
    {
        // Only `230002 bot not in chat` triggers the fallback retry. Other Lark codes (e.g.
        // 99992364 cross_tenant) propagate immediately so the user sees the actionable
        // recovery hint for the actual failure mode rather than a misleading retry.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig.ConversationId = "oc_dm_chat_1";
        initialize.OutboundConfig.LarkReceiveId = "oc_dm_chat_1";
        initialize.OutboundConfig.LarkReceiveIdType = "chat_id";
        initialize.OutboundConfig.LarkReceiveIdFallback = "on_user_1";
        initialize.OutboundConfig.LarkReceiveIdTypeFallback = "union_id";
        await _agent.HandleInitializeAsync(initialize);

        var handler = new SequencedHandler(
            """{"code":99992364,"msg":"user id cross tenant"}""");
        AttachNyxIdApiClient(_agent, handler);

        Func<Task> act = () => InvokeSendOutputAsync(_agent, "report");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*99992364*");
        handler.Requests.Should().ContainSingle("only 230002 should trigger the fallback retry");
    }

    [Fact]
    public async Task SendOutputAsync_ShouldIncludeRecreateHint_When_LarkRejectsAsCrossTenantUserId()
    {
        // Production failure mode after PR #409 switched p2p to union_id: NyxID's relay-side
        // ingress and `s/api-lark-bot` proxy turned out to be in different Lark tenants, so even
        // union_id is rejected. This PR pivots to chat_id-first; the cross_tenant error code is
        // surfaced with the same recreate guidance so legacy agents (still pinned to union_id)
        // give users a way to recover without reading source.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig.LarkReceiveId = "on_relay_tenant_user_1";
        initialize.OutboundConfig.LarkReceiveIdType = "union_id";
        await _agent.HandleInitializeAsync(initialize);

        var handler = new RecordingHandler(
            """{"code":99992364,"msg":"user id cross tenant","error":{"log_id":"L1"}}""");
        AttachNyxIdApiClient(_agent, handler);

        Func<Task> act = () => InvokeSendOutputAsync(_agent, "report");

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.WithMessage("*code=99992364*");
        assertion.WithMessage("*user id cross tenant*");
        assertion.WithMessage("*different tenant*");
        assertion.WithMessage("*chat_id-preferred*");
        assertion.WithMessage("*/agents*");
        assertion.WithMessage("*Delete*");
    }

    [Fact]
    public async Task TrySendFailureAsync_ShouldUseFailureNotificationSlug_WhenSetAndDistinctFromPrimary()
    {
        // Issue #423 §C: when a primary outbound delivery has just been rejected (e.g. cross-
        // tenant 99992364), retrying the failure notification through the SAME slug also
        // fails. The fix routes failure-notifications through the inbound channel-bot's slug
        // captured at agent-create time — by definition reachable, since the user just
        // messaged it. This test pins that the routing actually changes the proxy slug in
        // the outbound URL while the receive_id, body, and api key stay identical.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig.ConversationId = "oc_dm_chat_1";
        initialize.OutboundConfig.LarkReceiveId = "ou_user_1";
        initialize.OutboundConfig.LarkReceiveIdType = "open_id";
        initialize.OutboundConfig.FailureNotificationProviderSlug = "api-lark-bot-channel-loning";
        await _agent.HandleInitializeAsync(initialize);

        var handler = new RecordingHandler("""{"code":0,"msg":"success","data":{"message_id":"om_failure"}}""");
        AttachNyxIdApiClient(_agent, handler);

        await InvokeTrySendFailureAsync(_agent, "Lark message delivery rejected (code=99992364): user id cross tenant.");

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot-channel-loning/open-apis/im/v1/messages?receive_id_type=open_id");
        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("receive_id").GetString().Should().Be("ou_user_1");
        // The failure-notification message itself should carry the original error so the user
        // sees the actionable hint in chat, not just an unsubscribe ping.
        var contentJson = body.RootElement.GetProperty("content").GetString();
        contentJson.Should().Contain("99992364");
        contentJson.Should().Contain("Skill runner failed");
    }

    [Fact]
    public async Task TrySendFailureAsync_ShouldFallBackToPrimarySlug_WhenFailureNotificationSlugRejects()
    {
        // Defense-in-depth: the failure-notification slug MAY have been valid at create time
        // (in user-services) but become inactive (token revoked, bot uninstalled, etc.) by
        // the time the agent fires. If its send rejects, we still try the primary slug as
        // a last-resort attempt — better than the user seeing nothing.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig.ConversationId = "oc_dm_chat_1";
        initialize.OutboundConfig.LarkReceiveId = "ou_user_1";
        initialize.OutboundConfig.LarkReceiveIdType = "open_id";
        initialize.OutboundConfig.FailureNotificationProviderSlug = "api-lark-bot-channel-revoked";
        await _agent.HandleInitializeAsync(initialize);

        var handler = new SequencedHandler(
            // Failure-notification slug rejects with a Nyx envelope error (e.g. 401 on the proxy)
            """{"error":true,"message":"upstream auth failed"}""",
            // Primary slug succeeds — best-effort recovery.
            """{"code":0,"msg":"success","data":{"message_id":"om_primary_failure"}}""");
        AttachNyxIdApiClient(_agent, handler);

        await InvokeTrySendFailureAsync(_agent, "report rejected at primary");

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].RequestUri!.ToString()
            .Should().Contain("/proxy/s/api-lark-bot-channel-revoked/");
        handler.Requests[1].RequestUri!.ToString()
            .Should().Contain("/proxy/s/api-lark-bot/");
    }

    [Fact]
    public async Task TrySendFailureAsync_ShouldSkipFailureSlug_WhenEqualToPrimary()
    {
        // No-op fallback: if the inbound slug equals the primary slug there is no recovery
        // benefit — same proxy = same rejection mode. AgentBuilderTool leaves the field
        // empty in this case, but pin the runtime guard too so a future
        // mis-capture doesn't pay double-POST cost just to fail twice.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig.ConversationId = "oc_dm_chat_1";
        initialize.OutboundConfig.LarkReceiveId = "ou_user_1";
        initialize.OutboundConfig.LarkReceiveIdType = "open_id";
        initialize.OutboundConfig.FailureNotificationProviderSlug = "api-lark-bot";
        await _agent.HandleInitializeAsync(initialize);

        var handler = new RecordingHandler("""{"code":0,"msg":"success","data":{"message_id":"om_success"}}""");
        AttachNyxIdApiClient(_agent, handler);

        await InvokeTrySendFailureAsync(_agent, "primary failed");

        // Exactly one POST — no double-attempt against the same slug.
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Contain("/proxy/s/api-lark-bot/");
    }

    [Fact]
    public async Task TrySendFailureAsync_ShouldUsePrimary_WhenFailureNotificationSlugIsEmpty()
    {
        // Backwards compat: agents created before #423 §C have an empty
        // FailureNotificationProviderSlug. The runtime must transparently fall back to the
        // existing single-attempt behavior — this test is the regression guard against the
        // failure-notification fallback ever introducing a hidden dependency on the new
        // field being populated.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig.ConversationId = "oc_dm_chat_1";
        initialize.OutboundConfig.LarkReceiveId = "ou_user_1";
        initialize.OutboundConfig.LarkReceiveIdType = "open_id";
        await _agent.HandleInitializeAsync(initialize);

        var handler = new RecordingHandler("""{"code":0,"msg":"success","data":{"message_id":"om_success"}}""");
        AttachNyxIdApiClient(_agent, handler);

        await InvokeTrySendFailureAsync(_agent, "primary failed");

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Contain("/proxy/s/api-lark-bot/");
    }

    [Fact]
    public async Task TrySendFailureAsync_ShouldSwallow_WhenBothSlugsReject()
    {
        // Final guarantee: TrySendFailureAsync MUST NOT throw, ever. HandleTriggerAsync is
        // already in the failure-event-persist path; an exception here would mask the
        // SkillRunnerExecutionFailedEvent persist (which surfaces last_error in
        // /agent-status, the one path users have to recover regardless of Lark visibility).
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig.ConversationId = "oc_dm_chat_1";
        initialize.OutboundConfig.LarkReceiveId = "ou_user_1";
        initialize.OutboundConfig.LarkReceiveIdType = "open_id";
        initialize.OutboundConfig.FailureNotificationProviderSlug = "api-lark-bot-channel-loning";
        await _agent.HandleInitializeAsync(initialize);

        var handler = new SequencedHandler(
            """{"error":true,"message":"failure-slug down"}""",
            """{"code":99992364,"msg":"user id cross tenant"}""");
        AttachNyxIdApiClient(_agent, handler);

        // Should complete (not throw) even though both attempts fail.
        Func<Task> act = () => InvokeTrySendFailureAsync(_agent, "both broken");

        await act.Should().NotThrowAsync();
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task BuildExecutionLlmControl_ShouldPinOwnerLlmConfigOverrides_WhenSourceReturnsConfig()
    {
        // Regression for the "/summary failed: Provider 'openai' not connected" report:
        // skill runners must honor the bot owner's pre-configured model + NyxID route + tool
        // cap — same shape AgentRunGAgent applies for nyxid-chat. Without it,
        // every scheduled run falls through to NyxIdLLMProvider's compile-time `gpt-5.4` +
        // gateway default, which the gateway routes to OpenAI and 400s for bot owners who
        // wired a custom NyxID service like `chrono-llm` at `/api/v1/proxy/s/chrono-llm`.
        var source = new StubOwnerLlmConfigSource(new OwnerLlmConfig(
            DefaultModel: "gpt-5.5",
            PreferredLlmRoute: "/api/v1/proxy/s/chrono-llm",
            MaxToolRounds: 7));

        var agent = CreateAgent("skill-runner-userconfig", _serviceProvider, source);
        await agent.ActivateAsync();
        await agent.HandleInitializeAsync(CreateInitializeCommand());

        var control = await InvokeBuildExecutionLlmControlAsync(agent);

        control.ModelOverride.Should().Be("gpt-5.5");
        control.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/chrono-llm");
        control.MaxToolRoundsOverride.Should().Be(7);
        control.NyxIdAccessToken.Should().Be("nyx-api-key");
        source.RequestedScopeIds.Should().ContainSingle().Which.Should().Be("scope-1");
    }

    [Fact]
    public async Task BuildExecutionLlmControl_ShouldOmitOverrides_WhenOwnerLlmConfigSourceIsAbsent()
    {
        // No host wiring (e.g. tests that don't compose Studio + the bridge): valid metadata
        // still comes out, no override keys leak, NyxIdLLMProvider falls through to its
        // compile-time defaults.
        await _agent.HandleInitializeAsync(CreateInitializeCommand());

        var control = await InvokeBuildExecutionLlmControlAsync(_agent);

        control.ModelOverride.Should().BeNull();
        control.NyxIdRoutePreference.Should().BeNull();
        control.MaxToolRoundsOverride.Should().BeNull();
        control.NyxIdAccessToken.Should().Be("nyx-api-key");
    }

    [Fact]
    public async Task BuildExecutionLlmControl_ShouldOmitOverrides_WhenOwnerLlmConfigFieldsAreEmpty()
    {
        // Bot owners who haven't saved any LLM preference get OwnerLlmConfig.Empty (or empty
        // strings via the host adapter). The applier must NOT pin empty values onto metadata,
        // because NyxIdLLMProvider treats a non-empty NyxIdRoutePreference of "" as a relative
        // path against the authority and produces an invalid URL.
        var source = new StubOwnerLlmConfigSource(OwnerLlmConfig.Empty);

        var agent = CreateAgent("skill-runner-userconfig-empty", _serviceProvider, source);
        await agent.ActivateAsync();
        await agent.HandleInitializeAsync(CreateInitializeCommand());

        var control = await InvokeBuildExecutionLlmControlAsync(agent);

        control.ModelOverride.Should().BeNull();
        control.NyxIdRoutePreference.Should().BeNull();
        control.MaxToolRoundsOverride.Should().BeNull();
    }

    [Fact]
    public async Task BuildExecutionLlmControl_ShouldFallBackQuietly_WhenOwnerLlmConfigSourceThrows()
    {
        // The source can throw on transient projection failures. The agent's execution turn
        // must still proceed with provider defaults — the applier catches and logs the
        // failure, never bubbles it up to the trigger handler.
        var source = new ThrowingOwnerLlmConfigSource();

        var agent = CreateAgent("skill-runner-userconfig-throws", _serviceProvider, source);
        await agent.ActivateAsync();
        await agent.HandleInitializeAsync(CreateInitializeCommand());

        var control = await InvokeBuildExecutionLlmControlAsync(agent);

        control.ModelOverride.Should().BeNull();
        control.NyxIdRoutePreference.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteSkillAsync_AutoOutput_ShouldUseCardKitWithoutTextEdit()
    {
        var provider = new StubStreamingProviderFactory("a", "b", "c");
        var agent = CreateAgent("skill-runner-cardkit-auto", providerFactory: provider);
        await agent.ActivateAsync();
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig.LarkReceiveId = "oc_chat_1";
        initialize.OutboundConfig.LarkReceiveIdType = "chat_id";
        await agent.HandleInitializeAsync(initialize);
        var handler = new SequencedHandler(
            """{"code":0,"msg":"success","data":{"card_id":"card_auto"}}""",
            """{"code":0,"msg":"success","data":{"message_id":"om_card"}}""",
            """{"code":0,"msg":"success","data":{}}""",
            """{"code":0,"msg":"success","data":{}}""");
        AttachNyxIdApiClient(agent, handler);

        var output = await InvokeExecuteSkillAsync(agent);

        output.Should().Be("abc");
        handler.Requests.Should().HaveCount(4);
        handler.Requests[0].Method.Method.Should().Be("POST");
        handler.Requests[0].RequestUri!.AbsolutePath.Should().EndWith("/open-apis/cardkit/v1/cards");
        ExtractCardKitCreateType(handler.Bodies[0]!).Should().Be("card_json");
        handler.Requests[1].Method.Method.Should().Be("POST");
        handler.Requests[1].RequestUri!.ToString()
            .Should().Contain("/open-apis/im/v1/messages?receive_id_type=chat_id");
        ExtractLarkMessageType(handler.Bodies[1]!).Should().Be("interactive");
        ExtractInteractiveCardId(handler.Bodies[1]!).Should().Be("card_auto");
        handler.Requests[2].Method.Method.Should().Be("PUT");
        handler.Requests[2].RequestUri!.AbsolutePath.Should()
            .EndWith("/open-apis/cardkit/v1/cards/card_auto/elements/streaming_main/content");
        ExtractCardKitStreamContent(handler.Bodies[2]!).Should().Be("abc");
        handler.Requests[3].Method.Method.Should().Be("PATCH");
        handler.Requests[3].RequestUri!.AbsolutePath.Should()
            .EndWith("/open-apis/cardkit/v1/cards/card_auto/settings");
        ExtractCardKitSettings(handler.Bodies[3]!).Should().Contain("streaming_mode");
        var deliveries = await ReadDeliveryProducedEventsAsync(_store, "skill-runner-cardkit-auto");
        deliveries.Should().ContainSingle(delivery =>
            delivery.DeliveryKind == DeliveryKind.StreamingCard &&
            delivery.Status == DeliveryStatus.Succeeded &&
            delivery.LarkMessageId == "om_card" &&
            delivery.CardId == "card_auto");
        agent.State.LastSuccessfulDelivery.Should().NotBeNull();
        agent.State.LastSuccessfulDelivery!.CardId.Should().Be("card_auto");
    }

    [Fact]
    public async Task ExecuteSkillAsync_TextOutputFormat_ShouldUseLegacyStreamingTextEdit()
    {
        var provider = new StubStreamingProviderFactory("a", "b", "c");
        var agent = CreateAgent("skill-runner-stream-coalesce", providerFactory: provider);
        await agent.ActivateAsync();
        var initialize = CreateInitializeCommand();
        initialize.OutputFormat = SkillRunnerOutputFormat.Text;
        initialize.OutboundConfig.OutputFormat = SkillRunnerOutputFormat.Text;
        initialize.OutboundConfig.LarkReceiveId = "oc_chat_1";
        initialize.OutboundConfig.LarkReceiveIdType = "chat_id";
        await agent.HandleInitializeAsync(initialize);
        var handler = new SequencedHandler(
            """{"code":0,"msg":"success","data":{"message_id":"om_stream"}}""",
            """{"code":0,"msg":"success","data":{}}""");
        AttachNyxIdApiClient(agent, handler);

        var output = await InvokeExecuteSkillAsync(agent);

        output.Should().Be("abc");
        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Method.Method.Should().Be("POST");
        handler.Requests[1].Method.Method.Should().Be("PUT");
        ExtractLarkText(handler.Bodies[0]!).Should().Be("a");
        ExtractLarkText(handler.Bodies[1]!).Should().Be("abc");
    }

    [Fact]
    public async Task HandleTriggerAsync_WhenCardKitFailsAfterVisibleCard_ShouldPersistFailureWithoutRetry()
    {
        var scheduler = new RecordingCallbackScheduler();
        using var serviceProvider = BuildServiceProvider(
            new InMemoryEventStore(),
            services => services.AddSingleton<IActorRuntimeCallbackScheduler>(scheduler));
        var provider = new StubStreamingProviderFactory("visible but stream failed");
        var agent = CreateAgent(
            "skill-runner-cardkit-visible-failure",
            serviceProvider,
            providerFactory: provider);
        await agent.ActivateAsync();
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig.LarkReceiveId = "oc_chat_1";
        initialize.OutboundConfig.LarkReceiveIdType = "chat_id";
        await agent.HandleInitializeAsync(initialize);
        var handler = new SequencedHandler(
            """{"code":0,"msg":"success","data":{"card_id":"card_partial"}}""",
            """{"code":0,"msg":"success","data":{"message_id":"om_card"}}""",
            """{"code":230099,"msg":"card is unavailable"}""",
            """{"code":0,"msg":"success","data":{}}""",
            """{"code":0,"msg":"success","data":{"message_id":"om_failure"}}""");
        AttachNyxIdApiClient(agent, handler);

        await agent.HandleTriggerAsync(new TriggerSkillRunnerExecutionCommand { Reason = "manual" });

        var failed = await ReadSingleFailedEventAsync(
            serviceProvider.GetRequiredService<IEventStore>() as InMemoryEventStore
            ?? throw new InvalidOperationException("test store missing"),
            "skill-runner-cardkit-visible-failure");
        failed.Error.Should().Contain("230099");
        scheduler.Timeouts.Should().BeEmpty("a retry would create another visible card");
        handler.Requests.Should().HaveCount(5);
        handler.Requests[2].RequestUri!.AbsolutePath.Should()
            .EndWith("/open-apis/cardkit/v1/cards/card_partial/elements/streaming_main/content");
        handler.Requests[4].RequestUri!.AbsolutePath.Should().EndWith("/open-apis/im/v1/messages");
        ExtractLarkText(handler.Bodies[4]!).Should().Contain("Skill runner failed");
        var deliveries = await ReadDeliveryProducedEventsAsync(
            serviceProvider.GetRequiredService<IEventStore>() as InMemoryEventStore
            ?? throw new InvalidOperationException("test store missing"),
            "skill-runner-cardkit-visible-failure");
        deliveries.Should().Contain(delivery =>
            delivery.DeliveryKind == DeliveryKind.StreamingCard &&
            delivery.Status == DeliveryStatus.FailedPostSend &&
            delivery.CardId == "card_partial");
        deliveries.Should().Contain(delivery =>
            delivery.DeliveryKind == DeliveryKind.TextMessage &&
            delivery.Status == DeliveryStatus.Succeeded &&
            delivery.LarkMessageId == "om_failure");
    }

    [Fact]
    public async Task ExecuteSkillAsync_ShouldKeepOwnedScopeOutOfMetadata_AndCarryTypedToolContext()
    {
        var provider = new StubStreamingProviderFactory("done");
        var agent = CreateAgent("skill-runner-typed-context", providerFactory: provider);
        await agent.ActivateAsync();
        await agent.HandleInitializeAsync(CreateInitializeCommand());

        await InvokeExecuteSkillAsync(agent);

        var request = provider.Requests.Should().ContainSingle().Subject;
        request.Metadata.Should().ContainKey(ChannelMetadataKeys.ConversationId);
        request.Metadata.Should().NotContainKey("scope_id");
        request.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdAccessToken);
        request.ToolContext.Should().NotBeNull();
        request.ToolContext!.Request.RequestId.Should().NotBeNullOrWhiteSpace();
        request.ToolContext.Caller.ScopeId.Should().Be("scope-1");
        request.ToolContext.Channel.RegistrationScopeId.Should().Be("scope-1");
        request.ToolContext.Credentials.NyxIdAccessToken.Should().Be("nyx-api-key");
        request.ToolContext.ExternalMetadata.Should().ContainKey(ChannelMetadataKeys.ConversationId);
        request.ToolContext.ExternalMetadata.Should().NotContainKey("scope_id");
    }

    [Fact]
    public async Task ExecuteSkillAsync_WhenInteractiveLarkToolAlreadyDelivered_ShouldSkipOuterReply()
    {
        var provider = new StubStreamingProviderFactory(
            new StubStreamingTurn(
                [],
                [
                    new ToolCall
                    {
                        Id = "call-card",
                        Name = "lark_messages_send",
                        ArgumentsJson = """
                            {
                              "target_type": "chat_id",
                              "target_id": "oc_chat_1",
                              "message_type": "interactive_card",
                              "card_json": "{\"schema\":\"2.0\"}"
                            }
                            """,
                    },
                ]),
            new StubStreamingTurn(["card already sent"]));
        var tool = new FixedResultTool(
            "lark_messages_send",
            """{"success":true,"message_id":"om_card","chat_id":"oc_chat_1"}""");
        var handler = new SequencedHandler("""{"error":true,"message":"outer reply should not be sent"}""");
        var agent = CreateAgent(
            "skill-runner-interactive-tool-suppression",
            providerFactory: provider,
            toolSources: [new SingleToolSource(tool)]);
        await agent.ActivateAsync();
        await agent.HandleInitializeAsync(CreateInitializeCommand());
        AttachNyxIdApiClient(agent, handler);

        var output = await InvokeExecuteSkillAsync(agent);

        output.Should().Be("card already sent");
        tool.LastArgumentsJson.Should().NotBeNull();
        handler.Requests.Should().BeEmpty("the tool already delivered an interactive Lark card");
    }

    [Fact]
    public async Task ExecuteSkillAsync_RemotePromptSkill_ShouldFetchAndUseInstructionsAsSystemPromptWithoutPersisting()
    {
        var fetcher = new SequencedRemoteSkillFetcher(
            RemotePromptSkill("daily-report", "Remote system prompt."));
        var provider = new StubStreamingProviderFactory("remote output");
        var agent = CreateAgent(
            "skill-runner-remote-prompt",
            providerFactory: provider,
            remoteSkillFetcher: fetcher);
        await agent.ActivateAsync();
        await agent.HandleInitializeAsync(CreateSkillRefCommand("daily-report"));
        AttachNyxIdApiClient(agent, new RecordingHandler("""{"code":0,"msg":"success","data":{"message_id":"om_remote"}}"""));

        var result = await InvokeExecuteSkillResultAsync(agent);

        result.Output.Should().Be("remote output");
        result.ExecutionKind.Should().Be(SkillRunnerExecutionKind.Prompt);
        fetcher.Requests.Should().ContainSingle().Which.Should().Be(("nyx-api-key", "daily-report"));
        provider.Requests.Should().ContainSingle();
        provider.Requests[0].Messages[0].Content.Should().Be("Remote system prompt.");
        agent.State.SkillContent.Should().BeEmpty();
        agent.EffectiveConfig.SystemPrompt.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteSkillAsync_RemotePromptSkill_ShouldFetchEachTriggerAndNotCacheInstructions()
    {
        var fetcher = new SequencedRemoteSkillFetcher(
            RemotePromptSkill("daily-report", "Remote prompt v1."),
            RemotePromptSkill("daily-report", "Remote prompt v2."));
        var provider = new StubStreamingProviderFactory(
            new StubStreamingTurn(["first"]),
            new StubStreamingTurn(["second"]));
        var agent = CreateAgent(
            "skill-runner-remote-no-cache",
            providerFactory: provider,
            remoteSkillFetcher: fetcher);
        await agent.ActivateAsync();
        await agent.HandleInitializeAsync(CreateSkillRefCommand("daily-report"));
        AttachNyxIdApiClient(agent, new SequencedHandler(
            """{"code":0,"msg":"success","data":{"message_id":"om_1"}}""",
            """{"code":0,"msg":"success","data":{"message_id":"om_2"}}"""));

        await agent.HandleTriggerAsync(new TriggerSkillRunnerExecutionCommand { Reason = "manual" });
        await agent.HandleTriggerAsync(new TriggerSkillRunnerExecutionCommand { Reason = "manual" });

        fetcher.Requests.Should().HaveCount(2);
        provider.Requests.Should().HaveCount(2);
        provider.Requests[0].Messages[0].Content.Should().Be("Remote prompt v1.");
        provider.Requests[1].Messages[0].Content.Should().Be("Remote prompt v2.");
        agent.State.SkillContent.Should().BeEmpty();
        var completed = (await _store.GetEventsAsync("skill-runner-remote-no-cache"))
            .Select(x => x.EventData)
            .Where(x => x.Is(SkillRunnerExecutionCompletedEvent.Descriptor))
            .Select(x => x.Unpack<SkillRunnerExecutionCompletedEvent>())
            .ToArray();
        completed.Should().HaveCount(2);
        completed.Should().OnlyContain(x =>
            x.ExecutionKind == SkillRunnerExecutionKind.Prompt &&
            x.SkillName == "daily-report" &&
            string.IsNullOrEmpty(x.SkillVersion));
    }

    [Fact]
    public async Task ExecuteSkillAsync_RemoteWorkflowSkill_ShouldDispatchWorkflowAndSkipLlm()
    {
        var fetcher = new SequencedRemoteSkillFetcher(new SkillDefinition
        {
            Name = "workflow-skill",
            Description = "workflow",
            Instructions = "Do not use LLM.",
            Source = SkillSource.Remote,
            Workflows =
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "daily_flow",
                    WorkflowYamls =
                    [
                        "name: daily_flow\nsteps: []\n",
                    ],
                },
            ],
        });
        var provider = new StubStreamingProviderFactory("should-not-run");
        var workflowDispatch = new RecordingWorkflowDispatchService(
            CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
                new WorkflowChatRunAcceptedReceipt("workflow-run-1", "daily_flow", "cmd-1", "corr-1")));
        var agent = CreateAgent(
            "skill-runner-remote-workflow",
            providerFactory: provider,
            remoteSkillFetcher: fetcher,
            workflowDispatchService: workflowDispatch);
        await agent.ActivateAsync();
        var command = CreateSkillRefCommand("workflow-skill");
        command.SkillRef.WorkflowId = "daily_flow";
        await agent.HandleInitializeAsync(command);
        AttachNyxIdApiClient(agent, new RecordingHandler("""{"code":0,"msg":"success","data":{"message_id":"om_workflow"}}"""));

        var result = await InvokeExecuteSkillResultAsync(agent);

        provider.Requests.Should().BeEmpty("workflow-bearing remote skills dispatch workflow directly");
        workflowDispatch.Commands.Should().ContainSingle();
        var dispatched = workflowDispatch.Commands[0];
        dispatched.Source.Kind.Should().Be(WorkflowChatSourceKind.InlineYamlBundle);
        dispatched.Source.InlineBundle!.YamlDocuments.Should().ContainSingle()
            .Which.Yaml.Should().Contain("name: daily_flow");
        dispatched.Prompt.Should().Contain("Run the report.");
        dispatched.ScopeId.Should().Be("scope-1");
        dispatched.CallerCredential!.BearerToken.Should().Be("nyx-api-key");
        result.ExecutionKind.Should().Be(SkillRunnerExecutionKind.Workflow);
        result.WorkflowReceipt.Should().NotBeNull();
        result.WorkflowReceipt!.CommandId.Should().Be("cmd-1");
        result.Output.Should().Contain("Workflow start accepted");
    }

    [Fact]
    public async Task ExecuteSkillAsync_VersionedSkillRef_ShouldFailBeforeFetch()
    {
        var fetcher = new SequencedRemoteSkillFetcher(RemotePromptSkill("daily-report", "unused"));
        var agent = CreateAgent("skill-runner-versioned", remoteSkillFetcher: fetcher);
        await agent.ActivateAsync();
        var command = CreateSkillRefCommand("daily-report");
        command.SkillRef.Version = "1.2";
        await agent.HandleInitializeAsync(command);

        var act = () => InvokeExecuteSkillResultAsync(agent);

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.WithMessage("*Versioned scheduled skill references are not supported yet*");
        fetcher.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteSkillAsync_MultipleWorkflowsWithoutWorkflowId_ShouldFailClosed()
    {
        var fetcher = new SequencedRemoteSkillFetcher(new SkillDefinition
        {
            Name = "workflow-skill",
            Description = "workflow",
            Instructions = "body",
            Source = SkillSource.Remote,
            Workflows =
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "one",
                    WorkflowYamls = ["name: one\nsteps: []\n"],
                },
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "two",
                    WorkflowYamls = ["name: two\nsteps: []\n"],
                },
            ],
        });
        var workflowDispatch = new RecordingWorkflowDispatchService(
            CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
                new WorkflowChatRunAcceptedReceipt("actor", "one", "cmd", "corr")));
        var agent = CreateAgent(
            "skill-runner-multi-workflow",
            remoteSkillFetcher: fetcher,
            workflowDispatchService: workflowDispatch);
        await agent.ActivateAsync();
        await agent.HandleInitializeAsync(CreateSkillRefCommand("workflow-skill"));

        var act = () => InvokeExecuteSkillResultAsync(agent);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*has multiple workflows*workflow_id is required*");
        workflowDispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteSkillAsync_UnknownWorkflowId_ShouldFailClosed()
    {
        var fetcher = new SequencedRemoteSkillFetcher(new SkillDefinition
        {
            Name = "workflow-skill",
            Description = "workflow",
            Instructions = "body",
            Source = SkillSource.Remote,
            Workflows =
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "known",
                    WorkflowYamls = ["name: known\nsteps: []\n"],
                },
            ],
        });
        var agent = CreateAgent("skill-runner-unknown-workflow", remoteSkillFetcher: fetcher);
        await agent.ActivateAsync();
        var command = CreateSkillRefCommand("workflow-skill");
        command.SkillRef.WorkflowId = "missing";
        await agent.HandleInitializeAsync(command);

        var act = () => InvokeExecuteSkillResultAsync(agent);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Workflow 'missing' was not found*");
    }

    [Fact]
    public async Task ExecuteSkillAsync_InlineFallback_ShouldOnlyRunWhenExplicitlyAllowed()
    {
        var provider = new StubStreamingProviderFactory("fallback output");
        var agent = CreateAgent("skill-runner-inline-fallback", providerFactory: provider);
        await agent.ActivateAsync();
        var command = CreateInitializeCommand();
        command.SkillRef = new SkillRunnerSkillReference
        {
            Name = "remote-down",
            Source = SkillRunnerSkillSource.Ornn,
            AllowInlineFallback = true,
        };
        await agent.HandleInitializeAsync(command);
        AttachNyxIdApiClient(agent, new RecordingHandler("""{"code":0,"msg":"success","data":{"message_id":"om_fallback"}}"""));

        var result = await InvokeExecuteSkillResultAsync(agent);

        result.Output.Should().Be("fallback output");
        provider.Requests.Should().ContainSingle();
        provider.Requests[0].Messages[0].Content.Should().Be("You are a summary report runner.");
    }

    [Fact]
    public async Task HandleTriggerAsync_RemoteSkillWithoutFetcher_ShouldPersistFetcherUnavailableFailure()
    {
        var provider = new StubStreamingProviderFactory("should-not-run");
        var agent = CreateAgent("skill-runner-missing-fetcher", providerFactory: provider);
        await agent.ActivateAsync();
        await agent.HandleInitializeAsync(CreateSkillRefCommand("daily-report"));

        var failed = await TriggerExhaustedAndReadFailureAsync(
            _store,
            "skill-runner-missing-fetcher",
            agent);

        failed.ErrorCode.Should().Be(SkillRunnerExecutionErrorCode.SkillFetcherUnavailable);
        failed.SkillName.Should().Be("daily-report");
        failed.SkillVersion.Should().BeEmpty();
        failed.WorkflowId.Should().BeEmpty();
        provider.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleTriggerAsync_RemoteSkillNotFound_ShouldPersistSkillNotFoundFailure()
    {
        var fetcher = new SequencedRemoteSkillFetcher((SkillDefinition?)null);
        var provider = new StubStreamingProviderFactory("should-not-run");
        var agent = CreateAgent(
            "skill-runner-skill-not-found",
            providerFactory: provider,
            remoteSkillFetcher: fetcher);
        await agent.ActivateAsync();
        await agent.HandleInitializeAsync(CreateSkillRefCommand("daily-report"));

        var failed = await TriggerExhaustedAndReadFailureAsync(
            _store,
            "skill-runner-skill-not-found",
            agent);

        failed.ErrorCode.Should().Be(SkillRunnerExecutionErrorCode.SkillNotFound);
        failed.SkillName.Should().Be("daily-report");
        failed.SkillVersion.Should().BeEmpty();
        failed.WorkflowId.Should().BeEmpty();
        fetcher.Requests.Should().ContainSingle().Which.Should().Be(("nyx-api-key", "daily-report"));
        provider.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleTriggerAsync_RemoteSkillAccessDenied_ShouldPersistAccessDeniedFailure()
    {
        var fetcher = new FailingRemoteSkillFetcher(
            RemoteSkillFetchException.AccessDenied(
                "daily-report",
                "NyxID proxy returned 403 for Ornn skill fetch.",
                403));
        var provider = new StubStreamingProviderFactory("should-not-run");
        var agent = CreateAgent(
            "skill-runner-skill-access-denied",
            providerFactory: provider,
            remoteSkillFetcher: fetcher);
        await agent.ActivateAsync();
        await agent.HandleInitializeAsync(CreateSkillRefCommand("daily-report"));

        var failed = await TriggerExhaustedAndReadFailureAsync(
            _store,
            "skill-runner-skill-access-denied",
            agent);

        failed.ErrorCode.Should().Be(SkillRunnerExecutionErrorCode.SkillAccessDenied);
        failed.SkillName.Should().Be("daily-report");
        failed.Error.Should().Contain("access denied");
        failed.Error.Should().Contain("missing proxy scope or service authorization");
        failed.Error.Should().Contain("recreate or rotate the scheduled agent key");
        fetcher.Requests.Should().ContainSingle().Which.Should().Be(("nyx-api-key", "daily-report"));
        provider.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleTriggerAsync_WorkflowSkillWithoutDispatchService_ShouldPersistDispatchUnavailableFailure()
    {
        var fetcher = new SequencedRemoteSkillFetcher(RemoteWorkflowSkill("workflow-skill", "daily_flow"));
        var provider = new StubStreamingProviderFactory("should-not-run");
        var agent = CreateAgent(
            "skill-runner-workflow-dispatch-missing",
            providerFactory: provider,
            remoteSkillFetcher: fetcher);
        await agent.ActivateAsync();
        var command = CreateSkillRefCommand("workflow-skill");
        command.SkillRef.WorkflowId = "daily_flow";
        await agent.HandleInitializeAsync(command);

        var failed = await TriggerExhaustedAndReadFailureAsync(
            _store,
            "skill-runner-workflow-dispatch-missing",
            agent);

        failed.ErrorCode.Should().Be(SkillRunnerExecutionErrorCode.WorkflowDispatchUnavailable);
        failed.ExecutionKind.Should().Be(SkillRunnerExecutionKind.Workflow);
        failed.SkillName.Should().Be("workflow-skill");
        failed.WorkflowId.Should().Be("daily_flow");
        provider.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleTriggerAsync_WorkflowDispatchRejected_ShouldPersistDispatchRejectedFailure()
    {
        var fetcher = new SequencedRemoteSkillFetcher(RemoteWorkflowSkill("workflow-skill", "daily_flow"));
        var provider = new StubStreamingProviderFactory("should-not-run");
        var workflowDispatch = new RecordingWorkflowDispatchService(
            CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Failure(
                WorkflowChatRunStartError.WorkflowNotFound));
        var agent = CreateAgent(
            "skill-runner-workflow-dispatch-rejected",
            providerFactory: provider,
            remoteSkillFetcher: fetcher,
            workflowDispatchService: workflowDispatch);
        await agent.ActivateAsync();
        var command = CreateSkillRefCommand("workflow-skill");
        command.SkillRef.WorkflowId = "daily_flow";
        await agent.HandleInitializeAsync(command);

        var failed = await TriggerExhaustedAndReadFailureAsync(
            _store,
            "skill-runner-workflow-dispatch-rejected",
            agent);

        failed.ErrorCode.Should().Be(SkillRunnerExecutionErrorCode.WorkflowDispatchRejected);
        failed.ExecutionKind.Should().Be(SkillRunnerExecutionKind.Workflow);
        failed.SkillName.Should().Be("workflow-skill");
        failed.WorkflowId.Should().Be("daily_flow");
        failed.Error.Should().Contain(nameof(WorkflowChatRunStartError.WorkflowNotFound));
        workflowDispatch.Commands.Should().ContainSingle();
        provider.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleTriggerAsync_WorkflowDispatchAccepted_ShouldPersistWorkflowReceiptFields()
    {
        var fetcher = new SequencedRemoteSkillFetcher(RemoteWorkflowSkill("workflow-skill", "daily_flow"));
        var provider = new StubStreamingProviderFactory("should-not-run");
        var workflowDispatch = new RecordingWorkflowDispatchService(
            CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
                new WorkflowChatRunAcceptedReceipt("workflow-run-1", "daily_flow", "cmd-1", "corr-1")));
        var agent = CreateAgent(
            "skill-runner-workflow-trigger-success",
            providerFactory: provider,
            remoteSkillFetcher: fetcher,
            workflowDispatchService: workflowDispatch);
        await agent.ActivateAsync();
        var command = CreateSkillRefCommand("workflow-skill");
        command.SkillRef.WorkflowId = "daily_flow";
        await agent.HandleInitializeAsync(command);
        AttachNyxIdApiClient(agent, new RecordingHandler("""{"code":0,"msg":"success","data":{"message_id":"om_workflow"}}"""));

        await agent.HandleTriggerAsync(new TriggerSkillRunnerExecutionCommand { Reason = "manual" });

        var completed = await ReadSingleCompletedEventAsync(_store, "skill-runner-workflow-trigger-success");
        completed.ExecutionKind.Should().Be(SkillRunnerExecutionKind.Workflow);
        completed.SkillName.Should().Be("workflow-skill");
        completed.WorkflowId.Should().Be("daily_flow");
        completed.WorkflowActorId.Should().Be("workflow-run-1");
        completed.WorkflowName.Should().Be("daily_flow");
        completed.WorkflowCommandId.Should().Be("cmd-1");
        completed.WorkflowCorrelationId.Should().Be("corr-1");
        completed.Output.Should().Contain("Workflow start accepted");
        workflowDispatch.Commands.Should().ContainSingle();
        provider.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteSkillAsync_AutoOverLimitOutput_ShouldUseDocxDecisionReply_WhenLinkReturned()
    {
        var output = new string('x', SkillRunnerStreamingReplySink.MaxLarkTextLength + 100);
        var provider = new StubStreamingProviderFactory(
            new StubStreamingTurn([output]),
            new StubStreamingTurn(
                [],
                [
                    new ToolCall
                    {
                        Id = "call-docx",
                        Name = "lark_docx_create",
                        ArgumentsJson = "{}",
                    },
                ]),
            new StubStreamingTurn(["Full output moved to https://example.feishu.cn/docx/doccn_123"]));
        var handler = new SequencedHandler(
            """{"code":0,"msg":"success","data":{"card_id":"card_doc_link"}}""",
            """{"code":0,"msg":"success","data":{"message_id":"om_doc_link"}}""",
            """{"code":0,"msg":"success","data":{}}""",
            """{"code":0,"msg":"success","data":{}}""");
        var docxTool = new FixedResultTool(
            "lark_docx_create",
            """{"success":true,"document_token":"doccn_123","document_url":"https://example.feishu.cn/docx/doccn_123","visibility_applied":true}""");
        var agent = CreateAgent(
            "skill-runner-docx-success",
            providerFactory: provider,
            toolSources: [new SingleToolSource(docxTool)]);
        await agent.ActivateAsync();
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig.LarkReceiveId = "oc_chat_1";
        initialize.OutboundConfig.LarkReceiveIdType = "chat_id";
        await agent.HandleInitializeAsync(initialize);
        AttachNyxIdApiClient(agent, handler);

        var result = await InvokeExecuteSkillAsync(agent);

        result.Should().Be(output);
        provider.Requests.Should().HaveCount(3);
        provider.Requests[1].RequestId.Should().EndWith(":lark-docx");
        provider.Requests[1].ToolContext.Should().NotBeNull();
        var docxToolContext = provider.Requests[1].ToolContext!;
        docxToolContext.Routing.MaxToolRoundsOverride.Should().Be(2);
        docxToolContext.ExternalMetadata.Should().Contain(ChannelMetadataKeys.LarkReceiveId, "oc_chat_1");
        docxToolContext.ExternalMetadata.Should().Contain(ChannelMetadataKeys.LarkReceiveIdType, "chat_id");
        docxToolContext.ExternalMetadata.Should().Contain(ChannelMetadataKeys.LarkOutboundProxySlug, "api-lark-bot");
        provider.Requests[2].Messages.Any(message =>
            message.Role == "tool" &&
            message.Content is not null &&
            message.Content.Contains("https://example.feishu.cn/docx/doccn_123", StringComparison.Ordinal))
            .Should().BeTrue();
        handler.Requests.Should().HaveCount(4);
        ExtractLarkMessageType(handler.Bodies[1]!).Should().Be("interactive");
        ExtractInteractiveCardId(handler.Bodies[1]!).Should().Be("card_doc_link");
        ExtractCardKitStreamContent(handler.Bodies[2]!).Should().Be("Full output moved to https://example.feishu.cn/docx/doccn_123");
    }

    [Fact]
    public async Task ExecuteSkillAsync_FeishuDocOutputFormat_ShouldUseDocxDecisionReply_EvenWhenOutputFitsText()
    {
        var output = "short scheduled report";
        var provider = new StubStreamingProviderFactory(
            new StubStreamingTurn([output]),
            new StubStreamingTurn(
                [],
                [
                    new ToolCall
                    {
                        Id = "call-docx",
                        Name = "lark_docx_create",
                        ArgumentsJson = "{}",
                    },
                ]),
            new StubStreamingTurn(["Full output moved to https://example.feishu.cn/docx/doccn_forced"]));
        var handler = new SequencedHandler("""{"code":0,"msg":"success","data":{"message_id":"om_doc_link"}}""");
        var docxTool = new FixedResultTool(
            "lark_docx_create",
            """{"success":true,"document_token":"doccn_forced","document_url":"https://example.feishu.cn/docx/doccn_forced","visibility_applied":true}""");
        var agent = CreateAgent(
            "skill-runner-docx-forced",
            providerFactory: provider,
            toolSources: [new SingleToolSource(docxTool)]);
        await agent.ActivateAsync();
        var initialize = CreateInitializeCommand();
        initialize.OutputFormat = SkillRunnerOutputFormat.FeishuDoc;
        initialize.OutboundConfig.OutputFormat = SkillRunnerOutputFormat.FeishuDoc;
        initialize.OutboundConfig.LarkReceiveId = "oc_chat_1";
        initialize.OutboundConfig.LarkReceiveIdType = "chat_id";
        await agent.HandleInitializeAsync(initialize);
        AttachNyxIdApiClient(agent, handler);

        var result = await InvokeExecuteSkillAsync(agent);

        result.Should().Be(output);
        provider.Requests.Should().ContainSingle("FEISHU_DOC mode should create the document by direct tool execution, not by a second LLM decision round");
        docxTool.LastArgumentsJson.Should().NotBeNull();
        using (var arguments = JsonDocument.Parse(docxTool.LastArgumentsJson!))
        {
            arguments.RootElement.GetProperty("title").GetString().Should().Be("summary");
            arguments.RootElement.GetProperty("markdown_text").GetString().Should().Be(output);
            arguments.RootElement.GetProperty("visibility").GetString().Should().Be("readable");
        }
        docxTool.LastContext.Should().NotBeNull();
        docxTool.LastContext!.Request.RequestId.Should().EndWith(":lark-docx");
        docxTool.LastContext.ExternalMetadata.Should().Contain(ChannelMetadataKeys.LarkReceiveId, "oc_chat_1");
        docxTool.LastContext.ExternalMetadata.Should().Contain(ChannelMetadataKeys.LarkReceiveIdType, "chat_id");
        handler.Requests.Should().ContainSingle();
        ExtractLarkText(handler.Bodies[0]!).Should().Be("Full output moved to https://example.feishu.cn/docx/doccn_forced");
    }

    [Fact]
    public async Task ExecuteSkillAsync_TextOutputFormat_ShouldChunkLongOutput_AndSkipDocDecision()
    {
        var output = string.Join("\n\n", Enumerable.Repeat(
            new string('x', SkillRunnerStreamingReplySink.MaxLarkTextLength - 1_000),
            2));
        var expectedChunks = SkillRunnerOutputChunker.Split(output);
        var provider = new StubStreamingProviderFactory(new StubStreamingTurn([output]));
        var handler = new SequencedHandler(
            """{"code":0,"msg":"success","data":{"message_id":"om_part_1"}}""",
            """{"code":0,"msg":"success","data":{"message_id":"om_part_2"}}""");
        var agent = CreateAgent("skill-runner-text-forced", providerFactory: provider);
        await agent.ActivateAsync();
        var initialize = CreateInitializeCommand();
        initialize.OutputFormat = SkillRunnerOutputFormat.Text;
        initialize.OutboundConfig.OutputFormat = SkillRunnerOutputFormat.Text;
        initialize.OutboundConfig.LarkReceiveId = "oc_chat_1";
        initialize.OutboundConfig.LarkReceiveIdType = "chat_id";
        await agent.HandleInitializeAsync(initialize);
        AttachNyxIdApiClient(agent, handler);

        var result = await InvokeExecuteSkillAsync(agent);

        result.Should().Be(output);
        provider.Requests.Should().ContainSingle("text mode must not spend a second LLM/tool round deciding doc creation");
        handler.Requests.Should().HaveCount(expectedChunks.Count);
        ExtractLarkText(handler.Bodies[0]!).Should().Be(expectedChunks[0]);
        ExtractLarkText(handler.Bodies[1]!).Should().Be(expectedChunks[1]);
    }

    [Fact]
    public async Task ExecuteSkillAsync_AutoOverLimitOutput_ShouldFallBackToChunks_WhenDocDecisionHasNoLink()
    {
        var output = string.Join("\n\n", Enumerable.Repeat(
            new string('x', SkillRunnerStreamingReplySink.MaxLarkTextLength - 1_000),
            2));
        var expectedChunks = SkillRunnerOutputChunker.Split(output);
        var provider = new StubStreamingProviderFactory(
            new StubStreamingTurn([output]),
            new StubStreamingTurn(["DOCX_FALLBACK"]));
        var handler = new SequencedHandler(
            """{"code":0,"msg":"success","data":{"message_id":"om_part_1"}}""",
            """{"code":0,"msg":"success","data":{"message_id":"om_part_2"}}""");
        var agent = CreateAgent("skill-runner-docx-fallback", providerFactory: provider);
        await agent.ActivateAsync();
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig.LarkReceiveId = "oc_chat_1";
        initialize.OutboundConfig.LarkReceiveIdType = "chat_id";
        await agent.HandleInitializeAsync(initialize);
        AttachNyxIdApiClient(agent, handler);

        var result = await InvokeExecuteSkillAsync(agent);

        result.Should().Be(output);
        provider.Requests.Should().HaveCount(2);
        handler.Requests.Should().HaveCount(expectedChunks.Count);
        ExtractLarkText(handler.Bodies[0]!).Should().Be(expectedChunks[0]);
        ExtractLarkText(handler.Bodies[1]!).Should().Be(expectedChunks[1]);
    }

    [Fact]
    public async Task ExecuteSkillAsync_TextOutputFormat_ShouldChunkLongOutput_WhenDocDecisionWouldThrow()
    {
        var output = string.Join("\n\n", Enumerable.Repeat(
            new string('x', SkillRunnerStreamingReplySink.MaxLarkTextLength - 1_000),
            2));
        var expectedChunks = SkillRunnerOutputChunker.Split(output);
        var provider = new StubStreamingProviderFactory(
            new StubStreamingTurn([output]),
            new StubStreamingTurn(new InvalidOperationException("docx decision failed")));
        var handler = new SequencedHandler(
            """{"code":0,"msg":"success","data":{"message_id":"om_part_1"}}""",
            """{"code":0,"msg":"success","data":{"message_id":"om_part_2"}}""");
        var agent = CreateAgent("skill-runner-docx-exception-fallback", providerFactory: provider);
        await agent.ActivateAsync();
        var initialize = CreateInitializeCommand();
        initialize.OutputFormat = SkillRunnerOutputFormat.Text;
        initialize.OutboundConfig.OutputFormat = SkillRunnerOutputFormat.Text;
        await agent.HandleInitializeAsync(initialize);
        AttachNyxIdApiClient(agent, handler);

        var result = await InvokeExecuteSkillAsync(agent);

        result.Should().Be(output);
        provider.Requests.Should().ContainSingle("text mode must skip the doc decision and deliver chunks directly");
        handler.Requests.Should().HaveCount(expectedChunks.Count);
        ExtractLarkText(handler.Bodies[0]!).Should().Be(expectedChunks[0]);
        ExtractLarkText(handler.Bodies[1]!).Should().Be(expectedChunks[1]);
    }

    [Fact]
    public async Task ExecuteSkillAsync_BelowLimitOutput_ShouldNotInvokeDocDecision()
    {
        var provider = new StubStreamingProviderFactory("short output");
        var handler = new SequencedHandler(
            """{"code":0,"msg":"success","data":{"card_id":"card_short"}}""",
            """{"code":0,"msg":"success","data":{"message_id":"om_short"}}""",
            """{"code":0,"msg":"success","data":{}}""",
            """{"code":0,"msg":"success","data":{}}""");
        var agent = CreateAgent("skill-runner-docx-not-needed", providerFactory: provider);
        await agent.ActivateAsync();
        await agent.HandleInitializeAsync(CreateInitializeCommand());
        AttachNyxIdApiClient(agent, handler);

        var result = await InvokeExecuteSkillAsync(agent);

        result.Should().Be("short output");
        provider.Requests.Should().ContainSingle();
        handler.Requests.Should().HaveCount(4);
        ExtractLarkMessageType(handler.Bodies[1]!).Should().Be("interactive");
        ExtractCardKitStreamContent(handler.Bodies[2]!).Should().Be("short output");
    }

    [Fact]
    public async Task SkillRunnerStreamingRunState_CoalescesInsideThrottleAndDispatchesAfterThrottle()
    {
        var handler = new SequencedHandler(
            """{"code":0,"msg":"success","data":{"message_id":"om_stream"}}""",
            """{"code":0,"msg":"success","data":{}}""");
        var sink = CreateStreamingSink(handler);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 19, 9, 0, 0, TimeSpan.Zero));
        var runState = CreateStreamingRunState(sink, TimeSpan.FromMilliseconds(300), time);

        await InvokeStreamingRunStateAsync(runState, "OnDeltaAsync", "a");
        time.Advance(TimeSpan.FromMilliseconds(100));
        await InvokeStreamingRunStateAsync(runState, "OnDeltaAsync", "ab");
        time.Advance(TimeSpan.FromMilliseconds(250));
        await InvokeStreamingRunStateAsync(runState, "OnDeltaAsync", "abc");

        handler.Requests.Should().HaveCount(2);
        ExtractLarkText(handler.Bodies[0]!).Should().Be("a");
        ExtractLarkText(handler.Bodies[1]!).Should().Be("abc");
    }

    [Fact]
    public async Task SkillRunnerStreamingRunState_TruncatesBeforeDedupeAndSuppressesDuplicateFinal()
    {
        var handler = new SequencedHandler("""{"code":0,"msg":"success","data":{"message_id":"om_truncated"}}""");
        using var sink = CreateStreamingSink(handler);
        var runState = CreateStreamingRunState(sink, TimeSpan.Zero, TimeProvider.System);
        var longText = new string('x', SkillRunnerStreamingReplySink.MaxLarkTextLength + 100);

        await InvokeStreamingRunStateAsync(runState, "OnDeltaAsync", longText);
        await InvokeStreamingRunStateAsync(runState, "OnDeltaAsync", longText + "more");
        await InvokeStreamingRunStateAsync(runState, "FinalizeAsync", longText + "final");

        handler.Requests.Should().ContainSingle("all three snapshots cap to the same Lark body");
        ExtractLarkText(handler.Bodies[0]!).Should().Be(SkillRunnerStreamingReplySink.TruncateForLark(longText));
    }

    private static async Task<LLMControlContext> InvokeBuildExecutionLlmControlAsync(
        SkillRunnerGAgent agent)
    {
        var method = typeof(SkillRunnerGAgent).GetMethod(
            "BuildExecutionLlmControlAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(CancellationToken)],
            modifiers: null);
        method.Should().NotBeNull();
        var task = (Task<LLMControlContext>)method!.Invoke(agent, [CancellationToken.None])!;
        return await task;
    }

    private static async Task<string> InvokeExecuteSkillAsync(SkillRunnerGAgent agent)
    {
        var result = await InvokeExecuteSkillResultObjectAsync(agent);
        return ReadRequiredProperty<string>(result, "Output");
    }

    private static async Task<SkillRunnerExecutionResultSnapshot> InvokeExecuteSkillResultAsync(
        SkillRunnerGAgent agent)
    {
        var result = await InvokeExecuteSkillResultObjectAsync(agent);
        return new SkillRunnerExecutionResultSnapshot(
            ReadRequiredProperty<string>(result, "Output"),
            ReadRequiredProperty<SkillRunnerExecutionKind>(result, "ExecutionKind"),
            ReadRequiredProperty<string>(result, "SkillName"),
            ReadRequiredProperty<string>(result, "SkillVersion"),
            ReadRequiredProperty<string>(result, "WorkflowId"),
            ReadProperty<WorkflowChatRunAcceptedReceipt>(result, "WorkflowReceipt"));
    }

    private static async Task<SkillRunnerExecutionFailedEvent> TriggerExhaustedAndReadFailureAsync(
        InMemoryEventStore store,
        string actorId,
        SkillRunnerGAgent agent)
    {
        await agent.HandleTriggerAsync(new TriggerSkillRunnerExecutionCommand
        {
            Reason = "manual",
            RetryAttempt = SkillRunnerDefaults.MaxRetryAttempts,
        });
        return await ReadSingleFailedEventAsync(store, actorId);
    }

    private static async Task<SkillRunnerExecutionFailedEvent> ReadSingleFailedEventAsync(
        InMemoryEventStore store,
        string actorId)
    {
        var persisted = await store.GetEventsAsync(actorId);
        return persisted
            .Select(x => x.EventData)
            .Where(x => x.Is(SkillRunnerExecutionFailedEvent.Descriptor))
            .Select(x => x.Unpack<SkillRunnerExecutionFailedEvent>())
            .Should()
            .ContainSingle()
            .Subject;
    }

    private static Task AppendLegacyInitializedEventAsync(
        InMemoryEventStore store,
        string actorId,
        SkillRunnerOutboundConfig outboundConfig) =>
        store.AppendAsync(
            actorId,
            [new StateEvent
            {
                EventId = "legacy-init-1",
                Timestamp = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 5, 19, 8, 0, 0, TimeSpan.Zero)),
                Version = 1,
                EventType = SkillRunnerInitializedEvent.Descriptor.FullName,
                EventData = Any.Pack(new SkillRunnerInitializedEvent
                {
                    SkillName = "summary",
                    TemplateName = "summary",
                    SkillContent = "You are a summary report runner.",
                    ExecutionPrompt = "Run the report.",
                    ScheduleCron = string.Empty,
                    ScheduleTimezone = SkillRunnerDefaults.DefaultTimezone,
                    Enabled = true,
                    ScopeId = "scope-1",
                    ProviderName = SkillRunnerDefaults.DefaultProviderName,
                    OutboundConfig = outboundConfig,
                }),
                AgentId = actorId,
            }],
            expectedVersion: 0);

    private static async Task<SkillRunnerExecutionCompletedEvent> ReadSingleCompletedEventAsync(
        InMemoryEventStore store,
        string actorId)
    {
        var persisted = await store.GetEventsAsync(actorId);
        return persisted
            .Select(x => x.EventData)
            .Where(x => x.Is(SkillRunnerExecutionCompletedEvent.Descriptor))
            .Select(x => x.Unpack<SkillRunnerExecutionCompletedEvent>())
            .Should()
            .ContainSingle()
            .Subject;
    }

    private static async Task<DeliveryProducedEvent> ReadSingleDeliveryProducedEventAsync(
        InMemoryEventStore store,
        string actorId) =>
        (await ReadDeliveryProducedEventsAsync(store, actorId)).Should().ContainSingle().Subject;

    private static async Task<IReadOnlyList<DeliveryProducedEvent>> ReadDeliveryProducedEventsAsync(
        InMemoryEventStore store,
        string actorId)
    {
        var persisted = await store.GetEventsAsync(actorId);
        return persisted
            .Select(x => x.EventData)
            .Where(x => x.Is(DeliveryProducedEvent.Descriptor))
            .Select(x => x.Unpack<DeliveryProducedEvent>())
            .ToArray();
    }

    private static async Task<object> InvokeExecuteSkillResultObjectAsync(SkillRunnerGAgent agent)
    {
        var method = typeof(SkillRunnerGAgent).GetMethod(
            "ExecuteSkillAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        var task = (Task)method!.Invoke(
            agent,
            [new DateTimeOffset(2026, 5, 19, 9, 0, 0, TimeSpan.Zero), "test", CancellationToken.None])!;
        await task;
        var resultProperty = task.GetType().GetProperty("Result");
        resultProperty.Should().NotBeNull();
        return resultProperty!.GetValue(task)!;
    }

    private static T ReadRequiredProperty<T>(object instance, string propertyName) =>
        ReadProperty<T>(instance, propertyName)
        ?? throw new InvalidOperationException($"Property {propertyName} was null.");

    private static T? ReadProperty<T>(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        property.Should().NotBeNull();
        return (T?)property!.GetValue(instance);
    }

    private static bool IsExternalExecutionTerminalEvent(Any evt)
    {
        if (evt.Is(SkillRunnerExecutionCompletedEvent.Descriptor))
            return evt.Unpack<SkillRunnerExecutionCompletedEvent>().ExternalTriggerIdentity is not null;
        if (evt.Is(SkillRunnerExecutionFailedEvent.Descriptor))
            return evt.Unpack<SkillRunnerExecutionFailedEvent>().ExternalTriggerIdentity is not null;
        return evt.Is(SkillRunnerExecutionRejectedEvent.Descriptor) &&
               evt.Unpack<SkillRunnerExecutionRejectedEvent>().ExternalTriggerIdentity is not null;
    }

    private static Task DispatchCronFireAsync(
        SkillRunnerGAgent agent,
        string envelopeId,
        string? cronOccurrenceKey,
        TriggerSkillRunnerExecutionCommand? command = null)
    {
        var envelope = new EventEnvelope
        {
            Id = envelopeId,
            Timestamp = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 16, 0, 0, 0, TimeSpan.Zero)),
            Payload = Any.Pack(command ?? new TriggerSkillRunnerExecutionCommand
            {
                Reason = SkillRunnerDefaults.ScheduleTriggerReason,
            }),
            Route = EnvelopeRouteSemantics.CreateDirect("scheduled-dispatch:test", agent.Id),
            Propagation = new EnvelopePropagation(),
        };

        if (!string.IsNullOrWhiteSpace(cronOccurrenceKey))
            envelope.Propagation.Baggage[ScheduledDispatchMetadataKeys.IdempotencyKey] = cronOccurrenceKey.Trim();

        return agent.HandleEventAsync(envelope);
    }

    private sealed record SkillRunnerExecutionResultSnapshot(
        string Output,
        SkillRunnerExecutionKind ExecutionKind,
        string SkillName,
        string SkillVersion,
        string WorkflowId,
        WorkflowChatRunAcceptedReceipt? WorkflowReceipt);

    private static object CreateStreamingRunState(
        SkillRunnerStreamingReplySink sink,
        TimeSpan throttle,
        TimeProvider timeProvider)
    {
        var type = typeof(SkillRunnerGAgent).GetNestedType(
            "SkillRunnerStreamingRunState",
            BindingFlags.NonPublic);
        type.Should().NotBeNull();
        return Activator.CreateInstance(type!, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null,
            args: [sink, throttle, timeProvider],
            culture: null)!;
    }

    private static SkillRunnerStreamingReplySink CreateStreamingSink(HttpMessageHandler handler)
    {
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });
        return new SkillRunnerStreamingReplySink(
            new LarkOutboundDispatcher(client, NullLogger<LarkOutboundDispatcher>.Instance),
            new LarkSendNewMessageRequest(
                "nyx-api-key",
                "api-lark-bot",
                MessageType: "text",
                ContentJson: string.Empty,
                PrimaryTarget: new LarkReceiveTarget("oc_chat_1", "chat_id", FellBackToPrefixInference: false)),
            (_, detail) => detail,
            logger: null,
            editClient: client);
    }

    private static Task InvokeStreamingRunStateAsync(object runState, string methodName, string text)
    {
        var method = runState.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
        method.Should().NotBeNull();
        return (Task)method!.Invoke(runState, [text, CancellationToken.None])!;
    }

    private static string ExtractLarkText(string body)
    {
        using var document = JsonDocument.Parse(body);
        var content = document.RootElement.GetProperty("content").GetString();
        content.Should().NotBeNull();
        using var contentDocument = JsonDocument.Parse(content!);
        return contentDocument.RootElement.GetProperty("text").GetString()!;
    }

    private static string ExtractLarkMessageType(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("msg_type").GetString()!;
    }

    private static string ExtractInteractiveCardId(string body)
    {
        using var document = JsonDocument.Parse(body);
        var content = document.RootElement.GetProperty("content").GetString();
        content.Should().NotBeNull();
        using var contentDocument = JsonDocument.Parse(content!);
        return contentDocument.RootElement
            .GetProperty("data")
            .GetProperty("card_id")
            .GetString()!;
    }

    private static string ExtractCardKitCreateType(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("type").GetString()!;
    }

    private static string ExtractCardKitStreamContent(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("content").GetString()!;
    }

    private static string ExtractCardKitSettings(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("settings").GetString()!;
    }

    internal sealed class StubOwnerLlmConfigSource(OwnerLlmConfig config) : IOwnerLlmConfigSource
    {
        public List<string> RequestedScopeIds { get; } = new();

        public Task<OwnerLlmConfig> GetForScopeAsync(string scopeId, CancellationToken ct = default)
        {
            RequestedScopeIds.Add(scopeId);
            return Task.FromResult(config);
        }
    }

    internal sealed class ThrowingOwnerLlmConfigSource : IOwnerLlmConfigSource
    {
        public Task<OwnerLlmConfig> GetForScopeAsync(string scopeId, CancellationToken ct = default) =>
            throw new InvalidOperationException("projection unavailable");
    }

    private static void AttachNyxIdApiClient(SkillRunnerGAgent agent, HttpMessageHandler handler)
    {
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });
        var field = typeof(SkillRunnerGAgent).GetField(
            "_nyxIdApiClient",
            BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull();
        field!.SetValue(agent, client);
    }

    private static Task InvokeSendOutputAsync(SkillRunnerGAgent agent, string output)
    {
        // Disambiguate against the 3-arg `SendOutputAsync(output, providerSlugOverride, ct)`
        // overload introduced for the failure-notification fallback (#423 §C). The 2-arg
        // overload still routes through the primary `NyxProviderSlug`, which is what every
        // existing test exercises.
        var method = typeof(SkillRunnerGAgent).GetMethod(
            "SendOutputAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string), typeof(CancellationToken)],
            modifiers: null);
        method.Should().NotBeNull();
        return (Task)method!.Invoke(agent, [output, CancellationToken.None])!;
    }

    private static Task InvokeTrySendFailureAsync(SkillRunnerGAgent agent, string error)
    {
        var method = typeof(SkillRunnerGAgent).GetMethod(
            "TrySendFailureAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        return (Task)method!.Invoke(agent, [error, CancellationToken.None])!;
    }

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string?> Bodies { get; } = [];
        public HttpRequestMessage? LastRequest => Requests.LastOrDefault();
        public string? LastBody => Bodies.LastOrDefault();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class RecordingExecutionWriteDispatcher : IProjectionWriteDispatcher<SkillRunnerExecutionDocument>
    {
        public List<SkillRunnerExecutionDocument> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            SkillRunnerExecutionDocument readModel,
            CancellationToken ct = default)
        {
            Upserts.Add(readModel.Clone());
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(ProjectionWriteResult.Applied());
    }

    private sealed class FixedScheduledSecretVault : ISecretVault
    {
        public const string ApiKeyId = "key-1";
        private const string Ref = "sec-scheduled-test";
        private const string OwnerScopeKey = "scope-key-1";
        private const string Secret = "nyx-api-key";

        public static SecretReference Reference() => new()
        {
            Ref = Ref,
            Purpose = CredentialSecretPurposes.ScheduledNyxApiKey,
            OwnerScopeKey = OwnerScopeKey,
        };

        public Task<StoreSecretResult> PutAsync(StoreSecretRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new StoreSecretResult(Reference()));
        }

        public Task<ResolveSecretResult> ResolveAsync(ResolveSecretRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (string.Equals(request.Ref, Ref, StringComparison.Ordinal) &&
                string.Equals(request.Purpose, CredentialSecretPurposes.ScheduledNyxApiKey, StringComparison.Ordinal) &&
                string.Equals(request.OwnerScopeKey, OwnerScopeKey, StringComparison.Ordinal) &&
                string.Equals(request.SubjectId, ApiKeyId, StringComparison.Ordinal))
            {
                return Task.FromResult(new ResolveSecretResult(Reference(), Secret));
            }

            return Task.FromResult(new ResolveSecretResult(null, null));
        }

        public Task<RotateSecretResult> RotateAsync(RotateSecretRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new RotateSecretResult(Reference()));
        }

        public Task<RevokeSecretResult> RevokeAsync(RevokeSecretRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new RevokeSecretResult(true));
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset now) : Aevatar.CQRS.Projection.Core.Abstractions.IProjectionClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class RecordingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> Timeouts { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Timeouts.Add(request);
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Timeouts.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            throw new NotSupportedException("Timer scheduling is not required for this test.");
        }

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            _ = lease;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default)
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLarkOutboundDispatcher : ILarkOutboundDispatcher
    {
        public List<LarkSendNewMessageRequest> Requests { get; } = [];

        public Task<LarkSendNewMessageResult> SendNewMessageAsync(
            LarkSendNewMessageRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(LarkSendNewMessageResult.Sent(
                "om_one_shot",
                request.PrimaryTarget,
                usedFallback: false));
        }
    }

    /// <summary>
    /// Returns a different response per request in the order given. Used to simulate the
    /// `bot not in chat` rejection on the primary attempt followed by a successful fallback
    /// retry.
    /// </summary>
    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string?> Bodies { get; } = new();

        public SequencedHandler(params string[] responses)
        {
            _responses = new Queue<string>(responses);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            var body = _responses.Count > 0 ? _responses.Dequeue() : """{"code":0,"msg":"success","data":{"message_id":"om_success"}}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private SkillRunnerGAgent CreateAgent(
        string actorId,
        ServiceProvider? serviceProvider = null,
        IOwnerLlmConfigSource? ownerLlmConfigSource = null,
        ILLMProviderFactory? providerFactory = null,
        IEnumerable<IAgentToolSource>? toolSources = null,
        IRemoteSkillFetcher? remoteSkillFetcher = null,
        ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>? workflowDispatchService = null)
    {
        var resolvedServices = serviceProvider ?? _serviceProvider;
        var agent = new SkillRunnerGAgent(
            llmProviderFactory: providerFactory,
            ownerLlmConfigSource: ownerLlmConfigSource,
            toolSources: toolSources,
            remoteSkillFetcher: remoteSkillFetcher,
            workflowDispatchService: workflowDispatchService,
            larkOutboundDispatcher: resolvedServices.GetService<ILarkOutboundDispatcher>())
        {
            Services = resolvedServices,
            EventSourcingBehaviorFactory =
                resolvedServices.GetRequiredService<IEventSourcingBehaviorFactory<SkillRunnerState>>(),
        };
        AssignActorId(agent, actorId);
        return agent;
    }

    private static ServiceProvider BuildServiceProvider(
        IEventStore eventStore,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(eventStore);
        services.AddSingleton<ISecretVault>(new FixedScheduledSecretVault());
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddSingleton<IActorRuntimeCallbackScheduler>(new RecordingCallbackScheduler());
        services.AddTransient(
            typeof(IEventSourcingBehaviorFactory<>),
            typeof(DefaultEventSourcingBehaviorFactory<>));
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static InitializeSkillRunnerCommand CreateInitializeCommand() => new()
    {
        SkillName = "summary",
        TemplateName = "summary",
        SkillContent = "You are a summary report runner.",
        ExecutionPrompt = "Run the report.",
        ScheduleCron = string.Empty,
        ScheduleTimezone = SkillRunnerDefaults.DefaultTimezone,
        Enabled = true,
        ScopeId = "scope-1",
        ProviderName = SkillRunnerDefaults.DefaultProviderName,
        OutboundConfig = new SkillRunnerOutboundConfig
        {
            ConversationId = "oc_chat_1",
            NyxProviderSlug = "api-lark-bot",
            ApiKeyId = FixedScheduledSecretVault.ApiKeyId,
            NyxApiKey = string.Empty,
            NyxApiKeyReference = FixedScheduledSecretVault.Reference(),
        },
    };

    private static InitializeSkillRunnerCommand CreateTextOutputInitializeCommand()
    {
        var command = CreateInitializeCommand();
        command.OutputFormat = SkillRunnerOutputFormat.Text;
        command.OutboundConfig.OutputFormat = SkillRunnerOutputFormat.Text;
        return command;
    }

    private static InitializeSkillRunnerCommand CreateOneShotInitializeCommand(DateTimeOffset runAtUtc) => new()
    {
        SkillName = SkillRunnerDefaults.OneShotSkillName,
        TemplateName = "Reminder",
        ExecutionPrompt = "Send the configured one-shot reminder message exactly as written.",
        ScheduleMode = SkillRunnerScheduleMode.OneShot,
        OneShotRunAt = Timestamp.FromDateTimeOffset(runAtUtc.ToUniversalTime()),
        OneShotMessage = "Submit the report",
        ScheduleCron = string.Empty,
        ScheduleTimezone = SkillRunnerDefaults.DefaultTimezone,
        Enabled = true,
        ScopeId = "scope-1",
        ProviderName = SkillRunnerDefaults.DefaultProviderName,
        OutboundConfig = new SkillRunnerOutboundConfig
        {
            ConversationId = "oc_chat_1",
            NyxProviderSlug = "api-lark-bot",
            ApiKeyId = FixedScheduledSecretVault.ApiKeyId,
            NyxApiKey = string.Empty,
            NyxApiKeyReference = FixedScheduledSecretVault.Reference(),
        },
    };

    private static InitializeSkillRunnerCommand CreateInitializeCommandWithExternalSource(
        string sourceId = "webhook-main")
    {
        var command = CreateInitializeCommand();
        command.ExternalTriggerSources.Add(new ExternalTriggerSource
        {
            SourceId = sourceId,
            Kind = ExternalTriggerSourceKind.Webhook,
            Enabled = true,
            DisplayName = "Webhook main",
        });
        return command;
    }

    private static SkillRunnerExternalTriggerIdentity CreateExternalIdentity(
        string deliveryId,
        string sourceId = "webhook-main") => new()
    {
        SourceId = sourceId,
        DeliveryId = deliveryId,
        AdmissionId = $"admit-{deliveryId}",
        Kind = ExternalTriggerSourceKind.Webhook,
        ReceivedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
            new DateTimeOffset(2026, 6, 6, 8, 0, 0, TimeSpan.Zero)),
        PayloadSummary = "test delivery",
        PayloadRef = $"test://deliveries/{deliveryId}",
    };

    private static InitializeSkillRunnerCommand CreateSkillRefCommand(string name)
    {
        var command = CreateInitializeCommand();
        command.SkillContent = string.Empty;
        command.SkillName = name;
        command.TemplateName = name;
        command.SkillRef = new SkillRunnerSkillReference
        {
            Name = name,
            Source = SkillRunnerSkillSource.Ornn,
        };
        return command;
    }

    private static SkillDefinition RemotePromptSkill(string name, string instructions) => new()
    {
        Name = name,
        Description = "remote prompt skill",
        Instructions = instructions,
        Source = SkillSource.Remote,
    };

    private static SkillDefinition RemoteWorkflowSkill(string name, string workflowId) => new()
    {
        Name = name,
        Description = "remote workflow skill",
        Instructions = "workflow instructions",
        Source = SkillSource.Remote,
        Workflows =
        [
            new SkillWorkflowDescriptor
            {
                WorkflowId = workflowId,
                WorkflowYamls = [$"name: {workflowId}\nsteps: []\n"],
            },
        ],
    };

    private static void AssignActorId(GAgentBase agent, string actorId)
    {
        var setIdMethod = typeof(GAgentBase).GetMethod(
            "SetId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        setIdMethod.Should().NotBeNull();
        setIdMethod!.Invoke(agent, [actorId]);
    }

    private sealed record StubStreamingTurn(
        IReadOnlyList<string> Deltas,
        IReadOnlyList<ToolCall>? ToolCalls = null,
        Exception? Error = null)
    {
        public StubStreamingTurn(Exception error)
            : this([], null, error)
        {
        }
    }

    private sealed class SingleToolSource(IAgentTool tool) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IAgentTool>>([tool]);
    }

    private sealed class FixedResultTool(string name, string result) : IAgentTool
    {
        public string Name => name;
        public string Description => "Fixed result test tool";
        public string ParametersSchema => "{}";
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;
        public string? LastArgumentsJson { get; private set; }
        public AgentToolExecutionContext? LastContext { get; private set; }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            LastArgumentsJson = argumentsJson;
            LastContext = AgentToolRequestContext.Current;
            return Task.FromResult(result);
        }
    }

    private sealed class SequencedRemoteSkillFetcher(params SkillDefinition?[] skills) : IRemoteSkillFetcher
    {
        private readonly Queue<SkillDefinition?> _skills = new(skills);

        public List<(string AccessToken, string NameOrId)> Requests { get; } = [];

        public Task<SkillDefinition?> FetchSkillAsync(
            string accessToken,
            string nameOrId,
            CancellationToken ct = default)
        {
            Requests.Add((accessToken, nameOrId));
            return Task.FromResult(_skills.Count == 0 ? null : _skills.Dequeue());
        }
    }

    private sealed class FailingRemoteSkillFetcher(Exception exception) : IRemoteSkillFetcher
    {
        public List<(string AccessToken, string NameOrId)> Requests { get; } = [];

        public Task<SkillDefinition?> FetchSkillAsync(
            string accessToken,
            string nameOrId,
            CancellationToken ct = default)
        {
            Requests.Add((accessToken, nameOrId));
            return Task.FromException<SkillDefinition?>(exception);
        }
    }

    private sealed class RecordingWorkflowDispatchService(
        CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> result)
        : ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
    {
        public List<WorkflowChatRunRequest> Commands { get; } = [];

        public Task<CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>> DispatchAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(result);
        }
    }

    private sealed class StubStreamingProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        private readonly Queue<StubStreamingTurn> _turns;

        public StubStreamingProviderFactory(params string[] deltas)
            : this(new StubStreamingTurn(deltas))
        {
        }

        public StubStreamingProviderFactory(params StubStreamingTurn[] turns)
        {
            _turns = new Queue<StubStreamingTurn>(turns);
        }

        public string Name => "stub";
        public List<LLMRequest> Requests { get; } = [];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            var turn = _turns.Count > 0
                ? _turns.Dequeue()
                : new StubStreamingTurn([]);
            if (turn.Error is not null)
                throw turn.Error;
            foreach (var delta in turn.Deltas)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return new LLMStreamChunk { DeltaContent = delta };
            }

            if (turn.ToolCalls is { Count: > 0 })
            {
                foreach (var toolCall in turn.ToolCalls)
                    yield return new LLMStreamChunk { DeltaToolCall = toolCall };
            }

            yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
        }

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];
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
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = stream[^1].Version,
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
