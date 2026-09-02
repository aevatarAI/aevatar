using Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.AI.Infrastructure.ChronoSandbox.Tests;

public sealed class ManagedCodexCredentialMutationLeaseTests
{
    [Theory]
    [InlineData(true, nameof(InMemoryManagedCodexCredentialMutationLease))]
    [InlineData(false, nameof(GarnetManagedCodexCredentialMutationLease))]
    public void Composition_SelectsInMemoryOnlyWhenExplicitlyRequested(
        bool useInMemory,
        string expectedImplementation)
    {
        var services = new ServiceCollection();

        services.AddChronoSandboxCodexExecution(
            new ConfigurationBuilder().Build(),
            useInMemory);

        var descriptor = services.Single(service =>
            service.ServiceType == typeof(IManagedCodexCredentialMutationLease));
        descriptor.ImplementationType!.Name.Should().Be(expectedImplementation);
    }

    [Fact]
    public async Task GarnetLease_SerializesOneOwnerAndReleasesWithConfiguredTtl()
    {
        var store = new StatefulStore();
        var options = Options.Create(new ManagedCodexOptions
        {
            MutationLeaseSeconds = 300,
            MutationCompletionSeconds = 240,
        });
        var lease = new GarnetManagedCodexCredentialMutationLease(
            store,
            options,
            NullLogger<GarnetManagedCodexCredentialMutationLease>.Instance);

        var first = await lease.TryAcquireAsync("managed-codex-credential:nyxid::user-a");
        var concurrent = await lease.TryAcquireAsync("managed-codex-credential:nyxid::user-a");

        first.Should().NotBeNull();
        concurrent.Should().BeNull();
        store.LastExpiry.Should().Be(TimeSpan.FromSeconds(300));

        await first!.DisposeAsync();
        var afterRelease = await lease.TryAcquireAsync("managed-codex-credential:nyxid::user-a");
        afterRelease.Should().NotBeNull();
        await afterRelease!.DisposeAsync();
    }

    [Fact]
    public async Task GarnetLease_StaleHandleCannotDeleteAReplacementOwner()
    {
        var store = new StatefulStore();
        var lease = new GarnetManagedCodexCredentialMutationLease(
            store,
            Options.Create(new ManagedCodexOptions
            {
                MutationLeaseSeconds = 300,
                MutationCompletionSeconds = 240,
            }),
            NullLogger<GarnetManagedCodexCredentialMutationLease>.Instance);
        var stale = await lease.TryAcquireAsync("managed-codex-credential:nyxid::user-a");
        stale.Should().NotBeNull();
        store.ReplaceCurrentValue("replacement-owner"u8.ToArray());

        await stale!.DisposeAsync();

        store.CurrentValue.Should().Equal("replacement-owner"u8.ToArray());
    }

    [Fact]
    public async Task InMemoryLease_SerializesOnlyDevelopmentAndTestCallers()
    {
        var lease = new InMemoryManagedCodexCredentialMutationLease();

        var first = await lease.TryAcquireAsync("managed-codex-credential:nyxid::user-a");
        var concurrent = await lease.TryAcquireAsync("managed-codex-credential:nyxid::user-a");
        var otherUser = await lease.TryAcquireAsync("managed-codex-credential:nyxid::user-b");

        first.Should().NotBeNull();
        concurrent.Should().BeNull();
        otherUser.Should().NotBeNull();

        await first!.DisposeAsync();
        await otherUser!.DisposeAsync();
        var reacquired = await lease.TryAcquireAsync("managed-codex-credential:nyxid::user-a");
        reacquired.Should().NotBeNull();
        await reacquired!.DisposeAsync();
    }

    private sealed class StatefulStore : IGarnetSecretKeyValueStore
    {
        private readonly object _gate = new();
        private byte[]? _value;

        public TimeSpan? LastExpiry { get; private set; }

        public byte[]? CurrentValue
        {
            get
            {
                lock (_gate)
                    return _value?.ToArray();
            }
        }

        public void ReplaceCurrentValue(byte[] value)
        {
            lock (_gate)
                _value = value.ToArray();
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(CurrentValue);

        public Task SetAsync(
            string key,
            ReadOnlyMemory<byte> value,
            TimeSpan? expiry,
            CancellationToken ct = default)
        {
            lock (_gate)
            {
                _value = value.ToArray();
                LastExpiry = expiry;
            }
            return Task.CompletedTask;
        }

        public Task<bool> SetIfAbsentAsync(
            string key,
            ReadOnlyMemory<byte> value,
            TimeSpan? expiry,
            CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (_value is not null)
                    return Task.FromResult(false);
                _value = value.ToArray();
                LastExpiry = expiry;
                return Task.FromResult(true);
            }
        }

        public Task<bool> CompareSetAsync(
            string key,
            ReadOnlyMemory<byte> expectedValue,
            ReadOnlyMemory<byte> newValue,
            TimeSpan? expiry,
            CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (_value is null || !_value.AsSpan().SequenceEqual(expectedValue.Span))
                    return Task.FromResult(false);
                _value = newValue.ToArray();
                LastExpiry = expiry;
                return Task.FromResult(true);
            }
        }

        public Task<bool> CompareDeleteAsync(
            string key,
            ReadOnlyMemory<byte> expectedValue,
            CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (_value is null || !_value.AsSpan().SequenceEqual(expectedValue.Span))
                    return Task.FromResult(false);
                _value = null;
                return Task.FromResult(true);
            }
        }
    }
}
