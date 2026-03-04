using System.Diagnostics;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class CapabilityTraceContextTests
{
    [Fact]
    public void BeginApiScope_ShouldAttachThreeKeysToLogs()
    {
        var provider = new ScopeCaptureLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var logger = loggerFactory.CreateLogger("api-tracing-test");

        using (CapabilityTraceContext.BeginApiScope(logger, correlationId: "corr-api", causationId: "cause-api"))
        {
            logger.LogInformation("api log line");
        }

        provider.Entries.Should().ContainSingle();
        var fields = provider.Entries[0];
        fields["trace_id"].Should().NotBeNull();
        fields["correlation_id"].Should().Be("corr-api");
        fields["causation_id"].Should().Be("cause-api");
    }

    [Fact]
    public void BeginApiScope_WhenLoggerIsNull_ShouldReturnNull()
    {
        var scope = CapabilityTraceContext.BeginApiScope(null, "corr", "cause");
        scope.Should().BeNull();
    }

    [Fact]
    public void CurrentTraceId_ShouldReflectActivityCurrent()
    {
        using var activity = new Activity("capability-trace-test").Start();
        CapabilityTraceContext.CurrentTraceId().Should().Be(activity.TraceId.ToString());
    }

    private sealed class ScopeCaptureLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();
        public List<Dictionary<string, object?>> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new ScopeCaptureLogger(this);
        public void Dispose() { }
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

        private sealed class ScopeCaptureLogger(ScopeCaptureLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
                owner._scopeProvider.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
                owner._scopeProvider.ForEachScope((scope, dict) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                    {
                        foreach (var (key, value) in pairs)
                            dict[key] = value;
                    }
                }, fields);
                owner.Entries.Add(fields);
            }
        }
    }
}
