using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.Infrastructure.ToolExecution;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Aevatar.Capabilities.Tests;

public sealed class AgentToolAdmissionLedgerTests
{
    public static TheoryData<TimeSpan, TimeSpan, string> InvalidAdmissionPolicies => new()
    {
        { TimeSpan.Zero, TimeSpan.Zero, "MaximumReplayWindow" },
        { TimeSpan.FromTicks(-1), TimeSpan.Zero, "MaximumReplayWindow" },
        { TimeSpan.FromDays(30).Add(TimeSpan.FromTicks(1)), TimeSpan.Zero, "MaximumReplayWindow" },
        { TimeSpan.FromHours(1), TimeSpan.FromTicks(-1), "MaximumFutureClockSkew" },
        { TimeSpan.FromHours(1), TimeSpan.FromHours(1).Add(TimeSpan.FromTicks(1)), "MaximumFutureClockSkew" },
    };

    [Theory]
    [MemberData(nameof(InvalidAdmissionPolicies))]
    public void AddInMemoryLedger_WhenPolicyIsOutsideSupportedRange_ShouldRejectAtRegistration(
        TimeSpan maximumReplayWindow,
        TimeSpan maximumFutureClockSkew,
        string parameterName)
    {
        var services = new ServiceCollection();
        var policy = new AgentToolAdmissionPolicy(
            maximumReplayWindow,
            maximumFutureClockSkew);

        var action = () => services.AddInMemoryAgentToolAdmissionLedger(policy);

        action.Should().ThrowExactly<ArgumentOutOfRangeException>()
            .WithParameterName(parameterName);
    }

    [Fact]
    public void AddInMemoryLedger_WhenPolicyIsAtInclusiveMaximumBoundaries_ShouldRegisterExactPolicy()
    {
        var services = new ServiceCollection();
        var policy = new AgentToolAdmissionPolicy(
            TimeSpan.FromDays(30),
            TimeSpan.FromDays(30));

        services.AddInMemoryAgentToolAdmissionLedger(policy);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<AgentToolAdmissionPolicy>().Should().BeSameAs(policy);
    }

    [Fact]
    public async Task TryStartAsync_ShouldDistinguishFirstDuplicateAndConflict()
    {
        var store = new RecordingAdmissionFactStore();
        var ledger = new DistributedAgentToolAdmissionLedger(
            store,
            MainnetLedgerOptions());
        var fact = CreateFact();
        var conflictingFact = fact.Clone();
        conflictingFact.ToolName = "different_tool";

        (await ledger.TryStartAsync(fact)).Status.Should().Be(AgentToolAdmissionStatus.Started);
        (await ledger.TryStartAsync(fact.Clone())).Status.Should().Be(AgentToolAdmissionStatus.Duplicate);
        (await ledger.TryStartAsync(conflictingFact))
            .Status.Should().Be(AgentToolAdmissionStatus.Conflict);

        store.Keys.Should().ContainSingle()
            .Which.Should().StartWith("aevatar:mainnet:agent-tool-admission:v1:")
            .And.NotContain(fact.AdmissionId);
        store.Values.Should().ContainSingle().Which.Should().HaveCount(32);
        store.Retentions.Should().HaveCount(3)
            .And.OnlyContain(retention =>
                retention > TimeSpan.FromHours(23) &&
                retention <= TimeSpan.FromHours(24));
    }

    [Fact]
    public async Task DistributedLedgers_WithDifferentHostPrefixes_ShouldUseDistinctStorageKeys()
    {
        var mainnetStore = new RecordingAdmissionFactStore();
        var workflowStore = new RecordingAdmissionFactStore();
        var fact = CreateFact();
        var mainnetLedger = new DistributedAgentToolAdmissionLedger(
            mainnetStore,
            new AgentToolAdmissionLedgerOptions("aevatar:mainnet:agent-tool-admission:v1:"));
        var workflowLedger = new DistributedAgentToolAdmissionLedger(
            workflowStore,
            new AgentToolAdmissionLedgerOptions("aevatar:workflow:agent-tool-admission:v1:"));

        (await mainnetLedger.TryStartAsync(fact)).Status.Should().Be(AgentToolAdmissionStatus.Started);
        (await workflowLedger.TryStartAsync(fact)).Status.Should().Be(AgentToolAdmissionStatus.Started);

        var mainnetKey = mainnetStore.Keys.Should().ContainSingle().Subject;
        var workflowKey = workflowStore.Keys.Should().ContainSingle().Subject;
        mainnetKey.Should().StartWith("aevatar:mainnet:agent-tool-admission:v1:");
        workflowKey.Should().StartWith("aevatar:workflow:agent-tool-admission:v1:");
        mainnetKey.Should().NotBe(workflowKey);
    }

