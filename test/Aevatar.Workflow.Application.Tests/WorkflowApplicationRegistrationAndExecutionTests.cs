using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.DependencyInjection;
using Aevatar.Workflow.Application.Runs;
using Aevatar.Workflow.Application.Workflows;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowApplicationRegistrationAndExecutionTests
{
    [Fact]
    public void AddWorkflowApplication_Default_ShouldRegisterBuiltInDirectWorkflow()
    {
        var services = new ServiceCollection();

        services.AddWorkflowApplication();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkflowDefinitionRegistry>();
        var yaml = registry.GetYaml("direct");
        yaml.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void AddWorkflowApplication_WhenConfigured_ShouldAllowDisablingBuiltInDirectWorkflow()
    {
        var services = new ServiceCollection();

        services.AddWorkflowApplication(options => options.RegisterBuiltInDirectWorkflow = false);

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkflowDefinitionRegistry>();
        registry.GetYaml("direct").Should().BeNull();
    }

    [Fact]
    public void AddWorkflowApplication_Default_ShouldRegisterBuiltInAutoWorkflow()
    {
        var services = new ServiceCollection();

        services.AddWorkflowApplication();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkflowDefinitionRegistry>();
        var yaml = registry.GetYaml("auto");
        yaml.Should().NotBeNullOrWhiteSpace();
        yaml.Should().Contain("name: auto");
        yaml.Should().Contain("dynamic_workflow");
        yaml!.IndexOf("- id: done", StringComparison.Ordinal)
            .Should().BeGreaterThan(yaml.IndexOf("- id: extract_and_execute", StringComparison.Ordinal));
    }

    [Fact]
    public void AddWorkflowApplication_WhenConfigured_ShouldAllowDisablingBuiltInAutoWorkflow()
    {
        var services = new ServiceCollection();

        services.AddWorkflowApplication(options => options.RegisterBuiltInAutoWorkflow = false);

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkflowDefinitionRegistry>();
        registry.GetYaml("auto").Should().BeNull();
    }

    [Fact]
    public void AddWorkflowApplication_Default_ShouldRegisterBuiltInAutoReviewWorkflow()
    {
        var services = new ServiceCollection();

        services.AddWorkflowApplication();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkflowDefinitionRegistry>();
        var yaml = registry.GetYaml("auto_review");
        yaml.Should().NotBeNullOrWhiteSpace();
        yaml.Should().Contain("name: auto_review");
        yaml.Should().Contain("\"true\": done");
        yaml.Should().Contain("Approve to finalize YAML for manual run");
        yaml!.IndexOf("- id: done", StringComparison.Ordinal)
            .Should().BeGreaterThan(yaml.IndexOf("- id: show_for_approval", StringComparison.Ordinal));
    }

    [Fact]
    public void AddWorkflowApplication_WhenConfigured_ShouldAllowDisablingBuiltInAutoReviewWorkflow()
    {
        var services = new ServiceCollection();

        services.AddWorkflowApplication(options => options.RegisterBuiltInAutoReviewWorkflow = false);

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkflowDefinitionRegistry>();
        registry.GetYaml("auto_review").Should().BeNull();
    }

    [Fact]
    public void AddWorkflowApplication_Default_ShouldRegisterRunBehaviorOptions()
    {
        var services = new ServiceCollection();

        services.AddWorkflowApplication();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<WorkflowRunBehaviorOptions>();

        options.DefaultWorkflowName.Should().Be("direct");
        options.UseAutoAsDefaultWhenWorkflowUnspecified.Should().BeFalse();
        options.EnableDirectFallback.Should().BeTrue();
        options.DirectFallbackWorkflowWhitelist.Should().Contain("auto");
        options.DirectFallbackWorkflowWhitelist.Should().Contain("auto_review");
        options.DirectFallbackExceptionWhitelist.Should().Contain(typeof(WorkflowDirectFallbackTriggerException));
    }

    [Fact]
    public void AddWorkflowApplication_WhenConfigured_ShouldApplyRunBehaviorOptionsToFallbackPolicy()
    {
        var services = new ServiceCollection();
        services.AddWorkflowApplication(
            configureRunBehavior: options =>
            {
                options.DefaultWorkflowName = "direct";
                options.UseAutoAsDefaultWhenWorkflowUnspecified = true;
                options.EnableDirectFallback = true;
                options.DirectFallbackWorkflowWhitelist.Clear();
                options.DirectFallbackWorkflowWhitelist.Add("analysis");
                options.DirectFallbackExceptionWhitelist.Clear();
                options.DirectFallbackExceptionWhitelist.Add(typeof(TimeoutException));
            });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<WorkflowRunBehaviorOptions>();
        var policy = provider.GetRequiredService<WorkflowDirectFallbackPolicy>();

        options.UseAutoAsDefaultWhenWorkflowUnspecified.Should().BeTrue();
        options.DirectFallbackWorkflowWhitelist.Should().ContainSingle().Which.Should().Be("analysis");
        options.DirectFallbackExceptionWhitelist.Should().ContainSingle().Which.Should().Be(typeof(TimeoutException));

        policy.ShouldFallback(new WorkflowChatRunRequest("hello", "analysis", null), new TimeoutException("timeout"))
            .Should().BeTrue();
        policy.ShouldFallback(
                new WorkflowChatRunRequest("hello", "analysis", null),
                new WorkflowDirectFallbackTriggerException("boom"))
            .Should().BeFalse();
        policy.ShouldFallback(new WorkflowChatRunRequest("hello", "analysis", null), new InvalidOperationException("boom"))
            .Should().BeFalse();
    }

    [Fact]
    public void AddWorkflowApplication_ShouldWireWorkflowRunOutputStreamerAcrossAbstractions()
    {
        var services = new ServiceCollection();
        services.AddWorkflowApplication();
        using var provider = services.BuildServiceProvider();

        var concrete = provider.GetRequiredService<WorkflowRunOutputStreamer>();
        var outputStreamer = provider.GetRequiredService<IWorkflowRunOutputStreamer>();
        var eventOutputStream = provider.GetRequiredService<IEventOutputStream<WorkflowRunEvent, WorkflowOutputFrame>>();
        var frameMapper = provider.GetRequiredService<IEventFrameMapper<WorkflowRunEvent, WorkflowOutputFrame>>();

        outputStreamer.Should().BeSameAs(concrete);
        eventOutputStream.Should().BeSameAs(concrete);
        frameMapper.Should().BeSameAs(concrete);
    }

    [Fact]
    public void EnvelopeFactory_ShouldUseSessionIdFromMetadata()
    {
        var services = new ServiceCollection();
        services.AddWorkflowApplication();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ICommandEnvelopeFactory<WorkflowChatRunRequest>>();
        var context = new CommandContext(
            TargetId: "actor-1",
            CommandId: "cmd-1",
            CorrelationId: "corr-1",
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkflowRunCommandMetadataKeys.SessionId] = "session-42",
                [WorkflowRunCommandMetadataKeys.ChannelId] = "slack#ops",
            });
        var command = new WorkflowChatRunRequest("hello", "direct", "actor-1");

        var envelope = factory.CreateEnvelope(command, context);
        var request = envelope.Payload.Unpack<ChatRequestEvent>();

        envelope.TargetActorId.Should().Be("actor-1");
        envelope.CorrelationId.Should().Be("corr-1");
        envelope.Direction.Should().Be(EventDirection.Self);
        envelope.PublisherId.Should().Be("api");
        request.Prompt.Should().Be("hello");
        request.SessionId.Should().Be("session-42");
        request.Metadata[WorkflowRunCommandMetadataKeys.ChannelId].Should().Be("slack#ops");
    }

    [Fact]
    public void EnvelopeFactory_WhenSessionIdMissingOrWhitespace_ShouldFallbackToCorrelationId()
    {
        var services = new ServiceCollection();
        services.AddWorkflowApplication();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ICommandEnvelopeFactory<WorkflowChatRunRequest>>();
        var command = new WorkflowChatRunRequest("hello", null, null);

        var noMetadata = factory.CreateEnvelope(command, new CommandContext(
            "actor-2",
            "cmd-2",
            "corr-2",
            new Dictionary<string, string>()));
        noMetadata.Payload.Unpack<ChatRequestEvent>().SessionId.Should().Be("corr-2");

        var whiteSpaceSession = factory.CreateEnvelope(command, new CommandContext(
            "actor-3",
            "cmd-3",
            "corr-3",
            new Dictionary<string, string>
            {
                [WorkflowRunCommandMetadataKeys.SessionId] = "   ",
            }));
        whiteSpaceSession.Payload.Unpack<ChatRequestEvent>().SessionId.Should().Be("corr-3");
    }

    [Fact]
    public async Task WorkflowRunRequestExecutor_WhenActorSucceeds_ShouldNotPushError()
    {
        var actor = new RecordingActor();
        var sink = new RecordingSink();
        var executor = new WorkflowRunRequestExecutor(NullLogger<WorkflowRunRequestExecutor>.Instance);
        var envelope = new EventEnvelope { Id = "e-1" };

        await executor.ExecuteAsync(actor, actor.Id, envelope, sink, CancellationToken.None);

        actor.Received.Should().ContainSingle().Which.Should().BeSameAs(envelope);
        sink.Events.Should().BeEmpty();
        sink.CompleteCalls.Should().Be(0);
    }

    [Fact]
    public async Task WorkflowRunRequestExecutor_WhenActorThrows_ShouldPushErrorAndComplete()
    {
        var actor = new ThrowingActor(new InvalidOperationException("boom"));
        var sink = new RecordingSink();
        var executor = new WorkflowRunRequestExecutor(NullLogger<WorkflowRunRequestExecutor>.Instance);

        await executor.ExecuteAsync(actor, actor.Id, new EventEnvelope { Id = "e-2" }, sink, CancellationToken.None);

        sink.Events.Should().ContainSingle();
        var error = sink.Events[0].Should().BeOfType<WorkflowRunErrorEvent>().Subject;
        error.Code.Should().Be("INTERNAL_ERROR");
        error.Message.Should().Be("Workflow execution error: boom");
        sink.CompleteCalls.Should().Be(1);
    }

    [Fact]
    public async Task WorkflowRunRequestExecutor_WhenExceptionMessageContainsNewLine_ShouldSanitizeErrorMessage()
    {
        var actor = new ThrowingActor(new InvalidOperationException("boom\ntrace"));
        var sink = new RecordingSink();
        var executor = new WorkflowRunRequestExecutor(NullLogger<WorkflowRunRequestExecutor>.Instance);

        await executor.ExecuteAsync(actor, actor.Id, new EventEnvelope { Id = "e-2b" }, sink, CancellationToken.None);

        sink.Events.Should().ContainSingle();
        var error = sink.Events[0].Should().BeOfType<WorkflowRunErrorEvent>().Subject;
        error.Code.Should().Be("INTERNAL_ERROR");
        error.Message.Should().Be("Workflow execution error: boom trace");
        sink.CompleteCalls.Should().Be(1);
    }

    [Fact]
    public async Task WorkflowRunRequestExecutor_WhenExceptionMessageIsBlank_ShouldUseUnknownErrorFallback()
    {
        var actor = new ThrowingActor(new InvalidOperationException(string.Empty));
        var sink = new RecordingSink();
        var executor = new WorkflowRunRequestExecutor(NullLogger<WorkflowRunRequestExecutor>.Instance);

        await executor.ExecuteAsync(actor, actor.Id, new EventEnvelope { Id = "e-2c" }, sink, CancellationToken.None);

        sink.Events.Should().ContainSingle();
        var error = sink.Events[0].Should().BeOfType<WorkflowRunErrorEvent>().Subject;
        error.Message.Should().Be("Workflow execution error: unknown error");
        sink.CompleteCalls.Should().Be(1);
    }

    [Fact]
    public async Task WorkflowRunRequestExecutor_WhenSinkPushThrowsInvalidOperation_ShouldStillComplete()
    {
        var actor = new ThrowingActor(new InvalidOperationException("boom"));
        var sink = new ThrowingPushSink();
        var executor = new WorkflowRunRequestExecutor(NullLogger<WorkflowRunRequestExecutor>.Instance);

        await executor.ExecuteAsync(actor, actor.Id, new EventEnvelope { Id = "e-3" }, sink, CancellationToken.None);

        sink.CompleteCalls.Should().Be(1);
    }

    private sealed class RecordingActor : IActor
    {
        public string Id => "actor-recording";
        public IAgent Agent { get; } = new StubAgent("agent-recording");
        public List<EventEnvelope> Received { get; } = [];

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Received.Add(envelope);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingActor : IActor
    {
        private readonly Exception _exception;

        public ThrowingActor(Exception exception)
        {
            _exception = exception;
        }

        public string Id => "actor-throwing";
        public IAgent Agent { get; } = new StubAgent("agent-throwing");
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.FromException(_exception);
    }

    private sealed class RecordingSink : IWorkflowRunEventSink
    {
        public List<WorkflowRunEvent> Events { get; } = [];
        public int CompleteCalls { get; private set; }

        public void Push(WorkflowRunEvent evt) => Events.Add(evt);

        public ValueTask PushAsync(WorkflowRunEvent evt, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Events.Add(evt);
            return ValueTask.CompletedTask;
        }

        public void Complete() => CompleteCalls++;

        public async IAsyncEnumerable<WorkflowRunEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = ct;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingPushSink : IWorkflowRunEventSink
    {
        public int CompleteCalls { get; private set; }

        public void Push(WorkflowRunEvent evt) => throw new InvalidOperationException("push failed");

        public ValueTask PushAsync(WorkflowRunEvent evt, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = evt;
            throw new InvalidOperationException("push failed");
        }

        public void Complete() => CompleteCalls++;

        public async IAsyncEnumerable<WorkflowRunEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = ct;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }
}
