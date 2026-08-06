using Aevatar.AI.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Hosting.NyxId;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Tests;

public sealed class CachedNyxIdLlmCatalogPortTests
{
    [Fact]
    public async Task GetFreshServicesAsync_ShouldBypassSnapshotAndReplaceItAfterSuccess()
    {
        var inner = new RecordingCatalogPort();
        inner.Enqueue(MakeResult("anthropic", "/cached"));
        inner.EnqueueFresh(MakeResult("anthropic", "/fresh"));
        var port = CreatePort(inner);

        await port.GetServicesAsync("bearer-1", CancellationToken.None);
        var fresh = await port.GetFreshServicesAsync("bearer-1", CancellationToken.None);
        var cachedAfterFresh = await port.GetServicesAsync("bearer-1", CancellationToken.None);

        fresh.Services.Single().RouteValue.Should().Be("/fresh");
        cachedAfterFresh.Services.Single().RouteValue.Should().Be("/fresh");
        inner.GetCalls.Should().Be(1);
        inner.FreshCalls.Should().Be(1);
    }

    [Fact]
    public async Task GetServicesAsync_ShouldReuseFreshSnapshot_ForSameAuthorityAndBearer()
    {
        var inner = new RecordingCatalogPort();
        inner.Enqueue(MakeResult("anthropic", "/v1"));
        var port = CreatePort(inner);

        var first = await port.GetServicesAsync("bearer-1", CancellationToken.None);
        var second = await port.GetServicesAsync("bearer-1", CancellationToken.None);

        first.Services.Single().RouteValue.Should().Be("/v1");
        second.Services.Single().RouteValue.Should().Be("/v1");
        inner.GetCalls.Should().Be(1);
        inner.CapturedBearers.Should().Equal("bearer-1");
    }

    [Fact]
    public async Task GetServicesAsync_ShouldNotShareSnapshot_AcrossBearerFingerprints()
    {
        var inner = new RecordingCatalogPort();
        inner.Enqueue(MakeResult("anthropic", "/caller-a"));
        inner.Enqueue(MakeResult("anthropic", "/caller-b"));
        var port = CreatePort(inner);

        var first = await port.GetServicesAsync("bearer-A", CancellationToken.None);
        var second = await port.GetServicesAsync("bearer-B", CancellationToken.None);

        first.Services.Single().RouteValue.Should().Be("/caller-a");
        second.Services.Single().RouteValue.Should().Be("/caller-b");
        inner.GetCalls.Should().Be(2);
        inner.CapturedBearers.Should().Equal("bearer-A", "bearer-B");
    }

