using System.Diagnostics.Metrics;
using System.Net;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.Tests;

public class NyxIdSpecCatalogTests
{
    [Fact]
    public void Constructor_NoBaseUrl_DoesNotFetch()
    {
        var handler = new FakeHttpHandler();
        var http = new HttpClient(handler);
        var options = new NyxIdToolOptions { BaseUrl = null, SpecFetchToken = "ignored" };

        using var catalog = new NyxIdSpecCatalog(options, http);

        handler.RequestCount.Should().Be(0);
        catalog.Operations.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_BaseUrlWithoutSpecFetchToken_SkipsBackgroundRefresh()
    {
        // Regression guard: NyxID's /api/v1/docs/openapi.json is human-only and
        // returns 401 without a real user's API key. A configured BaseUrl alone
        // must not trigger a fetch — otherwise prod logs fill with 30-min 401s.
        var handler = new FakeHttpHandler();
        var http = new HttpClient(handler);
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test", SpecFetchToken = null };
        var logger = new RecordingLogger<NyxIdSpecCatalog>();

        using var catalog = new NyxIdSpecCatalog(options, http, logger);

        handler.RequestCount.Should().Be(0);
        catalog.Operations.Should().BeEmpty();
        catalog.GetStatus().Should().BeEquivalentTo(new NyxIdSpecCatalogStatus(
            BaseUrlConfigured: true,
            SpecFetchTokenConfigured: false,
            OperationCount: 0,
            LastSuccessfulRefreshUtc: null,
            LastRefreshError: null));
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("SpecFetchToken not configured", StringComparison.Ordinal));
    }

    [Fact]
    public void Constructor_WhitespaceSpecFetchToken_TreatedAsMissing()
    {
        var handler = new FakeHttpHandler();
        var http = new HttpClient(handler);
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test", SpecFetchToken = "   " };

        using var catalog = new NyxIdSpecCatalog(options, http);

        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task Constructor_BaseUrlAndSpecFetchToken_FetchesWithBearer()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "paths": {
                "/things": {
                  "get": { "operationId": "list_things", "summary": "List things" }
                }
              }
            }
            """;
        var handler = new FakeHttpHandler(spec);
        var http = new HttpClient(handler);
        var options = new NyxIdToolOptions
        {
            BaseUrl = "https://nyx.test",
            SpecFetchToken = "user-api-key-xyz",
        };

        using var catalog = new NyxIdSpecCatalog(options, http);

        await handler.FirstRequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));

        handler.LastRequestUri.Should().Be("https://nyx.test/api/v1/docs/openapi.json");
        handler.LastAuthHeader.Should().Be("Bearer user-api-key-xyz");
    }

    [Fact]
    public async Task Constructor_BaseUrlAndSpecFetchToken_ReportsSuccessfulRefreshStatus()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "paths": {
                "/things": {
                  "get": { "operationId": "list_things", "summary": "List things" }
                }
              }
            }
            """;
        var handler = new FakeHttpHandler(spec);
        var http = new HttpClient(handler);
        var logger = new RecordingLogger<NyxIdSpecCatalog>();
        var options = new NyxIdToolOptions
        {
            BaseUrl = "https://nyx.test",
            SpecFetchToken = "user-api-key-xyz",
        };

        using var catalog = new NyxIdSpecCatalog(options, http, logger);

        await logger.WaitForEntryAsync(
            entry => entry.Message.Contains("NyxIdSpecCatalog updated", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2));

        var status = catalog.GetStatus();
        status.BaseUrlConfigured.Should().BeTrue();
        status.SpecFetchTokenConfigured.Should().BeTrue();
        status.OperationCount.Should().Be(1);
        status.LastSuccessfulRefreshUtc.Should().NotBeNull();
        status.LastRefreshError.Should().BeNull();
    }

    [Fact]
    public async Task ProxyExecute_OperationMiss_ShouldRecordLookupMissMetric()
    {
        using var metricCapture = new NyxIdMetricCapture();
        var options = new NyxIdToolOptions { BaseUrl = null };
        using var catalog = new NyxIdSpecCatalog(options, new HttpClient(new FakeHttpHandler()));
        var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.test" });
        var tool = new NyxIdProxyExecuteTool(catalog, client);

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "token-123",
        };

        try
        {
            var result = await tool.ExecuteAsync("""{"operation_id":"missing_operation"}""");

            result.Should().Contain("Operation 'missing_operation' not found");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }

        metricCapture.LookupMisses.Should().ContainSingle(measurement =>
            measurement.Value == 1 &&
            measurement.Tags.Any(tag =>
                tag.Key == "operation_id" &&
                string.Equals(tag.Value as string, "missing_operation", StringComparison.Ordinal)));
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly string? _responseBody;
        private readonly HttpStatusCode _statusCode;

        public int RequestCount { get; private set; }
        public string? LastRequestUri { get; private set; }
        public string? LastAuthHeader { get; private set; }
        public TaskCompletionSource<bool> FirstRequestReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeHttpHandler(string? responseBody = null, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseBody = responseBody;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri?.ToString();
            LastAuthHeader = request.Headers.Authorization?.ToString();
            FirstRequestReceived.TrySetResult(true);

            var response = new HttpResponseMessage(_statusCode);
            if (_responseBody is not null)
                response.Content = new StringContent(_responseBody, System.Text.Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<LogEntry> _entries = [];
        private readonly object _gate = new();
        private readonly List<Waiter> _waiters = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_gate)
                    return _entries.ToArray();
            }
        }

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var entry = new LogEntry(logLevel, formatter(state, exception));
            List<Waiter> matched;

            lock (_gate)
            {
                _entries.Add(entry);
                matched = _waiters.Where(waiter => waiter.Predicate(entry)).ToList();
                foreach (var waiter in matched)
                    _waiters.Remove(waiter);
            }

            foreach (var waiter in matched)
                waiter.Completion.TrySetResult(entry);
        }

        public Task<LogEntry> WaitForEntryAsync(
            Func<LogEntry, bool> predicate,
            TimeSpan timeout)
        {
            lock (_gate)
            {
                var existing = _entries.FirstOrDefault(predicate);
                if (existing != null)
                    return Task.FromResult(existing);

                var waiter = new Waiter(predicate);
                _waiters.Add(waiter);
                return waiter.Completion.Task.WaitAsync(timeout);
            }
        }

        private sealed class Waiter(Func<LogEntry, bool> predicate)
        {
            public Func<LogEntry, bool> Predicate { get; } = predicate;
            public TaskCompletionSource<LogEntry> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose()
            {
            }
        }
    }

    private sealed class NyxIdMetricCapture : IDisposable
    {
        private readonly MeterListener _listener = new();

        public NyxIdMetricCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == NyxIdToolProviderMetrics.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            {
                if (instrument.Name == NyxIdToolProviderMetrics.SpecCatalogLookupMissTotal)
                    LookupMisses.Add(new MetricMeasurement(measurement, tags.ToArray()));
            });
            _listener.Start();
        }

        public List<MetricMeasurement> LookupMisses { get; } = [];

        public void Dispose() => _listener.Dispose();
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed record MetricMeasurement(long Value, KeyValuePair<string, object?>[] Tags);
}
