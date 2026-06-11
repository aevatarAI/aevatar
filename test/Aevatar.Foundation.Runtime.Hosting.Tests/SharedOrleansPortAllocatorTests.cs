using System.Net.Sockets;
using Aevatar.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class SharedOrleansPortAllocatorTests
{
    [Fact]
    public async Task StartHostAsync_WhenHostStarts_ReturnsStartedHostWithValidPorts()
    {
        ReservedOrleansPortSnapshot? allocatedPorts = null;

        var host = await SharedOrleansPortAllocator.StartHostAsync(ports =>
        {
            allocatedPorts = new ReservedOrleansPortSnapshot(ports.SiloPort, ports.GatewayPort);
            return new ControllableHost();
        });

        try
        {
            host.Should().BeOfType<ControllableHost>();
            allocatedPorts.Should().NotBeNull();
            allocatedPorts!.SiloPort.Should().BeInRange(1, 65535);
            allocatedPorts.GatewayPort.Should().BeInRange(1, 65535);
            allocatedPorts.GatewayPort.Should().NotBe(allocatedPorts.SiloPort);
        }
        finally
        {
            host.Dispose();
        }
    }

    [Fact]
    public async Task StartHostAsync_WhenBindFailureOccurs_RetriesAndDisposesFailedHost()
    {
        var attempts = 0;
        var failedHost = new ControllableHost(
            _ => throw new InvalidOperationException(
                "Silo bind failed.",
                new SocketException((int)SocketError.AddressAlreadyInUse)));
        var successfulHost = new ControllableHost();

        var host = await SharedOrleansPortAllocator.StartHostAsync(_ =>
        {
            attempts++;
            return attempts == 1 ? failedHost : successfulHost;
        });

        try
        {
            host.Should().BeSameAs(successfulHost);
            attempts.Should().Be(2);
            failedHost.DisposeCount.Should().Be(1);
            successfulHost.StartCount.Should().Be(1);
        }
        finally
        {
            host.Dispose();
        }
    }

    [Fact]
    public async Task StartHostAsync_WhenFailureIsNotBindFailure_DoesNotRetry()
    {
        var attempts = 0;
        var failedHost = new ControllableHost(_ => throw new InvalidOperationException("configuration failed"));

        var act = () => SharedOrleansPortAllocator.StartHostAsync(_ =>
        {
            attempts++;
            return failedHost;
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("configuration failed");
        attempts.Should().Be(1);
        failedHost.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task StartHostAsync_WhenStartupTimesOutOrIsCanceled_DisposesHostAndThrows()
    {
        var timedOutHost = ControllableHost.CreateBlocked();

        var timeoutAct = () => SharedOrleansPortAllocator.StartHostAsync(
            _ => timedOutHost,
            TimeSpan.Zero);

        await timeoutAct.Should().ThrowAsync<TimeoutException>();
        timedOutHost.DisposeCount.Should().Be(1);

        var canceledHost = ControllableHost.CreateBlocked();
        using var cancellation = new CancellationTokenSource();

        var canceledStartTask = SharedOrleansPortAllocator.StartHostAsync(
            _ => canceledHost,
            startupTimeout: null,
            cancellation.Token);

        canceledHost.StartCount.Should().Be(1);
        await cancellation.CancelAsync();

        var cancellationAct = async () => await canceledStartTask;

        await cancellationAct.Should().ThrowAsync<OperationCanceledException>();
        canceledHost.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task StartHostAsync_WhenCalledConcurrently_SerializesHostFactories()
    {
        var firstStartEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFactoryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;

        var firstStart = SharedOrleansPortAllocator.StartHostAsync(_ =>
        {
            factoryCalls++;
            return new ControllableHost(_ =>
            {
                firstStartEntered.SetResult();
                return releaseFirstStart.Task;
            });
        });

        await firstStartEntered.Task;

        var secondStart = SharedOrleansPortAllocator.StartHostAsync(_ =>
        {
            factoryCalls++;
            secondFactoryEntered.SetResult();
            return new ControllableHost();
        });

        secondFactoryEntered.Task.IsCompleted.Should().BeFalse();
        factoryCalls.Should().Be(1);

        releaseFirstStart.SetResult();
        var firstHost = await firstStart;

        try
        {
            var secondHost = await secondStart;

            try
            {
                secondFactoryEntered.Task.IsCompletedSuccessfully.Should().BeTrue();
                factoryCalls.Should().Be(2);
            }
            finally
            {
                secondHost.Dispose();
            }
        }
        finally
        {
            firstHost.Dispose();
        }
    }

    private sealed record ReservedOrleansPortSnapshot(int SiloPort, int GatewayPort);

    private sealed class ControllableHost : IHost
    {
        private readonly Func<CancellationToken, Task> _startAsync;

        private ControllableHost(Func<CancellationToken, Task> startAsync, TaskCompletionSource? blockedStart)
        {
            _startAsync = startAsync;
            BlockedStart = blockedStart;
        }

        public ControllableHost(Func<CancellationToken, Task>? startAsync = null)
            : this(startAsync ?? (_ => Task.CompletedTask), null)
        {
        }

        public IServiceProvider Services { get; } = new ServiceCollection().BuildServiceProvider();

        public int StartCount { get; private set; }

        public int DisposeCount { get; private set; }

        private TaskCompletionSource? BlockedStart { get; }

        public static ControllableHost CreateBlocked()
        {
            var blockedStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return new ControllableHost(
                cancellationToken =>
                {
                    cancellationToken.Register(() => blockedStart.TrySetCanceled(cancellationToken));
                    return blockedStart.Task;
                },
                blockedStart);
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            return _startAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Dispose()
        {
            DisposeCount++;
            BlockedStart?.TrySetCanceled();
        }
    }
}
