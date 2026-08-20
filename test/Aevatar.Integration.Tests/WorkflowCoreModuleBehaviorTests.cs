using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Integration.Tests;

public sealed class WorkflowCoreModuleBehaviorTests : WorkflowCoreModuleTestBase
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
                RunId = ctx.RunId,
                Input = "{}",
            };

            await ExecuteToolCallToCompletionAsync(module, request, ctx);

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
                RunId = ctx.RunId,
                Input = """{"x":1}""",
                Parameters = { ["tool"] = "missing_tool" },
            };

            await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

            ctx.Published.Should().HaveCount(2);
            ctx.Published.Select(x => x.evt.GetType()).Should().ContainInOrder(
                typeof(WorkflowToolCallCompletedEvent),
                typeof(StepCompletedEvent));

            var toolResult = ctx.Published[0].evt.Should().BeOfType<WorkflowToolCallCompletedEvent>().Subject;
            toolResult.Success.Should().BeFalse();
            toolResult.Error.Should().Contain("tool 'missing_tool' execution failed");

            var completed = ctx.Published[1].evt.Should().BeOfType<StepCompletedEvent>().Subject;
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

            await ExecuteToolCallToCompletionAsync(module, request, ctx);

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

            await ExecuteToolCallToCompletionAsync(
                module,
                new StepRequestEvent
                {
                    StepId = "step-4",
                    StepType = "tool_call",
                    Input = """{"n":1}""",
                    Parameters = { ["tool"] = "cached_echo" },
                },
                ctx);

            await ExecuteToolCallToCompletionAsync(
                module,
                new StepRequestEvent
                {
                    StepId = "step-5",
                    StepType = "tool_call",
                    Input = """{"n":2}""",
                    Parameters = { ["tool"] = "cached_echo" },
                },
                ctx);

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
                    await ExecuteToolCallToCompletionAsync(
                        module,
                        new StepRequestEvent
                        {
                            StepId = $"step-parallel-{i}",
                            StepType = "tool_call",
                            Input = """{"ok":true}""",
                            Parameters = { ["tool"] = "parallel_echo" },
                        },
                        ctx);
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
                    RunId = cancelledContext.RunId,
                    ExecutionId = "exec-step-cancelled",
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
            await ExecuteToolCallToCompletionAsync(
                module,
                new StepRequestEvent
                {
                    StepId = "step-retry",
                    StepType = "tool_call",
                    Input = """{"msg":"retry"}""",
                    Parameters = { ["tool"] = "delayed_echo" },
                },
                retryContext);

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

            await ExecuteToolCallToCompletionAsync(
                module,
                new StepRequestEvent
                {
                    StepId = "step-6",
                    StepType = "tool_call",
                    Parameters = { ["tool"] = "explode" },
                },
                ctx);

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

            await ExecuteToolCallToCompletionAsync(
                module,
                new StepRequestEvent
                {
                    StepId = "step-direct-tool",
                    StepType = "tool_call",
                    Input = """{"msg":"ok"}""",
                    Parameters = { ["tool"] = "safe_echo" },
                },
                ctx);

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
                RunId = ctx.RunId,
                Input = "",
            };

            await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

            ctx.Published.Should().ContainSingle();
            var completed = ctx.Published[0].evt.Should().BeOfType<StepCompletedEvent>().Subject;
            completed.StepId.Should().Be("foreach-1");
            completed.RunId.Should().Be(ctx.RunId);
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
                RunId = ctx.RunId,
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
            subRequests.Should().OnlyContain(child => child.RunId == ctx.RunId);
            subRequests.Should().OnlyContain(child => !string.IsNullOrWhiteSpace(child.ExecutionId));
            subRequests.Select(child => child.ExecutionId).Should().OnlyHaveUniqueItems();

            var countBeforeCompletions = ctx.Published.Count;
            await module.HandleAsync(Envelope(new StepCompletedEvent
            {
                StepId = subRequests[0].StepId,
                RunId = subRequests[0].RunId,
                ExecutionId = subRequests[0].ExecutionId,
                Success = true,
                Output = "A",
            }), ctx, CancellationToken.None);
            await module.HandleAsync(Envelope(new StepCompletedEvent
            {
                StepId = $"{subRequests[0].StepId}_sub_1",
                RunId = subRequests[0].RunId,
                ExecutionId = subRequests[0].ExecutionId,
                Success = true,
                Output = "IGNORED",
            }), ctx, CancellationToken.None);
            await module.HandleAsync(Envelope(new StepCompletedEvent
            {
                StepId = subRequests[1].StepId,
                RunId = subRequests[1].RunId,
                ExecutionId = subRequests[1].ExecutionId,
                Success = false,
                Output = "B",
            }), ctx, CancellationToken.None);

            var delta = ctx.Published.Skip(countBeforeCompletions).Select(x => x.evt).OfType<StepCompletedEvent>().ToList();
            delta.Should().ContainSingle();
            delta[0].StepId.Should().Be("foreach-2");
            delta[0].RunId.Should().Be(ctx.RunId);
            delta[0].Success.Should().BeFalse();
            delta[0].Output.Should().Be("A\n---\nB");
        }

        [Fact]
        public async Task ForEachModule_ShouldEvaluateSubParameterExpressionsForEachItem()
        {
            var module = new ForEachModule();
            var ctx = CreateContext();
            var request = new StepRequestEvent
            {
                StepId = "foreach-arguments",
                StepType = "foreach",
                RunId = ctx.RunId,
                Input = "[\"instance-alpha\",\"instance-beta\"]",
                Parameters =
                {
                    ["sub_step_type"] = "tool_call",
                    ["sub_param_tool"] = "nyxid_proxy",
                    ["sub_param_arguments"] = "{\"path_params\":{\"instance_id\":\"${input}\"}}",
                },
            };

            await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

            var subRequests = ctx.Published.Select(x => x.evt).OfType<StepRequestEvent>().ToList();
            subRequests.Should().HaveCount(2);
            using var firstArguments = JsonDocument.Parse(subRequests[0].Parameters["arguments"]);
            using var secondArguments = JsonDocument.Parse(subRequests[1].Parameters["arguments"]);
            firstArguments.RootElement.GetProperty("path_params").GetProperty("instance_id").GetString()
                .Should().Be("instance-alpha");
            secondArguments.RootElement.GetProperty("path_params").GetProperty("instance_id").GetString()
                .Should().Be("instance-beta");
            subRequests.Should().OnlyContain(child => child.RunId == ctx.RunId);
            subRequests.Should().OnlyContain(child => !string.IsNullOrWhiteSpace(child.ExecutionId));
            subRequests.Select(child => child.ExecutionId).Should().OnlyHaveUniqueItems();
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

            var firstDispatchEntry = ctx.Published.Single(x => x.evt is StepRequestEvent);
            var firstDispatch = firstDispatchEntry.evt.Should().BeOfType<StepRequestEvent>().Subject;
            firstDispatch.StepId.Should().Be("while-1_iter_0");
            firstDispatch.StepType.Should().Be("transform");
            firstDispatch.TargetRole.Should().Be("worker");
            firstDispatch.Input.Should().Be("initial");
            // 迭代子步骤必须回到本 run 的模块管线；投递到 Children 会让 while 永远挂起。
            firstDispatchEntry.direction.Should().Be(TopologyAudience.Self);

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
            deltaEvents[0].direction.Should().Be(TopologyAudience.Self);

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
        public async Task CacheModule_OnMiss_ShouldForwardSynthesizedChildContract()
        {
            var module = new CacheModule();
            var ctx = CreateContext();
            var invocation = new ExternalToolInvocationSpec
            {
                CallSiteId = "cache-workflow/cached-tool/sub-step",
                ToolName = "nyxid_proxy",
            };

            await module.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "cached-tool",
                    StepType = "cache",
                    RunId = "run-cache-contract",
                    ExecutionId = "exec-cache-parent",
                    Input = "input-value",
                    InputValueId = "value-cache-input",
                    ExternalInvocation = invocation,
                    Parameters =
                    {
                        ["cache_key"] = "cache-contract-key",
                        ["child_step_type"] = "tool_call",
                        ["child_target_role"] = "worker",
                        ["sub_param_tool"] = "nyxid_proxy",
                        ["sub_param_arguments"] = "{\"path_params\":{\"item_id\":\"input-value\"}}",
                    },
                }),
                ctx,
                CancellationToken.None);

            var child = ctx.Published.Select(x => x.evt).OfType<StepRequestEvent>().Single();
            child.StepType.Should().Be("tool_call");
            child.TargetRole.Should().Be("worker");
            child.ExecutionId.Should().NotBeNullOrWhiteSpace();
            child.InputValueId.Should().Be("value-cache-input");
            child.Parameters.Should().Contain("tool", "nyxid_proxy");
            child.Parameters.Should().Contain(
                "arguments",
                "{\"path_params\":{\"item_id\":\"input-value\"}}");
            child.Parameters.Should().NotContainKey("cache_key");
            child.ExternalInvocation.Should().NotBeSameAs(invocation);
            child.ExternalInvocation.Should().BeEquivalentTo(invocation);
        }

        [Fact]
        public async Task CacheModule_ToolChild_ShouldExecuteForwardedParametersAndCompleteParent()
        {
            var cache = new CacheModule();
            var tool = new CountingFakeAgentTool("cache_echo", static arguments => arguments);
            var toolCall = CreateToolCallModule([new CountingToolSource([tool])]);
            var ctx = CreateContext();

            await cache.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "cache-tool-parent",
                    StepType = "cache",
                    RunId = "run-cache-tool",
                    ExecutionId = "exec-cache-tool-parent",
                    Input = "ignored-input",
                    Parameters =
                    {
                        ["cache_key"] = "cache-tool-key",
                        ["child_step_type"] = "tool_call",
                        ["sub_param_tool"] = "cache_echo",
                        ["sub_param_arguments"] = "{\"value\":\"from-cache\"}",
                    },
                }),
                ctx,
                CancellationToken.None);

            var child = ctx.Published.Select(x => x.evt).OfType<StepRequestEvent>().Single();
            ctx.Published.Clear();

            await ExecuteToolCallToCompletionAsync(toolCall, child, ctx);

            tool.ExecuteCalls.Should().Be(1);
            var childCompletion = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>()
                .Single(x => x.StepId == child.StepId);
            childCompletion.Success.Should().BeTrue();
            childCompletion.Output.Should().Be("{\"value\":\"from-cache\"}");
            ctx.Published.Clear();

            await cache.HandleAsync(Envelope(childCompletion), ctx, CancellationToken.None);

            var parentCompletion = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
            parentCompletion.StepId.Should().Be("cache-tool-parent");
            parentCompletion.ExecutionId.Should().Be("exec-cache-tool-parent");
            parentCompletion.Success.Should().BeTrue();
            parentCompletion.Output.Should().Be("{\"value\":\"from-cache\"}");
            parentCompletion.Annotations["cache.hit"].Should().Be("false");
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
        public async Task TransformModule_Template_ShouldRenderBoundedJsonAggregation()
        {
            var module = new TransformModule();
            var ctx = CreateContext();

            await module.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "template-aggregate",
                    StepType = "transform",
                    Input =
                        """
                        {
                          "items": [
                            { "name": "alpha", "amount": 2 },
                            { "name": "beta", "amount": 3 }
                          ]
                        }
                        """,
                    Parameters =
                    {
                        ["op"] = "template",
                        ["template"] =
                            "{{~ total = 0 ~}}" +
                            "{{~ for item in data.items ~}}" +
                            "{{ item.name }}={{ item.amount }};" +
                            "{{~ total = total + item.amount ~}}" +
                            "{{~ end ~}}" +
                            "total={{ total }}",
                    },
                }),
                ctx,
                CancellationToken.None);

            var completion = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
            completion.Error.Should().BeEmpty();
            completion.Success.Should().BeTrue();
            completion.Output.Equals("alpha=2;beta=3;total=5", StringComparison.Ordinal).Should().BeTrue();
        }

        [Fact]
        public async Task TransformModule_Template_ShouldParseAccountingNumber()
        {
            var module = new TransformModule();
            var ctx = CreateContext();

            await module.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "template-accounting-number",
                    StepType = "transform",
                    Input = """{ "amount": "(1,234.50)" }""",
                    StepParameters = new WorkflowStepParameters
                    {
                        TransformOperation = new TransformOperationSpec
                        {
                            Kind = TransformOperationKind.Template,
                            Template = "{{ number(data.amount) }}",
                        },
                    },
                    Parameters = { ["op"] = "unknown_op" },
                }),
                ctx,
                CancellationToken.None);

            var completion = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
            completion.Success.Should().BeTrue();
            decimal.Parse(completion.Output, CultureInfo.InvariantCulture).Should().Be(-1234.50m);
        }

        [Fact]
        public async Task TransformModule_Template_ShouldAggregateDynamicJsonKeys()
        {
            var module = new TransformModule();
            var ctx = CreateContext();

            await module.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "template-dynamic-map",
                    StepType = "transform",
                    Input =
                        """
                        {
                          "items": [
                            { "category": "alpha", "amount": "2" },
                            { "category": "beta", "amount": "3" },
                            { "category": "alpha", "amount": "4" }
                          ]
                        }
                        """,
                    Parameters =
                    {
                        ["op"] = "template",
                        ["template"] =
                            "{{~ by_category = {} ~}}" +
                            "{{~ for item in data.items ~}}" +
                            "{{~ by_category[item.category] = get(by_category, item.category, 0) + number(item.amount) ~}}" +
                            "{{~ end ~}}" +
                            "{{ json(by_category) }}",
                    },
                }),
                ctx,
                CancellationToken.None);

            var completion = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
            completion.Success.Should().BeTrue();
            using var output = JsonDocument.Parse(completion.Output);
            output.RootElement.GetProperty("alpha").GetDecimal().Should().Be(6m);
            output.RootElement.GetProperty("beta").GetDecimal().Should().Be(3m);
        }

        [Fact]
        public async Task TransformModule_Template_ShouldNormalizeExplicitDateFormat()
        {
            var module = new TransformModule();
            var ctx = CreateContext();

            await module.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "template-date",
                    StepType = "transform",
                    Input = """{ "date": "3/8/2026" }""",
                    Parameters =
                    {
                        ["op"] = "template",
                        ["template"] = "{{ date(data.date) }}",
                    },
                }),
                ctx,
                CancellationToken.None);

            var completion = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
            completion.Success.Should().BeTrue();
            completion.Output.Should().Be("2026-08-03");
        }

        [Fact]
        public async Task TransformModule_Template_ShouldSortObjectKeysAndRoundDecimals()
        {
            var module = new TransformModule();
            var ctx = CreateContext();

            await module.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "template-keys-round",
                    StepType = "transform",
                    Input = """{ "values": { "zeta": 1.25, "alpha": 2.24 } }""",
                    Parameters =
                    {
                        ["op"] = "template",
                        ["template"] =
                            "{{~ for key in keys(data.values) ~}}" +
                            "{{ key }}={{ round(data.values[key], 1) }};" +
                            "{{~ end ~}}",
                    },
                }),
                ctx,
                CancellationToken.None);

            var completion = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
            completion.Success.Should().BeTrue();
            completion.Output.Should().Be("alpha=2.2;zeta=1.3;");
        }

        [Fact]
        public async Task TransformModule_TemplateJson_ShouldSerializeArraySizeAsNumber()
        {
            var module = new TransformModule();
            var ctx = CreateContext();

            await module.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "template-json-integer",
                    StepType = "transform",
                    Input = """{ "items": [1, 2, 3] }""",
                    Parameters =
                    {
                        ["op"] = "template",
                        ["template"] = "{{ json({ count: data.items.size }) }}",
                    },
                }),
                ctx,
                CancellationToken.None);

            var completion = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
            completion.Success.Should().BeTrue();
            using var output = JsonDocument.Parse(completion.Output);
            output.RootElement.GetProperty("count").GetInt32().Should().Be(3);
        }

        [Fact]
        public async Task TransformModule_Template_ShouldAggregateAndClassifyGroupedValues()
        {
            var module = new TransformModule();
            var ctx = CreateContext();
            const string template =
                "{{~ observed = {}; baseline = {}; seen = {}; rows = [] ~}}" +
                "{{~ for item in data.observed_items ~}}" +
                "{{~ observed[item.group] = get(observed, item.group, 0) + number(item.value) ~}}" +
                "{{~ end ~}}" +
                "{{~ for item in data.baseline_items ~}}" +
                "{{~ baseline[item.group] = get(baseline, item.group, 0) + number(item.value) ~}}" +
                "{{~ end ~}}" +
                "{{~ for group in keys(observed) ~}}" +
                "{{~ seen[group] = true; o = get(observed, group, 0); b = get(baseline, group, 0) ~}}" +
                "{{~ if b > 0; ratio = round(o / b * 100, 1); else if o > 0; ratio = -1; else; ratio = 0; end ~}}" +
                "{{~ if ratio == -1 || ratio >= 120; band = 'high'; else if ratio >= 100; band = 'elevated'; else if ratio >= 80; band = 'near'; else; band = 'within'; end ~}}" +
                "{{~ rows = append(rows, { group: group, baseline: round(b, 2), observed: round(o, 2), ratio: ratio, band: band }) ~}}" +
                "{{~ end ~}}" +
                "{{~ for group in keys(baseline) ~}}" +
                "{{~ if !get(seen, group, false) ~}}" +
                "{{~ b = get(baseline, group, 0) ~}}" +
                "{{~ rows = append(rows, { group: group, baseline: round(b, 2), observed: 0, ratio: 0, band: 'within' }) ~}}" +
                "{{~ end ~}}" +
                "{{~ end ~}}" +
                "{{ json({ rows: rows, truncated: data.truncated }) }}";

            await module.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "template-grouped-values",
                    StepType = "transform",
                    Input =
                        """
                        {
                          "observed_items": [
                            { "group": "alpha", "value": "80" },
                            { "group": "alpha", "value": "50" },
                            { "group": "beta", "value": "20" }
                          ],
                          "baseline_items": [
                            { "group": "alpha", "value": "100" },
                            { "group": "gamma", "value": "40" }
                          ],
                          "truncated": false
                        }
                        """,
                    Parameters =
                    {
                        ["op"] = "template",
                        ["template"] = template,
                    },
                }),
                ctx,
                CancellationToken.None);

            var completion = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
            completion.Success.Should().BeTrue();
            using var output = JsonDocument.Parse(completion.Output);
            var rows = output.RootElement.GetProperty("rows");
            rows.GetArrayLength().Should().Be(3);
            rows[0].GetProperty("group").GetString().Should().Be("alpha");
            rows[0].GetProperty("ratio").GetDecimal().Should().Be(130m);
            rows[0].GetProperty("band").GetString().Should().Be("high");
            rows[2].GetProperty("group").GetString().Should().Be("gamma");
            output.RootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();
        }

        [Fact]
        public async Task TransformModule_TemplateUnsafeOrInvalidInputs_ShouldFailClosed()
        {
            var module = new TransformModule();
            var ctx = CreateContext();
            var cases = new (string StepId, string Input, string Template)[]
            {
                ("missing-template", "{}", string.Empty),
                ("invalid-json", "not-json", "ok"),
                ("invalid-template", "{}", "{{ if }}"),
                ("unknown-variable", "{}", "{{ missing_value }}"),
                ("mutate-input", "{\"value\":1}", "{{ data.value = 2 }}"),
                ("append-to-input", "{\"items\":[]}", "{{ append(data.items, 1) }}"),
                ("hidden-builtin", "{}", "{{ [1] | array.insert_at 5 'x' }}"),
            };

            foreach (var (stepId, input, template) in cases)
            {
                await module.HandleAsync(
                    Envelope(new StepRequestEvent
                    {
                        StepId = stepId,
                        StepType = "transform",
                        Input = input,
                        Parameters =
                        {
                            ["op"] = "template",
                            ["template"] = template,
                        },
                    }),
                    ctx,
                    CancellationToken.None);
            }

            var completions = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().ToDictionary(x => x.StepId);
            completions.Should().HaveCount(cases.Length);
            completions.Values.Should().OnlyContain(x => !x.Success && x.Output.Length == 0 && x.Error.Contains("template"));
        }

        [Fact]
        public async Task TransformModule_TemplateResourceLimits_ShouldFailClosed()
        {
            var module = new TransformModule();
            var ctx = CreateContext();
            var loopInput = JsonSerializer.Serialize(new { items = Enumerable.Range(0, 10_001) });
            var outputInput = JsonSerializer.Serialize(new { items = Enumerable.Range(0, 10_000) });
            var outputChunk = new string('x', 500);
            var arrayLimitTemplate =
                "{{v=[]}}" +
                string.Concat(Enumerable.Repeat("{{v=append(v,1)}}", 10_001));

            await module.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "loop-limit",
                    StepType = "transform",
                    Input = loopInput,
                    Parameters =
                    {
                        ["op"] = "template",
                        ["template"] = "{{ for item in data.items }}x{{ end }}",
                    },
                }),
                ctx,
                CancellationToken.None);

            await module.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "output-limit",
                    StepType = "transform",
                    Input = outputInput,
                    Parameters =
                    {
                        ["op"] = "template",
                        ["template"] = $"{{{{ for item in data.items }}}}{outputChunk}{{{{ end }}}}",
                    },
                }),
                ctx,
                CancellationToken.None);

            await module.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "array-limit",
                    StepType = "transform",
                    Input = "{}",
                    Parameters =
                    {
                        ["op"] = "template",
                        ["template"] = arrayLimitTemplate,
                    },
                }),
                ctx,
                CancellationToken.None);

            var completions = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().ToDictionary(x => x.StepId);
            completions.Values.Should().OnlyContain(x => !x.Success && x.Output.Length == 0);
            completions["loop-limit"].Error.Should().Contain("evaluation");
            completions["output-limit"].Error.Should().Contain("output");
            completions["array-limit"].Error.Should().Contain("evaluation");
        }

        [Fact]
        public async Task TransformModule_JsonParseParametersPath_ShouldParseSelectedJsonStringAsRootOutput()
        {
            var module = new TransformModule();
            var ctx = CreateContext();

            await module.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "json-parse-path",
                    StepType = "transform",
                    Input = """
                            {
                              "output": {
                                "stdout": "{\"route\":\"ROUTE_REJECTED\",\"report\":\"denied\"}"
                              }
                            }
                            """,
                    Parameters =
                    {
                        ["op"] = "json_parse",
                        ["path"] = "output.stdout",
                    },
                }),
                ctx,
                CancellationToken.None);

            var completion = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
            completion.Success.Should().BeTrue();

            using var output = JsonDocument.Parse(completion.Output);
            output.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
            output.RootElement.GetProperty("route").GetString().Should().Be("ROUTE_REJECTED");
            output.RootElement.GetProperty("report").GetString().Should().Be("denied");
        }

        [Fact]
        public async Task TransformModule_JsonParseParametersJsonPathAlias_ShouldParseSelectedJsonStringAsRootOutput()
        {
            var module = new TransformModule();
            var ctx = CreateContext();

            await module.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "json-parse-json-path",
                    StepType = "transform",
                    Input = """
                            {
                              "output": {
                                "stdout": "{\"route\":\"ROUTE_APPROVED\",\"report\":\"accepted\"}"
                              }
                            }
                            """,
                    Parameters =
                    {
                        ["op"] = "json_parse",
                        ["json_path"] = "output.stdout",
                    },
                }),
                ctx,
                CancellationToken.None);

            var completion = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
            completion.Success.Should().BeTrue();

            using var output = JsonDocument.Parse(completion.Output);
            output.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
            output.RootElement.GetProperty("route").GetString().Should().Be("ROUTE_APPROVED");
            output.RootElement.GetProperty("report").GetString().Should().Be("accepted");
        }

        [Theory]
        [InlineData("missing-path", """{"output":{"stdout":"{\"route\":\"ROUTE_REJECTED\"}"}}""", null, "path")]
        [InlineData("missing-selected-node", """{"output":{}}""", "output.stdout", "output.stdout")]
        [InlineData("selected-node-not-string", """{"output":{"stdout":{"route":"ROUTE_REJECTED"}}}""", "output.stdout", "string")]
        [InlineData("invalid-json-string", """{"output":{"stdout":"not-json"}}""", "output.stdout", "JSON")]
        public async Task TransformModule_JsonParseParametersFailures_ShouldPublishFailedCompletion(
            string stepId,
            string input,
            string? path,
            string expectedError)
        {
            var module = new TransformModule();
            var ctx = CreateContext();
            var request = new StepRequestEvent
            {
                StepId = stepId,
                StepType = "transform",
                Input = input,
                Parameters = { ["op"] = "json_parse" },
            };

            if (path is not null)
            {
                request.Parameters["path"] = path;
            }

            await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

            var completion = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
            completion.StepId.Should().Be(stepId);
            completion.Success.Should().BeFalse();
            completion.Error.Should().Contain(expectedError);
        }

        [Fact]
        public async Task TransformModule_ShouldApplyTypedNumericAndGroupOperations()
        {
            var module = new TransformModule();
            var ctx = CreateContext();

            await module.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "sum-typed",
                    StepType = "transform",
                    Input = "[0.1,0.2]",
                    StepParameters = new WorkflowStepParameters
                    {
                        TransformOperation = new TransformOperationSpec
                        {
                            Kind = TransformOperationKind.Sum,
                            Precision = 2,
                        },
                    },
                    Parameters = { ["op"] = "unknown_op" },
                }),
                ctx,
                CancellationToken.None);

            await module.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "divide-map",
                    StepType = "transform",
                    Input = "10,4",
                    Parameters =
                    {
                        ["op"] = "divide",
                        ["precision"] = "1",
                    },
                }),
                ctx,
                CancellationToken.None);

            await module.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "group-map",
                    StepType = "transform",
                    Input = """
                        [
                          { "dept": "ops", "amount": "10.25" },
                          { "dept": "sales", "amount": "7" },
                          { "dept": "ops", "amount": "2.25" }
                        ]
                        """,
                    Parameters =
                    {
                        ["op"] = "group_by",
                        ["key"] = "dept",
                        ["value"] = "amount",
                        ["aggregate"] = "sum",
                        ["precision"] = "2",
                    },
                }),
                ctx,
                CancellationToken.None);

            var completions = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().ToDictionary(x => x.StepId);
            completions["sum-typed"].Success.Should().BeTrue();
            decimal.Parse(completions["sum-typed"].Output, CultureInfo.InvariantCulture).Should().Be(0.3m);
            completions["divide-map"].Output.Should().Be("2.5");

            using var groupOutput = JsonDocument.Parse(completions["group-map"].Output);
            groupOutput.RootElement.GetArrayLength().Should().Be(2);
            groupOutput.RootElement[0].GetProperty("key").GetString().Should().Be("ops");
            groupOutput.RootElement[0].GetProperty("value").GetDecimal().Should().Be(12.50m);
            groupOutput.RootElement[1].GetProperty("key").GetString().Should().Be("sales");
            groupOutput.RootElement[1].GetProperty("value").GetDecimal().Should().Be(7.00m);
        }

        [Fact]
        public async Task TransformModule_RecognizedNumericAndGroupFailures_ShouldPublishFailedCompletion()
        {
            var module = new TransformModule();
            var ctx = CreateContext();

            await module.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "divide-zero",
                    StepType = "transform",
                    Input = "1,0",
                    Parameters = { ["op"] = "divide" },
                }),
                ctx,
                CancellationToken.None);

            await module.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "group-missing",
                    StepType = "transform",
                    Input = """[{ "dept": "ops", "amount": 1 }]""",
                    Parameters =
                    {
                        ["op"] = "group_by",
                        ["aggregate"] = "sum",
                    },
                }),
                ctx,
                CancellationToken.None);

            await module.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "bad-precision",
                    StepType = "transform",
                    Input = "1,2",
                    Parameters =
                    {
                        ["op"] = "sum",
                        ["precision"] = "not-a-number",
                    },
                }),
                ctx,
                CancellationToken.None);

            var completions = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().ToDictionary(x => x.StepId);
            completions["divide-zero"].Success.Should().BeFalse();
            completions["divide-zero"].Error.Should().Contain("divide");
            completions["group-missing"].Success.Should().BeFalse();
            completions["group-missing"].Error.Should().Contain("key");
            completions["bad-precision"].Success.Should().BeFalse();
            completions["bad-precision"].Error.Should().Contain("precision");
        }

        [Fact]
        public async Task TransformModule_ShouldExtractRssAndAtomItemsDeterministically()
        {
            var module = new TransformModule();
            var ctx = CreateContext();

            await module.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "rss",
                    StepType = "transform",
                    Input = """
                        <rss version="2.0">
                          <channel>
                            <item>
                              <guid>rss-1</guid>
                              <title>RSS title</title>
                              <link>https://example.com/rss-1</link>
                              <pubDate>Wed, 10 Jun 2026 12:30:00 GMT</pubDate>
                              <description>RSS summary</description>
                            </item>
                          </channel>
                        </rss>
                        """,
                    Parameters =
                    {
                        ["op"] = "rss_extract_items",
                        ["source_id"] = "rss-feed",
                        ["source_url"] = "https://example.com/rss.xml",
                    },
                }),
                ctx,
                CancellationToken.None);

            await module.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "atom",
                    StepType = "transform",
                    Input = """
                        <feed xmlns="http://www.w3.org/2005/Atom">
                          <entry>
                            <id>atom-1</id>
                            <title>Atom title</title>
                            <link href="https://example.com/atom-1" />
                            <updated>2026-06-10T13:00:00Z</updated>
                            <summary>Atom summary</summary>
                          </entry>
                        </feed>
                        """,
                    Parameters =
                    {
                        ["op"] = "rss_extract_items",
                        ["source_id"] = "atom-feed",
                        ["source_url"] = "https://example.com/atom.xml",
                    },
                }

    ),
                ctx,
                CancellationToken.None);

            var completions = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().ToDictionary(x => x.StepId);
            completions.Values.Should().OnlyContain(x => x.Success);

            using var rss = JsonDocument.Parse(completions["rss"].Output);
            var rssItem = rss.RootElement.EnumerateArray().Should().ContainSingle().Subject;
            rssItem.EnumerateObject().Select(x => x.Name).Should().Equal(
                "source_id", "source_url", "id", "title", "link", "published_at", "summary");
            rssItem.GetProperty("source_id").GetString().Should().Be("rss-feed");
            rssItem.GetProperty("id").GetString().Should().Be("rss-1");
            rssItem.GetProperty("published_at").GetString().Should().Be("2026-06-10T12:30:00.0000000+00:00");

            using var atom = JsonDocument.Parse(completions["atom"].Output);
            var atomItem = atom.RootElement.EnumerateArray().Should().ContainSingle().Subject;
            atomItem.GetProperty("source_id").GetString().Should().Be("atom-feed");
            atomItem.GetProperty("link").GetString().Should().Be("https://example.com/atom-1");
            atomItem.GetProperty("summary").GetString().Should().Be("Atom summary");
        }

        [Fact]
        public async Task TransformModule_RssExtractItems_ShouldRejectDtd()
        {
            var module = new TransformModule();
            var ctx = CreateContext();

            await module.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "rss-dtd",
                    StepType = "transform",
                    Input = """
                        <!DOCTYPE rss [
                          <!ENTITY xxe SYSTEM "file:///etc/passwd">
                        ]>
                        <rss version="2.0">
                          <channel>
                            <item>
                              <title>&xxe;</title>
                            </item>
                          </channel>
                        </rss>
                        """,
                    Parameters = { ["op"] = "rss_extract_items" },
                }),
                ctx,
                CancellationToken.None);

            var completion = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
            completion.Success.Should().BeFalse();
            completion.Error.Should().Contain("DTD");
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

}
