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

    [Fact]
    public async Task CompareSetAsync_LongRelativeTtl_ShouldCarrySecondsFallbackIntoLua()
    {
        string? capturedScript = null;
        RedisValue[]? capturedValues = null;
        var database = Substitute.For<IDatabase>();
        database.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                capturedScript = call.ArgAt<string>(0);
                capturedValues = call.ArgAt<RedisValue[]>(2);
                return Task.FromResult(RedisResult.Create((RedisValue)1));
            });
        var connection = Substitute.For<IGarnetSecretConnection>();
        connection.GetDatabase(Arg.Any<int>()).Returns(database);
        var store = new GarnetSecretKeyValueStore(connection, CreateOptions());

        var replaced = await store.CompareSetAsync(
            "long-cas-ttl",
            new byte[] { 0x01 },
            new byte[] { 0x02 },
            TimeSpan.FromDays(90) + TimeSpan.FromMilliseconds(123));

        replaced.Should().BeTrue();
        capturedScript.Should().Contain("effectiveTtl > maximumRelativeMilliseconds");
        capturedScript.Should().Contain("'EX'");
        capturedValues.Should().NotBeNull();
        capturedValues.Should().HaveCount(4);
        capturedValues![3].ToString().Should().Be(int.MaxValue.ToString());
    }

    [GarnetIntegrationFact]
    public async Task SetIfAbsentAndCompareSet_LongRelativeTtl_ShouldRemainNearNinetyDays()
    {
        var options = CreateOptions();
        options.ConnectionString =
            Environment.GetEnvironmentVariable("AEVATAR_TEST_GARNET_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Missing AEVATAR_TEST_GARNET_CONNECTION_STRING.");
        using var connection = new GarnetSecretConnectionMultiplexer(options);
        var store = new GarnetSecretKeyValueStore(connection, options);
        var key = $"{options.SecretVaultPrefix}:long-ttl:{Guid.NewGuid():N}";
        var original = new byte[] { 0x01 };
        var updated = new byte[] { 0x02 };
        var initialTtl = TimeSpan.FromDays(60) + TimeSpan.FromMilliseconds(123);
        var requestedTtl = TimeSpan.FromDays(90) + TimeSpan.FromMilliseconds(456);

        try
        {
            (await store.SetIfAbsentAsync(key, original, initialTtl)).Should().BeTrue();
            var before = await connection.GetDatabase(options.Database).KeyTimeToLiveAsync(key);
            before.Should().NotBeNull();
            (await store.CompareSetAsync(key, original, updated, requestedTtl)).Should().BeTrue();

            var after = await connection.GetDatabase(options.Database).KeyTimeToLiveAsync(key);
            after.Should().NotBeNull();
            after.Should().BeGreaterThan(TimeSpan.FromDays(59));
            after.Should().BeLessThanOrEqualTo(before!.Value + TimeSpan.FromSeconds(1));
        }
        finally
        {
            await connection.GetDatabase(options.Database).KeyDeleteAsync(key);
        }
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
