using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Infrastructure.Runs;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowBindingReaderCoverageTests
{
    [Fact]
    public async Task GetAsync_ShouldReturnBinding_WhenWorkflowActorReplies()
    {
        var streams = new InMemoryStreamProvider();
        var dispatchPort = new RecordingDispatchPort(
            streams,
            (_, envelope) =>
            {
                var request = envelope.Payload.Unpack<QueryWorkflowActorBindingRequestedEvent>();
                return new WorkflowActorBindingRespondedEvent
                {
                    RequestId = request.RequestId,
                    ActorId = "actor-1",
                    ActorKind = "run",
                    DefinitionActorId = "definition-1",
                    RunId = "run-1",
                    WorkflowName = "auto",
                    WorkflowYaml = "name: auto",
                };
            });
        var reader = CreateReader(
            streams,
            dispatchPort,
            new FakeAgentTypeVerifier(("actor-1", typeof(WorkflowRunGAgent), true)));

        var binding = await reader.GetAsync("actor-1");

        binding.Should().NotBeNull();
        binding!.ActorKind.Should().Be(WorkflowActorKind.Run);
        binding.ActorId.Should().Be("actor-1");
        binding.DefinitionActorId.Should().Be("definition-1");
        binding.RunId.Should().Be("run-1");
        binding.WorkflowName.Should().Be("auto");
        dispatchPort.DispatchCalls.Should().ContainSingle().Which.Should().Be("actor-1");
    }

    [Fact]
    public async Task GetAsync_ShouldReturnUnsupported_WhenTypeVerifierRejectsAndNoReplyArrives()
    {
        var streams = new InMemoryStreamProvider();
        var dispatchPort = new RecordingDispatchPort(streams, static (_, _) => null);
        var reader = CreateReader(
            streams,
            dispatchPort,
            new FakeAgentTypeVerifier());

        var binding = await reader.GetAsync("actor-unsupported");

        binding.Should().NotBeNull();
        binding!.ActorKind.Should().Be(WorkflowActorKind.Unsupported);
        binding.ActorId.Should().Be("actor-unsupported");
        binding.IsWorkflowCapable.Should().BeFalse();
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenDispatchReportsActorMissing()
    {
        var streams = new InMemoryStreamProvider();
        var dispatchPort = new RecordingDispatchPort(
            streams,
            static (_, _) => throw new InvalidOperationException("Actor actor-missing not found."));
        var reader = CreateReader(
            streams,
            dispatchPort,
            new FakeAgentTypeVerifier());

        var binding = await reader.GetAsync("actor-missing");

        binding.Should().BeNull();
    }

    private static RuntimeWorkflowActorBindingReader CreateReader(
        InMemoryStreamProvider streams,
        IActorDispatchPort dispatchPort,
        IAgentTypeVerifier agentTypeVerifier) =>
        new(
            new RuntimeWorkflowQueryClient(
                streams,
                new RuntimeStreamRequestReplyClient(),
                dispatchPort),
            agentTypeVerifier);

    private sealed class RecordingDispatchPort(
        IStreamProvider streams,
        Func<string, EventEnvelope, WorkflowActorBindingRespondedEvent?> responder) : IActorDispatchPort
    {
        public List<string> DispatchCalls { get; } = [];

        public async Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            DispatchCalls.Add(actorId);
            var response = responder(actorId, envelope);
            if (response == null)
                return;

            var request = envelope.Payload.Unpack<QueryWorkflowActorBindingRequestedEvent>();
            await streams.GetStream(request.ReplyStreamId).ProduceAsync(response, ct);
        }
    }

    private sealed class FakeAgentTypeVerifier(
        params (string ActorId, Type ExpectedType, bool Result)[] entries) : IAgentTypeVerifier
    {
        private readonly IReadOnlyDictionary<(string ActorId, Type ExpectedType), bool> _entries = entries
            .ToDictionary(
                x => (x.ActorId, x.ExpectedType),
                x => x.Result);

        public Task<bool> IsExpectedAsync(string actorId, Type expectedType, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_entries.TryGetValue((actorId, expectedType), out var result) && result);
        }
    }
}
