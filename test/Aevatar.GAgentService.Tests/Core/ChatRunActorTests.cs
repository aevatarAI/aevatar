using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Infrastructure.Adapters;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class ChatRunActorTests
{
    [Fact]
    public async Task StartAndTerminate_ShouldInitializeSessionState_AndClearActiveSubscriptions()
    {
        var actor = CreateActor("resp_1");
        var startedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-23T00:00:00+00:00"));

        await actor.HandleStartAsync(new StartChatRunRequested
        {
            ResponseId = "resp_1",
            ModelName = "gpt-test",
            IdleTtl = Duration.FromTimeSpan(TimeSpan.FromMinutes(5)),
            StartedAt = startedAt,
            Messages =
            {
                new ChatRunMessageRecord
                {
                    Role = "user",
                    Content = "hello",
                },
            },
        });
        await actor.HandleSubmitToolCallAsync(BuildSubmit(
            toolCallId: "call_complete",
            runId: "run_complete",
            waitMode: ChatRunSubRunWaitMode.Complete));

        await actor.HandleTerminateAsync(new TerminateChatRunRequested
        {
            Reason = "client_closed",
            ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-23T00:01:00+00:00")),
        });

        actor.State.ResponseId.Should().Be("resp_1");
        actor.State.ModelName.Should().Be("gpt-test");
        actor.State.Messages.Should().ContainSingle(message => message.Role == "user" && message.Content == "hello");
        actor.State.IdleTtl.Should().Be(Duration.FromTimeSpan(TimeSpan.FromMinutes(5)));
        actor.State.InitializedAt.Should().Be(startedAt);
        actor.State.Terminated.Should().BeTrue();
        actor.State.ActiveSubRunSubscriptions.Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitWaitStreamToolCall_ShouldRecordHistory_WithoutCreatingActiveSubscription()
    {
        var actor = await StartedActorAsync("resp_stream");

        await actor.HandleSubmitToolCallAsync(BuildSubmit(
            toolCallId: "call_stream",
            runId: "run_stream",
            waitMode: ChatRunSubRunWaitMode.Stream,
            streamTopic: "aevatar://actors/actor-1/runs/run_stream",
            resultJson: """
                {"run_id":"run_stream","stream_topic":"aevatar://actors/actor-1/runs/run_stream","wait":"stream"}
                """));

        actor.State.ActiveSubRunSubscriptions.Should().BeEmpty();
        var history = actor.State.ToolCallHistory.Should().ContainSingle().Subject;
        history.ToolCallId.Should().Be("call_stream");
        history.RunId.Should().Be("run_stream");
        history.ResultJson.Should().Contain("aevatar://actors/actor-1/runs/run_stream");
        history.LlmRound.Should().Be(1);
    }

    [Fact]
    public async Task SubmitWaitCompleteThenTerminal_ShouldFoldToolResult_AndAdvanceRound()
    {
        var actor = await StartedActorAsync("resp_complete");

        await actor.HandleSubmitToolCallAsync(BuildSubmit(
            toolCallId: "call_complete",
            runId: "run_complete",
            waitMode: ChatRunSubRunWaitMode.Complete,
            llmRound: 2));

        actor.State.ActiveSubRunSubscriptions.Should().ContainSingle(subscription =>
            subscription.RunId == "run_complete" &&
            subscription.CallerToolCallId == "call_complete" &&
            subscription.WaitMode == ChatRunSubRunWaitMode.Complete);

        await actor.HandleSubRunTerminalAsync(new ChatRunSubRunTerminalObserved
        {
            RunId = "run_complete",
            Status = "RunFinished",
            ResultJson = """{"run_id":"run_complete","status":"RunFinished","content":"done"}""",
        });

        actor.State.ActiveSubRunSubscriptions.Should().BeEmpty();
        actor.State.ToolCallHistory.Should().ContainSingle()
            .Which.ResultJson.Should().Contain("\"content\":\"done\"");
        actor.State.Messages.Should().ContainSingle(message =>
            message.Role == "tool" &&
            message.ToolCallId == "call_complete" &&
            message.Content.Contains("\"content\":\"done\"", StringComparison.Ordinal));
        actor.State.CurrentLlmRound.Should().Be(3);
    }

    [Fact]
    public async Task State_ShouldExposeTypedSubRunSubscriptionShape()
    {
        var actor = await StartedActorAsync("resp_typed");

        await actor.HandleSubmitToolCallAsync(BuildSubmit(
            toolCallId: "call_complete",
            runId: "run_typed",
            waitMode: ChatRunSubRunWaitMode.Complete));

        typeof(ChatRunState)
            .GetProperty(nameof(ChatRunState.ActiveSubRunSubscriptions))!
            .PropertyType
            .Should()
            .Be(typeof(RepeatedField<ChatRunSubRunSubscription>));
        var subscription = actor.State.ActiveSubRunSubscriptions.Should().ContainSingle().Subject;
        subscription.RunId.Should().Be("run_typed");
        subscription.TargetKind.Should().Be(ChatRunSubRunTargetKind.Gagent);
        subscription.TargetId.Should().Be("actor-1");
        subscription.StreamTopic.Should().Be("aevatar://actors/actor-1/runs/run_typed");
    }

    [Fact]
    public async Task MultipleWaitCompleteSubRuns_ShouldCorrelateTerminalEventsByRunId()
    {
        var actor = await StartedActorAsync("resp_multi");

        await actor.HandleSubmitToolCallAsync(BuildSubmit(
            toolCallId: "call_first",
            runId: "run_first",
            waitMode: ChatRunSubRunWaitMode.Complete));
        await actor.HandleSubmitToolCallAsync(BuildSubmit(
            toolCallId: "call_second",
            runId: "run_second",
            waitMode: ChatRunSubRunWaitMode.Complete));

        actor.State.ActiveSubRunSubscriptions.Select(static subscription => subscription.RunId)
            .Should()
            .Equal("run_first", "run_second");

        await actor.HandleSubRunTerminalAsync(new ChatRunSubRunTerminalObserved
        {
            RunId = "run_second",
            ResultJson = """{"run_id":"run_second","content":"second"}""",
        });

        actor.State.ActiveSubRunSubscriptions.Should().ContainSingle()
            .Which.RunId.Should().Be("run_first");
        actor.State.Messages.Should().ContainSingle(message =>
            message.Role == "tool" &&
            message.ToolCallId == "call_second" &&
            message.Content.Contains("second", StringComparison.Ordinal));

        await actor.HandleSubRunTerminalAsync(new ChatRunSubRunTerminalObserved
        {
            RunId = "run_first",
            ResultJson = """{"run_id":"run_first","content":"first"}""",
        });

        actor.State.ActiveSubRunSubscriptions.Should().BeEmpty();
        actor.State.Messages
            .Where(static message => message.Role == "tool")
            .Select(static message => message.ToolCallId)
            .Should()
            .Equal("call_second", "call_first");
        actor.State.ToolCallHistory.Single(call => call.RunId == "run_first")
            .ResultJson.Should().Contain("first");
        actor.State.ToolCallHistory.Single(call => call.RunId == "run_second")
            .ResultJson.Should().Contain("second");
    }

    [Fact]
    public async Task DispatchPortPath_ShouldPublishToolResultReadyOnSelfStream()
    {
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddAevatarRuntime()
            .AddSingleton<IChatRunActorPort, ChatRunActorAdapter>()
            .BuildServiceProvider();
        var port = services.GetRequiredService<IChatRunActorPort>();
        var subscriptionProvider = services.GetRequiredService<IActorEventSubscriptionProvider>();
        var runtime = services.GetRequiredService<IActorRuntime>();
        var ready = new TaskCompletionSource<ChatRunToolResultReady>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var actorId = await port.StartAsync(new ChatRunStartRequest(
            "resp_publisher",
            "gpt-test",
            [ChatMessage.User("hello")]));
        await using var subscription = await subscriptionProvider.SubscribeAsync<ChatRunToolResultReady>(
            actorId,
            message =>
            {
                ready.TrySetResult(message.Clone());
                return Task.CompletedTask;
            });

        await port.SubmitToolCallAsync(
            actorId,
            new ChatRunToolCompletionRequest(
                "resp_publisher",
                "gpt-test",
                [ChatMessage.User("hello")],
                new ToolCall
                {
                    Id = "call_publish",
                    Name = "aevatar_invoke_gagent",
                    ArgumentsJson = """{"actor_id":"actor-1","wait":"complete"}""",
                },
                """{"actor_id":"actor-1","wait":"complete"}""",
                """{"run_id":"run_publish","actor_id":"actor-1","stream_topic":"aevatar://actors/actor-1/runs/run_publish","wait":"complete"}""",
                1));
        await port.ObserveSubRunTerminalAsync(actorId, new ChatRunSubRunTerminalObserved
        {
            RunId = "run_publish",
            ResultJson = """{"run_id":"run_publish","content":"published"}""",
        });

        var published = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        published.RunId.Should().Be("run_publish");
        published.CallerToolCallId.Should().Be("call_publish");
        published.ResultJson.Should().Contain("published");

        var actor = await runtime.GetAsync(actorId);
        ((ChatRunActor)actor!.Agent).State.ActiveSubRunSubscriptions.Should().BeEmpty();
    }

    private static ChatRunActor CreateActor(string responseId) =>
        GAgentServiceTestKit.CreateStatefulAgent<ChatRunActor, ChatRunState>(
            new InMemoryEventStore(),
            "chat-run-" + responseId,
            static () => new ChatRunActor());

    private static async Task<ChatRunActor> StartedActorAsync(string responseId)
    {
        var actor = CreateActor(responseId);
        await actor.HandleStartAsync(new StartChatRunRequested
        {
            ResponseId = responseId,
            ModelName = "gpt-test",
            Messages =
            {
                new ChatRunMessageRecord
                {
                    Role = "user",
                    Content = "hello",
                },
            },
        });
        return actor;
    }

    private static SubmitChatRunToolCallRequested BuildSubmit(
        string toolCallId,
        string runId,
        ChatRunSubRunWaitMode waitMode,
        string streamTopic = "",
        string resultJson = "",
        int llmRound = 1)
    {
        streamTopic = string.IsNullOrWhiteSpace(streamTopic)
            ? $"aevatar://actors/actor-1/runs/{runId}"
            : streamTopic;
        resultJson = string.IsNullOrWhiteSpace(resultJson)
            ? $$"""{"run_id":"{{runId}}","actor_id":"actor-1","stream_topic":"{{streamTopic}}","wait":"{{WaitModeValue(waitMode)}}"}"""
            : resultJson;
        return new SubmitChatRunToolCallRequested
        {
            ToolCallId = toolCallId,
            ToolName = "aevatar_invoke_gagent",
            Arguments = BuildArguments(waitMode),
            ResultJson = resultJson,
            RunId = runId,
            TargetKind = ChatRunSubRunTargetKind.Gagent,
            TargetId = "actor-1",
            WaitMode = waitMode,
            StreamTopic = streamTopic,
            ActorId = "actor-1",
            LlmRound = llmRound,
        };
    }

    private static Struct BuildArguments(ChatRunSubRunWaitMode waitMode)
    {
        var arguments = new Struct();
        arguments.Fields["actor_id"] = Google.Protobuf.WellKnownTypes.Value.ForString("actor-1");
        arguments.Fields["wait"] = Google.Protobuf.WellKnownTypes.Value.ForString(WaitModeValue(waitMode));
        return arguments;
    }

    private static string WaitModeValue(ChatRunSubRunWaitMode waitMode) =>
        waitMode switch
        {
            ChatRunSubRunWaitMode.Ack => "ack",
            ChatRunSubRunWaitMode.Complete => "complete",
            _ => "stream",
        };
}
