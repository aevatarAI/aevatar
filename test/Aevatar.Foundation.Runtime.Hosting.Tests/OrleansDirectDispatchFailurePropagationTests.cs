using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Delivery;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Observability;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

[Collection(nameof(EnvironmentVariableDependentCollection))]
public sealed class OrleansDirectDispatchFailurePropagationTests
{
    [Fact]
    public async Task DispatchAsync_ShouldReturn_WhenRuntimeRetryIsDisabledAndHandlerFails()
    {
        RetryAwareDirectDispatchAgent.Reset();
        using var metricProbe = new RuntimeTerminalFailureMetricProbe();
        var actorId = $"actor-{Guid.NewGuid():N}";
        var siloPort = ReserveTcpPort();
        var gatewayPort = ReserveTcpPort();
        var logProbe = new RuntimeRetryLogProbe();

        using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["AEVATAR_RUNTIME_AUTO_RETRY_MAX_ATTEMPTS"] = "0",
            ["AEVATAR_RUNTIME_AUTO_RETRY_DELAY_MS"] = "50",
            ["AEVATAR_TEST_NODE_VERSION_TAG"] = "new",
            ["AEVATAR_TEST_FAIL_EVENT_TYPE_URLS"] = string.Empty,
        });

        var host = await StartSiloHostAsync(siloPort, gatewayPort, logProbe);

        try
        {
            await InitializeAgentByKindAsync(host, actorId);

            var dispatchPort = host.Services.GetRequiredService<IActorDispatchPort>();
            var envelope = CreateEnvelope("always-fail-no-retry");

            await dispatchPort.DispatchAsync(actorId, envelope, CancellationToken.None);
            await RetryAwareDirectDispatchAgent.WaitForAttemptAsync(envelope.Id, TimeSpan.FromSeconds(20));
            await logProbe.WaitForRuntimeHandlingFailureAsync(TimeSpan.FromSeconds(20));
            RetryAwareDirectDispatchAgent.GetAttemptCount(envelope.Id).Should().Be(1);
            metricProbe.Measurements.Should().Contain(measurement =>
                measurement.Reason == AgentMetrics.FailureReasonHandlerRetryExhausted &&
                measurement.Disposition == AgentMetrics.FailureDispositionReturned);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task DispatchAsync_ShouldNotRetryNonOccFailure_WhenRuntimeRetryUsesDefaultClassifier()
    {
        RetryAwareDirectDispatchAgent.Reset();
        var actorId = $"actor-{Guid.NewGuid():N}";
        var siloPort = ReserveTcpPort();
        var gatewayPort = ReserveTcpPort();
        var logProbe = new RuntimeRetryLogProbe();

        using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["AEVATAR_RUNTIME_AUTO_RETRY_MAX_ATTEMPTS"] = null,
            ["AEVATAR_RUNTIME_AUTO_RETRY_DELAY_MS"] = "50",
            ["AEVATAR_TEST_NODE_VERSION_TAG"] = "new",
            ["AEVATAR_TEST_FAIL_EVENT_TYPE_URLS"] = string.Empty,
        });

        var host = await StartSiloHostAsync(siloPort, gatewayPort, logProbe);

        try
        {
            await InitializeAgentByKindAsync(host, actorId);

            var dispatchPort = host.Services.GetRequiredService<IActorDispatchPort>();
            var envelope = CreateEnvelope("always-fail-non-occ-default");

            await dispatchPort.DispatchAsync(actorId, envelope, CancellationToken.None);
            await RetryAwareDirectDispatchAgent.WaitForAttemptAsync(envelope.Id, TimeSpan.FromSeconds(20));
            await logProbe.WaitForRuntimeHandlingFailureAsync(TimeSpan.FromSeconds(20));
            RetryAwareDirectDispatchAgent.GetAttemptCount(envelope.Id).Should().Be(1);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task DispatchAsync_ShouldReturn_WhenRuntimeRetryIsAlreadyExhaustedAndHandlerFails()
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
            await InitializeAgentByKindAsync(host, actorId);

            var dispatchPort = host.Services.GetRequiredService<IActorDispatchPort>();
            var envelope = CreateEnvelope("always-fail-retry-exhausted");
            envelope.Runtime = new EnvelopeRuntime
            {
                Retry = new EnvelopeRetryContext
                {
                    Attempt = 1,
                },
            };

            await dispatchPort.DispatchAsync(actorId, envelope, CancellationToken.None);
            await RetryAwareDirectDispatchAgent.WaitForAttemptAsync(envelope.Id, TimeSpan.FromSeconds(20));
            await logProbe.WaitForRuntimeHandlingFailureAsync(TimeSpan.FromSeconds(20));
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
            await InitializeAgentByKindAsync(host, actorId);

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

    [Fact]
    public async Task DispatchAsync_ShouldRetryOccFailure_WhenRuntimeRetryUsesDefaultClassifier()
    {
        RetryAwareDirectDispatchAgent.Reset();
        var actorId = $"actor-{Guid.NewGuid():N}";
        var siloPort = ReserveTcpPort();
        var gatewayPort = ReserveTcpPort();
        var logProbe = new RuntimeRetryLogProbe();

        using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["AEVATAR_RUNTIME_AUTO_RETRY_MAX_ATTEMPTS"] = null,
            ["AEVATAR_RUNTIME_AUTO_RETRY_DELAY_MS"] = "50",
            ["AEVATAR_TEST_NODE_VERSION_TAG"] = "new",
            ["AEVATAR_TEST_FAIL_EVENT_TYPE_URLS"] = string.Empty,
        });

        var host = await StartSiloHostAsync(siloPort, gatewayPort, logProbe);

        try
        {
            await InitializeAgentByKindAsync(host, actorId);

            var dispatchPort = host.Services.GetRequiredService<IActorDispatchPort>();
            var envelope = CreateEnvelope("occ-fail-once-then-succeed");

            await dispatchPort.DispatchAsync(actorId, envelope, CancellationToken.None);
            await logProbe.WaitForRuntimeRetryScheduledAsync(TimeSpan.FromSeconds(20));
            await RetryAwareDirectDispatchAgent.WaitForDeactivationAsync(TimeSpan.FromSeconds(20));
            var successfulEnvelope =
                await RetryAwareDirectDispatchAgent.WaitForSuccessAsync(envelope.Id, TimeSpan.FromSeconds(20));

            RuntimeEnvelopeDeliveryIdentity.GetAttempt(successfulEnvelope).Should().Be(1);
            RetryAwareDirectDispatchAgent.ActivationCount.Should().Be(2,
                "an OCC retry must rehydrate from committed state on a fresh activation");
            RetryAwareDirectDispatchAgent.DeactivationCount.Should().Be(1);
            RetryAwareDirectDispatchAgent.GetAttemptCount(envelope.Id).Should().Be(2);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldSurfaceOriginalHandlerFailure_WhenDurableRetryEnvelopeCarriesRuntimeCredential()
    {
        RetryAwareDirectDispatchAgent.Reset();
        var actorId = $"actor-{Guid.NewGuid():N}";
        var siloPort = ReserveTcpPort();
        var gatewayPort = ReserveTcpPort();
        var callbackScheduler = new CredentialGuardedRecordingCallbackScheduler();

        using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["AEVATAR_RUNTIME_AUTO_RETRY_MAX_ATTEMPTS"] = null,
            ["AEVATAR_RUNTIME_AUTO_RETRY_DELAY_MS"] = "50",
            ["AEVATAR_TEST_NODE_VERSION_TAG"] = "new",
            ["AEVATAR_TEST_FAIL_EVENT_TYPE_URLS"] = string.Empty,
        });

        var host = await StartSiloHostAsync(siloPort, gatewayPort, callbackScheduler: callbackScheduler);

        try
        {
            await InitializeAgentByKindAsync(host, actorId);

            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);
            var envelope = CreateCredentialCarryingEnvelope();

            var act = () => grain.HandleEnvelopeAsync(envelope.ToByteArray());

            // Durable retry must be skipped (the callback store rejects runtime
            // credentials), and the delivery must fail with the handler's own
            // exception so stream redelivery semantics stay intact — not with the
            // credential-guard InvalidOperationException.
            await act.Should()
                .ThrowAsync<EventStoreOptimisticConcurrencyException>()
                .WithMessage("*Optimistic concurrency conflict*");
            callbackScheduler.TimeoutRequests.Should().BeEmpty();
            RetryAwareDirectDispatchAgent.GetAttemptCount(envelope.Id).Should().Be(1);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldInvokeHandlerAgainForSameAttemptAfterPropagatedHandlerFailure()
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
            await InitializeAgentByKindAsync(host, actorId);

            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);
            var envelope = CreateEnvelope("always-fail-no-retry");
            envelope.Runtime = new EnvelopeRuntime
            {
                Dispatch = new EnvelopeDispatchControl { PropagateFailure = true },
            };

            await grain.Invoking(x => x.HandleEnvelopeAsync(envelope.ToByteArray()))
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("always-fail-no-retry");
            await grain.Invoking(x => x.HandleEnvelopeAsync(envelope.ToByteArray()))
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("always-fail-no-retry");

            RetryAwareDirectDispatchAgent.GetAttemptCount(envelope.Id).Should().Be(2,
                "provider redelivery must always reach the authoritative actor");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task HandleEnvelopeAsync_WhenRequiredRedeliveryRetryIsExhausted_ShouldPropagateAndReactivate()
    {
        RetryAwareDirectDispatchAgent.Reset();
        using var metricProbe = new RuntimeTerminalFailureMetricProbe();
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
            await InitializeAgentByKindAsync(host, actorId);
            RetryAwareDirectDispatchAgent.ActivationCount.Should().Be(1);

            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);
            var envelope = CreateEnvelope("runtime-retryable-exhausted");
            envelope.Runtime = new EnvelopeRuntime
            {
                Retry = new EnvelopeRetryContext { Attempt = 1 },
            };

            await grain.Invoking(x => x.HandleEnvelopeAsync(envelope.ToByteArray()))
                .Should().ThrowAsync<Exception>()
                .WithMessage("*runtime-retryable-exhausted*");
            await RetryAwareDirectDispatchAgent.WaitForDeactivationAsync(TimeSpan.FromSeconds(20));

            RetryAwareDirectDispatchAgent.DeactivationCount.Should().Be(1);
            metricProbe.Measurements.Should().Contain(measurement =>
                measurement.Reason == AgentMetrics.FailureReasonHandlerRetryExhausted &&
                measurement.Disposition == AgentMetrics.FailureDispositionPropagated);

            await grain.HandleEnvelopeAsync(envelope.ToByteArray());
            await RetryAwareDirectDispatchAgent.WaitForSuccessAsync(envelope.Id, TimeSpan.FromSeconds(20));

            RetryAwareDirectDispatchAgent.ActivationCount.Should().Be(2);
            RetryAwareDirectDispatchAgent.GetAttemptCount(envelope.Id).Should().Be(2);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task HandleEnvelopeAsync_WhenRetryUntilResolvedExceedsBudget_ShouldPersistStableLineageContinuation()
    {
        RetryAwareDirectDispatchAgent.Reset();
        var actorId = $"actor-{Guid.NewGuid():N}";
        var siloPort = ReserveTcpPort();
        var gatewayPort = ReserveTcpPort();
        var callbackScheduler = new CredentialGuardedRecordingCallbackScheduler();

        using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["AEVATAR_RUNTIME_AUTO_RETRY_MAX_ATTEMPTS"] = "3",
            ["AEVATAR_RUNTIME_AUTO_RETRY_DELAY_MS"] = "0",
            ["AEVATAR_TEST_NODE_VERSION_TAG"] = "new",
            ["AEVATAR_TEST_FAIL_EVENT_TYPE_URLS"] = string.Empty,
        });

        var host = await StartSiloHostAsync(
            siloPort,
            gatewayPort,
            callbackScheduler: callbackScheduler);

        try
        {
            await InitializeAgentByKindAsync(host, actorId);

            var grain = host.Services.GetRequiredService<IGrainFactory>()
                .GetGrain<IRuntimeActorGrain>(actorId);
            var envelope = CreateEnvelope("retry-until-resolved");
            envelope.Runtime = new EnvelopeRuntime
            {
                DeliveryIdentity = new DeliveryIdentity
                {
                    OperationId = "phase-b-proof-lineage",
                },
                Retry = new EnvelopeRetryContext { Attempt = 3 },
            };

            await grain.HandleEnvelopeAsync(envelope.ToByteArray());
            callbackScheduler.TimeoutRequests.Should().ContainSingle();
            var firstRequest = callbackScheduler.TimeoutRequests.Single();

            await grain.HandleEnvelopeAsync(firstRequest.TriggerEnvelope.ToByteArray());

            callbackScheduler.TimeoutRequests.Should().HaveCount(2);
            var secondRequest = callbackScheduler.TimeoutRequests[1];
            firstRequest.CallbackId.Should().Be(secondRequest.CallbackId);
            firstRequest.CallbackId.Should().StartWith("runtime-envelope-retry-until-resolved:");
            firstRequest.DeliveryMode.Should().Be(RuntimeCallbackDeliveryMode.EnvelopeRedelivery);
            secondRequest.DeliveryMode.Should().Be(RuntimeCallbackDeliveryMode.EnvelopeRedelivery);
            firstRequest.DueTime.Should().BeGreaterThan(TimeSpan.Zero,
                "retry-until-resolved always uses a durable callback even when the configured delay is zero");
            firstRequest.TriggerEnvelope.Runtime.Retry.Attempt.Should().Be(4);
            secondRequest.TriggerEnvelope.Runtime.Retry.Attempt.Should().Be(5);
            firstRequest.TriggerEnvelope.Runtime.Retry.OriginEventId.Should().Be("phase-b-proof-lineage");
            secondRequest.TriggerEnvelope.Runtime.Retry.OriginEventId.Should().Be("phase-b-proof-lineage");
            RetryAwareDirectDispatchAgent.GetAttemptCount(envelope.Id).Should().Be(2);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task HandleEnvelopeAsync_WhenRetryUntilResolvedSchedulerFailsAfterBudget_ShouldPreserveFailureAndReactivate()
    {
        RetryAwareDirectDispatchAgent.Reset();
        var actorId = $"actor-{Guid.NewGuid():N}";
        var siloPort = ReserveTcpPort();
        var gatewayPort = ReserveTcpPort();
        var callbackScheduler = new AlwaysFailingCallbackScheduler();

        using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["AEVATAR_RUNTIME_AUTO_RETRY_MAX_ATTEMPTS"] = "1",
            ["AEVATAR_RUNTIME_AUTO_RETRY_DELAY_MS"] = "0",
            ["AEVATAR_TEST_NODE_VERSION_TAG"] = "new",
            ["AEVATAR_TEST_FAIL_EVENT_TYPE_URLS"] = string.Empty,
        });

        var host = await StartSiloHostAsync(
            siloPort,
            gatewayPort,
            callbackScheduler: callbackScheduler);

        try
        {
            await InitializeAgentByKindAsync(host, actorId);

            var grain = host.Services.GetRequiredService<IGrainFactory>()
                .GetGrain<IRuntimeActorGrain>(actorId);
            var envelope = CreateEnvelope("retry-until-resolved-scheduler-failure");
            envelope.Runtime = new EnvelopeRuntime
            {
                Retry = new EnvelopeRetryContext { Attempt = 1 },
            };

            await grain.Invoking(x => x.HandleEnvelopeAsync(envelope.ToByteArray()))
                .Should().ThrowExactlyAsync<ProjectionScopeStatusPhaseBProofUnavailableException>()
                .WithMessage("*cannot prove Phase-B admission*");
            await RetryAwareDirectDispatchAgent.WaitForDeactivationAsync(TimeSpan.FromSeconds(20));

            callbackScheduler.ScheduleTimeoutCallCount.Should().Be(1);
            RetryAwareDirectDispatchAgent.DeactivationCount.Should().Be(1,
                "a failed durable handoff must preserve transport redelivery semantics");

            await grain.HandleEnvelopeAsync(envelope.ToByteArray());
            await RetryAwareDirectDispatchAgent.WaitForSuccessAsync(envelope.Id, TimeSpan.FromSeconds(20));

            RetryAwareDirectDispatchAgent.ActivationCount.Should().Be(2);
            RetryAwareDirectDispatchAgent.GetAttemptCount(envelope.Id).Should().Be(2);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task HandleEnvelopeAsync_WhenRetrySchedulerFailsForRequiredRedelivery_ShouldPreserveFailureAndReactivate()
    {
        RetryAwareDirectDispatchAgent.Reset();
        using var metricProbe = new RuntimeTerminalFailureMetricProbe();
        var actorId = $"actor-{Guid.NewGuid():N}";
        var siloPort = ReserveTcpPort();
        var gatewayPort = ReserveTcpPort();
        var callbackScheduler = new AlwaysFailingCallbackScheduler();

        using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["AEVATAR_RUNTIME_AUTO_RETRY_MAX_ATTEMPTS"] = "1",
            ["AEVATAR_RUNTIME_AUTO_RETRY_DELAY_MS"] = "50",
            ["AEVATAR_TEST_NODE_VERSION_TAG"] = "new",
            ["AEVATAR_TEST_FAIL_EVENT_TYPE_URLS"] = string.Empty,
        });

        var host = await StartSiloHostAsync(
            siloPort,
            gatewayPort,
            callbackScheduler: callbackScheduler);

        try
        {
            await InitializeAgentByKindAsync(host, actorId);
            RetryAwareDirectDispatchAgent.ActivationCount.Should().Be(1);

            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);
            var envelope = CreateEnvelope("runtime-retryable-scheduler-failure");

            await grain.Invoking(x => x.HandleEnvelopeAsync(envelope.ToByteArray()))
                .Should().ThrowAsync<Exception>()
                .WithMessage("*runtime-retryable-scheduler-failure*");
            await RetryAwareDirectDispatchAgent.WaitForDeactivationAsync(TimeSpan.FromSeconds(20));

            callbackScheduler.ScheduleTimeoutCallCount.Should().Be(1);
            RetryAwareDirectDispatchAgent.DeactivationCount.Should().Be(1);
            metricProbe.Measurements.Should().Contain(measurement =>
                measurement.Reason == AgentMetrics.FailureReasonHandlerRetryExhausted &&
                measurement.Disposition == AgentMetrics.FailureDispositionPropagated);

            await grain.HandleEnvelopeAsync(envelope.ToByteArray());
            await RetryAwareDirectDispatchAgent.WaitForSuccessAsync(envelope.Id, TimeSpan.FromSeconds(20));

            RetryAwareDirectDispatchAgent.ActivationCount.Should().Be(2);
            RetryAwareDirectDispatchAgent.GetAttemptCount(envelope.Id).Should().Be(2);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Theory]
    [InlineData(ProjectionWriteDisposition.Conflict)]
    [InlineData(ProjectionWriteDisposition.Gap)]
    public async Task HandleEnvelopeAsync_ForwardedObserverStatusWriteRejection_ShouldPropagateShedAndRedeliver(
        ProjectionWriteDisposition disposition)
    {
        RetryAwareDirectDispatchAgent.Reset();
        var actorId = $"actor-{Guid.NewGuid():N}";
        var sourceActorId = $"source-{Guid.NewGuid():N}";
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
            await InitializeAgentByKindAsync(host, actorId);
            var grain = host.Services.GetRequiredService<IGrainFactory>()
                .GetGrain<IRuntimeActorGrain>(actorId);
            var envelope = CreateForwardedObserverEnvelope(sourceActorId, actorId, disposition);

            await grain.Invoking(x => x.HandleEnvelopeAsync(envelope.ToByteArray()))
                .Should().ThrowAsync<Exception>()
                .WithMessage($"*({disposition})*");
            await RetryAwareDirectDispatchAgent.WaitForDeactivationAsync(TimeSpan.FromSeconds(20));

            RetryAwareDirectDispatchAgent.GetAttemptCount(envelope.Id).Should().Be(1);
            RetryAwareDirectDispatchAgent.DeactivationCount.Should().Be(1,
                "a retryable status rejection must shed the activation before provider redelivery");

            // The provider redelivers the exact same forwarded observation after the failed call.
            await grain.HandleEnvelopeAsync(envelope.ToByteArray());
            var delivered = await RetryAwareDirectDispatchAgent.WaitForSuccessAsync(
                envelope.Id,
                TimeSpan.FromSeconds(20));

            RetryAwareDirectDispatchAgent.ActivationCount.Should().Be(2);
            RetryAwareDirectDispatchAgent.GetAttemptCount(envelope.Id).Should().Be(2);
            delivered.Route.IsObserverPublication().Should().BeTrue();
            StreamForwardingEnvelopeState.GetSourceStreamId(delivered).Should().Be(sourceActorId);
            StreamForwardingEnvelopeState.GetTargetStreamId(delivered).Should().Be(actorId);
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
        ILoggerProvider? loggerProvider = null,
        IActorRuntimeCallbackScheduler? callbackScheduler = null)
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
            .ConfigureServices(services =>
            {
                services.AddAevatarAgentKindRegistry(builder =>
                    builder.Register<RetryAwareDirectDispatchAgent>());
                if (callbackScheduler != null)
                {
                    services.Replace(
                        ServiceDescriptor.Singleton(callbackScheduler));
                }
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task InitializeAgentByKindAsync(IHost host, string actorId)
    {
        var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
        var grain = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);
        var initialized = await grain.InitializeAgentByKindAsync("tests.retry-aware-direct-dispatch");
        initialized.Should().BeTrue();
    }

    private static EventEnvelope CreateEnvelope(string payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Payload = Any.Pack(new StringValue { Value = payload }),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(string.Empty, TopologyAudience.Children),
        };

    private static EventEnvelope CreateCredentialCarryingEnvelope() =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Payload = Any.Pack(new NeedsCredentialPayload { ReplyToken = "runtime-reply-token" }),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(string.Empty, TopologyAudience.Children),
        };

    private static EventEnvelope CreateForwardedObserverEnvelope(
        string sourceActorId,
        string targetActorId,
        ProjectionWriteDisposition disposition)
    {
        var published = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Payload = Any.Pack(new StringValue { Value = $"projection-write-rejected:{disposition}" }),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(
                sourceActorId,
                ObserverAudience.CommittedFacts),
        };
        return StreamForwardingRules.BuildForwardedEnvelope(
            published,
            sourceActorId,
            targetActorId,
            StreamForwardingMode.HandleThenForward);
    }

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

    private sealed class CredentialGuardedRecordingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> TimeoutRequests { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            // Mirrors RuntimeCallbackSchedulerGrain.ValidateScheduleRequest: the
            // durable callback store rejects credential-carrying envelopes before
            // persisting anything.
            DurableCallbackEnvelopeCredentialGuard.ThrowIfContainsRuntimeCredential(request.TriggerEnvelope);
            TimeoutRequests.Add(request);
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Generation: 1,
                RuntimeCallbackBackend.Dedicated));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            _ = lease;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default)
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysFailingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        private int _scheduleTimeoutCallCount;

        public int ScheduleTimeoutCallCount => Volatile.Read(ref _scheduleTimeoutCallCount);

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _scheduleTimeoutCallCount);
            return Task.FromException<RuntimeCallbackLease>(
                new InvalidOperationException("callback-scheduler-fail-always"));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            _ = lease;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default)
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RuntimeRetryLogProbe : ILoggerProvider, ILogger
    {
        private readonly TaskCompletionSource<bool> _runtimeRetryScheduledDetected =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _runtimeHandlingFailureDetected =
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
            if (message.Contains("Runtime envelope handling failed after retry exhausted", StringComparison.Ordinal))
                _runtimeHandlingFailureDetected.TrySetResult(true);
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

        public async Task WaitForRuntimeHandlingFailureAsync(TimeSpan timeout)
        {
            try
            {
                await _runtimeHandlingFailureDetected.Task.WaitAsync(timeout);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"Timed out after {timeout} waiting for runtime handling failure to be logged.");
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

    private sealed class RuntimeTerminalFailureMetricProbe : IDisposable
    {
        private readonly MeterListener _listener = new();

        public RuntimeTerminalFailureMetricProbe()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name == AgentMetrics.RuntimeEnvelopeTerminalFailuresMetricName)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                string? reason = null;
                string? disposition = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == AgentMetrics.FailureReasonTag)
                        reason = tag.Value?.ToString();
                    else if (tag.Key == AgentMetrics.FailureDispositionTag)
                        disposition = tag.Value?.ToString();
                }

                Measurements.Enqueue((reason, disposition));
            });
            _listener.Start();
        }

        public ConcurrentQueue<(string? Reason, string? Disposition)> Measurements { get; } = new();

        public void Dispose() => _listener.Dispose();
    }

    [GAgent("tests.retry-aware-direct-dispatch")]
    public sealed class RetryAwareDirectDispatchAgent : IAgent
    {
        private static readonly Lock SyncLock = new();
        private static readonly Dictionary<string, int> AttemptsByEnvelopeId = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, TaskCompletionSource<int>> AttemptSourcesByEnvelopeId =
            new(StringComparer.Ordinal);
        private static TaskCompletionSource<EventEnvelope> _successfulEnvelopeSource = CreateSuccessSource();
        private static TaskCompletionSource<bool> _deactivationSource = CreateDeactivationSource();
        private static int _activationCount;
        private static int _deactivationCount;
        private static int _runtimeRetryableFailuresRemaining;
        private static int _retryUntilResolvedSchedulerFailuresRemaining;
        private static int _projectionWriteRejectionsRemaining;

        public static void Reset()
        {
            lock (SyncLock)
            {
                AttemptsByEnvelopeId.Clear();
                AttemptSourcesByEnvelopeId.Clear();
                _successfulEnvelopeSource = CreateSuccessSource();
                _deactivationSource = CreateDeactivationSource();
                _activationCount = 0;
                _deactivationCount = 0;
                _runtimeRetryableFailuresRemaining = 1;
                _retryUntilResolvedSchedulerFailuresRemaining = 1;
                _projectionWriteRejectionsRemaining = 1;
            }
        }

        public static int ActivationCount => Volatile.Read(ref _activationCount);

        public static int DeactivationCount => Volatile.Read(ref _deactivationCount);

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

        public static async Task<int> WaitForAttemptAsync(string envelopeId, TimeSpan timeout)
        {
            try
            {
                return await GetAttemptSource(envelopeId).Task.WaitAsync(timeout);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"Timed out after {timeout} waiting for direct-dispatch attempt of '{envelopeId}'.");
            }
        }

        public static async Task WaitForDeactivationAsync(TimeSpan timeout)
        {
            try
            {
                await _deactivationSource.Task.WaitAsync(timeout);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"Timed out after {timeout} waiting for direct-dispatch agent deactivation.");
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

            if (envelope.Payload?.Is(NeedsCredentialPayload.Descriptor) == true)
            {
                throw new EventStoreOptimisticConcurrencyException(
                    envelope.Id,
                    expectedVersion: 1,
                    actualVersion: 2);
            }

            if (payload == "always-fail-no-retry")
                throw new InvalidOperationException("always-fail-no-retry");

            if (payload == "always-fail-non-occ-default")
                throw new InvalidOperationException("always-fail-non-occ-default");

            if (payload == "always-fail-retry-exhausted")
                throw new InvalidOperationException("always-fail-retry-exhausted");

            if (payload == "retry-until-resolved" ||
                (payload == "retry-until-resolved-scheduler-failure" &&
                 Interlocked.Exchange(ref _retryUntilResolvedSchedulerFailuresRemaining, 0) == 1))
            {
                throw new ProjectionScopeStatusPhaseBProofUnavailableException(
                    Id,
                    "source-phase-b-proof",
                    7,
                    ProjectionScopeStatusRoutePhase.Warming,
                    ProjectionScopeStatusActorRole.TerminalWriter);
            }

            if ((payload == "runtime-retryable-exhausted" ||
                 payload == "runtime-retryable-scheduler-failure") &&
                Interlocked.Exchange(ref _runtimeRetryableFailuresRemaining, 0) == 1)
            {
                throw new RuntimeRetryableDirectDispatchException(payload);
            }

            if (payload.StartsWith("projection-write-rejected:", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _projectionWriteRejectionsRemaining, 0) == 1)
            {
                var disposition = System.Enum.Parse<ProjectionWriteDisposition>(
                    payload["projection-write-rejected:".Length..],
                    ignoreCase: false);
                throw new ProjectionScopeStatusWriteRejectedException(
                    Id,
                    new ProjectionSourceCoordinate
                    {
                        ActorId = "source-projection-scope",
                        StateVersion = 17,
                        EventId = envelope.Id,
                    },
                    disposition);
            }

            if (payload == "fail-once-then-succeed" &&
                RuntimeEnvelopeDeliveryIdentity.GetAttempt(envelope) == 0)
            {
                throw new InvalidOperationException("fail-once-before-retry");
            }

            if (payload == "occ-fail-once-then-succeed" &&
                RuntimeEnvelopeDeliveryIdentity.GetAttempt(envelope) == 0)
            {
                throw new EventStoreOptimisticConcurrencyException(
                    envelope.Id,
                    expectedVersion: 1,
                    actualVersion: 2);
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

        public Task ActivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _activationCount);
            return Task.CompletedTask;
        }

        public Task DeactivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _deactivationCount);
            _deactivationSource.TrySetResult(true);
            return Task.CompletedTask;
        }

        private static TaskCompletionSource<EventEnvelope> CreateSuccessSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static TaskCompletionSource<bool> CreateDeactivationSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static void RecordAttempt(string envelopeId)
        {
            lock (SyncLock)
            {
                var attempt = AttemptsByEnvelopeId.GetValueOrDefault(envelopeId, 0) + 1;
                AttemptsByEnvelopeId[envelopeId] = attempt;
                GetAttemptSourceUnderLock(envelopeId).TrySetResult(attempt);
            }
        }

        private static TaskCompletionSource<int> GetAttemptSource(string envelopeId)
        {
            lock (SyncLock)
            {
                var existingAttempt = AttemptsByEnvelopeId.GetValueOrDefault(envelopeId, 0);
                var source = GetAttemptSourceUnderLock(envelopeId);
                if (existingAttempt > 0)
                    source.TrySetResult(existingAttempt);
                return source;
            }
        }

        private static TaskCompletionSource<int> GetAttemptSourceUnderLock(string envelopeId)
        {
            if (!AttemptSourcesByEnvelopeId.TryGetValue(envelopeId, out var source))
            {
                source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                AttemptSourcesByEnvelopeId[envelopeId] = source;
            }

            return source;
        }

        private sealed class RuntimeRetryableDirectDispatchException(string message)
            : Exception(message), IRuntimeEnvelopeRetryableException;
    }
}
