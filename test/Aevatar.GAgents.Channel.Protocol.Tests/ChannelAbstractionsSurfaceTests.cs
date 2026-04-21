using System.Reflection;
using System.Threading.Channels;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Shouldly;
using FoundationCredentialProvider = Aevatar.Foundation.Abstractions.Credentials.ICredentialProvider;

namespace Aevatar.GAgents.Channel.Protocol.Tests;

public sealed class ChannelAbstractionsSurfaceTests
{
    [Fact]
    public void ConversationReferenceHelpers_ShouldBuildDeterministicCanonicalKey()
    {
        var reference = ConversationReference.Create(
            ChannelId.From("slack"),
            BotInstanceId.From("ops-bot"),
            ConversationScope.Thread,
            "team-1",
            "team-1",
            "C123",
            "thread",
            "1710000.123");

        reference.CanonicalKey.ShouldBe("slack:team-1:C123:thread:1710000.123");
        reference.Partition.ShouldBe("team-1");
        reference.Channel.Value.ShouldBe("slack");
    }

    [Fact]
    public void ConversationReferenceHelpers_ShouldRejectMissingCanonicalSegments()
    {
        var exception = Should.Throw<ArgumentException>(() => ConversationReference.Create(
            ChannelId.From("slack"),
            BotInstanceId.From("ops-bot"),
            ConversationScope.Thread,
            "team-1"));

        exception.ParamName.ShouldBe("segments");
    }

    [Fact]
    public void ConversationReferenceHelpers_ShouldBuildTelegramCanonicalKeys()
    {
        var bot = BotInstanceId.From("telegram-bot");

        var direct = ConversationReference.TelegramPrivate(bot, "42");
        var group = ConversationReference.TelegramGroup(bot, "-1001");
        var supergroup = ConversationReference.TelegramGroup(bot, "-1002", isSupergroup: true);
        var channel = ConversationReference.TelegramChannel(bot, "-1003");

        direct.CanonicalKey.ShouldBe("telegram:private:42");
        direct.Scope.ShouldBe(ConversationScope.DirectMessage);
        group.CanonicalKey.ShouldBe("telegram:group:-1001");
        group.Scope.ShouldBe(ConversationScope.Group);
        supergroup.CanonicalKey.ShouldBe("telegram:supergroup:-1002");
        supergroup.Scope.ShouldBe(ConversationScope.Group);
        channel.CanonicalKey.ShouldBe("telegram:channel:-1003");
        channel.Scope.ShouldBe(ConversationScope.Channel);
    }

    [Fact]
    public void EmitResultHelpers_ShouldCaptureRetryDelay()
    {
        var sent = EmitResult.Sent("msg-1");
        var failed = EmitResult.Failed("rate_limited", "retry later", TimeSpan.FromSeconds(5));

        sent.Success.ShouldBeTrue();
        sent.SentActivityId.ShouldBe("msg-1");
        failed.Success.ShouldBeFalse();
        failed.RetryAfterTimeSpan.ShouldBe(TimeSpan.FromSeconds(5));
        failed.ErrorCode.ShouldBe("rate_limited");
    }

    [Fact]
    public void RedactionResultHelpers_ShouldDefensivelyCopyPayloadBytes()
    {
        var payload = new byte[] { 1, 2, 3 };

        var unchanged = RedactionResult.Unchanged(payload);
        var modified = RedactionResult.Modified(payload);

        payload[0] = 9;

        unchanged.SanitizedPayload.ShouldNotBeSameAs(payload);
        modified.SanitizedPayload.ShouldNotBeSameAs(payload);
        unchanged.SanitizedPayload[0].ShouldBe((byte)1);
        modified.SanitizedPayload[0].ShouldBe((byte)1);
        unchanged.WasModified.ShouldBeFalse();
        modified.WasModified.ShouldBeTrue();
    }

    [Fact]
    public void ScheduleStateHelpers_ShouldRoundTripDateTimes()
    {
        var nextRun = new DateTimeOffset(2026, 4, 21, 12, 0, 0, TimeSpan.Zero);
        var lastRun = nextRun.AddMinutes(-30);
        var schedule = new ScheduleState();

        schedule.NextRunAtUtc = nextRun;
        schedule.LastRunAtUtc = lastRun;

        schedule.NextRunAtUtc.ShouldBe(nextRun);
        schedule.LastRunAtUtc.ShouldBe(lastRun);
    }

    [Fact]
    public async Task ChannelCredentialProviderExtensions_ShouldReuseFoundationCredentialContract()
    {
        var provider = new StubCredentialProvider();
        var binding = ChannelTransportBinding.Create(
            ChannelBotDescriptor.Create(
                "bot-reg-1",
                ChannelId.From("slack"),
                BotInstanceId.From("ops-bot"),
                "scope-1"),
            "vault://bots/ops-bot",
            "verify-me");
        var authContext = AuthContext.OnBehalfOfUser("vault://users/u1", "U1");

        (await provider.ResolveBotCredentialAsync(binding)).ShouldBe("secret:vault://bots/ops-bot");
        (await provider.ResolveUserCredentialAsync(authContext)).ShouldBe("secret:vault://users/u1");
        provider.ResolvedRefs.ShouldBe(["vault://bots/ops-bot", "vault://users/u1"]);
    }

