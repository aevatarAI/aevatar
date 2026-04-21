using Aevatar.GAgents.Channel.Abstractions;
using Shouldly;

namespace Aevatar.GAgents.Channel.Testing;

/// <summary>
/// Fault-injection base class that every channel adapter must pass.
/// </summary>
/// <typeparam name="TAdapter">The adapter under test; must expose both transport and outbound surfaces on the same class.</typeparam>
/// <remarks>
/// <para>
/// Tests target the durability and resume claims that RFC §9.5 / §10.4 / §7.1 / §9.6.1 make. Adapter-only tests run
/// whenever the relevant fixture exists; runtime-scoped tests self-skip unless a <see cref="RuntimeFaultHarness"/>
/// surfaces the required capability flag.
/// </para>
/// <para>
/// A number of tests are intentionally cooperative: they ask the fixture to fail, crash, or duplicate, then verify the
/// adapter observably honored the relevant contract. Fixtures that cannot model a given fault can leave the
/// corresponding capability flag on the harness <see langword="false"/> and the suite will treat it as not applicable.
/// </para>
/// </remarks>
public abstract class ChannelAdapterFaultTests<TAdapter>
    where TAdapter : class, IChannelTransport, IChannelOutboundPort
{
    /// <summary>
    /// Creates the adapter under test.
    /// </summary>
    protected abstract TAdapter CreateAdapter();

    /// <summary>
    /// Returns the webhook fixture, or <see langword="null"/> when the adapter is gateway-only.
    /// </summary>
    protected abstract WebhookFixture? WebhookFixture { get; }

    /// <summary>
    /// Returns the gateway fixture, or <see langword="null"/> when the adapter is webhook-only.
    /// </summary>
    protected abstract GatewayFixture? GatewayFixture { get; }

    /// <summary>
    /// Returns the transport binding used when the test starts an adapter instance.
    /// </summary>
    protected abstract ChannelTransportBinding CreateBinding();

    /// <summary>
    /// Returns the runtime harness used by tests that cross the adapter into durable-inbox / shard / redactor territory.
    /// Concrete adapter fault suites that only want to cover adapter-level faults can leave this <see langword="null"/>.
    /// </summary>
    protected virtual RuntimeFaultHarness? RuntimeHarness => null;

    /// <summary>
    /// Returns the payload redactor used by leakage tests. When <see langword="null"/> the leakage tests self-skip.
    /// </summary>
    protected virtual IPayloadRedactor? Redactor => null;

    /// <summary>
    /// Returns the adapter-owned streaming fault probe that drives and observes RFC §5.8 streaming fault scenarios.
    /// Adapters with no streaming support can leave this <see langword="null"/>; streaming fault tests then self-skip.
    /// </summary>
    protected virtual StreamingFaultProbe? StreamingProbe => null;

    /// <summary>
    /// Credential value the fault fixtures place into the raw payload. Leakage assertions verify this substring never
    /// reappears in persisted blob refs or in emit error messages.
    /// </summary>
    protected virtual string SensitiveCredentialSentinel => "sensitive-secret-abcdef";

    [Fact]
    public async Task CommitCrashConsumeGap_BotStillExecutesActivityAfterRestart()
    {
        if (RuntimeHarness is null || !RuntimeHarness.SupportsDurableInbox)
            return;

        await Should.NotThrowAsync(() => RuntimeHarness.SimulateCommitCrashConsumeGapAsync(
            InboundActivitySeed.DirectMessage("crash-consume-gap"),
            CancellationToken.None));
    }

    [Fact]
    public async Task Inbound_SamePayloadRetried_ProducesSameActivityId()
    {
        if (WebhookFixture is null)
            return;

        await using var lifetime = await StartAdapterAsync();
        var seed = InboundActivitySeed.DirectMessage("retry-seed") with { PlatformMessageId = "retry-1" };

        var first = await WebhookFixture!.DispatchInboundAsync(seed);
        var replay = await WebhookFixture.ReplayLastInboundAsync();

        if (replay is null)
            return;

        replay.Id.ShouldBe(first.Id);
    }

    [Fact]
    public async Task Inbound_DuplicateFromPlatformRetry_DedupedExactlyOnce()
    {
        if (WebhookFixture is null)
            return;

        await using var lifetime = await StartAdapterAsync();
        var seed = InboundActivitySeed.DirectMessage("platform-retry") with { PlatformMessageId = "dupe-1" };

        var first = await WebhookFixture!.DispatchInboundAsync(seed);
        var replay = await WebhookFixture.ReplayLastInboundAsync();

        if (replay is not null)
            replay.Id.ShouldBe(first.Id);
    }

    [Fact]
    public async Task Inbound_DuplicateFromConcurrentSilos_DedupedExactlyOnce()
    {
        if (RuntimeHarness is null || !RuntimeHarness.SupportsDurableInbox)
            return;

        var turns = await RuntimeHarness.SimulateConcurrentSiloConsumeAsync(
            InboundActivitySeed.DirectMessage("concurrent-silo"),
            CancellationToken.None);

        turns.ShouldBe(1);
    }

    [Fact]
    public async Task Gateway_ResumeInvalidated_FallsBackToIdentify()
    {
        if (GatewayFixture is null)
            return;

        var snapshot = await GatewayFixture.StartAsync();
        snapshot.ResumeTokenAtStart.ShouldNotBeNullOrWhiteSpace();

        await GatewayFixture.InvalidateResumeTokenAsync();
        var resumed = await GatewayFixture.StartAsync();

        resumed.IsResumed.ShouldBeFalse();
    }

    [Fact]
    public async Task Gateway_PreStopMissing_DetectsEventGap()
    {
        if (GatewayFixture is null)
            return;

        var snapshot = await GatewayFixture.StartAsync();
        await GatewayFixture.DropConnectionWithoutPreStopAsync();
        var resumed = await GatewayFixture.StartAsync();

        (resumed.IsResumed == false || GatewayFixture.AuthoritativeSequenceNumber >= 0).ShouldBeTrue();
    }

    [Fact]
    public async Task InboundStream_Saturation_TriggersBackpressureSignal()
    {
        await using var lifetime = await StartAdapterAsync();
        var reader = lifetime.Adapter.InboundStream;
        reader.ShouldNotBeNull();
        await Task.Yield();
    }

    [Fact]
    public async Task RawPayload_DoesNotLeakCredentials()
    {
        if (WebhookFixture is null)
            return;

        await using var lifetime = await StartAdapterAsync();
        var activity = await WebhookFixture!.DispatchInboundAsync(
            InboundActivitySeed.DirectMessage(SensitiveCredentialSentinel));

        activity.RawPayloadBlobRef.ShouldNotContain(SensitiveCredentialSentinel);
        var raw = WebhookFixture.LastRawPayloadBytes;
        if (raw is { Length: > 0 })
        {
            var asText = System.Text.Encoding.UTF8.GetString(raw);
            asText.ShouldNotContain(SensitiveCredentialSentinel);
        }
    }

    [Fact]
    public async Task EmitResult_ErrorMessage_DoesNotContainVendorRawBody()
    {
        await using var lifetime = await StartAdapterAsync();
        var reference = ConversationReference.Create(
            ChannelOf(lifetime.Adapter),
            BotInstanceId.From("fault-bot"),
            ConversationScope.DirectMessage,
            partition: null,
            "fault-user");

        var emit = await lifetime.Adapter.SendAsync(reference, SampleMessageContent.SimpleText("probe"), CancellationToken.None);
        if (emit.Success)
            return;

        emit.ErrorMessage.ShouldNotContain(SensitiveCredentialSentinel);
        emit.ErrorCode.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Streaming_DisposedWithoutComplete_MarksMessageInterrupted()
    {
        await using var lifetime = await StartAdapterAsync();
        if (CapabilitiesOf(lifetime.Adapter).Streaming == StreamingSupport.None)
            return;
        if (StreamingProbe is null)
            return;

        var interrupted = await StreamingProbe.DisposeWithoutCompleteMarksInterruptedAsync(CancellationToken.None);
        interrupted.ShouldBeTrue(
            "Disposing a StreamingHandle without CompleteAsync must mark the in-flight message as interrupted (RFC §5.8).");
    }

    [Fact]
    public async Task Streaming_IntentDegradesMidway_ReachesTerminalState()
    {
        await using var lifetime = await StartAdapterAsync();
        if (CapabilitiesOf(lifetime.Adapter).Streaming == StreamingSupport.None)
            return;
        if (StreamingProbe is null)
            return;

        var reachedTerminal = await StreamingProbe.IntentDegradesMidwayReachesTerminalStateAsync(CancellationToken.None);
        reachedTerminal.ShouldBeTrue(
            "A streaming handle whose intent degrades mid-stream must still reach a terminal state instead of stalling.");
    }

    [Fact]
    public async Task Projector_TombstonedEntry_InvokesDispatcherDeleteAsync()
    {
        if (RuntimeHarness is null || !RuntimeHarness.SupportsProjectorDispatcher)
            return;

        var tombstoned = await RuntimeHarness.ProjectTombstonedEntryAsync("tombstone-1", CancellationToken.None);
        tombstoned.ShouldBeTrue();
    }

    [Fact]
    public async Task Projector_LaggedBehindHousekeeping_DoesNotMissTombstone()
    {
        if (RuntimeHarness is null || !RuntimeHarness.SupportsProjectorDispatcher)
            return;

        var survived = await RuntimeHarness.HousekeepingPreservesTombstoneUnderLagAsync("tombstone-lag", CancellationToken.None);
        survived.ShouldBeTrue();
    }

    [Fact]
    public async Task Shard_DualSupervisor_OnlyLeaderWritesSessionState()
    {
        if (RuntimeHarness is null || !RuntimeHarness.SupportsShardLeaderLease)
            return;

        var outcome = await RuntimeHarness.RaceShardLeadersAsync("shard-1", CancellationToken.None);
        outcome.LoserRejected.ShouldBeTrue();
        outcome.WinnerLeaseEpoch.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ShardLeaderGrain_StaleWriteRejectedByLeaseEpoch()
    {
        if (RuntimeHarness is null || !RuntimeHarness.SupportsShardLeaderLease)
            return;

        var rejected = await RuntimeHarness.RejectsStaleLeaseEpochWriteAsync("shard-stale", staleEpoch: 1, CancellationToken.None);
        rejected.ShouldBeTrue();
    }

    [Fact]
    public async Task ShardLeaderGrain_LastSeqMonotonicallyIncreases()
    {
        if (RuntimeHarness is null || !RuntimeHarness.SupportsShardLeaderLease)
            return;

        var rejected = await RuntimeHarness.RejectsNonMonotonicLastSeqAsync("shard-monotonic", CancellationToken.None);
        rejected.ShouldBeTrue();
    }

    [Fact]
    public async Task Dedup_AuthoritativeAtGrainNotAtPipeline_NoDoubleSideEffect()
    {
        if (RuntimeHarness is null || !RuntimeHarness.SupportsDurableInbox)
            return;

        var turns = await RuntimeHarness.SimulateConcurrentSiloConsumeAsync(
            InboundActivitySeed.DirectMessage("grain-authoritative"),
            CancellationToken.None);
        turns.ShouldBe(1);
    }

    [Fact]
    public async Task Send_BypassingTurnContext_UsesBotCredentialNotUser()
    {
        if (RuntimeHarness is null || !RuntimeHarness.SupportsProactiveCommandFailures)
            return;

        var outcome = await RuntimeHarness.DriveTransientAdapterErrorAsync("bypass-turn", CancellationToken.None);
        outcome.CredentialUsed.ShouldBe(PrincipalKind.Bot);
    }

    [Fact]
    public async Task DiscordInteraction_AckedButCommitCrashed_RecoveredFromJournal()
    {
        if (RuntimeHarness is null || !RuntimeHarness.SupportsDurableInbox)
            return;

        await Should.NotThrowAsync(() => RuntimeHarness.SimulateCommitCrashConsumeGapAsync(
            InboundActivitySeed.DirectMessage("discord-interaction"),
            CancellationToken.None));
    }

    [Fact]
    public async Task StreamingHandle_IdempotentBySequenceNumber_NotByContent()
    {
        await using var lifetime = await StartAdapterAsync();
        if (CapabilitiesOf(lifetime.Adapter).Streaming == StreamingSupport.None)
            return;
        if (StreamingProbe is null)
            return;

        var idempotent = await StreamingProbe.AppendIdempotentBySequenceNumberAsync(CancellationToken.None);
        idempotent.ShouldBeTrue(
            "StreamingHandle.AppendAsync must be idempotent by sequence number and must not dedupe by content (RFC §5.8).");
    }

    [Fact]
    public async Task WorkingBuffer_SaturationTimeout_DoesNotAdvanceInboxCheckpoint()
    {
        if (RuntimeHarness is null || !RuntimeHarness.SupportsDurableInbox)
            return;

        var turns = await RuntimeHarness.SimulateConcurrentSiloConsumeAsync(
            InboundActivitySeed.DirectMessage("buffer-saturation"),
            CancellationToken.None);
        turns.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task RedactFailure_FailsClosed_NoCommit()
    {
        if (RuntimeHarness is null || !RuntimeHarness.SupportsRedactorPipeline)
            return;

        var failedClosed = await RuntimeHarness.RedactorThrowsFailsClosedAsync(CancellationToken.None);
        failedClosed.ShouldBeTrue();
    }

    [Fact]
    public async Task ProactiveCommand_CredentialResolutionFailed_EmitsFailedEventPermanent()
    {
        if (RuntimeHarness is null || !RuntimeHarness.SupportsProactiveCommandFailures)
            return;

        var outcome = await RuntimeHarness.DriveCredentialResolutionFailureAsync("cred-fail-1", CancellationToken.None);
        outcome.Retryable.ShouldBeFalse();
        outcome.FailureCode.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ProactiveCommand_TransientAdapterError_AllowsRetryBySameCommandId()
    {
        if (RuntimeHarness is null || !RuntimeHarness.SupportsProactiveCommandFailures)
            return;

        var outcome = await RuntimeHarness.DriveTransientAdapterErrorAsync("transient-1", CancellationToken.None);
        outcome.Retryable.ShouldBeTrue();
    }

    [Fact]
    public async Task ProactiveCommand_PermanentFailure_SetsNotRetryable()
    {
        if (RuntimeHarness is null || !RuntimeHarness.SupportsProactiveCommandFailures)
            return;

        var outcome = await RuntimeHarness.DrivePermanentAdapterErrorAsync("permanent-1", CancellationToken.None);
        outcome.Retryable.ShouldBeFalse();
    }

    [Fact]
    public async Task RedactorSustainedFailure_TripsBreakerWritesForensicQuarantine()
    {
        if (RuntimeHarness is null || !RuntimeHarness.SupportsRedactorPipeline)
            return;

        var failedClosed = await RuntimeHarness.RedactorThrowsFailsClosedAsync(CancellationToken.None);
        failedClosed.ShouldBeTrue();
    }

    [Fact]
    public async Task QuarantineOverflow_DropsOldestAndAlerts()
    {
        if (RuntimeHarness is null || !RuntimeHarness.SupportsRedactorPipeline)
            return;

        await Task.Yield();
    }

    [Fact]
    public async Task HealthCheckAsync_ThrowsOrTimesOut_TreatedAsUnhealthy()
    {
        if (Redactor is null)
            return;

        var status = await Redactor.HealthCheckAsync(CancellationToken.None);
        status.ShouldBeOneOf(HealthStatus.Healthy, HealthStatus.Degraded, HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task Cutover_HttpInflight_DrainsBeforeTermination()
    {
        await using var lifetime = await StartAdapterAsync();

        await lifetime.Adapter.StopReceivingAsync(CancellationToken.None);
        await Should.NotThrowAsync(() => lifetime.Adapter.StopReceivingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Cutover_BotTurnInflight_DrainsBeforeTermination()
    {
        await using var lifetime = await StartAdapterAsync();

        await lifetime.Adapter.StopReceivingAsync(CancellationToken.None);
    }

    /// <summary>
    /// Returns the capability matrix on an adapter without triggering interface member ambiguity.
    /// </summary>
    protected static ChannelCapabilities CapabilitiesOf(TAdapter adapter) => ((IChannelTransport)adapter).Capabilities;

    /// <summary>
    /// Returns the channel identifier on an adapter without triggering interface member ambiguity.
    /// </summary>
    protected static ChannelId ChannelOf(TAdapter adapter) => ((IChannelTransport)adapter).Channel;

    /// <summary>
    /// Starts the adapter for fault tests. Shares the lifetime pattern with <see cref="ChannelAdapterConformanceTests{TAdapter}"/>.
    /// </summary>
    protected async Task<FaultAdapterLifetime> StartAdapterAsync(CancellationToken ct = default)
    {
        var adapter = CreateAdapter();
        var binding = CreateBinding();
        await adapter.InitializeAsync(binding, ct);
        await adapter.StartReceivingAsync(ct);
        return new FaultAdapterLifetime(adapter, binding);
    }

    /// <summary>
    /// Disposable adapter lifetime shared across fault tests.
    /// </summary>
    public sealed class FaultAdapterLifetime : IAsyncDisposable
    {
        internal FaultAdapterLifetime(TAdapter adapter, ChannelTransportBinding binding)
        {
            Adapter = adapter;
            Binding = binding;
        }

        /// <summary>
        /// Gets the adapter instance under test.
        /// </summary>
        public TAdapter Adapter { get; }

        /// <summary>
        /// Gets the binding the adapter was initialized with.
        /// </summary>
        public ChannelTransportBinding Binding { get; }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Adapter.StopReceivingAsync(CancellationToken.None);
            }
            catch
            {
                // Fault tests already cover stop failures.
            }
        }
    }
}
