using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Modules;

public sealed class ToolCallModuleContextTests
{
    [Fact]
    public async Task ToolCallModule_ShouldPassCurrentStepInputFileRefsToToolRequest()
    {
        var tool = new CapturingWorkflowTool("document_extract");
        var module = CreateModule(tool);
        var context = new RecordingWorkflowContext();
        var fileRef = BuildWorkflowFileRef("file-step");

        await ExecuteToolCallAsync(
            module,
            context,
            tool.Name,
            inputFileRefs: [fileRef]);

        tool.LastRequest.Should().NotBeNull();
        var requestFileRef = tool.LastRequest!.InputFileRefs.Should().ContainSingle().Subject;
        requestFileRef.FileId.Should().Be("file-step");
        requestFileRef.Should().NotBeSameAs(fileRef);
        context.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single().Success.Should().BeTrue();
    }

    [Fact]
    public async Task WorkflowToolExecutionRequest_ShouldCloneInputFileRefs()
    {
        var source = BuildWorkflowFileRef("file-clone");
        var request = new WorkflowToolExecutionRequest("{}", [source]);

        source.FileName = "mutated.txt";

        request.InputFileRefs.Should().ContainSingle().Which.FileName.Should().Be("file-clone.txt");
    }

    private static ToolCallModule CreateModule(IWorkflowTool tool) =>
        new([new SingleToolSource(tool)], NullLogger<ToolCallModule>.Instance);

    private static async Task ExecuteToolCallAsync(
        ToolCallModule module,
        RecordingWorkflowContext context,
        string toolName,
        IReadOnlyList<WorkflowFileRef>? inputFileRefs = null)
    {
        var request = new StepRequestEvent
        {
            StepId = "extract",
            StepType = "tool_call",
            RunId = context.RunId,
            Input = "{}",
            Parameters = { ["tool"] = toolName },
        };
        request.InputFileRefs.Add(inputFileRefs?.Select(static fileRef => fileRef.Clone()) ?? []);

        await module.HandleAsync(Envelope(request), context, CancellationToken.None);
    }

    private static WorkflowFileRef BuildWorkflowFileRef(string fileId) =>
        new()
        {
            FileId = fileId,
            ArtifactId = $"artifact-{fileId}",
            SourceKind = WorkflowFileSourceKind.ChatInput,
            FileName = $"{fileId}.txt",
            MediaType = "text/plain",
        };

    private static EventEnvelope Envelope(IMessage evt) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
        };

    private sealed class CapturingWorkflowTool(string name) : IWorkflowTool
    {
        public string Name { get; } = name;

        public WorkflowToolExecutionRequest? LastRequest { get; private set; }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("""{"legacy":true}""");
        }

        public Task<string> ExecuteAsync(WorkflowToolExecutionRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastRequest = request;
            return Task.FromResult("""{"typed":true}""");
        }
    }

    private sealed class SingleToolSource(IWorkflowTool tool) : IWorkflowToolSource
    {
        public Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<IWorkflowTool>>([tool]);
        }
    }

    private sealed class RecordingWorkflowContext : IWorkflowExecutionContext
    {
        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "agent-1";
        public string RunId => "run-1";
        public IServiceProvider Services { get; } = new EmptyServiceProvider();
        public ILogger Logger { get; } = NullLogger.Instance;
        public List<(IMessage Event, TopologyAudience Direction)> Published { get; } = [];

        public TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new() => new();

        public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new() => [];

        public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
            where TState : class, IMessage<TState> => Task.CompletedTask;

        public Task ClearStateAsync(string scopeKey, CancellationToken ct = default) => Task.CompletedTask;

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            Published.Add((evt, direction));
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage => Task.CompletedTask;

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, 1, RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, 1, RuntimeCallbackBackend.InMemory));

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }
}