    [Fact]
    public async Task TryStartAsync_AfterRetentionCleanup_ShouldStillRejectExpiredFact()
    {
        var now = new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(now);
        var store = new RecordingAdmissionFactStore();
        var ledger = CreateLedger(store, timeProvider, maximumReplayWindow: TimeSpan.FromHours(1));
        var fact = CreateFact(now);

        (await ledger.TryStartAsync(fact)).Status.Should().Be(AgentToolAdmissionStatus.Started);
        store.Clear();
        timeProvider.Advance(TimeSpan.FromHours(1));

        var replay = await ledger.TryStartAsync(fact);

        replay.Status.Should().Be(AgentToolAdmissionStatus.Expired);
        store.SetAttempts.Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(600_001)]
    public async Task TryStartAsync_WhenIssuedTimeIsInvalid_ShouldFailClosed(long issuedOffsetMilliseconds)
    {
        var now = new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);
        var store = new RecordingAdmissionFactStore();
        var ledger = CreateLedger(store, new ManualTimeProvider(now));
        var fact = CreateFact(now);
        fact.IssuedAtUnixMs = issuedOffsetMilliseconds == 0
            ? 0
            : now.AddMilliseconds(issuedOffsetMilliseconds).ToUnixTimeMilliseconds();

        var result = await ledger.TryStartAsync(fact);

