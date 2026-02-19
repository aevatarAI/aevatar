using System.Collections.Concurrent;
using Aevatar.CQRS.Runtime.Abstractions.Commands;
using Aevatar.CQRS.Runtime.Abstractions.Dispatch;
using Aevatar.CQRS.Runtime.Hosting.DependencyInjection;
using Aevatar.CQRS.Runtime.Hosting.Hosting;
using Aevatar.CQRS.Sagas.Abstractions.Runtime;
using Aevatar.CQRS.Sagas.Abstractions.State;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.DependencyInjection;
using Aevatar.Workflow.Core;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Integration.Tests;

public class CqrsRuntimeSagaConsistencyTests
{
    [Theory]
    [InlineData("Wolverine")]
    [InlineData("MassTransit")]
    public async Task Runtime_ShouldProcessSagaCommandAndTimeout_Consistently(string runtimeName)
    {
        var workingDirectory = Path.Combine(
            Path.GetTempPath(),
            "aevatar-cqrs-consistency",
            runtimeName.ToLowerInvariant(),
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(workingDirectory);
        var configuration = BuildConfiguration(runtimeName, workingDirectory);

        var hostBuilder = Host.CreateDefaultBuilder()
            .UseAevatarCqrsRuntime(configuration)
            .ConfigureServices((_, services) =>
            {
                services.AddAevatarRuntime();
                services.AddAevatarCqrsRuntime(configuration);
                services.AddSingleton<ISaga, RuntimeConsistencySaga>();
                services.AddSingleton<RuntimeCommandTracker>();
                services.AddSingleton<ICommandHandler<RuntimeProbeCommand>, RuntimeProbeCommandHandler>();
            });

        using var host = hostBuilder.Build();
        await host.StartAsync();
        try
        {
            var runtime = host.Services.GetRequiredService<ISagaRuntime>();
            var repository = host.Services.GetRequiredService<ISagaRepository>();
            var tracker = host.Services.GetRequiredService<RuntimeCommandTracker>();

            var actorId = $"actor-{Guid.NewGuid():N}";
            var correlationId = $"corr-{Guid.NewGuid():N}";

            await runtime.ObserveAsync(actorId, CreateStartEnvelope(correlationId));

            await tracker.WaitForCountAsync(expectedCount: 2, timeout: TimeSpan.FromSeconds(8));
            await WaitUntilAsync(async () =>
            {
                var state = await repository.LoadAsync<RuntimeConsistencySagaState>(
                    RuntimeConsistencySaga.NameValue,
                    correlationId);
                return state is { TimeoutObserved: true, IsCompleted: true };
            }, TimeSpan.FromSeconds(8));
        }
        finally
        {
            await host.StopAsync();
            TryDeleteDirectory(workingDirectory);
        }
    }

    private static IConfiguration BuildConfiguration(string runtimeName, string workingDirectory)
    {
        var values = new Dictionary<string, string?>
        {
            ["Cqrs:Runtime"] = runtimeName,
            ["Cqrs:WorkingDirectory"] = workingDirectory,
            ["Cqrs:OutboxDispatchIntervalMs"] = "100",
            ["Cqrs:OutboxDispatchBatchSize"] = "64",
            ["Cqrs:Sagas:WorkingDirectory"] = Path.Combine(workingDirectory, "sagas"),
            ["Cqrs:Sagas:TimeoutDispatchIntervalMs"] = "100",
            ["Cqrs:Sagas:TimeoutDispatchBatchSize"] = "64",
            ["Cqrs:Sagas:ConcurrencyRetryAttempts"] = "5",
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static EventEnvelope CreateStartEnvelope(string correlationId) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new StartWorkflowEvent { WorkflowName = "runtime-consistency" }),
            PublisherId = "test.runtime",
            Direction = EventDirection.Self,
            CorrelationId = correlationId,
        };

    private static async Task WaitUntilAsync(
        Func<Task<bool>> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await predicate())
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException("Condition was not satisfied within the expected timeout.");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private sealed class RuntimeConsistencySagaState : SagaStateBase
    {
        public bool Started { get; set; }
        public bool TimeoutObserved { get; set; }
    }

    private sealed class RuntimeConsistencySaga : SagaBase<RuntimeConsistencySagaState>
    {
        private static readonly string StartTypeUrl = Any.Pack(new StartWorkflowEvent()).TypeUrl;
        public const string NameValue = "runtime_consistency_saga";

        public override string Name => NameValue;

        public override ValueTask<bool> CanHandleAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = ct;
            var isStart = string.Equals(envelope.Payload?.TypeUrl, StartTypeUrl, StringComparison.Ordinal);
            var isTimeout = SagaTimeoutEnvelope.IsTimeout(envelope);
            return ValueTask.FromResult(isStart || isTimeout);
        }

        public override ValueTask<bool> CanStartAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = ct;
            return ValueTask.FromResult(
                string.Equals(envelope.Payload?.TypeUrl, StartTypeUrl, StringComparison.Ordinal));
        }

        protected override ValueTask HandleAsync(
            RuntimeConsistencySagaState state,
            EventEnvelope envelope,
            ISagaActionSink actions,
            CancellationToken ct = default)
        {
            _ = ct;
            if (!state.Started &&
                string.Equals(envelope.Payload?.TypeUrl, StartTypeUrl, StringComparison.Ordinal))
            {
                state.Started = true;
                actions.EnqueueCommand("runtime-consistency", new RuntimeProbeCommand("enqueue"));
                actions.ScheduleCommand(
                    "runtime-consistency",
                    new RuntimeProbeCommand("schedule"),
                    TimeSpan.FromMilliseconds(20));
                actions.ScheduleTimeout("runtime-timeout", TimeSpan.FromMilliseconds(60));
                return ValueTask.CompletedTask;
            }

            if (SagaTimeoutEnvelope.IsTimeout(envelope))
            {
                state.TimeoutObserved = true;
                actions.MarkCompleted();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed record RuntimeProbeCommand(string Name);

    private sealed class RuntimeCommandTracker
    {
        private readonly ConcurrentQueue<string> _commands = new();

        public int Count => _commands.Count;

        public void Add(string name)
        {
            _commands.Enqueue(name);
        }

        public async Task WaitForCountAsync(int expectedCount, TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (Count >= expectedCount)
                    return;
                await Task.Delay(50);
            }

            throw new TimeoutException(
                $"Expected at least {expectedCount} commands, but only observed {Count}.");
        }
    }

    private sealed class RuntimeProbeCommandHandler : ICommandHandler<RuntimeProbeCommand>
    {
        private readonly RuntimeCommandTracker _tracker;

        public RuntimeProbeCommandHandler(RuntimeCommandTracker tracker)
        {
            _tracker = tracker;
        }

        public Task HandleAsync(
            CommandEnvelope envelope,
            RuntimeProbeCommand command,
            CancellationToken ct = default)
        {
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            _tracker.Add(command.Name);
            return Task.CompletedTask;
        }
    }
}
