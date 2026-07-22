using Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;
using FluentAssertions;
using NSubstitute;
using StackExchange.Redis;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class GarnetSecretKeyValueStoreExpirationTests
{
    [Fact]
    public async Task SetIfAbsentAsync_LongRelativeTtl_ShouldUseWholeSeconds()
    {
        Expiration? capturedExpiration = null;
        var store = CreateStore(expiration => capturedExpiration = expiration);

        var created = await store.SetIfAbsentAsync(
            "long-ttl",
            new byte[] { 0x01 },
            TimeSpan.FromDays(90) + TimeSpan.FromMilliseconds(123));

        created.Should().BeTrue();
        capturedExpiration.Should().NotBeNull();
        capturedExpiration!.Value.ToString().Should().Be("EX 7776001");
    }

    [Fact]
    public async Task SetIfAbsentAsync_ShortRelativeTtl_ShouldKeepMillisecondPrecision()
    {
        Expiration? capturedExpiration = null;
        var store = CreateStore(expiration => capturedExpiration = expiration);

        var created = await store.SetIfAbsentAsync(
            "short-ttl",
            new byte[] { 0x01 },
            TimeSpan.FromMilliseconds(1500));

        created.Should().BeTrue();
        capturedExpiration.Should().NotBeNull();
        capturedExpiration!.Value.ToString().Should().Be("PX 1500");
    }

    [Fact]
    public async Task SetIfAbsentAsync_ExpiryBeyondWholeSecondRange_ShouldReject()
    {
        var store = CreateStore(_ => { });

        var act = () => store.SetIfAbsentAsync(
            "unsupported-ttl",
            new byte[] { 0x01 },
            TimeSpan.FromSeconds((double)int.MaxValue + 1));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithMessage("*whole-second range*");
    }

    private static GarnetSecretKeyValueStore CreateStore(Action<Expiration> captureExpiration)
    {
        var database = Substitute.For<IDatabase>();
        database.StringSetAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<Expiration>(),
                Arg.Any<ValueCondition>(),
                Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                captureExpiration(call.ArgAt<Expiration>(2));
                return Task.FromResult(true);
            });
        var connection = Substitute.For<IGarnetSecretConnection>();
        connection.GetDatabase(Arg.Any<int>()).Returns(database);
        return new GarnetSecretKeyValueStore(connection, CreateOptions());
    }

    private static GarnetSecretStoreOptions CreateOptions() => new()
    {
        KeyringPath = "/unused/keyring.json",
        SecretVaultPrefix = "test:secret-vault",
        RuntimeSecretPrefix = "test:runtime-secrets",
    };
}