        result.Status.Should().Be(AgentToolAdmissionStatus.InvalidFact);
        store.SetAttempts.Should().Be(0);
    }

    [Fact]
    public async Task TryStartAsync_WhenIssuedTimeCannotBeRepresented_ShouldFailClosed()
    {
        var now = new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);
        var store = new RecordingAdmissionFactStore();
        var ledger = CreateLedger(store, new ManualTimeProvider(now));
        var fact = CreateFact(now);
        fact.IssuedAtUnixMs = long.MaxValue;

        var result = await ledger.TryStartAsync(fact);

        result.Status.Should().Be(AgentToolAdmissionStatus.InvalidFact);
        store.SetAttempts.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TryStartAsync_WhenOperationIdIsMissing_ShouldFailClosedWithoutWriting(
        string operationId)
    {
        var store = new RecordingAdmissionFactStore();
        var ledger = new DistributedAgentToolAdmissionLedger(store, MainnetLedgerOptions());
        var fact = CreateFact();
        fact.OperationId = operationId;

        var result = await ledger.TryStartAsync(fact);

        result.Status.Should().Be(AgentToolAdmissionStatus.InvalidFact);
        store.SetAttempts.Should().Be(0);
    }

    [Theory]
    [InlineData(AgentToolReplayPolicy.Unspecified)]
    [InlineData((AgentToolReplayPolicy)999)]
    public async Task TryStartAsync_WhenReplayPolicyIsUnsupported_ShouldFailClosedWithoutWriting(
        AgentToolReplayPolicy replayPolicy)
    {
        var store = new RecordingAdmissionFactStore();
        var ledger = new DistributedAgentToolAdmissionLedger(store, MainnetLedgerOptions());
        var fact = CreateFact();
        fact.ReplayPolicy = replayPolicy;

        var result = await ledger.TryStartAsync(fact);

        result.Status.Should().Be(AgentToolAdmissionStatus.InvalidFact);
        store.SetAttempts.Should().Be(0);
    }

    [Fact]
    public async Task TryStartAsync_WhenRecoveryContractChangesForSameAdmissionId_ShouldConflict()
    {
        var store = new RecordingAdmissionFactStore();
        var ledger = new DistributedAgentToolAdmissionLedger(store, MainnetLedgerOptions());
        var fact = CreateFact();
        var changedOperation = fact.Clone();
        changedOperation.OperationId = "operation-2";
        var changedReplayPolicy = fact.Clone();
        changedReplayPolicy.ReplayPolicy = AgentToolReplayPolicy.NonReplayable;

        (await ledger.TryStartAsync(fact)).Status.Should().Be(AgentToolAdmissionStatus.Started);
        (await ledger.TryStartAsync(changedOperation)).Status.Should().Be(AgentToolAdmissionStatus.Conflict);
        (await ledger.TryStartAsync(changedReplayPolicy)).Status.Should().Be(AgentToolAdmissionStatus.Conflict);
    }

    [Fact]
    public async Task TryStartAsync_WhenStoreThrows_ShouldFailClosed()
    {
        var ledger = new DistributedAgentToolAdmissionLedger(
            new RecordingAdmissionFactStore { ThrowOnAdd = true },
            MainnetLedgerOptions());

        var result = await ledger.TryStartAsync(CreateFact());

        result.Status.Should().Be(AgentToolAdmissionStatus.StoreUnavailable);
        result.SafeMessage.Should().Be(nameof(InvalidOperationException));
    }

    [Fact]
    public async Task TryStartAsync_WhenRejectedFactCannotBeRead_ShouldReportStoreUnavailable()
    {
        var ledger = new DistributedAgentToolAdmissionLedger(
            new RecordingAdmissionFactStore { RejectAddWithoutValue = true },
            MainnetLedgerOptions());

        var result = await ledger.TryStartAsync(CreateFact());

        result.Status.Should().Be(AgentToolAdmissionStatus.StoreUnavailable);
        result.SafeMessage.Should().Be(
            "The admission fact could not be read after the atomic insert was rejected.");
    }

    [Fact]
    public async Task UnavailableLedger_ShouldFailClosedWithStableMessage()
    {
        var result = await new UnavailableAgentToolAdmissionLedger().TryStartAsync(CreateFact());

        result.Status.Should().Be(AgentToolAdmissionStatus.StoreUnavailable);
        result.SafeMessage.Should().Be("The durable tool admission ledger is not configured.");
    }

    [Fact]
    public async Task UnavailableLedger_WhenFactIsNull_ShouldRejectInvalidCall()
    {
        var ledger = new UnavailableAgentToolAdmissionLedger();

        var action = () => ledger.TryStartAsync(null!);

        await action.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("fact");
    }

    [Fact]
    public async Task UnavailableLedger_WhenCallerCancels_ShouldPropagateCancellation()
    {
        var ledger = new UnavailableAgentToolAdmissionLedger();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var action = () => ledger.TryStartAsync(CreateFact(), cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task InMemoryLedger_ShouldApplyTheSameFactSemantics()
    {
        var ledger = new InMemoryAgentToolAdmissionLedger();
        var fact = CreateFact();
        var conflictingFact = fact.Clone();
        conflictingFact.ArgumentsSha256 = new string('f', 64);

        (await ledger.TryStartAsync(fact)).Status.Should().Be(AgentToolAdmissionStatus.Started);
        (await ledger.TryStartAsync(fact.Clone())).Status.Should().Be(AgentToolAdmissionStatus.Duplicate);
        (await ledger.TryStartAsync(conflictingFact))
            .Status.Should().Be(AgentToolAdmissionStatus.Conflict);
    }

    [Fact]
    public async Task InMemoryLedger_AfterRetentionCleanup_ShouldStartCurrentFactWithSameAdmissionId()
    {
        var now = new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(now);
        var ledger = new InMemoryAgentToolAdmissionLedger(
            new AgentToolAdmissionPolicy(
                TimeSpan.FromHours(1),
                TimeSpan.FromMinutes(10)),
            timeProvider);
        var expiredFact = CreateFact(now);

        (await ledger.TryStartAsync(expiredFact)).Status.Should().Be(AgentToolAdmissionStatus.Started);
        timeProvider.Advance(TimeSpan.FromHours(1));
        var currentFact = expiredFact.Clone();
        currentFact.IssuedAtUnixMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        var expiredReplay = await ledger.TryStartAsync(expiredFact);
        var currentAttempt = await ledger.TryStartAsync(currentFact);

        expiredReplay.Status.Should().Be(AgentToolAdmissionStatus.Expired);
        currentAttempt.Status.Should().Be(AgentToolAdmissionStatus.Started);
    }

    [Fact]
    public async Task GarnetStore_WithPinnedRedis_ShouldRoundTripBinaryAndExpireKey()
    {
        await using var server = await PinnedRedisServer.StartAsync();
        using var connection = await ConnectionMultiplexer.ConnectAsync(server.ConnectionString);
        var store = new GarnetAgentToolAdmissionFactStore(connection);
        var key = $"agent-tool-admission-test:{Guid.NewGuid():N}";
        var value = Enumerable.Range(0, 256).Select(static value => checked((byte)value)).ToArray();

        var added = await store.SetIfAbsentAsync(key, value, TimeSpan.FromHours(24));

        added.Should().BeTrue();
        (await store.GetAsync(key)).Should().Equal(value);
        var database = connection.GetDatabase();
        ((byte[]?)(await database.StringGetAsync(key))).Should().Equal(value);
        var retention = await database.KeyTimeToLiveAsync(key);
        retention.Should().NotBeNull();
        retention.Should().BePositive().And.BeLessThanOrEqualTo(TimeSpan.FromHours(24));
    }

    [Fact]
    public async Task DistributedLedger_WithPinnedRedis_ShouldAtomicallyStartOnceThenRejectDuplicatesAndConflict()
    {
        await using var server = await PinnedRedisServer.StartAsync();
        using var connection = await ConnectionMultiplexer.ConnectAsync(server.ConnectionString);
        var ledger = new DistributedAgentToolAdmissionLedger(
            new GarnetAgentToolAdmissionFactStore(connection),
            MainnetLedgerOptions());
        var fact = CreateFact(admissionId: $"tool:v1:admission:{Guid.NewGuid():N}");

        var identicalResults = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => ledger.TryStartAsync(fact.Clone())));
        var conflictingFact = fact.Clone();
        conflictingFact.ToolName = "different_tool";
        var conflict = await ledger.TryStartAsync(conflictingFact);

        identicalResults.Count(result => result.Status == AgentToolAdmissionStatus.Started).Should().Be(1);
        identicalResults.Count(result => result.Status == AgentToolAdmissionStatus.Duplicate).Should().Be(15);
        conflict.Status.Should().Be(AgentToolAdmissionStatus.Conflict);
    }

    [Fact]
    public async Task GarnetStore_WhenCallerCancels_ShouldPropagateWithoutWriting()
    {
        await using var server = await PinnedRedisServer.StartAsync();
        using var connection = await ConnectionMultiplexer.ConnectAsync(server.ConnectionString);
        var store = new GarnetAgentToolAdmissionFactStore(connection);
        var key = $"agent-tool-admission-cancelled:{Guid.NewGuid():N}";
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var action = () => store.SetIfAbsentAsync(
            key,
            new byte[] { 1, 2, 3 },
            TimeSpan.FromHours(24),
            cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        (await connection.GetDatabase().KeyExistsAsync(key)).Should().BeFalse();
    }

    private static DistributedAgentToolAdmissionLedger CreateLedger(
        IAgentToolAdmissionFactStore store,
        TimeProvider timeProvider,
        TimeSpan? maximumReplayWindow = null) =>
        new(
            store,
            MainnetLedgerOptions(),
            new AgentToolAdmissionPolicy(
                maximumReplayWindow ?? TimeSpan.FromHours(24),
                TimeSpan.FromMinutes(10)),
            timeProvider);

    private static AgentToolAdmissionLedgerOptions MainnetLedgerOptions() =>
        new("aevatar:mainnet:agent-tool-admission:v1:");

    private static AgentToolAdmissionFact CreateFact(
        DateTimeOffset? issuedAt = null,
        string admissionId = "tool:v1:admission:sensitive-id") => new()
    {
        AdmissionId = admissionId,
        RequestId = "request-1",
        ToolCallId = "call-1",
        ToolName = "test_tool",
        ArgumentsSha256 = new string('a', 64),
        IssuedAtUnixMs = (issuedAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds(),
        OperationId = "operation-1",
        ReplayPolicy = AgentToolReplayPolicy.ReadOnlyRetryable,
    };

    private sealed class RecordingAdmissionFactStore : IAgentToolAdmissionFactStore
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

        public bool ThrowOnAdd { get; init; }
        public bool RejectAddWithoutValue { get; init; }
        public int SetAttempts { get; private set; }
        public IReadOnlyCollection<string> Keys => _values.Keys;
        public IReadOnlyCollection<byte[]> Values => _values.Values;
        public List<TimeSpan> Retentions { get; } = [];

        public Task<bool> SetIfAbsentAsync(
            string key,
            ReadOnlyMemory<byte> value,
            TimeSpan retention,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            SetAttempts++;
            Retentions.Add(retention);
            if (ThrowOnAdd)
                throw new InvalidOperationException("offline");
            if (RejectAddWithoutValue)
                return Task.FromResult(false);

            if (_values.ContainsKey(key))
                return Task.FromResult(false);
            _values.Add(key, value.ToArray());
            return Task.FromResult(true);
        }

        public void Clear() => _values.Clear();

        public Task<byte[]?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_values.TryGetValue(key, out var value) ? value.ToArray() : null);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed) => _utcNow += elapsed;
    }

    private sealed class PinnedRedisServer : IAsyncDisposable
    {
        private const string ExpectedVersion = "7.2.3";
        private const string ConnectionStringEnvironmentVariable =
            "AGENT_TOOL_ADMISSION_REDIS_CONNECTION_STRING";
        private readonly Process? _process;

        private PinnedRedisServer(Process? process, string connectionString)
        {
            _process = process;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public static async Task<PinnedRedisServer> StartAsync()
        {
            var configuredConnectionString = Environment.GetEnvironmentVariable(
                ConnectionStringEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configuredConnectionString))
            {
                var configuredServer = new PinnedRedisServer(
                    null,
                    configuredConnectionString.Trim());
                await configuredServer.VerifyVersionAsync();
                return configuredServer;
            }

            var port = ReservePort();
            var startInfo = new ProcessStartInfo("redis-server")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--bind");
            startInfo.ArgumentList.Add("127.0.0.1");
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--save");
            startInfo.ArgumentList.Add(string.Empty);
            startInfo.ArgumentList.Add("--appendonly");
            startInfo.ArgumentList.Add("no");

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start redis-server 7.2.3.");
            try
            {
                var ready = WaitForReadyAsync(process);
                var exited = process.WaitForExitAsync();
                if (await Task.WhenAny(ready, exited) != ready)
                {
                    throw new InvalidOperationException(
                        $"redis-server exited before readiness: {await process.StandardError.ReadToEndAsync()}");
                }

                await ready;
                var server = new PinnedRedisServer(
                    process,
                    $"127.0.0.1:{port},abortConnect=true,allowAdmin=true,connectTimeout=5000,syncTimeout=5000");
                await server.VerifyVersionAsync();
                return server;
            }
            catch
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                process.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_process is null)
                return;

            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
            _process.Dispose();
        }

        private async Task VerifyVersionAsync()
        {
            using var connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
            var endpoint = connection.GetEndPoints().Should().ContainSingle().Subject;
            var info = (await connection.GetServer(endpoint).InfoAsync("server"))
                .SelectMany(static section => section)
                .Single(pair => pair.Key == "redis_version")
                .Value;
            info.Should().Be(ExpectedVersion);
        }

        private static async Task WaitForReadyAsync(Process process)
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Contains("Ready to accept connections", StringComparison.Ordinal))
                    return;
            }

            throw new InvalidOperationException("redis-server closed stdout before readiness.");
        }

        private static int ReservePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
