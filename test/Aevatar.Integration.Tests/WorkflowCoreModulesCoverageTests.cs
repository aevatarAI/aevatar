using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "WorkflowCoreModules")]
public sealed class WorkflowCoreModulesCoverageTests
{
    [Fact]
    public async Task ToolCallModule_MissingToolParameter_ShouldPublishFailedStepCompleted()
    {
        var module = new ToolCallModule();
        var ctx = CreateContext();
        var request = new StepRequestEvent
        {
            StepId = "step-1",
            StepType = "tool_call",
            Input = "{}",
        };

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        ctx.Published.Should().ContainSingle();
        ctx.Published[0].direction.Should().Be(EventDirection.Self);
        var completed = ctx.Published[0].evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.StepId.Should().Be("step-1");
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("缺少 tool 参数");
    }

    [Fact]
    public async Task ToolCallModule_ToolNotFound_ShouldPublishToolFailureEvents()
    {
        var module = new ToolCallModule();
        var ctx = CreateContext();
        var request = new StepRequestEvent
        {
            StepId = "step-2",
            StepType = "tool_call",
            Input = """{"x":1}""",
            Parameters = { ["tool"] = "missing_tool" },
        };

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        ctx.Published.Should().HaveCount(3);
        ctx.Published.Select(x => x.evt.GetType()).Should().ContainInOrder(
            typeof(ToolCallEvent),
            typeof(ToolResultEvent),
            typeof(StepCompletedEvent));

        var toolResult = ctx.Published[1].evt.Should().BeOfType<ToolResultEvent>().Subject;
        toolResult.Success.Should().BeFalse();
        toolResult.Error.Should().Contain("tool 'missing_tool' execution failed");

        var completed = ctx.Published[2].evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("tool 'missing_tool' execution failed");
    }

    [Fact]
    public async Task ToolCallModule_WhenDiscoveryFailsThenToolFound_ShouldStillExecuteSuccessfully()
    {
        var module = new ToolCallModule();
        var source = new CountingToolSource(
            [
                new FakeAgentTool("echo", args => args),
            ]);
        var services = new ServiceCollection()
            .AddSingleton<IAgentToolSource>(new ThrowingToolSource())
            .AddSingleton<IAgentToolSource>(source)
            .BuildServiceProvider();
        var ctx = CreateContext(services);
        var request = new StepRequestEvent
        {
            StepId = "step-3",
            StepType = "tool_call",
            Input = """{"msg":"ok"}""",
            Parameters = { ["tool"] = "echo" },
        };

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        source.DiscoverCalls.Should().Be(1);
        var toolResult = ctx.Published.Select(x => x.evt).OfType<ToolResultEvent>().Single();
        toolResult.Success.Should().BeTrue();
        toolResult.ResultJson.Should().Be("""{"msg":"ok"}""");
    }