    [Fact]
    public async Task GetServicesAsync_ShouldNotShareSnapshot_AcrossAuthorities()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:NyxId:Authority"] = "https://nyx-a.test",
            })
            .Build();
        var inner = new RecordingCatalogPort();
        inner.Enqueue(MakeResult("anthropic", "/authority-a"));
        inner.Enqueue(MakeResult("anthropic", "/authority-b"));
        var port = CreatePort(inner, configuration);

        var first = await port.GetServicesAsync("bearer-1", CancellationToken.None);
        configuration["Aevatar:NyxId:Authority"] = "https://nyx-b.test";
        var second = await port.GetServicesAsync("bearer-1", CancellationToken.None);

        first.Services.Single().RouteValue.Should().Be("/authority-a");
        second.Services.Single().RouteValue.Should().Be("/authority-b");
        inner.GetCalls.Should().Be(2);
    }

    [Fact]
    public async Task GetServicesAsync_WhenCacheDisabled_ShouldCallInnerForRepeatedSameBearer()
    {
        var inner = new RecordingCatalogPort();
        inner.Enqueue(MakeResult("anthropic", "/first"));
        inner.Enqueue(MakeResult("anthropic", "/second"));
        inner.Enqueue(MakeResult("anthropic", "/third"));
        var port = CreatePort(inner, cacheEnabled: false);

        var first = await port.GetServicesAsync("bearer-1", CancellationToken.None);
        var second = await port.GetServicesAsync("bearer-1", CancellationToken.None);
        var third = await port.GetServicesAsync("bearer-1", CancellationToken.None);

        first.Services.Single().RouteValue.Should().Be("/first");
        second.Services.Single().RouteValue.Should().Be("/second");
        third.Services.Single().RouteValue.Should().Be("/third");
        inner.GetCalls.Should().Be(3);
        inner.CapturedBearers.Should().Equal("bearer-1", "bearer-1", "bearer-1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-uri")]
    public async Task GetServicesAsync_WhenCacheDisabled_ShouldNotConsultAuthority(string? authority)
    {
        var inner = new RecordingCatalogPort();
        inner.Enqueue(MakeResult("anthropic", "/inner"));
        var port = CreatePort(
            inner,
            configuration: CreateConfiguration(authority),
            cacheEnabled: false);

        var result = await port.GetServicesAsync("bearer-1", CancellationToken.None);

        result.Services.Single().RouteValue.Should().Be("/inner");
        inner.GetCalls.Should().Be(1);
    }

    [Fact]
    public async Task GetServicesAsync_ShouldReturnStaleSnapshot_AndRefreshOnce()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-05-29T10:00:00Z"));
        var inner = new RecordingCatalogPort();
        inner.Enqueue(MakeResult("anthropic", "/old"));
        inner.Enqueue(MakeResult("anthropic", "/new"));
        var refreshCompleted = inner.SignalOnCallCompletion(callNumber: 2);
        var port = CreatePort(inner, timeProvider: clock);

        await port.GetServicesAsync("bearer-1", CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(61));

        var stale = await port.GetServicesAsync("bearer-1", CancellationToken.None);
        await refreshCompleted.Task;
        var refreshed = await port.GetServicesAsync("bearer-1", CancellationToken.None);

        stale.Services.Single().RouteValue.Should().Be("/old");
        refreshed.Services.Single().RouteValue.Should().Be("/new");
        inner.GetCalls.Should().Be(2);
    }

    [Fact]
    public async Task GetServicesAsync_ShouldCoalesceStaleReads_WhenRefreshIsInFlight()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-05-29T10:00:00Z"));
        var inner = new RecordingCatalogPort();
        var refreshStarted = inner.SignalOnCallStart(callNumber: 2);
        var refreshCompleted = inner.SignalOnCallCompletion(callNumber: 2);
        var releaseRefresh = new TaskCompletionSource();
        inner.Enqueue(MakeResult("anthropic", "/old"));
        inner.EnqueueAfter(releaseRefresh.Task, MakeResult("anthropic", "/new"));
        var port = CreatePort(inner, timeProvider: clock);

        await port.GetServicesAsync("bearer-1", CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(61));

        var firstStale = await port.GetServicesAsync("bearer-1", CancellationToken.None);
        await refreshStarted.Task;
        var secondStale = await port.GetServicesAsync("bearer-1", CancellationToken.None);
        var thirdStale = await port.GetServicesAsync("bearer-1", CancellationToken.None);

        firstStale.Services.Single().RouteValue.Should().Be("/old");
        secondStale.Services.Single().RouteValue.Should().Be("/old");
        thirdStale.Services.Single().RouteValue.Should().Be("/old");
        inner.GetCalls.Should().Be(2);

        releaseRefresh.SetResult();
        await refreshCompleted.Task;
        inner.GetCalls.Should().Be(2);
    }

    [Fact]
    public async Task GetServicesAsync_ShouldKeepStaleSnapshot_WhenRefreshFails()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-05-29T10:00:00Z"));
        var inner = new RecordingCatalogPort();
        inner.Enqueue(MakeResult("anthropic", "/old"));
        inner.EnqueueFailure(new InvalidOperationException("NyxID unavailable"));
        inner.Enqueue(MakeResult("anthropic", "/new"));
        var refreshCompleted = inner.SignalOnCallCompletion(callNumber: 2);
        var retryRefreshCompleted = inner.SignalOnCallCompletion(callNumber: 3);
        var port = CreatePort(inner, timeProvider: clock);

        await port.GetServicesAsync("bearer-1", CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(61));

        var stale = await port.GetServicesAsync("bearer-1", CancellationToken.None);
        await refreshCompleted.Task;
        var stillStale = await port.GetServicesAsync("bearer-1", CancellationToken.None);
        await retryRefreshCompleted.Task;
        var refreshed = await port.GetServicesAsync("bearer-1", CancellationToken.None);

        stale.Services.Single().RouteValue.Should().Be("/old");
        stillStale.Services.Single().RouteValue.Should().Be("/old");
        refreshed.Services.Single().RouteValue.Should().Be("/new");
        inner.GetCalls.Should().Be(3);
    }

    [Fact]
    public async Task GetServicesAsync_ShouldFetchSynchronously_AndSurfaceFailure_AfterStaleWindowExpires()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-05-29T10:00:00Z"));
        var inner = new RecordingCatalogPort();
        inner.Enqueue(MakeResult("anthropic", "/old"));
        inner.EnqueueFailure(new InvalidOperationException("NyxID unavailable"));
        var port = CreatePort(inner, timeProvider: clock);

        await port.GetServicesAsync("bearer-1", CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(7));

        var act = async () => await port.GetServicesAsync("bearer-1", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("NyxID unavailable");
        inner.GetCalls.Should().Be(2);
    }

    [Fact]
    public async Task ProvisionAsync_ShouldInvalidateOnlyCurrentCallerAuthoritySnapshot()
    {
        var inner = new RecordingCatalogPort();
        inner.Enqueue(MakeResult("anthropic", "/caller-a-old"));
        inner.Enqueue(MakeResult("anthropic", "/caller-b"));
        inner.Enqueue(MakeResult("anthropic", "/caller-a-new"));
        inner.ProvisionResult = MakeService("anthropic", "/provisioned");
        var port = CreatePort(inner);

        var callerAOld = await port.GetServicesAsync("bearer-A", CancellationToken.None);
        var callerB = await port.GetServicesAsync("bearer-B", CancellationToken.None);
        await port.ProvisionAsync("bearer-A", "anthropic", CancellationToken.None);
        var callerANew = await port.GetServicesAsync("bearer-A", CancellationToken.None);
        var callerBAfterProvision = await port.GetServicesAsync("bearer-B", CancellationToken.None);

        callerAOld.Services.Single().RouteValue.Should().Be("/caller-a-old");
        callerB.Services.Single().RouteValue.Should().Be("/caller-b");
        callerANew.Services.Single().RouteValue.Should().Be("/caller-a-new");
        callerBAfterProvision.Services.Single().RouteValue.Should().Be("/caller-b");
        inner.GetCalls.Should().Be(3);
        inner.ProvisionBearers.Should().Equal("bearer-A");
    }

    [Fact]
    public async Task ProvisionAsync_WhenCacheDisabled_ShouldCallInnerForRepeatedSameBearer()
    {
        var inner = new RecordingCatalogPort();
        inner.ProvisionResults.Enqueue(MakeService("anthropic", "/first"));
        inner.ProvisionResults.Enqueue(MakeService("anthropic", "/second"));
        inner.ProvisionResults.Enqueue(MakeService("anthropic", "/third"));
        var port = CreatePort(inner, cacheEnabled: false);

        var first = await port.ProvisionAsync("bearer-1", "anthropic", CancellationToken.None);
        var second = await port.ProvisionAsync("bearer-1", "anthropic", CancellationToken.None);
        var third = await port.ProvisionAsync("bearer-1", "anthropic", CancellationToken.None);

        first.RouteValue.Should().Be("/first");
        second.RouteValue.Should().Be("/second");
        third.RouteValue.Should().Be("/third");
        inner.ProvisionCalls.Should().Be(3);
        inner.ProvisionBearers.Should().Equal("bearer-1", "bearer-1", "bearer-1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-uri")]
    public async Task ProvisionAsync_WhenCacheDisabled_ShouldNotConsultAuthority(string? authority)
    {
        var inner = new RecordingCatalogPort();
        inner.ProvisionResult = MakeService("anthropic", "/provisioned");
        var port = CreatePort(
            inner,
            configuration: CreateConfiguration(authority),
            cacheEnabled: false);

        var result = await port.ProvisionAsync("bearer-1", "anthropic", CancellationToken.None);

        result.RouteValue.Should().Be("/provisioned");
        inner.ProvisionCalls.Should().Be(1);
    }

    private static CachedNyxIdLlmCatalogPort CreatePort(
        RecordingCatalogPort inner,
        IConfiguration? configuration = null,
        TimeProvider? timeProvider = null,
        bool cacheEnabled = true)
    {
        return new CachedNyxIdLlmCatalogPort(
            inner,
            configuration ?? CreateConfiguration("https://nyx.test/api/v1/llm/gateway/v1"),
            new FixedOptionsMonitor<NyxIdLlmCatalogCacheOptions>(new NyxIdLlmCatalogCacheOptions
            {
                Enabled = cacheEnabled,
                FreshTtl = TimeSpan.FromSeconds(60),
                StaleTtl = TimeSpan.FromMinutes(5),
                MaxEntries = 16,
            }),
            timeProvider ?? new ManualTimeProvider(DateTimeOffset.Parse("2026-05-29T10:00:00Z")),
            NullLogger<CachedNyxIdLlmCatalogPort>.Instance);
    }

    private static IConfiguration CreateConfiguration(string? authority)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:NyxId:Authority"] = authority,
            })
            .Build();
    }

    private static NyxIdLlmServicesResult MakeResult(string slug, string routeValue) =>
        new([MakeService(slug, routeValue)], null);

    private static NyxIdLlmService MakeService(string slug, string routeValue) =>
        new(
            CatalogEntryId: slug,
            ServiceSlug: slug,
            DisplayName: slug,
            RouteValue: routeValue,
            ModelCatalog: new LLMModelCatalog
            {
                Certainty = LLMModelCatalogCertainty.NotVerifiable,
                DiagnosticKind = LLMModelCatalogDiagnosticKind.NotPublished,
            },
            Status: "ready",
            Source: NyxIdLlmProviderSource.GatewayProvider,
            Allowed: true,
            Description: null);

    private sealed class RecordingCatalogPort : IUserLlmCatalogPort
    {
        private readonly Queue<Func<Task<NyxIdLlmServicesResult>>> _responses = new();
        private readonly Queue<Func<Task<NyxIdLlmServicesResult>>> _freshResponses = new();
        private readonly Dictionary<int, TaskCompletionSource> _callStartSignals = new();
        private readonly Dictionary<int, TaskCompletionSource> _callSignals = new();

        public int GetCalls { get; private set; }
        public int FreshCalls { get; private set; }

        public List<string> CapturedBearers { get; } = [];

        public List<string> ProvisionBearers { get; } = [];

        public int ProvisionCalls { get; private set; }

        public Queue<NyxIdLlmService> ProvisionResults { get; } = new();

        public NyxIdLlmService ProvisionResult { get; set; } = MakeService("provisioned", "/provisioned");

        public void Enqueue(NyxIdLlmServicesResult result) =>
            _responses.Enqueue(() => Task.FromResult(result));

        public void EnqueueFresh(NyxIdLlmServicesResult result) =>
            _freshResponses.Enqueue(() => Task.FromResult(result));

        public void EnqueueAfter(Task gate, NyxIdLlmServicesResult result) =>
            _responses.Enqueue(async () =>
            {
                await gate.ConfigureAwait(false);
                return result;
            });

        public void EnqueueFailure(Exception exception) =>
            _responses.Enqueue(() => Task.FromException<NyxIdLlmServicesResult>(exception));

        public TaskCompletionSource SignalOnCallStart(int callNumber)
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _callStartSignals.Add(callNumber, source);
            return source;
        }

        public TaskCompletionSource SignalOnCallCompletion(int callNumber)
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _callSignals.Add(callNumber, source);
            return source;
        }

        public async Task<NyxIdLlmServicesResult> GetServicesAsync(string bearerToken, CancellationToken ct)
        {
            GetCalls++;
            var callNumber = GetCalls;
            CapturedBearers.Add(bearerToken);
            if (_callStartSignals.TryGetValue(callNumber, out var startSignal))
                startSignal.SetResult();

            if (_responses.Count == 0)
                throw new InvalidOperationException("No catalog response queued.");

            try
            {
                return await _responses.Dequeue().Invoke();
            }
            finally
            {
                if (_callSignals.TryGetValue(callNumber, out var signal))
                    signal.SetResult();
            }
        }

        public async Task<NyxIdLlmServicesResult> GetFreshServicesAsync(string bearerToken, CancellationToken ct)
        {
            FreshCalls++;
            CapturedBearers.Add(bearerToken);
            if (_freshResponses.Count == 0)
                throw new InvalidOperationException("No fresh catalog response queued.");

            return await _freshResponses.Dequeue().Invoke();
        }

        public Task<NyxIdLlmService> ProvisionAsync(
            string bearerToken,
            string provisionEndpointId,
            CancellationToken ct)
        {
            ProvisionCalls++;
            ProvisionBearers.Add(bearerToken);
            var service = ProvisionResults.Count > 0
                ? ProvisionResults.Dequeue()
                : ProvisionResult;
            return Task.FromResult(service);
        }
    }

    private sealed class FixedOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
