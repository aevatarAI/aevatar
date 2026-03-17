using System.Reflection;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionHotspotCoverageTests
{
    [Fact]
    public async Task ProjectionSessionEventHub_ShouldValidateInputs_AndPublishTransportMessages()
    {
        var streamProvider = new RecordingStreamProvider();
        var hub = new ProjectionSessionEventHub<StringValue>(streamProvider, new TestSessionCodec("projection-run"));

        Func<Task> blankScopePublish = () => hub.PublishAsync("", "session-1", new StringValue { Value = "evt" });
        Func<Task> blankSessionPublish = () => hub.PublishAsync("scope-1", "", new StringValue { Value = "evt" });
        Func<Task> nullEventPublish = () => hub.PublishAsync("scope-1", "session-1", null!);
        Func<Task> blankScopeSubscribe = () => hub.SubscribeAsync("", "session-1", _ => ValueTask.CompletedTask);
        Func<Task> blankSessionSubscribe = () => hub.SubscribeAsync("scope-1", "", _ => ValueTask.CompletedTask);
        Func<Task> nullHandlerSubscribe = () => hub.SubscribeAsync("scope-1", "session-1", null!);

        await blankScopePublish.Should().ThrowAsync<ArgumentException>().WithParameterName("scopeId");
        await blankSessionPublish.Should().ThrowAsync<ArgumentException>().WithParameterName("sessionId");
        await nullEventPublish.Should().ThrowAsync<ArgumentNullException>().WithParameterName("evt");
        await blankScopeSubscribe.Should().ThrowAsync<ArgumentException>().WithParameterName("scopeId");
        await blankSessionSubscribe.Should().ThrowAsync<ArgumentException>().WithParameterName("sessionId");
        await nullHandlerSubscribe.Should().ThrowAsync<ArgumentNullException>().WithParameterName("handler");

        await hub.PublishAsync("scope-1", "session-1", new StringValue { Value = "native:ok" });

        streamProvider.Streams.Should().ContainKey("projection-run:scope-1:session-1");
        var message = streamProvider.Streams["projection-run:scope-1:session-1"].Produced.Should().ContainSingle().Subject;
        message.ScopeId.Should().Be("scope-1");
        message.SessionId.Should().Be("session-1");
        message.EventType.Should().Be("native");
        message.LegacyPayload.Should().Be("legacy:native:ok");
        message.Payload.Should().NotBeNull();
        message.Payload.IsEmpty.Should().BeFalse();

        var invalidHub = new ProjectionSessionEventHub<StringValue>(streamProvider, new TestSessionCodec(""));
        Func<Task> invalidPublish = () => invalidHub.PublishAsync("scope-1", "session-1", new StringValue { Value = "evt" });
        Func<Task> invalidSubscribe = () => invalidHub.SubscribeAsync("scope-1", "session-1", _ => ValueTask.CompletedTask);

        await invalidPublish.Should().ThrowAsync<InvalidOperationException>();
        await invalidSubscribe.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ProjectionSessionEventHub_ShouldHandleNativeLegacyAndUndecodableMessages()
    {
        var streamProvider = new RecordingStreamProvider();
        var stream = streamProvider.GetOrAdd("projection-run:scope-1:session-1");
        var hub = new ProjectionSessionEventHub<StringValue>(streamProvider, new TestSessionCodec("projection-run"));
        var received = new List<string>();

        await using var subscription = await hub.SubscribeAsync(
            "scope-1",
            "session-1",
            evt =>
            {
                received.Add(evt.Value);
                return ValueTask.CompletedTask;
            });

        await stream.ProduceAsync(
            new ProjectionSessionEventTransportMessage
            {
                ScopeId = "scope-x",
                SessionId = "session-1",
                EventType = "native",
                Payload = ByteString.CopyFromUtf8("native:ignored"),
            });
        await stream.ProduceAsync(
            new ProjectionSessionEventTransportMessage
            {
                ScopeId = "scope-1",
                SessionId = "session-1",
                EventType = "native",
            });
        await stream.ProduceAsync(
            new ProjectionSessionEventTransportMessage
            {
                ScopeId = "scope-1",
                SessionId = "session-1",
                EventType = "native",
                Payload = ByteString.CopyFromUtf8("native:ok"),
            });
        await stream.ProduceAsync(
            new ProjectionSessionEventTransportMessage
            {
                ScopeId = "scope-1",
                SessionId = "session-1",
                EventType = "legacy",
                Payload = ByteString.CopyFromUtf8("broken"),
                LegacyPayload = "legacy:fallback",
            });
        await stream.ProduceAsync(
            new ProjectionSessionEventTransportMessage
            {
                ScopeId = "scope-1",
                SessionId = "session-1",
                EventType = "legacy",
                Payload = ByteString.CopyFromUtf8("broken"),
                LegacyPayload = "broken",
            });

        received.Should().Equal("native:ok", "fallback");
    }

    [Fact]
    public async Task ProjectionScopeDispatchExecutor_ShouldAggregateFailures_AndHonorCancellation()
    {
        var executorType = typeof(ProjectionFailureReplayService).Assembly
            .GetType("Aevatar.CQRS.Projection.Core.Orchestration.ProjectionScopeDispatchExecutor");
        executorType.Should().NotBeNull();

        var executeMethod = executorType!.GetMethod(
            "ExecuteMaterializersAsync",
            BindingFlags.Public | BindingFlags.Static);
        executeMethod.Should().NotBeNull();

        var context = new TestMaterializationContext
        {
            RootActorId = "actor-1",
            ProjectionKind = "projection-a",
        };
        var envelope = new EventEnvelope { Id = "evt-1" };

        var aggregateTask = (Task)executeMethod!
            .MakeGenericMethod(typeof(TestMaterializationContext))
            .Invoke(
                null,
                [
                    new IProjectionMaterializer<TestMaterializationContext>[]
                    {
                        new TestMaterializer((_, _, _) => ValueTask.CompletedTask),
                        new TestMaterializer((_, _, _) => ValueTask.FromException(new InvalidOperationException("boom"))),
                    },
                    context,
                    envelope,
                    CancellationToken.None,
                ])!;

        var aggregate = await Assert.ThrowsAsync<ProjectionDispatchAggregateException>(() => aggregateTask);
        aggregate.Failures.Should().ContainSingle();
        aggregate.Failures[0].ProjectorOrder.Should().Be(2);
        aggregate.Failures[0].ProjectorName.Should().Be(nameof(TestMaterializer));
        aggregate.InnerException.Should().BeOfType<InvalidOperationException>();
        aggregate.Message.Should().Contain("TestMaterializer#2");

        var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledTask = (Task)executeMethod
            .MakeGenericMethod(typeof(TestMaterializationContext))
            .Invoke(
                null,
                [
                    new IProjectionMaterializer<TestMaterializationContext>[]
                    {
                        new TestMaterializer((_, _, ct) => ValueTask.FromException(new OperationCanceledException(ct))),
                    },
                    context,
                    envelope,
                    cancelled.Token,
                ])!;

        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelledTask);
    }

    [Fact]
    public void ProjectionHelpers_ShouldCoverTimestampAggregateAndWriteEvaluatorBranches()
    {
        var fallback = DateTimeOffset.Parse("2026-03-17T07:31:00+00:00");
        EventEnvelopeTimestampResolver.Resolve(new EventEnvelope(), fallback).Should().Be(fallback);
        EventEnvelopeTimestampResolver.Resolve(
                new EventEnvelope
                {
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-17T07:32:00+00:00")),
                },
                fallback)
            .Should()
            .Be(DateTimeOffset.Parse("2026-03-17T07:32:00+00:00"));

        var emptyAggregate = new ProjectionDispatchAggregateException([]);
        emptyAggregate.Message.Should().Be("Projection dispatch failed.");
        emptyAggregate.Failures.Should().BeEmpty();

        var applied = ProjectionWriteResult.Applied();
        var duplicate = ProjectionWriteResult.Duplicate();
        var stale = ProjectionWriteResult.Stale();
        var gap = ProjectionWriteResult.Gap();
        var conflict = ProjectionWriteResult.Conflict();

        applied.IsApplied.Should().BeTrue();
        duplicate.IsNonTerminal.Should().BeTrue();
        stale.IsNonTerminal.Should().BeTrue();
        gap.IsRejected.Should().BeTrue();
        conflict.IsRejected.Should().BeTrue();

        var existing = new TestReadModel("actor-1", 4, "evt-4");
        ProjectionWriteResultEvaluator.Evaluate(null, new TestReadModel("actor-1", 1, "evt-1"))
            .Disposition.Should().Be(ProjectionWriteDisposition.Applied);
        ProjectionWriteResultEvaluator.Evaluate(existing, new TestReadModel("actor-2", 5, "evt-5"))
            .Disposition.Should().Be(ProjectionWriteDisposition.Conflict);
        ProjectionWriteResultEvaluator.Evaluate(existing, new TestReadModel("actor-1", 3, "evt-3"))
            .Disposition.Should().Be(ProjectionWriteDisposition.Stale);
        ProjectionWriteResultEvaluator.Evaluate(existing, new TestReadModel("actor-1", 4, "evt-4"))
            .Disposition.Should().Be(ProjectionWriteDisposition.Applied);
        ProjectionWriteResultEvaluator.Evaluate(existing, new TestReadModel("actor-1", 4, "evt-4b"))
            .Disposition.Should().Be(ProjectionWriteDisposition.Conflict);
        ProjectionWriteResultEvaluator.Evaluate(existing, new TestReadModel("actor-1", 6, "evt-6"))
            .Disposition.Should().Be(ProjectionWriteDisposition.Gap);
        ProjectionWriteResultEvaluator.Evaluate(existing, new TestReadModel("actor-1", 5, "evt-5"))
            .Disposition.Should().Be(ProjectionWriteDisposition.Applied);

        Action noId = () => ProjectionWriteResultEvaluator.Evaluate(existing, new TestReadModel("", 5, "evt-5"));
        Action noActor = () => ProjectionWriteResultEvaluator.Evaluate(existing, new TestReadModel("actor-1", 5, "evt-5") { ActorIdOverride = "" });

        noId.Should().Throw<InvalidOperationException>();
        noActor.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ProjectionDocumentValue_ShouldNormalizeCollectionsAndUtcValues()
    {
        ProjectionDocumentValue.Empty.Kind.Should().Be(ProjectionDocumentValueKind.None);
        ProjectionDocumentValue.Empty.RawValue.Should().BeNull();

        ProjectionDocumentValue.FromString(null).RawValue.Should().Be(string.Empty);
        ((string[])ProjectionDocumentValue.FromStrings(["a", null]).RawValue!).Should().Equal("a", string.Empty);
        ((long[])ProjectionDocumentValue.FromInt64s([1, 2]).RawValue!).Should().Equal(1, 2);
        ((long[])ProjectionDocumentValue.FromInt64s(null!).RawValue!).Should().BeEmpty();
        ((double[])ProjectionDocumentValue.FromDoubles([1.5, 2.5]).RawValue!).Should().Equal(1.5, 2.5);
        ((bool[])ProjectionDocumentValue.FromBools([true, false]).RawValue!).Should().Equal(true, false);

        var utc = DateTime.SpecifyKind(new DateTime(2026, 3, 17, 7, 40, 0), DateTimeKind.Utc);
        var local = new DateTime(2026, 3, 17, 15, 40, 0, DateTimeKind.Local);
        ((DateTime)ProjectionDocumentValue.FromDateTime(utc).RawValue!).Kind.Should().Be(DateTimeKind.Utc);
        ((DateTime)ProjectionDocumentValue.FromDateTime(local).RawValue!).Kind.Should().Be(DateTimeKind.Utc);
        ((DateTime[])ProjectionDocumentValue.FromDateTimes([utc, local]).RawValue!).Should().OnlyContain(x => x.Kind == DateTimeKind.Utc);
    }

    [Fact]
    public async Task ProjectionScopeActorId_And_MaterializationPortBase_ShouldCoverValidationBranches()
    {
        Action missingRoot = () => ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey("", "kind-a", ProjectionRuntimeMode.DurableMaterialization));
        Action missingKind = () => ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey("actor-1", "", ProjectionRuntimeMode.DurableMaterialization));

        missingRoot.Should().Throw<ArgumentException>().WithParameterName("scopeKey");
        missingKind.Should().Throw<ArgumentException>().WithParameterName("scopeKey");
        ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey("actor-1", "kind-a", ProjectionRuntimeMode.DurableMaterialization))
            .Should()
            .Be("projection.durable.scope:kind-a:actor-1");
        ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey("actor-1", "kind-a", ProjectionRuntimeMode.SessionObservation, "session-1"))
            .Should()
            .Be("projection.session.scope:kind-a:actor-1:session-1");

        var scopeModeMapper = typeof(ProjectionScopeActorId).Assembly
            .GetType("Aevatar.CQRS.Projection.Core.Orchestration.ProjectionScopeModeMapper");
        scopeModeMapper.Should().NotBeNull();
        var toProto = scopeModeMapper!.GetMethod("ToProto", BindingFlags.Public | BindingFlags.Static);
        var toRuntime = scopeModeMapper.GetMethod("ToRuntime", BindingFlags.Public | BindingFlags.Static);
        toProto.Should().NotBeNull();
        toRuntime.Should().NotBeNull();
        Convert.ToInt32(toProto!.Invoke(null, [ProjectionRuntimeMode.DurableMaterialization])).Should().Be(1);
        Convert.ToInt32(toProto.Invoke(null, [ProjectionRuntimeMode.SessionObservation])).Should().Be(2);
        toRuntime!.Invoke(null, [System.Enum.ToObject(toProto.ReturnType, 1)]).Should().Be(ProjectionRuntimeMode.DurableMaterialization);
        toRuntime.Invoke(null, [System.Enum.ToObject(toProto.ReturnType, 0)]).Should().Be(ProjectionRuntimeMode.SessionObservation);

        var activation = new TestMaterializationActivationService();
        var release = new TestMaterializationReleaseService();
        var enabledPort = new TestMaterializationPort(() => true, activation, release);
        var enabledWithoutRelease = new TestMaterializationPort(() => true, activation, null);
        var disabledPort = new TestMaterializationPort(() => false, activation, null);

        (await enabledPort.EnsureProjectionFromRequestPublicAsync(null, CancellationToken.None)).Should().BeNull();
        (await disabledPort.EnsureProjectionPublicAsync("", CancellationToken.None)).Should().BeNull();
        (await disabledPort.EnsureProjectionPublicAsync("actor-1", CancellationToken.None)).Should().BeNull();
        activation.Requests.Should().BeEmpty();

        var lease = await enabledPort.EnsureProjectionPublicAsync("actor-1", CancellationToken.None);
        lease.Should().NotBeNull();
        activation.Requests.Should().ContainSingle();
        activation.Requests[0].RootActorId.Should().Be("actor-1");

        await enabledPort.ReleaseProjectionPublicAsync(lease!, CancellationToken.None);
        release.Released.Should().ContainSingle().Which.Should().BeSameAs(lease);

        await enabledWithoutRelease.ReleaseProjectionPublicAsync(new TestMaterializationLease("actor-3"), CancellationToken.None);
        await disabledPort.ReleaseProjectionPublicAsync(new TestMaterializationLease("actor-2"), CancellationToken.None);
        release.Released.Should().ContainSingle();

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Func<Task> canceledRelease = () => enabledPort.ReleaseProjectionPublicAsync(lease, cancellation.Token);
        Func<Task> nullLease = () => enabledPort.ReleaseProjectionPublicAsync(null!, CancellationToken.None);
        await canceledRelease.Should().ThrowAsync<OperationCanceledException>();
        await nullLease.Should().ThrowAsync<ArgumentNullException>().WithParameterName("runtimeLease");
    }

    private sealed class TestSessionCodec
        : IProjectionSessionEventCodec<StringValue>,
          ILegacyProjectionSessionEventCodec<StringValue>
    {
        public TestSessionCodec(string channel)
        {
            Channel = channel;
        }

        public string Channel { get; }

        public string GetEventType(StringValue evt)
        {
            ArgumentNullException.ThrowIfNull(evt);
            return evt.Value.StartsWith("legacy:", StringComparison.Ordinal) ? "legacy" : "native";
        }

        public ByteString Serialize(StringValue evt)
        {
            ArgumentNullException.ThrowIfNull(evt);
            return ByteString.CopyFromUtf8(evt.Value);
        }

        public StringValue? Deserialize(string eventType, ByteString payload)
        {
            if (string.IsNullOrWhiteSpace(eventType) || payload == null || payload.IsEmpty)
                return null;

            var text = payload.ToStringUtf8();
            return text.StartsWith(eventType + ":", StringComparison.Ordinal)
                ? new StringValue { Value = text }
                : null;
        }

        public string SerializeLegacy(StringValue evt)
        {
            ArgumentNullException.ThrowIfNull(evt);
            return "legacy:" + evt.Value;
        }

        public StringValue? DeserializeLegacy(string eventType, string payload)
        {
            if (!string.Equals(eventType, "legacy", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(payload) ||
                !payload.StartsWith("legacy:", StringComparison.Ordinal))
            {
                return null;
            }

            return new StringValue { Value = payload["legacy:".Length..] };
        }
    }

    private sealed class RecordingStreamProvider : IStreamProvider
    {
        public Dictionary<string, RecordingStream> Streams { get; } = new(StringComparer.Ordinal);

        public IStream GetStream(string actorId) => GetOrAdd(actorId);

        public RecordingStream GetOrAdd(string streamId)
        {
            if (!Streams.TryGetValue(streamId, out var stream))
            {
                stream = new RecordingStream(streamId);
                Streams[streamId] = stream;
            }

            return stream;
        }
    }

    private sealed class RecordingStream : IStream
    {
        private readonly List<Func<ProjectionSessionEventTransportMessage, Task>> _handlers = [];

        public RecordingStream(string streamId)
        {
            StreamId = streamId;
        }

        public string StreamId { get; }

        public List<ProjectionSessionEventTransportMessage> Produced { get; } = [];

        public async Task ProduceAsync<T>(T message, CancellationToken ct = default)
            where T : IMessage
        {
            ct.ThrowIfCancellationRequested();
            if (message is not ProjectionSessionEventTransportMessage transport)
                throw new NotSupportedException();

            Produced.Add(transport);
            foreach (var handler in _handlers.ToArray())
                await handler(transport);
        }

        public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
            where T : IMessage, new()
        {
            ct.ThrowIfCancellationRequested();
            if (typeof(T) != typeof(ProjectionSessionEventTransportMessage))
                throw new NotSupportedException();

            Func<ProjectionSessionEventTransportMessage, Task> callback = message =>
                handler((T)(IMessage)message);
            _handlers.Add(callback);
            return Task.FromResult<IAsyncDisposable>(new Subscription(() => _handlers.Remove(callback)));
        }

        public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StreamForwardingBinding>>([]);

        private sealed class Subscription : IAsyncDisposable
        {
            private readonly Action _dispose;

            public Subscription(Action dispose)
            {
                _dispose = dispose;
            }

            public ValueTask DisposeAsync()
            {
                _dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class TestMaterializationContext : IProjectionMaterializationContext
    {
        public string RootActorId { get; init; } = string.Empty;

        public string ProjectionKind { get; init; } = string.Empty;
    }

    private sealed class TestMaterializer : IProjectionMaterializer<TestMaterializationContext>
    {
        private readonly Func<TestMaterializationContext, EventEnvelope, CancellationToken, ValueTask> _projectAsync;

        public TestMaterializer(Func<TestMaterializationContext, EventEnvelope, CancellationToken, ValueTask> projectAsync)
        {
            _projectAsync = projectAsync;
        }

        public ValueTask ProjectAsync(TestMaterializationContext context, EventEnvelope envelope, CancellationToken ct = default) =>
            _projectAsync(context, envelope, ct);
    }

    private sealed class TestReadModel : IProjectionReadModel
    {
        public TestReadModel(string actorId, long stateVersion, string lastEventId)
        {
            Id = "read-model";
            ActorId = actorId;
            StateVersion = stateVersion;
            LastEventId = lastEventId;
        }

        public string ActorId { get; }

        public string? ActorIdOverride { get; init; }

        public string Id { get; init; }

        public long StateVersion { get; init; }

        public string LastEventId { get; init; }

        public DateTimeOffset UpdatedAt { get; init; }

        string IProjectionReadModel.ActorId => ActorIdOverride ?? ActorId;
    }

    private sealed class TestMaterializationPort : MaterializationProjectionPortBase<TestMaterializationLease>
    {
        public TestMaterializationPort(
            Func<bool> projectionEnabledAccessor,
            IProjectionScopeActivationService<TestMaterializationLease> activationService,
            IProjectionScopeReleaseService<TestMaterializationLease>? releaseService)
            : base(projectionEnabledAccessor, activationService, releaseService)
        {
        }

        public Task<TestMaterializationLease?> EnsureProjectionPublicAsync(string rootActorId, CancellationToken ct) =>
            EnsureProjectionAsync(
                new ProjectionScopeStartRequest
                {
                    RootActorId = rootActorId,
                    ProjectionKind = "kind-a",
                    Mode = ProjectionRuntimeMode.DurableMaterialization,
                },
                ct);

        public Task<TestMaterializationLease?> EnsureProjectionFromRequestPublicAsync(
            ProjectionScopeStartRequest? request,
            CancellationToken ct) =>
            EnsureProjectionAsync(request!, ct);

        public Task ReleaseProjectionPublicAsync(TestMaterializationLease lease, CancellationToken ct) =>
            ReleaseProjectionAsync(lease, ct);
    }

    private sealed class TestMaterializationActivationService : IProjectionScopeActivationService<TestMaterializationLease>
    {
        public List<ProjectionScopeStartRequest> Requests { get; } = [];

        public Task<TestMaterializationLease> EnsureAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new TestMaterializationLease(request.RootActorId));
        }
    }

    private sealed class TestMaterializationReleaseService : IProjectionScopeReleaseService<TestMaterializationLease>
    {
        public List<TestMaterializationLease> Released { get; } = [];

        public Task ReleaseIfIdleAsync(TestMaterializationLease lease, CancellationToken ct = default)
        {
            Released.Add(lease);
            return Task.CompletedTask;
        }
    }

    private sealed class TestMaterializationLease : ProjectionRuntimeLeaseBase
    {
        public TestMaterializationLease(string rootEntityId)
            : base(rootEntityId)
        {
        }
    }
}
