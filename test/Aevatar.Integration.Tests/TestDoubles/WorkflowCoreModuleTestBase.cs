using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Core;
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

public abstract class WorkflowCoreModuleTestBase
{


        internal static TestEventHandlerContext CreateContext(IServiceProvider? services = null)
        {
            return new TestEventHandlerContext(
                services ?? new ServiceCollection().BuildServiceProvider(),
                new TestAgent("module-test-agent"),
                NullLogger.Instance);
        }

        internal static EventEnvelope Envelope(IMessage evt, string? publisherId = null)
        {
            return new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Payload = Any.Pack(evt),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication(publisherId ?? "test-publisher", TopologyAudience.Self),
            };
        }

        internal static ToolCallModule CreateToolCallModule(IReadOnlyList<IWorkflowToolSource> toolSources) =>
            new(toolSources, NullLogger<ToolCallModule>.Instance);

        internal static async Task ExecuteToolCallToCompletionAsync(
            ToolCallModule module,
            StepRequestEvent request,
            TestEventHandlerContext ctx,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(request.RunId))
                request.RunId = ctx.RunId;
            if (string.IsNullOrWhiteSpace(request.ExecutionId))
                request.ExecutionId = $"exec-{Guid.NewGuid():N}";

            await module.HandleAsync(Envelope(request), ctx, ct);
            await DrainToolCallContinuationsAsync(module, request, ctx, ct);
        }

        internal static async Task DrainToolCallContinuationsAsync(
            ToolCallModule module,
            StepRequestEvent request,
            TestEventHandlerContext ctx,
            CancellationToken ct = default)
        {
            while (true)
            {
                var matchingPending = ctx.LoadState<ToolCallModuleState>("tool_call")
                    .PendingExecutions.Values.Where(candidate =>
                        string.Equals(candidate.RunId, request.RunId, StringComparison.Ordinal) &&
                        string.Equals(candidate.StepId, request.StepId, StringComparison.Ordinal) &&
                        string.Equals(candidate.ExecutionId, request.ExecutionId, StringComparison.Ordinal))
                    .ToList();
                if (matchingPending.Count == 0)
                    break;
                if (matchingPending.Count != 1)
                    throw new InvalidOperationException("Expected exactly one pending tool-call execution.");

                var pending = matchingPending[0];
                if (pending.ExecutionPhase != WorkflowToolCallExecutionPhase.ExecutionPending)
                    break;

                var continuation = await ctx.WaitForPublishedAsync<WorkflowToolCallAttemptCompletedEvent>(candidate =>
                        string.Equals(candidate.CallId, pending.CallId, StringComparison.Ordinal) &&
                        string.Equals(candidate.ExecutionId, pending.ExecutionId, StringComparison.Ordinal) &&
                        candidate.Attempt == pending.Attempt &&
                        string.Equals(candidate.ContinuationId, pending.ContinuationId, StringComparison.Ordinal),
                    ct);
                var envelope = ctx.CreatePublishedEnvelope(continuation);
                ctx.RemovePublished(continuation);
                await module.HandleAsync(envelope, ctx, ct);
            }

            var completed = ctx.GetPublishedSnapshot()
                .Select(static item => item.evt)
                .OfType<StepCompletedEvent>()
                .Count(candidate =>
                    string.Equals(candidate.RunId, request.RunId, StringComparison.Ordinal) &&
                    string.Equals(candidate.StepId, request.StepId, StringComparison.Ordinal) &&
                    string.Equals(candidate.ExecutionId, request.ExecutionId, StringComparison.Ordinal));
            if (completed != 1)
                throw new InvalidOperationException("Expected exactly one terminal step completion for the tool call.");
        }

        internal sealed class FakeAgentTool(string name, Func<string, string> execute) : IWorkflowTool
        {
            public string Name { get; } = name;

            public Task<WorkflowToolExecutionResult> ExecuteAsync(WorkflowToolExecutionRequest request, CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(WorkflowToolExecutionResult.Success(execute(request.ArgumentsJson)));
            }
        }

        internal sealed class CountingFakeAgentTool(string name, Func<string, string> execute) : IWorkflowTool
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

        internal sealed class CountingToolSource(IReadOnlyList<IWorkflowTool> tools) : IWorkflowToolSource
        {
            public int DiscoverCalls { get; private set; }

            public Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default)
            {
                DiscoverCalls++;
                return Task.FromResult(tools);
            }
        }

        internal sealed class ThrowingToolSource : IWorkflowToolSource
        {
            public Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default)
            {
                throw new InvalidOperationException("discovery failed");
            }
        }

        internal sealed class CancellableToolSource(IReadOnlyList<IWorkflowTool> tools) : IWorkflowToolSource
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

        internal sealed class BlockingCountingToolSource(IReadOnlyList<IWorkflowTool> tools) : IWorkflowToolSource, IDisposable
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
