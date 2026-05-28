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
    public async Task GetServicesAsync_ShouldKeepStaleSnapshot_WhenRefreshFails()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-05-29T10:00:00Z"));
        var inner = new RecordingCatalogPort();
        inner.Enqueue(MakeResult("anthropic", "/old"));
        inner.EnqueueFailure(new InvalidOperationException("NyxID unavailable"));
        var refreshCompleted = inner.SignalOnCallCompletion(callNumber: 2);
        var port = CreatePort(inner, timeProvider: clock);

        await port.GetServicesAsync("bearer-1", CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(61));

        var stale = await port.GetServicesAsync("bearer-1", CancellationToken.None);
        await refreshCompleted.Task;
        var stillStale = await port.GetServicesAsync("bearer-1", CancellationToken.None);

        stale.Services.Single().RouteValue.Should().Be("/old");
        stillStale.Services.Single().RouteValue.Should().Be("/old");
        inner.GetCalls.Should().Be(2);
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

    private static CachedNyxIdLlmCatalogPort CreatePort(
        RecordingCatalogPort inner,
        IConfiguration? configuration = null,
        TimeProvider? timeProvider = null)
    {
        return new CachedNyxIdLlmCatalogPort(
            inner,
            configuration ?? CreateConfiguration("https://nyx.test/api/v1/llm/gateway/v1"),
            new FixedOptionsMonitor<NyxIdLlmCatalogCacheOptions>(new NyxIdLlmCatalogCacheOptions
            {
                FreshTtl = TimeSpan.FromSeconds(60),
                StaleTtl = TimeSpan.FromMinutes(5),
                MaxEntries = 16,
            }),
            timeProvider ?? new ManualTimeProvider(DateTimeOffset.Parse("2026-05-29T10:00:00Z")),
            NullLogger<CachedNyxIdLlmCatalogPort>.Instance);
    }

    private static IConfiguration CreateConfiguration(string authority)
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
            UserServiceId: slug,
            ServiceSlug: slug,
            DisplayName: slug,
            RouteValue: routeValue,
            DefaultModel: null,
            Models: [],
            Status: "ready",
            Source: NyxIdLlmProviderSource.GatewayProvider,
            Allowed: true,
            Description: null);

    private sealed class RecordingCatalogPort : IUserLlmCatalogPort
    {
        private readonly Queue<Func<Task<NyxIdLlmServicesResult>>> _responses = new();
        private readonly Dictionary<int, TaskCompletionSource> _callSignals = new();

        public int GetCalls { get; private set; }

        public List<string> CapturedBearers { get; } = [];

        public List<string> ProvisionBearers { get; } = [];

        public NyxIdLlmService ProvisionResult { get; set; } = MakeService("provisioned", "/provisioned");

        public void Enqueue(NyxIdLlmServicesResult result) =>
            _responses.Enqueue(() => Task.FromResult(result));

        public void EnqueueFailure(Exception exception) =>
            _responses.Enqueue(() => Task.FromException<NyxIdLlmServicesResult>(exception));

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

        public Task<NyxIdLlmService> ProvisionAsync(
            string bearerToken,
            string provisionEndpointId,
            CancellationToken ct)
        {
            ProvisionBearers.Add(bearerToken);
            return Task.FromResult(ProvisionResult);
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
