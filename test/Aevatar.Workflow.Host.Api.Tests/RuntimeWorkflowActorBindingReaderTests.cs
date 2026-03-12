using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.Runs;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class RuntimeWorkflowActorBindingReaderTests
{
    [Fact]
    public async Task GetAsync_ShouldThrow_WhenActorIdBlank()
    {
        var reader = CreateReader();

        var act = async () => await reader.GetAsync(" ", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenActorMissing()
    {
        var reader = CreateReader(
            requestReply: new FakeStreamRequestReplyClient
            {
                ActorQueryException = new InvalidOperationException("actor not found"),
            });

        var result = await reader.GetAsync("missing", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ShouldReturnUnsupportedBinding_WhenActorIsNotWorkflowCapable()
    {
        var requestReply = new FakeStreamRequestReplyClient
        {
            Response = null,
        };
        var reader = CreateReader(requestReply, new FakeAgentTypeVerifier());

        var result = await reader.GetAsync("actor-1", CancellationToken.None);

        result.Should().NotBeNull();
        result!.ActorKind.Should().Be(WorkflowActorKind.Unsupported);
        result.ActorId.Should().Be("actor-1");
        result.WorkflowName.Should().BeEmpty();
        result.WorkflowYaml.Should().BeEmpty();
        requestReply.QueryActorCalls.Should().Be(1);
        requestReply.CapturedActorId.Should().Be("actor-1");
        requestReply.CapturedTimeout.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetAsync_ShouldUseProbeTimeout_WhenVerifierMissesButActorReplies()
    {
        var requestReply = new FakeStreamRequestReplyClient
        {
            Response = new WorkflowActorBindingRespondedEvent
            {
                RequestId = "req-proxy",
                ActorId = "proxy-actor",
                ActorKind = "definition",
                WorkflowName = "direct",
                WorkflowYaml = "name: direct\nroles: []\nsteps: []\n",
            },
        };
        var reader = CreateReader(requestReply, new FakeAgentTypeVerifier());

        var result = await reader.GetAsync("proxy-actor", CancellationToken.None);

        result.Should().NotBeNull();
        result!.ActorKind.Should().Be(WorkflowActorKind.Definition);
        result.ActorId.Should().Be("proxy-actor");
        requestReply.CapturedTimeout.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetAsync_ShouldQueryAndNormalizeRunBinding_WhenVerifierMatches()
    {
        var requestReply = new FakeStreamRequestReplyClient
        {
            Response = new WorkflowActorBindingRespondedEvent
            {
                RequestId = "req-1",
                ActorId = "",
                ActorKind = " run ",
                DefinitionActorId = "definition-1",
                RunId = "run-1",
                WorkflowName = "direct",
                WorkflowYaml = "yaml",
                InlineWorkflowYamls =
                {
                    ["child"] = "yaml-child",
                },
            },
        };
        var verifier = new FakeAgentTypeVerifier
        {
            Results =
            {
                ["actor-1:WorkflowGAgent"] = false,
                ["actor-1:WorkflowRunGAgent"] = true,
            },
        };
        var reader = CreateReader(requestReply, verifier);

        var result = await reader.GetAsync("actor-1", CancellationToken.None);

        result.Should().NotBeNull();
        result!.ActorKind.Should().Be(WorkflowActorKind.Run);
        result.ActorId.Should().Be("actor-1");
        result.DefinitionActorId.Should().Be("definition-1");
        result.RunId.Should().Be("run-1");
        result.WorkflowName.Should().Be("direct");
        result.WorkflowYaml.Should().Be("yaml");
        result.InlineWorkflowYamls.Should().ContainKey("child").WhoseValue.Should().Be("yaml-child");
        requestReply.QueryActorCalls.Should().Be(1);
        requestReply.CapturedReplyPrefix.Should().Be(WorkflowQueryRouteConventions.ActorBindingReplyStreamPrefix);
        requestReply.CapturedTimeout.Should().Be(TimeSpan.FromSeconds(5));
        requestReply.CapturedTimeoutMessage.Should().Contain("request_id=req-1");
        requestReply.CapturedEnvelope.Should().NotBeNull();
        requestReply.CapturedEnvelope!.Route.TargetActorId.Should().Be("actor-1");
        requestReply.CapturedEnvelope.Propagation.CorrelationId.Should().Be("req-1");
    }

    [Fact]
    public async Task GetAsync_ShouldNormalizeUnknownActorKind_AsUnsupported()
    {
        var requestReply = new FakeStreamRequestReplyClient
        {
            Response = new WorkflowActorBindingRespondedEvent
            {
                RequestId = "req-2",
                ActorId = "binding-actor-2",
                ActorKind = "mystery",
            },
        };
        var verifier = new FakeAgentTypeVerifier
        {
            Results =
            {
                ["actor-2:WorkflowGAgent"] = true,
            },
        };
        var reader = CreateReader(requestReply, verifier);

        var result = await reader.GetAsync("actor-2", CancellationToken.None);

        result.Should().NotBeNull();
        result!.ActorKind.Should().Be(WorkflowActorKind.Unsupported);
        result.ActorId.Should().Be("binding-actor-2");
    }

    [Fact]
    public async Task GetAsync_ShouldShortCircuitVerifierAndMapDefinitionBinding()
    {
        var requestReply = new FakeStreamRequestReplyClient
        {
            Response = new WorkflowActorBindingRespondedEvent
            {
                RequestId = "req-3",
                ActorId = "definition-3",
                ActorKind = "definition",
                WorkflowName = "direct",
            },
        };
        var verifier = new FakeAgentTypeVerifier
        {
            Results =
            {
                ["actor-3:WorkflowGAgent"] = true,
                ["actor-3:WorkflowRunGAgent"] = false,
            },
        };
        var reader = CreateReader(requestReply, verifier);

        var result = await reader.GetAsync("actor-3", CancellationToken.None);

        result.Should().NotBeNull();
        result!.ActorKind.Should().Be(WorkflowActorKind.Definition);
        result.ActorId.Should().Be("definition-3");
        verifier.Calls.Should().ContainSingle().Which.Should().Be("actor-3:WorkflowGAgent");
    }

    [Fact]
    public async Task GetAsync_ShouldHonorCancellation()
    {
        var reader = CreateReader();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await reader.GetAsync("actor-1", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static RuntimeWorkflowActorBindingReader CreateReader(
        FakeStreamRequestReplyClient? requestReply = null,
        FakeAgentTypeVerifier? verifier = null)
    {
        requestReply ??= new FakeStreamRequestReplyClient();
        verifier ??= new FakeAgentTypeVerifier();
        return new RuntimeWorkflowActorBindingReader(
            new RuntimeWorkflowQueryClient(new FakeStreamProvider(), requestReply, new FakeActorDispatchPort()),
            verifier);
    }

    private sealed class FakeActorDispatchPort : IActorDispatchPort
    {
        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = actorId;
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class StubAgent(string id) : IAgent
    {
        public string Id { get; } = id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeAgentTypeVerifier : IAgentTypeVerifier
    {
        public Dictionary<string, bool> Results { get; } = new(StringComparer.Ordinal);
        public List<string> Calls { get; } = [];

        public Task<bool> IsExpectedAsync(string actorId, Type expectedType, CancellationToken ct = default)
        {
            var key = $"{actorId}:{expectedType.Name}";
            Calls.Add(key);
            return Task.FromResult(Results.TryGetValue(key, out var result) && result);
        }
    }

    private sealed class FakeStreamProvider : IStreamProvider
    {
        public IStream GetStream(string actorId) => new FakeStream(actorId);
    }

    private sealed class FakeStream(string actorId) : IStream
    {
        public string StreamId { get; } = actorId;

        public Task ProduceAsync<T>(T message, CancellationToken ct = default) where T : IMessage => Task.CompletedTask;

        public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default) where T : IMessage, new() =>
            Task.FromResult<IAsyncDisposable>(new AsyncDisposableStub());

        public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default) => Task.CompletedTask;

        public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StreamForwardingBinding>>([]);
    }

    private sealed class AsyncDisposableStub : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeActor(string id, IAgent agent) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = agent;

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeStreamRequestReplyClient : IStreamRequestReplyClient
    {
        public int QueryActorCalls { get; private set; }
        public WorkflowActorBindingRespondedEvent? Response { get; set; } = new()
        {
            RequestId = "req-1",
            ActorKind = "run",
        };
        public Exception? QueryException { get; set; }
        public Exception? ActorQueryException { get; set; }
        public string? CapturedActorId { get; private set; }
        public string? CapturedReplyPrefix { get; private set; }
        public TimeSpan CapturedTimeout { get; private set; }
        public string? CapturedTimeoutMessage { get; private set; }
        public EventEnvelope? CapturedEnvelope { get; private set; }

        public Task<TResponse> QueryAsync<TResponse>(
            IStreamProvider streams,
            string replyStreamPrefix,
            TimeSpan timeout,
            Func<string, string, Task> dispatchAsync,
            Func<TResponse, string, bool> isMatch,
            Func<string, string> timeoutMessageFactory,
            CancellationToken ct = default)
            where TResponse : IMessage, new() =>
            throw new NotSupportedException();

        public Task<TResponse> QueryActorAsync<TResponse>(
            IStreamProvider streams,
            IActor actor,
            string replyStreamPrefix,
            TimeSpan timeout,
            Func<string, string, EventEnvelope> envelopeFactory,
            Func<TResponse, string, bool> isMatch,
            Func<string, string> timeoutMessageFactory,
            CancellationToken ct = default)
            where TResponse : IMessage, new()
        {
            _ = streams;
            return QueryActorAsync<TResponse>(
                streams,
                actor.Id,
                new FakeActorDispatchPort(),
                replyStreamPrefix,
                timeout,
                envelopeFactory,
                isMatch,
                timeoutMessageFactory,
                ct);
        }

        public Task<TResponse> QueryActorAsync<TResponse>(
            IStreamProvider streams,
            string actorId,
            IActorDispatchPort dispatchPort,
            string replyStreamPrefix,
            TimeSpan timeout,
            Func<string, string, EventEnvelope> envelopeFactory,
            Func<TResponse, string, bool> isMatch,
            Func<string, string> timeoutMessageFactory,
            CancellationToken ct = default)
            where TResponse : IMessage, new()
        {
            ct.ThrowIfCancellationRequested();
            _ = streams;
            _ = dispatchPort;

            QueryActorCalls++;
            CapturedActorId = actorId;
            CapturedReplyPrefix = replyStreamPrefix;
            CapturedTimeout = timeout;

            if (ActorQueryException != null)
                throw ActorQueryException;
            if (QueryException != null)
                return Task.FromException<TResponse>(QueryException);

            if (Response == null)
            {
                var requestId = "req-timeout";
                CapturedEnvelope = envelopeFactory(requestId, "reply-stream");
                CapturedTimeoutMessage = timeoutMessageFactory(requestId);
                return Task.FromException<TResponse>(new TimeoutException(CapturedTimeoutMessage));
            }

            CapturedEnvelope = envelopeFactory(Response.RequestId, "reply-stream");
            CapturedTimeoutMessage = timeoutMessageFactory(Response.RequestId);
            isMatch((TResponse)(IMessage)Response, Response.RequestId).Should().BeTrue();
            return Task.FromResult((TResponse)(IMessage)Response);
        }
    }
}