    [Fact]
    public void ChannelInterfaces_ShouldExposeExpectedCoreSignatures()
    {
        typeof(IChannelTransport).GetProperty(nameof(IChannelTransport.InboundStream))!.PropertyType
            .ShouldBe(typeof(ChannelReader<ChatActivity>));
        typeof(ITurnContext).GetProperty(nameof(ITurnContext.Bot))!.PropertyType
            .ShouldBe(typeof(ChannelBotDescriptor));
        typeof(IChannelOutboundPort).GetMethod(nameof(IChannelOutboundPort.ContinueConversationAsync))!
            .GetParameters()[2]
            .ParameterType
            .ShouldBe(typeof(AuthContext));

        var genericComposer = typeof(IMessageComposer<>);
        genericComposer.IsGenericTypeDefinition.ShouldBeTrue();
        genericComposer.GetMethod(nameof(IMessageComposer.Compose))!.ReturnType.IsGenericParameter.ShouldBeTrue();
    }

    [Fact]
    public async Task PerEntryDocumentProjector_ShouldUpsertAndDeleteByVerdict()
    {
        var now = new DateTimeOffset(2026, 4, 21, 12, 0, 0, TimeSpan.Zero);
        var dispatcher = new TestProjectionWriteDispatcher();
        var projector = new TestProjector(dispatcher, new FixedProjectionClock(now));
        var state = new TestProjectorState();
        state.Entries.Add(new TestProjectorEntry
        {
            Id = "entry-1",
            Value = "alpha",
        });
        state.Entries.Add(new TestProjectorEntry
        {
            Id = "entry-2",
            Value = "beta",
            IsDeleted = true,
        });

        var envelope = new EventEnvelope
        {
            Id = "outer-envelope",
            Route = EnvelopeRouteSemantics.CreateObserverPublication("actor-1"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = "evt-1",
                    Version = 7,
                    Timestamp = Timestamp.FromDateTimeOffset(now),
                    EventData = Any.Pack(new StringValue { Value = "projected" }),
                },
                StateRoot = Any.Pack(state),
            }),
        };

        await projector.ProjectAsync(new TestProjectionContext("actor-1", "channel.current-state"), envelope);

        dispatcher.Upserts.Count.ShouldBe(1);
        dispatcher.Upserts[0].Id.ShouldBe("entry-1");
        dispatcher.Upserts[0].StateVersion.ShouldBe(7);
        dispatcher.Upserts[0].ActorId.ShouldBe("actor-1");
        dispatcher.Upserts[0].Value.ShouldBe("alpha");
        dispatcher.Upserts[0].UpdatedAt.ShouldBe(now);
        dispatcher.Deletes.ShouldBe(["entry-2"]);
    }

    private sealed class TestProjector
        : PerEntryDocumentProjector<TestProjectorState, TestProjectorEntry, TestProjectorDocument>
    {
        public TestProjector(
            IProjectionWriteDispatcher<TestProjectorDocument> writeDispatcher,
            IProjectionClock clock)
            : base(writeDispatcher, clock)
        {
        }

        protected override IEnumerable<TestProjectorEntry> ExtractEntries(TestProjectorState state) => state.Entries;

        protected override string EntryKey(TestProjectorEntry entry) => entry.Id;

        protected override TestProjectorDocument Materialize(
            TestProjectorEntry entry,
            IProjectionMaterializationContext context,
            StateEvent stateEvent,
            DateTimeOffset updatedAt) => new()
        {
            Id = entry.Id,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = updatedAt,
            Value = entry.Value,
        };

        protected override ProjectionVerdict Evaluate(TestProjectorEntry entry) =>
            entry.IsDeleted ? ProjectionVerdict.Tombstone : ProjectionVerdict.Project;
    }

    private sealed class TestProjectionWriteDispatcher : IProjectionWriteDispatcher<TestProjectorDocument>
    {
        public List<TestProjectorDocument> Upserts { get; } = [];

        public List<string> Deletes { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(TestProjectorDocument readModel, CancellationToken ct = default)
        {
            Upserts.Add(readModel.Clone());
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            Deletes.Add(id);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class TestProjectionContext(string rootActorId, string projectionKind) : IProjectionMaterializationContext
    {
        public string RootActorId { get; } = rootActorId;

        public string ProjectionKind { get; } = projectionKind;
    }

    private sealed class StubCredentialProvider : FoundationCredentialProvider
    {
        public List<string> ResolvedRefs { get; } = [];

        public Task<string?> ResolveAsync(string credentialRef, CancellationToken ct = default)
        {
            ResolvedRefs.Add(credentialRef);
            return Task.FromResult<string?>($"secret:{credentialRef}");
        }
    }
}