    [Fact]
    public async Task ToolCallModule_ShouldCacheDiscoveredToolsAcrossCalls()
    {
        var module = new ToolCallModule();
        var source = new CountingToolSource(
            [
                new FakeAgentTool("cached_echo", args => args),
            ]);
        var services = new ServiceCollection()
            .AddSingleton<IAgentToolSource>(source)
            .BuildServiceProvider();
        var ctx = CreateContext(services);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "step-4",
                StepType = "tool_call",
                Input = """{"n":1}""",
                Parameters = { ["tool"] = "cached_echo" },
            }),
            ctx,
            CancellationToken.None);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "step-5",
                StepType = "tool_call",
                Input = """{"n":2}""",
                Parameters = { ["tool"] = "cached_echo" },
            }),
            ctx,
            CancellationToken.None);

        source.DiscoverCalls.Should().Be(1);
        ctx.Published.Select(x => x.evt).OfType<ToolResultEvent>().Should().OnlyContain(x => x.Success);
    }

    [Fact]
    public async Task ToolCallModule_WhenToolThrows_ShouldPublishFailedStepCompleted()
    {
        var module = new ToolCallModule();
        var source = new CountingToolSource(
            [
                new FakeAgentTool("explode", _ => throw new InvalidOperationException("boom")),
            ]);
        var services = new ServiceCollection()
            .AddSingleton<IAgentToolSource>(source)
            .BuildServiceProvider();
        var ctx = CreateContext(services);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "step-6",
                StepType = "tool_call",
                Parameters = { ["tool"] = "explode" },
            }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Last();
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("execution failed: boom");
    }

    [Fact]
    public async Task ForEachModule_WithEmptyInput_ShouldCompleteImmediately()
    {
        var module = new ForEachModule();
        var ctx = CreateContext();
        var request = new StepRequestEvent
        {
            StepId = "foreach-1",
            StepType = "foreach",
            Input = "",
        };

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        ctx.Published.Should().ContainSingle();
        var completed = ctx.Published[0].evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.StepId.Should().Be("foreach-1");
        completed.Success.Should().BeTrue();
        completed.Output.Should().BeEmpty();
    }

    [Fact]
    public async Task ForEachModule_ShouldDispatchSubRequestsAndAggregateCompletion()
    {
        var module = new ForEachModule();
        var ctx = CreateContext();
        var request = new StepRequestEvent
        {
            StepId = "foreach-2",
            StepType = "foreach",
            Input = "alpha\n---\nbeta",
            Parameters =
            {
                ["sub_step_type"] = "transform",
                ["sub_target_role"] = "worker_role",
                ["sub_param_op"] = "uppercase",
            },
        };

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        var subRequests = ctx.Published.Select(x => x.evt).OfType<StepRequestEvent>().ToList();
        subRequests.Should().HaveCount(2);
        subRequests[0].StepId.Should().Be("foreach-2_item_0");
        subRequests[0].StepType.Should().Be("transform");
        subRequests[0].TargetRole.Should().Be("worker_role");
        subRequests[0].Parameters["op"].Should().Be("uppercase");
        subRequests[1].StepId.Should().Be("foreach-2_item_1");

        var countBeforeCompletions = ctx.Published.Count;
        await module.HandleAsync(Envelope(new StepCompletedEvent { StepId = "foreach-2_item_0", Success = true, Output = "A" }), ctx, CancellationToken.None);
        await module.HandleAsync(Envelope(new StepCompletedEvent { StepId = "foreach-2_item_0_sub_1", Success = true, Output = "IGNORED" }), ctx, CancellationToken.None);
        await module.HandleAsync(Envelope(new StepCompletedEvent { StepId = "foreach-2_item_1", Success = false, Output = "B" }), ctx, CancellationToken.None);

        var delta = ctx.Published.Skip(countBeforeCompletions).Select(x => x.evt).OfType<StepCompletedEvent>().ToList();
        delta.Should().ContainSingle();
        delta[0].StepId.Should().Be("foreach-2");
        delta[0].Success.Should().BeFalse();
        delta[0].Output.Should().Be("A\n---\nB");
    }

    [Fact]
    public async Task WhileModule_ShouldIterateAndThenComplete()
    {
        var module = new WhileModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "while-1",
                StepType = "while",
                Input = "initial",
                TargetRole = "worker",
                Parameters =
                {
                    ["step"] = "transform",
                    ["max_iterations"] = "3",
                },
            }),
            ctx,
            CancellationToken.None);

        var firstDispatch = ctx.Published.Select(x => x.evt).OfType<StepRequestEvent>().Single();
        firstDispatch.StepId.Should().Be("while-1_iter_0");
        firstDispatch.StepType.Should().Be("transform");
        firstDispatch.TargetRole.Should().Be("worker");
        firstDispatch.Input.Should().Be("initial");

        var countAfterStart = ctx.Published.Count;
        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "while-1_iter_0", Success = true, Output = "continue" }),
            ctx,
            CancellationToken.None);
        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "while-1_iter_1", Success = true, Output = "more" }),
            ctx,
            CancellationToken.None);
        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "while-1_iter_2", Success = true, Output = "final" }),
            ctx,
            CancellationToken.None);
        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "not_a_loop", Success = true, Output = "ignored" }),
            ctx,
            CancellationToken.None);

        var deltaEvents = ctx.Published.Skip(countAfterStart).ToList();
        deltaEvents.Should().HaveCount(3);

        var secondDispatch = deltaEvents[0].evt.Should().BeOfType<StepRequestEvent>().Subject;
        secondDispatch.StepId.Should().Be("while-1_iter_1");
        secondDispatch.StepType.Should().Be("transform");
        secondDispatch.Input.Should().Be("continue");
        deltaEvents[0].direction.Should().Be(EventDirection.Self);

        var thirdDispatch = deltaEvents[1].evt.Should().BeOfType<StepRequestEvent>().Subject;
        thirdDispatch.StepId.Should().Be("while-1_iter_2");
        thirdDispatch.StepType.Should().Be("transform");
        thirdDispatch.Input.Should().Be("more");
        deltaEvents[1].direction.Should().Be(EventDirection.Self);

        var completed = deltaEvents[2].evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.StepId.Should().Be("while-1");
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("final");
    }

    [Fact]
    public async Task TransformModule_ShouldApplyOperationsAndFallbacks()
    {
        var module = new TransformModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "transform-1",
                StepType = "transform",
                Input = "a b c",
                Parameters = { ["op"] = "count_words" },
            }),
            ctx,
            CancellationToken.None);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "transform-2",
                StepType = "transform",
                Input = "first\nsecond\nthird",
                Parameters =
                {
                    ["op"] = "take",
                    ["n"] = "2",
                },
            }),
            ctx,
            CancellationToken.None);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "transform-3",
                StepType = "transform",
                Input = "x,y,z",
                Parameters =
                {
                    ["op"] = "split",
                    ["separator"] = ",",
                },
            }),
            ctx,
            CancellationToken.None);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "transform-4",
                StepType = "transform",
                Input = "raw",
                Parameters = { ["op"] = "unknown_op" },
            }),
            ctx,
            CancellationToken.None);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "transform-5",
                StepType = "transform",
                Input = "hello",
                Parameters =
                {
                    ["op"] = "uppercase",
                },
            }),
            ctx,
            CancellationToken.None);

        var completions = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().ToDictionary(x => x.StepId, x => x);
        completions["transform-1"].Output.Should().Be("3");
        completions["transform-2"].Output.Should().Be("first\nsecond");
        completions["transform-3"].Output.Should().Be("x\n---\ny\n---\nz");
        completions["transform-4"].Output.Should().Be("raw");
        completions["transform-5"].Output.Should().Be("HELLO");
    }

    [Fact]
    public async Task RetrieveFactsModule_ShouldRankAndTakeTopK()
    {
        var module = new RetrieveFactsModule();
        var ctx = CreateContext();
        var request = new StepRequestEvent
        {
            StepId = "facts-1",
            StepType = "retrieve_facts",
            Input = "alpha beta\nalpha\ngamma beta\nother",
            Parameters =
            {
                ["query"] = "alpha beta",
                ["top_k"] = "2",
            },
        };

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        var completion = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        completion.Success.Should().BeTrue();
        completion.Output.Should().Contain("alpha beta");
        completion.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(2);
    }

    [Fact]
    public async Task VoteConsensusModule_ShouldHandleEmptyAndPickLongestCandidate()
    {
        var module = new VoteConsensusModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "vote-empty",
                StepType = "vote",
                Input = "",
            }),
            ctx,
            CancellationToken.None);

        var emptyResult = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        emptyResult.StepId.Should().Be("vote-empty");
        emptyResult.Success.Should().BeFalse();
        emptyResult.Error.Should().Contain("没有候选结果");

        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "vote-1",
                StepType = "vote",
                Input = "short\n---\nvery very long candidate\n---\nmid",
            }),
            ctx,
            CancellationToken.None);

        var winner = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        winner.Success.Should().BeTrue();
        winner.Output.Should().Be("very very long candidate");
    }

    [Fact]
    public async Task WorkflowCallModule_ShouldPublishFailureWhenMissingWorkflow_AndStartWhenPresent()
    {
        var module = new WorkflowCallModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wf-1",
                StepType = "workflow_call",
                Input = "payload",
            }),
            ctx,
            CancellationToken.None);

        var failure = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        failure.Success.Should().BeFalse();
        failure.Error.Should().Contain("缺少 workflow 参数");

        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wf-2",
                StepType = "workflow_call",
                Input = "payload-2",
                Parameters = { ["workflow"] = "sub_flow" },
            }),
            ctx,
            CancellationToken.None);

        var start = ctx.Published.Select(x => x.evt).OfType<StartWorkflowEvent>().Single();
        start.WorkflowName.Should().Be("sub_flow");
        start.Input.Should().Be("payload-2");
    }

    [Fact]
    public void LLMCallModule_CanHandle_ShouldMatchSupportedPayloads()
    {
        var module = new LLMCallModule();

        module.CanHandle(Envelope(new StepRequestEvent { StepType = "llm_call", StepId = "s1" })).Should().BeTrue();
        module.CanHandle(Envelope(new TextMessageEndEvent { SessionId = "s1", Content = "done" })).Should().BeTrue();
        module.CanHandle(Envelope(new ChatResponseEvent { SessionId = "s1", Content = "done" })).Should().BeTrue();
        module.CanHandle(Envelope(new WorkflowCompletedEvent { WorkflowName = "wf", Success = true })).Should().BeFalse();
        module.CanHandle(new EventEnvelope()).Should().BeFalse();
    }

    [Fact]
    public async Task LLMCallModule_ShouldIgnoreNonLlmStep_AndPublishSelfChatRequestForLlmStep()
    {
        var module = new LLMCallModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "not-llm",
                StepType = "transform",
                Input = "x",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Should().BeEmpty();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "llm-1",
                StepType = "llm_call",
                Input = "question",
                Parameters = { ["prompt_prefix"] = "system" },
            }),
            ctx,
            CancellationToken.None);

        var chat = ctx.Published.Select(x => x.evt).OfType<ChatRequestEvent>().Single();
        chat.Prompt.Should().Be("system\n\nquestion");
        chat.SessionId.Should().Be(WorkflowSessionKeys.CreateWorkflowStepSessionId(ctx.AgentId, "default", "llm-1", 1));
        ctx.Published.Last().direction.Should().Be(EventDirection.Self);
    }

    [Fact]
    public async Task LLMCallModule_TextMessageEndAndChatResponse_ShouldCompleteMatchingPendingStep()
    {
        var module = new LLMCallModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "llm-text",
                StepType = "llm_call",
                Input = "q1",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        var textSessionId = WorkflowSessionKeys.CreateWorkflowStepSessionId(ctx.AgentId, "default", "llm-text", 1);
        await module.HandleAsync(
            Envelope(new TextMessageEndEvent
            {
                SessionId = textSessionId,
                Content = "a1",
            }, publisherId: "role-worker-1"),
            ctx,
            CancellationToken.None);

        var textCompleted = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        textCompleted.StepId.Should().Be("llm-text");
        textCompleted.Success.Should().BeTrue();
        textCompleted.Output.Should().Be("a1");
        textCompleted.WorkerId.Should().Be("role-worker-1");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "llm-chat",
                StepType = "llm_call",
                Input = "q2",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        var chatSessionId = WorkflowSessionKeys.CreateWorkflowStepSessionId(ctx.AgentId, "default", "llm-chat", 1);
        await module.HandleAsync(
            Envelope(new ChatResponseEvent
            {
                SessionId = chatSessionId,
                Content = "a2",
            }),
            ctx,
            CancellationToken.None);

        var chatCompleted = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        chatCompleted.StepId.Should().Be("llm-chat");
        chatCompleted.WorkerId.Should().Be(ctx.AgentId);
        chatCompleted.Output.Should().Be("a2");
    }

    [Fact]
    public async Task LLMCallModule_WhenSessionNotPendingOrEmpty_ShouldIgnoreCompletionEvents()
    {
        var module = new LLMCallModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new TextMessageEndEvent
            {
                SessionId = "",
                Content = "x",
            }),
            ctx,
            CancellationToken.None);

        await module.HandleAsync(
            Envelope(new ChatResponseEvent
            {
                SessionId = "missing",
                Content = "y",
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task TransformModule_ShouldCoverAdditionalOperationsAndIgnoreNonTransformStep()
    {
        var module = new TransformModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "noop",
                StepType = "llm_call",
                Input = "raw",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Should().BeEmpty();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "count-1",
                StepType = "transform",
                Input = "a\nb\nc",
                Parameters = { ["op"] = "count" },
            }),
            ctx,
            CancellationToken.None);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "last-1",
                StepType = "transform",
                Input = "1\n2\n3",
                Parameters = { ["op"] = "take_last", ["n"] = "2" },
            }),
            ctx,
            CancellationToken.None);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "join-1",
                StepType = "transform",
                Input = "a\n---\nb",
                Parameters = { ["op"] = "join", ["separator"] = "|" },
            }),
            ctx,
            CancellationToken.None);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "distinct-1",
                StepType = "transform",
                Input = "x\nx\ny",
                Parameters = { ["op"] = "distinct" },
            }),
            ctx,
            CancellationToken.None);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "lower-1",
                StepType = "transform",
                Input = "AbC",
                Parameters = { ["op"] = "lowercase" },
            }),
            ctx,
            CancellationToken.None);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "trim-1",
                StepType = "transform",
                Input = "  hi  ",
                Parameters = { ["op"] = "trim" },
            }),
            ctx,
            CancellationToken.None);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "rev-1",
                StepType = "transform",
                Input = "1\n2\n3",
                Parameters = { ["op"] = "reverse_lines" },
            }),
            ctx,
            CancellationToken.None);

        var outputs = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().ToDictionary(x => x.StepId, x => x.Output);
        outputs["count-1"].Should().Be("3");
        outputs["last-1"].Should().Be("2\n3");
        outputs["join-1"].Should().Be("a|b");
        outputs["distinct-1"].Should().Be("x\ny");
        outputs["lower-1"].Should().Be("abc");
        outputs["trim-1"].Should().Be("hi");
        outputs["rev-1"].Should().Be("3\n2\n1");
    }

    [Fact]
    public async Task AssignModule_ConditionalModule_CheckpointModule_ShouldHandleCorePaths()
    {
        var assign = new AssignModule();
        var conditional = new ConditionalModule();
        var checkpoint = new CheckpointModule();
        var ctx = CreateContext();

        await assign.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "assign-1",
                StepType = "assign",
                Input = "input-value",
                Parameters =
                {
                    ["target"] = "x",
                    ["value"] = "$prev",
                },
            }),
            ctx,
            CancellationToken.None);

        await assign.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "assign-2",
                StepType = "assign",
                Parameters =
                {
                    ["target"] = "x",
                    ["value"] = "literal",
                },
            }),
            ctx,
            CancellationToken.None);

        await conditional.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "cond-1",
                StepType = "conditional",
                Input = "contains KEY text",
                Parameters = { ["condition"] = "key" },
            }),
            ctx,
            CancellationToken.None);

        await conditional.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "cond-2",
                StepType = "conditional",
                Input = "other text",
                Parameters = { ["condition"] = "missing" },
            }),
            ctx,
            CancellationToken.None);

        await checkpoint.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "cp-1",
                StepType = "checkpoint",
                Input = "snapshot",
                Parameters = { ["name"] = "ck1" },
            }),
            ctx,
            CancellationToken.None);

        var completions = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().ToDictionary(x => x.StepId, x => x);
        completions["assign-1"].Output.Should().Be("input-value");
        completions["assign-2"].Output.Should().Be("literal");
        completions["cond-1"].Output.Should().Be("contains KEY text");
        completions["cond-2"].Output.Should().Be("other text");
        completions["cp-1"].Output.Should().Be("snapshot");
    }

    private static RecordingEventHandlerContext CreateContext(IServiceProvider? services = null)
    {
        return new RecordingEventHandlerContext(
            services ?? new ServiceCollection().BuildServiceProvider(),
            new StubAgent("module-test-agent"),
            NullLogger.Instance);
    }

    private static EventEnvelope Envelope(IMessage evt, string? publisherId = null)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            PublisherId = publisherId ?? "test-publisher",
            Direction = EventDirection.Self,
        };
    }

    private sealed class RecordingEventHandlerContext : IEventHandlerContext
    {
        public RecordingEventHandlerContext(IServiceProvider services, IAgent agent, ILogger logger)
        {
            Services = services;
            Agent = agent;
            Logger = logger;
            InboundEnvelope = new EventEnvelope();
        }

        public List<(IMessage evt, EventDirection direction)> Published { get; } = [];
        public EventEnvelope InboundEnvelope { get; }
        public string AgentId => Agent.Id;
        public IAgent Agent { get; }
        public IServiceProvider Services { get; }
        public ILogger Logger { get; }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            EventDirection direction = EventDirection.Down,
            CancellationToken ct = default)
            where TEvent : IMessage
        {
            Published.Add((evt, direction));
            return Task.CompletedTask;
        }
    }

    private sealed class StubAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeAgentTool(string name, Func<string, string> execute) : IAgentTool
    {
        public string Name { get; } = name;
        public string Description => "fake tool";
        public string ParametersSchema => "{}";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            return Task.FromResult(execute(argumentsJson));
        }
    }

    private sealed class CountingToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public int DiscoverCalls { get; private set; }

        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            DiscoverCalls++;
            return Task.FromResult(tools);
        }
    }

    private sealed class ThrowingToolSource : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            throw new InvalidOperationException("discovery failed");
        }
    }
}
