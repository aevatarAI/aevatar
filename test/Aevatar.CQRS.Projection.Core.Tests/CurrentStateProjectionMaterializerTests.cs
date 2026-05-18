using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class CurrentStateProjectionMaterializerTests
{
    [Fact]
    public async Task ProjectAsync_WhenEnvelopeCannotUnpackState_ShouldSkipMappingAndWrite()
    {
        var dispatcher = new RecordingDispatcher<TestStoreReadModel>();
        var mapperCalls = 0;
        var materializer = CreateMaterializer(dispatcher, (_, _, _) =>
        {
            mapperCalls++;
            return new TestStoreReadModel();
        });

        await materializer.ProjectAsync(
            new TestContext(),
            new EventEnvelope { Id = "raw", Payload = Any.Pack(new StringValue { Value = "not-state" }) });

        mapperCalls.Should().Be(0);
        dispatcher.Upserts.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_ShouldInvokeMapperWithInfo_AndPropagateFrameworkFields()
    {
        var observedAt = new DateTimeOffset(2026, 5, 18, 9, 30, 0, TimeSpan.Zero);
        var dispatcher = new RecordingDispatcher<TestStoreReadModel>();
        CurrentStateProjectionInfo? capturedInfo = null;
        TestContext? capturedContext = null;
        StringValue? capturedState = null;
        var materializer = CreateMaterializer(
            dispatcher,
            (context, state, info) =>
            {
                capturedContext = context;
                capturedState = state;
                capturedInfo = info;
                return new TestStoreReadModel
                {
                    Value = state.Value,
                };
            },
            new FixedClock(DateTimeOffset.MinValue));

        await materializer.ProjectAsync(
            new TestContext
            {
                RootActorId = "actor-1",
                ProjectionKind = "current-state",
            },
            BuildCommittedEnvelope(
                state: new StringValue { Value = "mapped-value" },
                eventPayload: new Int32Value { Value = 17 },
                eventId: "event-7",
                version: 7,
                observedAt: observedAt,
                correlationId: "corr-1",
                causationEventId: "cmd-1"));

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        document.Id.Should().Be("actor-1");
        document.ActorId.Should().Be("actor-1");
        document.StateVersion.Should().Be(7);
        document.LastEventId.Should().Be("event-7");
        document.UpdatedAt.Should().Be(observedAt);
        document.Value.Should().Be("mapped-value");
        capturedContext.Should().NotBeNull();
        capturedState!.Value.Should().Be("mapped-value");
        capturedInfo.Should().NotBeNull();
        capturedInfo!.RootActorId.Should().Be("actor-1");
        capturedInfo.CommandId.Should().Be("cmd-1");
        capturedInfo.CorrelationId.Should().Be("corr-1");
        capturedInfo.StateVersion.Should().Be(7);
        capturedInfo.LastEventId.Should().Be("event-7");
        capturedInfo.ObservedAt.Should().Be(observedAt);
        capturedInfo.Envelope.Should().NotBeNull();
        capturedInfo.ObservedPayload.Should().NotBeNull();
    }

    [Fact]
    public async Task ProjectAsync_ShouldLetStoreApplyVersionMonotonicity()
    {
        var dispatcher = new MonotonicDispatcher<TestStoreReadModel>();
        var materializer = CreateMaterializer(
            dispatcher,
            (_, state, _) => new TestStoreReadModel
            {
                Value = state.Value,
            });
        var context = new TestContext
        {
            RootActorId = "actor-1",
        };

        await materializer.ProjectAsync(
            context,
            BuildCommittedEnvelope(new StringValue { Value = "v2" }, new StringValue { Value = "payload" }, "event-2", 2));
        await materializer.ProjectAsync(
            context,
            BuildCommittedEnvelope(new StringValue { Value = "v1-stale" }, new StringValue { Value = "payload" }, "event-1", 1));
        await materializer.ProjectAsync(
            context,
            BuildCommittedEnvelope(new StringValue { Value = "v2-duplicate" }, new StringValue { Value = "payload" }, "event-2", 2));

        dispatcher.Results.Should().Equal(
            ProjectionWriteDisposition.Applied,
            ProjectionWriteDisposition.Stale,
            ProjectionWriteDisposition.Applied);
        dispatcher.Current!.StateVersion.Should().Be(2);
        dispatcher.Current.Value.Should().Be("v2-duplicate");
    }

    [Fact]
    public void AddCurrentStateProjection_ShouldRegisterExistingMaterializerContracts()
    {
        var dispatcher = new RecordingDispatcher<TestStoreReadModel>();
        var services = new ServiceCollection();
        services.AddSingleton<IProjectionWriteDispatcher<TestStoreReadModel>>(dispatcher);

        services.AddCurrentStateProjection<TestContext, StringValue, TestStoreReadModel>(
            static (_, state, _) => new TestStoreReadModel
            {
                Value = state.Value,
            });

        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionMaterializer<TestContext>) &&
            x.ImplementationType != null &&
            x.ImplementationType.Name.StartsWith("ObservedProjectionMaterializer", StringComparison.Ordinal));
        services.Should().Contain(x =>
            x.ServiceType == typeof(ICurrentStateProjectionMaterializer<TestContext>) &&
            x.ImplementationType != null &&
            x.ImplementationType.Name.StartsWith("ObservedCurrentStateProjectionMaterializer", StringComparison.Ordinal));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IProjectionMaterializer<TestContext>>().Should().NotBeNull();
        provider.GetRequiredService<ICurrentStateProjectionMaterializer<TestContext>>().Should().NotBeNull();
    }

    private static ICurrentStateProjectionMaterializer<TestContext> CreateMaterializer(
        IProjectionWriteDispatcher<TestStoreReadModel> dispatcher,
        Func<TestContext, StringValue, CurrentStateProjectionInfo, TestStoreReadModel> map,
        IProjectionClock? clock = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(dispatcher);
        if (clock != null)
            services.AddSingleton(clock);

        services.AddCurrentStateProjection<TestContext, StringValue, TestStoreReadModel>(map);
        return services.BuildServiceProvider()
            .GetRequiredService<ICurrentStateProjectionMaterializer<TestContext>>();
    }

    private static EventEnvelope BuildCommittedEnvelope(
        IMessage state,
        IMessage eventPayload,
        string eventId,
        long version,
        DateTimeOffset? observedAt = null,
        string correlationId = "",
        string causationEventId = "")
    {
        var envelope = new EventEnvelope
        {
            Id = $"outer-{eventId}",
            Timestamp = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 5, 18, 1, 0, 0, TimeSpan.Zero)),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    Timestamp = observedAt == null ? null : Timestamp.FromDateTimeOffset(observedAt.Value),
                    EventData = Any.Pack(eventPayload),
                },
                StateRoot = Any.Pack(state),
            }),
        };

        if (!string.IsNullOrWhiteSpace(correlationId) || !string.IsNullOrWhiteSpace(causationEventId))
        {
            envelope.Propagation = new EnvelopePropagation
            {
                CorrelationId = correlationId,
                CausationEventId = causationEventId,
            };
        }

        return envelope;
    }

    private sealed class TestContext : IProjectionMaterializationContext
    {
        public string RootActorId { get; init; } = "actor";

        public string ProjectionKind { get; init; } = "projection";
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class RecordingDispatcher<TReadModel> : IProjectionWriteDispatcher<TReadModel>
        where TReadModel : class, IProjectionReadModel
    {
        public List<TReadModel> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class MonotonicDispatcher<TReadModel> : IProjectionWriteDispatcher<TReadModel>
        where TReadModel : class, IProjectionReadModel
    {
        public TReadModel? Current { get; private set; }

        public List<ProjectionWriteDisposition> Results { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var result = ProjectionWriteResultEvaluator.Evaluate(Current, readModel);
            if (result.IsApplied)
                Current = readModel;
            Results.Add(result.Disposition);
            return Task.FromResult(result);
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }
}
