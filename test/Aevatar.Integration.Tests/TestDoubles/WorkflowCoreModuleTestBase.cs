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
