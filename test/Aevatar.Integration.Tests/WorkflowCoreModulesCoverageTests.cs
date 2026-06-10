using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        var module = new ToolCallModule([], NullLogger<ToolCallModule>.Instance);
        var ctx = CreateContext();
        var request = new StepRequestEvent
        {
            StepId = "step-1",
            StepType = "tool_call",
            Input = "{}",
        };

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        ctx.Published.Should().ContainSingle();
        ctx.Published[0].direction.Should().Be(TopologyAudience.Self);
        var completed = ctx.Published[0].evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.StepId.Should().Be("step-1");
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("缺少 tool 参数");
    }

    [Fact]
    public async Task ToolCallModule_ToolNotFound_ShouldPublishToolFailureEvents()
    {
        var module = new ToolCallModule([], NullLogger<ToolCallModule>.Instance);
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
            typeof(WorkflowToolCallStartedEvent),
            typeof(WorkflowToolCallCompletedEvent),
            typeof(StepCompletedEvent));

        var toolResult = ctx.Published[1].evt.Should().BeOfType<WorkflowToolCallCompletedEvent>().Subject;
        toolResult.Success.Should().BeFalse();
        toolResult.Error.Should().Contain("tool 'missing_tool' execution failed");

        var completed = ctx.Published[2].evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("tool 'missing_tool' execution failed");
    }

    [Fact]
    public async Task ToolCallModule_WhenDiscoveryFailsThenToolFound_ShouldStillExecuteSuccessfully()
    {
        var source = new CountingToolSource(
            [
                new FakeAgentTool("echo", args => args),
            ]);
        IWorkflowToolSource[] toolSources = [new ThrowingToolSource(), source];
        var module = CreateToolCallModule(toolSources);
        var ctx = CreateContext();
        var request = new StepRequestEvent
        {
            StepId = "step-3",
            StepType = "tool_call",
            Input = """{"msg":"ok"}""",
            Parameters = { ["tool"] = "echo" },
        };

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        source.DiscoverCalls.Should().Be(1);
        var toolResult = ctx.Published.Select(x => x.evt).OfType<WorkflowToolCallCompletedEvent>().Single();
        toolResult.Success.Should().BeTrue();
        toolResult.ResultJson.Should().Be("""{"msg":"ok"}""");
    }

    [Fact]
    public async Task ToolCallModule_ShouldCacheDiscoveredToolsAcrossCalls()
    {
        var source = new CountingToolSource(
            [
                new FakeAgentTool("cached_echo", args => args),
            ]);
        var module = CreateToolCallModule([source]);
        var ctx = CreateContext();

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
        ctx.Published.Select(x => x.evt).OfType<WorkflowToolCallCompletedEvent>().Should().OnlyContain(x => x.Success);
    }

    [Fact]
    public async Task ToolCallModule_ConcurrentFirstUse_ShouldStartSourceDiscoveryOnce()
    {
        using var source = new BlockingCountingToolSource(
            [
                new FakeAgentTool("parallel_echo", args => args),
            ]);
        var module = CreateToolCallModule([source]);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;

        var tasks = Enumerable.Range(0, 32)
            .Select(i => Task.Run(async () =>
            {
                if (Interlocked.Increment(ref readyCount) == 32)
                    ready.TrySetResult(true);

                await start.Task;
                var ctx = CreateContext();
                await module.HandleAsync(
                    Envelope(new StepRequestEvent
                    {
                        StepId = $"step-parallel-{i}",
                        StepType = "tool_call",
                        Input = """{"ok":true}""",
                        Parameters = { ["tool"] = "parallel_echo" },
                    }),
                    ctx,
                    CancellationToken.None);
                return ctx;
            }))
            .ToArray();

        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        start.SetResult(true);
        await source.WaitForFirstDiscoveryAsync();
        source.Release();

        var contexts = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));

        source.DiscoverCalls.Should().Be(1);
        contexts.SelectMany(ctx => ctx.Published.Select(x => x.evt))
            .OfType<WorkflowToolCallCompletedEvent>()
            .Should()
            .HaveCount(32)
            .And.OnlyContain(x => x.Success);
    }

    [Fact]
    public async Task ToolCallModule_ShouldHonorDiscoveryCancellation_AndRetryOnNextCall()
    {
        var source = new CancellableToolSource(
            [
                new FakeAgentTool("delayed_echo", args => args),
            ]);
        var module = CreateToolCallModule([source]);
        var cancelledContext = CreateContext();
        using var cts = new CancellationTokenSource();

        var cancelledAttempt = module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "step-cancelled",
                StepType = "tool_call",
                Input = """{"msg":"cancel"}""",
                Parameters = { ["tool"] = "delayed_echo" },
            }),
            cancelledContext,
            cts.Token);

        await source.FirstDiscoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await source.FirstDiscoveryCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledAttempt);

        var retryContext = CreateContext();
        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "step-retry",
                StepType = "tool_call",
                Input = """{"msg":"retry"}""",
                Parameters = { ["tool"] = "delayed_echo" },
            }),
            retryContext,
            CancellationToken.None);

        source.DiscoverCalls.Should().Be(2);
        retryContext.Published.Select(x => x.evt).OfType<WorkflowToolCallCompletedEvent>().Should().ContainSingle()
            .Which.ResultJson.Should().Be("""{"msg":"retry"}""");
    }

    [Fact]
    public async Task ToolCallModule_WhenToolThrows_ShouldPublishFailedStepCompleted()
    {
        var source = new CountingToolSource(
            [
                new FakeAgentTool("explode", _ => throw new InvalidOperationException("boom")),
            ]);
        var module = CreateToolCallModule([source]);
        var ctx = CreateContext();

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
    public async Task ToolCallModule_ShouldExecuteWorkflowToolDirectly()
    {
        var tool = new CountingFakeAgentTool("safe_echo", args => args);
        var module = new ToolCallModule([new CountingToolSource([tool])], NullLogger<ToolCallModule>.Instance);
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "step-direct-tool",
                StepType = "tool_call",
                Input = """{"msg":"ok"}""",
                Parameters = { ["tool"] = "safe_echo" },
            }),
            ctx,
            CancellationToken.None);

        tool.ExecuteCalls.Should().Be(1);
        var toolResult = ctx.Published.Select(x => x.evt).OfType<WorkflowToolCallCompletedEvent>().Single();
        toolResult.Success.Should().BeTrue();
        toolResult.ResultJson.Should().Be("""{"msg":"ok"}""");
        var completed = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("""{"msg":"ok"}""");
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
                    ["condition"] = "not(eq(output, 'DONE'))",
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
        secondDispatch.StepType.Should().Be("transform");
        secondDispatch.Input.Should().Be("continue");
        deltaEvents[0].direction.Should().Be(TopologyAudience.Children);

        var completed = deltaEvents[1].evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.StepId.Should().Be("while-1");
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("DONE");
        completed.Annotations["while.iterations"].Should().Be("2");
    }

    [Fact]
    public async Task WhileModule_ShouldEvaluateSubParametersPerIteration()
    {
        var module = new WhileModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "while-dynamic",
                StepType = "while",
                Input = "seed",
                TargetRole = "worker",
                Parameters =
                {
                    ["step"] = "transform",
                    ["max_iterations"] = "3",
                    ["condition"] = "${lt(iteration, 3)}",
                    ["sub_param_prompt"] = "${concat('iter=', iteration, ',input=', input)}",
                },
            }),
            ctx,
            CancellationToken.None);

        var firstDispatch = ctx.Published.Select(x => x.evt).OfType<StepRequestEvent>().Single();
        firstDispatch.Parameters["prompt"].Should().Be("iter=0,input=seed");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "while-dynamic_iter_0", Success = true, Output = "out-0" }),
            ctx,
            CancellationToken.None);
        var secondDispatch = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepRequestEvent>().Subject;
        secondDispatch.Parameters["prompt"].Should().Be("iter=1,input=out-0");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "while-dynamic_iter_1", Success = true, Output = "out-1" }),
            ctx,
            CancellationToken.None);
        var thirdDispatch = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepRequestEvent>().Subject;
        thirdDispatch.Parameters["prompt"].Should().Be("iter=2,input=out-1");
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
    public async Task VoteAgreementModule_ShouldHandleEmptyAndUseStructuredAgreement()
    {
        var module = new VoteAgreementModule();
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
        emptyResult.Error.Should().Contain("at least one candidate");

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
        winner.Output.Should().Be("short");
        winner.VoteAgreementDecision.Kind.Should().Be(AgreementDecisionKind.Agreed);
        winner.BranchKey.Should().Be("agreed");
    }

    [Fact]
    public async Task WorkflowCallModule_ShouldPublishFailureWhenMissingWorkflow_AndEmitInvocationRequestWhenPresent()
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
        failure.Error.Should().Contain("missing workflow parameter");

        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wf-2",
                StepType = "workflow_call",
                Input = "payload-2",
                Parameters =
                {
                    ["workflow"] = "sub_flow",
                    ["lifecycle"] = "singleton",
                },
            }),
            ctx,
            CancellationToken.None);

        var invocation = ctx.Published.Select(x => x.evt).OfType<SubWorkflowInvokeRequestedEvent>().Single();
        invocation.WorkflowName.Should().Be("sub_flow");
        invocation.Input.Should().Be("payload-2");
        invocation.ParentStepId.Should().Be("wf-2");
        invocation.ParentRunId.Should().Be("default");
        invocation.Lifecycle.Should().Be("singleton");
        Regex.IsMatch(invocation.InvocationId, "^default:workflow_call:wf-2:[0-9a-f]{32}$")
            .Should().BeTrue("workflow_call invocation id should follow canonical format");

        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wf-invalid-lifecycle",
                StepType = "workflow_call",
                Input = "payload-invalid",
                Parameters =
                {
                    ["workflow"] = "sub_flow",
                    ["lifecycle"] = "isolate",
                },
            }),
            ctx,
            CancellationToken.None);

        var invalidLifecycleFailure = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        invalidLifecycleFailure.Success.Should().BeFalse();
        invalidLifecycleFailure.Error.Should().Contain("lifecycle must be singleton/transient/scope");
        ctx.Published.Select(x => x.evt).OfType<SubWorkflowInvokeRequestedEvent>().Should().BeEmpty();

        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "",
                StepType = "workflow_call",
                Input = "payload-3",
                Parameters =
                {
                    ["workflow"] = "sub_flow",
                },
            }),
            ctx,
            CancellationToken.None);

        var emptyStepFailure = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        emptyStepFailure.Success.Should().BeFalse();
        emptyStepFailure.Error.Should().Contain("missing step_id");
        ctx.Published.Select(x => x.evt).OfType<SubWorkflowInvokeRequestedEvent>().Should().BeEmpty();
    }

    [Fact]
    public void WorkflowCallModule_ShouldNotKeepProcessLevelInvocationDictionaries()
    {
        var forbidden = new[]
        {
            typeof(Dictionary<,>),
            typeof(ConcurrentDictionary<,>),
            typeof(HashSet<>),
            typeof(Queue<>),
        };

        var fields = typeof(WorkflowCallModule).GetFields(
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);

        var violations = fields
            .Where(field => field.FieldType.IsGenericType)
            .Select(field => (field.Name, genericType: field.FieldType.GetGenericTypeDefinition()))
            .Where(x => forbidden.Contains(x.genericType))
            .Select(x => x.Name)
            .ToList();

        violations.Should().BeEmpty("workflow_call fact state must be persisted in WorkflowGAgent state");
    }

    [Fact]
    public void CacheModule_MetadataAndCanHandle_ShouldCoverModuleSurface()
    {
        var module = new CacheModule();

        module.Name.Should().Be("cache");
        module.Priority.Should().Be(3);
        module.CanHandle(Envelope(new StepRequestEvent { StepId = "cache-1", StepType = "cache" })).Should().BeTrue();
        module.CanHandle(Envelope(new StepCompletedEvent { StepId = "cache-1", Success = true })).Should().BeTrue();
        module.CanHandle(Envelope(new WorkflowCompletedEvent { WorkflowName = "wf", Success = true })).Should().BeFalse();
        module.CanHandle(new EventEnvelope()).Should().BeFalse();
    }

    [Fact]
    public async Task CacheModule_WithLongCacheKey_ShouldShortenMetadataKey()
    {
        var module = new CacheModule();
        var ctx = CreateContext();
        var longKey = new string('k', 80);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "cache-long-1",
                StepType = "cache",
                RunId = "run-long-1",
                Input = "input",
                Parameters = { ["cache_key"] = longKey },
            }),
            ctx,
            CancellationToken.None);

        var childRequest = ctx.Published.Select(x => x.evt).OfType<StepRequestEvent>().Single();
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = childRequest.StepId,
                RunId = "run-long-1",
                Success = true,
                Output = "ok",
            }),
            ctx,
            CancellationToken.None);

        var completion = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        completion.Annotations["cache.key"].Should().EndWith("...");
        completion.Annotations["cache.key"].Length.Should().Be(63);
    }

    [Fact]
    public async Task CacheModule_MissThenHit_ShouldDispatchDefaultChildAndServeCachedValue()
    {
        var module = new CacheModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "cache-parent-1",
                StepType = "cache",
                RunId = "run-cache-1",
                Input = "input-1",
                Parameters =
                {
                    ["cache_key"] = "key-1",
                    ["ttl_seconds"] = "60",
                },
            }),
            ctx,
            CancellationToken.None);

        var childRequest = ctx.Published.Select(x => x.evt).OfType<StepRequestEvent>().Single();
        childRequest.StepId.Should().StartWith("cache-parent-1_cached_");
        childRequest.StepType.Should().Be("llm_call");
        childRequest.RunId.Should().Be("run-cache-1");
        childRequest.Input.Should().Be("input-1");

        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = childRequest.StepId,
                RunId = "run-cache-1",
                Success = true,
                Output = "cached-output",
            }),
            ctx,
            CancellationToken.None);

        var missCompletion = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        missCompletion.StepId.Should().Be("cache-parent-1");
        missCompletion.Success.Should().BeTrue();
        missCompletion.Output.Should().Be("cached-output");
        missCompletion.Annotations["cache.hit"].Should().Be("false");
        missCompletion.Annotations.Should().ContainKey("cache.key");

        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "cache-parent-2",
                StepType = "cache",
                RunId = "run-cache-2",
                Input = "input-2",
                Parameters =
                {
                    ["cache_key"] = "key-1",
                },
            }),
            ctx,
            CancellationToken.None);

        var hitCompletion = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        hitCompletion.StepId.Should().Be("cache-parent-2");
        hitCompletion.Success.Should().BeTrue();
        hitCompletion.Output.Should().Be("cached-output");
        hitCompletion.Annotations["cache.hit"].Should().Be("true");
        hitCompletion.Annotations.Should().ContainKey("cache.key");
    }

    [Fact]
    public async Task CacheModule_WhenSecondCallerJoinsPending_ShouldFanOutCompletionAndNotCacheFailures()
    {
        var module = new CacheModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "cache-parent-a",
                StepType = "cache",
                RunId = "run-a",
                Input = "input-a",
                Parameters = { ["cache_key"] = "shared-key" },
            }),
            ctx,
            CancellationToken.None);

        var childRequest = ctx.Published.Select(x => x.evt).OfType<StepRequestEvent>().Single();
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "cache-parent-b",
                StepType = "cache",
                RunId = "run-b",
                Input = "input-b",
                Parameters = { ["cache_key"] = "shared-key" },
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().BeEmpty();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = childRequest.StepId,
                RunId = "run-a",
                Success = false,
                Error = "child failed",
            }),
            ctx,
            CancellationToken.None);

        var waiterCompletions = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().ToList();
        waiterCompletions.Should().HaveCount(2);
        waiterCompletions.Should().ContainSingle(x => x.StepId == "cache-parent-a" && !x.Success && x.Error == "child failed");
        waiterCompletions.Should().ContainSingle(x => x.StepId == "cache-parent-b" && !x.Success && x.Error == "child failed");
        foreach (var completion in waiterCompletions)
        {
            completion.Annotations.TryGetValue("cache.hit", out var hit).Should().BeTrue();
            hit.Should().Be("false");
        }

        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "cache-parent-c",
                StepType = "cache",
                RunId = "run-c",
                Input = "input-c",
                Parameters = { ["cache_key"] = "shared-key" },
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Select(x => x.evt).OfType<StepRequestEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task CacheModule_WhenCompletionDoesNotMatchPendingChild_ShouldIgnore()
    {
        var module = new CacheModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "unknown-child",
                RunId = "run-unknown",
                Success = true,
                Output = "x",
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().BeEmpty();
    }

    [Fact]
    public void LLMCallModule_CanHandle_ShouldMatchSupportedPayloads()
    {
        var module = new LLMCallModule();

        module.CanHandle(Envelope(new StepRequestEvent { StepType = "llm_call", StepId = "s1" })).Should().BeTrue();
        module.CanHandle(Envelope(new WorkflowLlmInvocationCompletedEvent { SessionId = "s1", Content = "done" })).Should().BeTrue();
        module.CanHandle(Envelope(new WorkflowCompletedEvent { WorkflowName = "wf", Success = true })).Should().BeFalse();
        module.CanHandle(new EventEnvelope()).Should().BeFalse();
    }

    [Fact]
    public async Task LLMCallModule_ShouldIgnoreNonLlmStep_AndSendBareLlmCallToImplicitAssistant()
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
                RunId = "run-llm-1",
                Input = "question",
                Parameters = { ["prompt_prefix"] = "system" },
            }),
            ctx,
            CancellationToken.None);

        var intent = ctx.Published.Select(x => x.evt).OfType<WorkflowLlmExecutionIntent>().Single();
        intent.Prompt.Should().Be("system\n\nquestion");
        intent.SessionId.Should().Be($"{ctx.AgentId}:run-llm-1:llm-1:a1");
        intent.TimeoutMs.Should().Be(1800000);
        ctx.Sent.Should().ContainSingle();
        ctx.Sent[0].targetActorId.Should().Be($"{ctx.AgentId}:assistant");
        ctx.Published.Last().direction.Should().Be(TopologyAudience.Self);
    }

    [Fact]
    public async Task LLMCallModule_WhenReservedTimeoutParameterIsZero_ShouldNotCopyItToIntentAnnotations()
    {
        var module = new LLMCallModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "llm-telegram-zero",
                StepType = "llm_call",
                RunId = "run-telegram-zero",
                Input = "wait",
                Parameters =
                {
                    ["llm_timeout_ms"] = "0",
                    ["custom"] = "value",
                },
            }),
            ctx,
            CancellationToken.None);

        var zeroIntent = ctx.Published.Select(x => x.evt).OfType<WorkflowLlmExecutionIntent>().Single();
        zeroIntent.TimeoutMs.Should().Be(1800000);
        zeroIntent.Annotations.Should().NotContainKey("llm_timeout_ms");
        zeroIntent.Annotations["custom"].Should().Be("value");

        ctx = CreateContext();
        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "llm-telegram-absent",
                StepType = "llm_call",
                RunId = "run-telegram-absent",
                Input = "wait",
            }),
            ctx,
            CancellationToken.None);

        var absentIntent = ctx.Published.Select(x => x.evt).OfType<WorkflowLlmExecutionIntent>().Single();
        absentIntent.TimeoutMs.Should().Be(1800000);
        absentIntent.Annotations.Should().BeEmpty();
    }

    [Fact]
    public async Task LLMCallModule_WorkflowLlmInvocationCompleted_ShouldCompleteMatchingPendingStep()
    {
        var module = new LLMCallModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "llm-text",
                StepType = "llm_call",
                RunId = "run-text",
                Input = "q1",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        var textSessionId = $"{ctx.AgentId}:run-text:llm-text:a1";
        await module.HandleAsync(
            Envelope(new WorkflowLlmInvocationCompletedEvent
            {
                RunId = "run-text",
                StepId = "llm-text",
                SessionId = textSessionId,
                Success = true,
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
                RunId = "run-chat",
                Input = "q2",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        var chatSessionId = $"{ctx.AgentId}:run-chat:llm-chat:a1";
        await module.HandleAsync(
            Envelope(new WorkflowLlmInvocationCompletedEvent
            {
                RunId = "run-chat",
                StepId = "llm-chat",
                SessionId = chatSessionId,
                Success = true,
                Content = "a2",
            }),
            ctx,
            CancellationToken.None);

        var chatCompleted = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        chatCompleted.StepId.Should().Be("llm-chat");
        chatCompleted.WorkerId.Should().Be("test-publisher");
        chatCompleted.Output.Should().Be("a2");
    }

    [Fact]
    public async Task LLMCallModule_WhenRolePublishesFailureCompletion_ShouldPublishFailedStepCompleted()
    {
        var module = new LLMCallModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "llm-failed",
                StepType = "llm_call",
                RunId = "run-failed",
                Input = "q",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        var sessionId = $"{ctx.AgentId}:run-failed:llm-failed:a1";
        await module.HandleAsync(
            Envelope(new WorkflowLlmInvocationCompletedEvent
            {
                RunId = "run-failed",
                StepId = "llm-failed",
                SessionId = sessionId,
                Success = false,
                Error = "provider returned 429",
            }, publisherId: "role-worker-failed"),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        completed.StepId.Should().Be("llm-failed");
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("provider returned 429");
        completed.WorkerId.Should().Be("role-worker-failed");
    }

    [Fact]
    public async Task LLMCallModule_WhenNoCompletionArrivesWithinTimeout_ShouldPublishFailedStepCompleted()
    {
        var module = new LLMCallModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "llm-timeout",
                StepType = "llm_call",
                RunId = "run-timeout",
                Input = "q-timeout",
                Parameters =
                {
                    ["timeout_ms"] = "50",
                },
            }),
            ctx,
            CancellationToken.None);

        var scheduled = ctx.Scheduled.Should().ContainSingle(x => x.Event is LlmCallWatchdogTimeoutFiredEvent).Subject;
        await module.HandleAsync(ctx.CreateScheduledEnvelope(scheduled), ctx, CancellationToken.None);

        var timeoutEvent = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        timeoutEvent.StepId.Should().Be("llm-timeout");
        timeoutEvent.RunId.Should().Be("run-timeout");
        timeoutEvent.Success.Should().BeFalse();
    }

    [Fact]
    public async Task LLMCallModule_WhenSessionNotPendingOrEmpty_ShouldIgnoreCompletionEvents()
    {
        var module = new LLMCallModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = "missing",
                Success = true,
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
    public async Task TransformModule_ShouldSupportJsonExtractProjectionSortingAndTake()
    {
        var module = new TransformModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "json-extract-1",
                StepType = "transform",
                Input =
                    """
                    {
                      "nodes": [
                        {
                          "id": "node-1",
                          "createdAt": "2026-03-20T10:00:00Z",
                          "properties": {
                            "abstract": "older abstract",
                            "body": "older body"
                          }
                        },
                        {
                          "id": "node-2",
                          "createdAt": "2026-03-22T10:00:00Z",
                          "properties": {
                            "abstract": "newest abstract",
                            "body": "newest body"
                          }
                        },
                        {
                          "id": "node-3",
                          "createdAt": "2026-03-21T10:00:00Z",
                          "properties": {
                            "abstract": "middle abstract",
                            "body": "middle body"
                          }
                        }
                      ]
                    }
                    """,
                Parameters =
                {
                    ["op"] = "json_extract",
                    ["path"] = "nodes",
                    ["field"] = "id,properties.abstract",
                    ["sort_by"] = "createdAt",
                    ["order"] = "desc",
                    ["n"] = "2",
                },
            }),
            ctx,
            CancellationToken.None);

        var completion = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        completion.Success.Should().BeTrue();

        using var output = JsonDocument.Parse(completion.Output);
        output.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        output.RootElement.GetArrayLength().Should().Be(2);
        output.RootElement[0].GetProperty("id").GetString().Should().Be("node-2");
        output.RootElement[0].GetProperty("properties").GetProperty("abstract").GetString().Should().Be("newest abstract");
        output.RootElement[1].GetProperty("id").GetString().Should().Be("node-3");
        output.RootElement[1].GetProperty("properties").GetProperty("abstract").GetString().Should().Be("middle abstract");
        output.RootElement[0].TryGetProperty("createdAt", out _).Should().BeFalse();
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
        completions["assign-1"].AssignedVariable.Should().Be("x");
        completions["assign-1"].AssignedValue.Should().Be("input-value");
        completions["assign-2"].Output.Should().Be("literal");
        completions["cond-1"].Output.Should().Be("contains KEY text");
        completions["cond-1"].BranchKey.Should().Be("true");
        completions["cond-2"].Output.Should().Be("other text");
        completions["cond-2"].BranchKey.Should().Be("false");
        completions["cp-1"].Output.Should().Be("snapshot");
    }

    private static TestEventHandlerContext CreateContext(IServiceProvider? services = null)
    {
        return new TestEventHandlerContext(
            services ?? new ServiceCollection().BuildServiceProvider(),
            new TestAgent("module-test-agent"),
            NullLogger.Instance);
    }

    private static EventEnvelope Envelope(IMessage evt, string? publisherId = null)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(publisherId ?? "test-publisher", TopologyAudience.Self),
        };
    }

    private static ToolCallModule CreateToolCallModule(IReadOnlyList<IWorkflowToolSource> toolSources) =>
        new(toolSources, NullLogger<ToolCallModule>.Instance);

    private sealed class FakeAgentTool(string name, Func<string, string> execute) : IWorkflowTool
    {
        public string Name { get; } = name;

        public Task<WorkflowToolExecutionResult> ExecuteAsync(WorkflowToolExecutionRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(WorkflowToolExecutionResult.Success(execute(request.ArgumentsJson)));
        }
    }

    private sealed class CountingFakeAgentTool(string name, Func<string, string> execute) : IWorkflowTool
    {
        public string Name { get; } = name;
        public int ExecuteCalls { get; private set; }

        public Task<WorkflowToolExecutionResult> ExecuteAsync(WorkflowToolExecutionRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ExecuteCalls++;
            return Task.FromResult(WorkflowToolExecutionResult.Success(execute(request.ArgumentsJson)));
        }
    }

    private sealed class CountingToolSource(IReadOnlyList<IWorkflowTool> tools) : IWorkflowToolSource
    {
        public int DiscoverCalls { get; private set; }

        public Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default)
        {
            DiscoverCalls++;
            return Task.FromResult(tools);
        }
    }

    private sealed class ThrowingToolSource : IWorkflowToolSource
    {
        public Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default)
        {
            throw new InvalidOperationException("discovery failed");
        }
    }

    private sealed class CancellableToolSource(IReadOnlyList<IWorkflowTool> tools) : IWorkflowToolSource
    {
        public int DiscoverCalls { get; private set; }
        public TaskCompletionSource<bool> FirstDiscoveryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> FirstDiscoveryCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default)
        {
            DiscoverCalls++;
            if (DiscoverCalls > 1)
                return tools;

            FirstDiscoveryStarted.TrySetResult(true);
            var pending = new TaskCompletionSource<IReadOnlyList<IWorkflowTool>>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(() =>
            {
                FirstDiscoveryCancelled.TrySetResult(true);
                pending.TrySetCanceled(ct);
            });

            return await pending.Task;
        }
    }

    private sealed class BlockingCountingToolSource(IReadOnlyList<IWorkflowTool> tools) : IWorkflowToolSource, IDisposable
    {
        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _discoverCalls;

        public int DiscoverCalls => Volatile.Read(ref _discoverCalls);

        public async Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _discoverCalls);
            _entered.TrySetResult(true);
            await _release.Task.WaitAsync(ct);
            return tools;
        }

        public Task WaitForFirstDiscoveryAsync() =>
            _entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Release() => _release.SetResult(true);

        public void Dispose()
        {
        }
    }
}
