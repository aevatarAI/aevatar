using System.Net;
using System.Net.Sockets;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Deduplication;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

[Collection(nameof(EnvironmentVariableDependentCollection))]
public sealed class OrleansDirectDispatchFailurePropagationTests
{
    [Fact]
    public async Task DispatchAsync_ShouldThrow_WhenRuntimeRetryIsDisabled()
    {
        RetryAwareDirectDispatchAgent.Reset();
        var actorId = $"actor-{Guid.NewGuid():N}";
        var siloPort = ReserveTcpPort();
        var gatewayPort = ReserveTcpPort();

        using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["AEVATAR_RUNTIME_AUTO_RETRY_MAX_ATTEMPTS"] = "0",
            ["AEVATAR_RUNTIME_AUTO_RETRY_DELAY_MS"] = "50",
            ["AEVATAR_TEST_NODE_VERSION_TAG"] = "new",
            ["AEVATAR_TEST_FAIL_EVENT_TYPE_URLS"] = string.Empty,
        });

        var host = await StartSiloHostAsync(siloPort, gatewayPort);

        try
        {
            await InitializeAgentAsync(host, actorId);

            var dispatchPort = host.Services.GetRequiredService<IActorDispatchPort>();
            var envelope = CreateEnvelope("always-fail-no-retry");

            Func<Task> act = () => dispatchPort.DispatchAsync(actorId, envelope, CancellationToken.None);

            var failure = await act.Should().ThrowAsync<Exception>();
            failure.Which.ToString().Should().Contain("always-fail-no-retry");
            RetryAwareDirectDispatchAgent.GetAttemptCount(envelope.Id).Should().Be(1);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task DispatchAsync_ShouldThrow_WhenRuntimeRetryIsAlreadyExhausted()
    {
        RetryAwareDirectDispatchAgent.Reset();
        var actorId = $"actor-{Guid.NewGuid():N}";
        var siloPort = ReserveTcpPort();
        var gatewayPort = ReserveTcpPort();

        using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["AEVATAR_RUNTIME_AUTO_RETRY_MAX_ATTEMPTS"] = "1",
            ["AEVATAR_RUNTIME_AUTO_RETRY_DELAY_MS"] = "50",
            ["AEVATAR_TEST_NODE_VERSION_TAG"] = "new",
            ["AEVATAR_TEST_FAIL_EVENT_TYPE_URLS"] = string.Empty,
        });

        var host = await StartSiloHostAsync(siloPort, gatewayPort);

        try
        {
            await InitializeAgentAsync(host, actorId);

            var dispatchPort = host.Services.GetRequiredService<IActorDispatchPort>();
            var envelope = CreateEnvelope("always-fail-retry-exhausted");
            envelope.Metadata[RuntimeEnvelopeDeduplication.RetryAttemptMetadataKey] = "1";

            Func<Task> act = () => dispatchPort.DispatchAsync(actorId, envelope, CancellationToken.None);

            var failure = await act.Should().ThrowAsync<Exception>();
            failure.Which.ToString().Should().Contain("always-fail-retry-exhausted");
            RetryAwareDirectDispatchAgent.GetAttemptCount(envelope.Id).Should().Be(1);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task DispatchAsync_ShouldReturn_WhenRuntimeRetryIsScheduled()
    {
        RetryAwareDirectDispatchAgent.Reset();
        var actorId = $"actor-{Guid.NewGuid():N}";
        var siloPort = ReserveTcpPort();
        var gatewayPort = ReserveTcpPort();
        var logProbe = new RuntimeRetryLogProbe();

        using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["AEVATAR_RUNTIME_AUTO_RETRY_MAX_ATTEMPTS"] = "1",
            ["AEVATAR_RUNTIME_AUTO_RETRY_DELAY_MS"] = "50",
            ["AEVATAR_TEST_NODE_VERSION_TAG"] = "new",
            ["AEVATAR_TEST_FAIL_EVENT_TYPE_URLS"] = string.Empty,
        });

        var host = await StartSiloHostAsync(siloPort, gatewayPort, logProbe);

        try
        {
            await InitializeAgentAsync(host, actorId);

            var dispatchPort = host.Services.GetRequiredService<IActorDispatchPort>();
            var envelope = CreateEnvelope("fail-once-then-succeed");

            await dispatchPort.DispatchAsync(actorId, envelope, CancellationToken.None);
            await logProbe.WaitForRuntimeRetryScheduledAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    private static async Task<IHost> StartSiloHostAsync(
        int siloPort,
        int gatewayPort,
        ILoggerProvider? loggerProvider = null)
    {
        var host = Host.CreateDefaultBuilder()
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering(
                    siloPort: siloPort,
                    gatewayPort: gatewayPort,
                    serviceId: $"aevatar-orleans-direct-dispatch-it-service-{Guid.NewGuid():N}",
                    clusterId: $"aevatar-orleans-direct-dispatch-it-cluster-{Guid.NewGuid():N}");
                siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendInMemory;
                    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendInMemory;
                });
            })
            .ConfigureLogging(logging =>
            {
                if (loggerProvider != null)
                    logging.AddProvider(loggerProvider);
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task InitializeAgentAsync(IHost host, string actorId)
    {
        var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
        var grain = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);
        var initialized = await grain.InitializeAgentAsync(typeof(RetryAwareDirectDispatchAgent).AssemblyQualifiedName!);
        initialized.Should().BeTrue();
    }

    private static EventEnvelope CreateEnvelope(string payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Payload = Any.Pack(new StringValue { Value = payload }),
            Direction = EventDirection.Down,
        };

    private static int ReserveTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> _originalValues = new(StringComparer.Ordinal);

        public EnvironmentVariableScope(IReadOnlyDictionary<string, string?> overrides)
        {
            foreach (var pair in overrides)
            {
                _originalValues[pair.Key] = Environment.GetEnvironmentVariable(pair.Key);
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }

        public void Dispose()
        {
            foreach (var pair in _originalValues)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    private sealed class RuntimeRetryLogProbe : ILoggerProvider, ILogger
    {
        private readonly TaskCompletionSource<bool> _runtimeRetryScheduledDetected =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ILogger CreateLogger(string categoryName)
        {
            _ = categoryName;
            return this;
        }

        public void Dispose()
        {
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _ = logLevel;
            _ = eventId;
            _ = state;
            _ = exception;
            var message = formatter(state, exception);
            if (message.Contains("Runtime envelope retry scheduled", StringComparison.Ordinal))
                _runtimeRetryScheduledDetected.TrySetResult(true);
        }

        public async Task WaitForRuntimeRetryScheduledAsync(TimeSpan timeout)
        {
            try
            {
                await _runtimeRetryScheduledDetected.Task.WaitAsync(timeout);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"Timed out after {timeout} waiting for runtime retry scheduling to be logged.");
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }

    public sealed class RetryAwareDirectDispatchAgent : IAgent
    {
        private static readonly Lock SyncLock = new();
        private static readonly Dictionary<string, int> AttemptsByEnvelopeId = new(StringComparer.Ordinal);
        private static TaskCompletionSource<EventEnvelope> _successfulEnvelopeSource = CreateSuccessSource();

        public static void Reset()
        {
            lock (SyncLock)
            {
                AttemptsByEnvelopeId.Clear();
                _successfulEnvelopeSource = CreateSuccessSource();
            }
        }

        public static int GetAttemptCount(string envelopeId)
        {
            lock (SyncLock)
            {
                return AttemptsByEnvelopeId.GetValueOrDefault(envelopeId, 0);
            }
        }

        public static async Task<EventEnvelope> WaitForSuccessAsync(string envelopeId, TimeSpan timeout)
        {
            try
            {
                var envelope = await _successfulEnvelopeSource.Task.WaitAsync(timeout);
                envelope.Id.Should().Be(envelopeId);
                return envelope;
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"Timed out after {timeout} waiting for successful direct-dispatch retry of '{envelopeId}'.");
            }
        }

        public string Id => "retry-aware-direct-dispatch-agent";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var payload = envelope.Payload?.Is(StringValue.Descriptor) == true
                ? envelope.Payload.Unpack<StringValue>().Value
                : string.Empty;

            RecordAttempt(envelope.Id);

            if (payload == "always-fail-no-retry")
                throw new InvalidOperationException("always-fail-no-retry");

            if (payload == "always-fail-retry-exhausted")
                throw new InvalidOperationException("always-fail-retry-exhausted");

            if (payload == "fail-once-then-succeed" &&
                RuntimeEnvelopeDeduplication.GetAttempt(envelope) == 0)
            {
                throw new InvalidOperationException("fail-once-before-retry");
            }

            lock (SyncLock)
            {
                _successfulEnvelopeSource.TrySetResult(envelope.Clone());
            }

            return Task.CompletedTask;
        }

        public Task<string> GetDescriptionAsync() =>
            Task.FromResult("retry-aware-direct-dispatch-agent");

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        private static TaskCompletionSource<EventEnvelope> CreateSuccessSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static void RecordAttempt(string envelopeId)
        {
            lock (SyncLock)
            {
                AttemptsByEnvelopeId[envelopeId] = AttemptsByEnvelopeId.GetValueOrDefault(envelopeId, 0) + 1;
            }
        }
    }
}
