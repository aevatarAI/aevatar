using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
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
            Envelope(new StepCompletedEvent { StepId = "while-1_iter_1", Success = true, Output = "DONE" }),
            ctx,
            CancellationToken.None);
        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "not_a_loop", Success = true, Output = "ignored" }),
            ctx,
            CancellationToken.None);

        var deltaEvents = ctx.Published.Skip(countAfterStart).ToList();
        deltaEvents.Should().HaveCount(2);

        var secondDispatch = deltaEvents[0].evt.Should().BeOfType<StepRequestEvent>().Subject;
        secondDispatch.StepId.Should().Be("while-1_iter_1");
        secondDispatch.StepType.Should().Be("llm_call");
        secondDispatch.Input.Should().Be("continue");
        deltaEvents[0].direction.Should().Be(EventDirection.Down);

        var completed = deltaEvents[1].evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.StepId.Should().Be("while-1");
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("DONE");
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

    private static RecordingEventHandlerContext CreateContext(IServiceProvider? services = null)
    {
        return new RecordingEventHandlerContext(
            services ?? new ServiceCollection().BuildServiceProvider(),
            new StubAgent("module-test-agent"),
            NullLogger.Instance);
    }

    private static EventEnvelope Envelope(IMessage evt)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
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
