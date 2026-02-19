using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Core.Agents;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.DependencyInjection;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
public class WorkflowExecutionOwnerBindingTests
{
    private const string MinimalWorkflowYaml = """
        name: runid-check
        description: minimal workflow for execution tests
        roles: []
        steps:
          - id: step-1
            type: passthrough
        """;

    [Fact]
    public async Task WorkflowExecutionGAgent_ShouldBindOwnerId_Once()
    {
        using var provider = BuildProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        var executionActor = await runtime.CreateAsync<WorkflowExecutionGAgent>("run-bind");
        var execution = executionActor.Agent.Should().BeOfType<WorkflowExecutionGAgent>().Subject;

        execution.BindWorkflowAgentId("owner-a");
        execution.WorkflowAgentId.Should().Be("owner-a");

        var bindSameOwner = () => execution.BindWorkflowAgentId("owner-a");
        bindSameOwner.Should().NotThrow();

        var bindDifferentOwner = () => execution.BindWorkflowAgentId("owner-b");
        bindDifferentOwner.Should().Throw<InvalidOperationException>();

        await runtime.DestroyAsync(executionActor.Id);
    }

    [Fact]
    public async Task WorkflowExecutionGAgent_WithoutOwnerBinding_ShouldFailFast()
    {
        using var provider = BuildProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        var streams = provider.GetRequiredService<IStreamProvider>();
        var ownerActor = await runtime.CreateAsync<WorkflowGAgent>("owner-1");
        var executionActor = await runtime.CreateAsync<WorkflowExecutionGAgent>("run-no-owner");
        await runtime.LinkAsync(ownerActor.Id, executionActor.Id);

        var ownerStream = streams.GetStream(ownerActor.Id);
        var errorResponse = new TaskCompletionSource<ChatResponseEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sub = await ownerStream.SubscribeAsync<EventEnvelope>(envelope =>
        {
            if (envelope.Payload?.Is(ChatResponseEvent.Descriptor) == true)
                errorResponse.TrySetResult(envelope.Payload.Unpack<ChatResponseEvent>());

            return Task.CompletedTask;
        });

        await executionActor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = "hello",
                MessageId = "run-no-owner:s1",
            }),
            PublisherId = "test",
            Direction = EventDirection.Self,
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var response = await errorResponse.Task.WaitAsync(timeout.Token);
        response.Content.Should().Contain("未绑定长期工作流 Actor");
        response.MessageId.Should().Be("run-no-owner:s1");

        await runtime.DestroyAsync(executionActor.Id);
        await runtime.DestroyAsync(ownerActor.Id);
    }

    [Fact]
    public async Task WorkflowExecutionGAgent_DirectChatRequest_ShouldUseActorIdAsRunId()
    {
        using var provider = BuildProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        var streams = provider.GetRequiredService<IStreamProvider>();
        var executionActor = await runtime.CreateAsync<WorkflowExecutionGAgent>("run-direct");
        var execution = executionActor.Agent.Should().BeOfType<WorkflowExecutionGAgent>().Subject;
        execution.BindWorkflowAgentId("owner-direct");
        execution.ConfigureWorkflow(MinimalWorkflowYaml, "runid-check");

        var executionStream = streams.GetStream(executionActor.Id);
        var started = new TaskCompletionSource<StartWorkflowEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sub = await executionStream.SubscribeAsync<EventEnvelope>(envelope =>
        {
            if (envelope.Payload?.Is(StartWorkflowEvent.Descriptor) == true)
                started.TrySetResult(envelope.Payload.Unpack<StartWorkflowEvent>());

            return Task.CompletedTask;
        });

        await executionActor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = "hello",
                MessageId = "run-direct:s1",
            }),
            PublisherId = "test",
            Direction = EventDirection.Self,
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var startEvent = await started.Task.WaitAsync(timeout.Token);
        startEvent.RunId.Should().Be(executionActor.Id);

        await runtime.DestroyAsync(executionActor.Id);
    }

    [Fact]
    public async Task WorkflowExecutionGAgent_MetadataRunIdMismatch_ShouldFailFast()
    {
        using var provider = BuildProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        var streams = provider.GetRequiredService<IStreamProvider>();
        var ownerActor = await runtime.CreateAsync<WorkflowGAgent>("owner-mismatch");
        var executionActor = await runtime.CreateAsync<WorkflowExecutionGAgent>("run-mismatch");
        var execution = executionActor.Agent.Should().BeOfType<WorkflowExecutionGAgent>().Subject;
        execution.BindWorkflowAgentId(ownerActor.Id);
        await runtime.LinkAsync(ownerActor.Id, executionActor.Id);

        var ownerStream = streams.GetStream(ownerActor.Id);
        var errorResponse = new TaskCompletionSource<ChatResponseEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sub = await ownerStream.SubscribeAsync<EventEnvelope>(envelope =>
        {
            if (envelope.Payload?.Is(ChatResponseEvent.Descriptor) == true)
                errorResponse.TrySetResult(envelope.Payload.Unpack<ChatResponseEvent>());

            return Task.CompletedTask;
        });

        var request = new ChatRequestEvent
        {
            Prompt = "hello",
            MessageId = "run-mismatch:s1",
        };
        request.Metadata[ChatRequestMetadataKeys.RunId] = "run-from-metadata";

        await executionActor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(request),
            PublisherId = "test",
            Direction = EventDirection.Self,
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var response = await errorResponse.Task.WaitAsync(timeout.Token);
        response.Content.Should().Contain("run_id 与 actorId 不一致");
        response.MessageId.Should().Be("run-mismatch:s1");

        await runtime.DestroyAsync(executionActor.Id);
        await runtime.DestroyAsync(ownerActor.Id);
    }

    [Fact]
    public async Task WorkflowGAgent_WorkflowCompleted_ShouldPublishTextEndWithMessageId_AndAccumulateStats()
    {
        using var provider = BuildProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        var streams = provider.GetRequiredService<IStreamProvider>();
        var ownerActor = await runtime.CreateAsync<WorkflowGAgent>("owner-completed");
        var workflowActor = await runtime.CreateAsync<WorkflowGAgent>("workflow-completed");
        var workflow = workflowActor.Agent.Should().BeOfType<WorkflowGAgent>().Subject;
        await runtime.LinkAsync(ownerActor.Id, workflowActor.Id);

        var ownerStream = streams.GetStream(ownerActor.Id);
        var messageEnd = new TaskCompletionSource<TextMessageEndEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sub = await ownerStream.SubscribeAsync<EventEnvelope>(envelope =>
        {
            if (envelope.Payload?.Is(TextMessageEndEvent.Descriptor) == true)
                messageEnd.TrySetResult(envelope.Payload.Unpack<TextMessageEndEvent>());

            return Task.CompletedTask;
        });

        await workflowActor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new WorkflowCompletedEvent
            {
                WorkflowName = "wf",
                RunId = "run-1",
                Success = true,
                Output = "done",
            }),
            PublisherId = "test",
            Direction = EventDirection.Self,
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var end = await messageEnd.Task.WaitAsync(timeout.Token);
        end.MessageId.Should().Be("run-1");
        end.Content.Should().Be("done");

        workflow.State.TotalExecutions.Should().Be(1);
        workflow.State.SuccessfulExecutions.Should().Be(1);
        workflow.State.FailedExecutions.Should().Be(0);

        await runtime.DestroyAsync(workflowActor.Id);
        await runtime.DestroyAsync(ownerActor.Id);
    }

    [Fact]
    public async Task WorkflowExecutionGAgent_WorkflowCompleted_ShouldPublishTextEndWithMessageId_WithoutAccumulatingStats()
    {
        using var provider = BuildProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        var streams = provider.GetRequiredService<IStreamProvider>();
        var ownerActor = await runtime.CreateAsync<WorkflowGAgent>("owner-exec-completed");
        var executionActor = await runtime.CreateAsync<WorkflowExecutionGAgent>("run-exec-completed");
        var execution = executionActor.Agent.Should().BeOfType<WorkflowExecutionGAgent>().Subject;
        execution.BindWorkflowAgentId(ownerActor.Id);
        await runtime.LinkAsync(ownerActor.Id, executionActor.Id);

        var ownerStream = streams.GetStream(ownerActor.Id);
        var messageEnd = new TaskCompletionSource<TextMessageEndEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sub = await ownerStream.SubscribeAsync<EventEnvelope>(envelope =>
        {
            if (envelope.Payload?.Is(TextMessageEndEvent.Descriptor) == true)
                messageEnd.TrySetResult(envelope.Payload.Unpack<TextMessageEndEvent>());

            return Task.CompletedTask;
        });

        await executionActor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new WorkflowCompletedEvent
            {
                WorkflowName = "wf",
                RunId = executionActor.Id,
                Success = false,
                Error = "boom",
            }),
            PublisherId = "test",
            Direction = EventDirection.Self,
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var end = await messageEnd.Task.WaitAsync(timeout.Token);
        end.MessageId.Should().Be(executionActor.Id);
        end.Content.Should().Contain("工作流执行失败");

        execution.State.TotalExecutions.Should().Be(0);
        execution.State.SuccessfulExecutions.Should().Be(0);
        execution.State.FailedExecutions.Should().Be(0);

        await runtime.DestroyAsync(executionActor.Id);
        await runtime.DestroyAsync(ownerActor.Id);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddSingleton<IRoleAgentTypeResolver, RoleGAgentTypeResolver>();
        return services.BuildServiceProvider();
    }
}
