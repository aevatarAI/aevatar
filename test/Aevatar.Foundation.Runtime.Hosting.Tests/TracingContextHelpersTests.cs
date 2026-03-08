using System.Diagnostics;
using Aevatar.Foundation.Abstractions.Propagation;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Observability;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class TracingContextHelpersTests
{
    [Fact]
    public void CreateLogScopeState_ShouldResolveThreeKeys()
    {
        var envelope = new EventEnvelope
        {
            CorrelationId = "corr-1",
        };
        envelope.Metadata[EnvelopeMetadataKeys.TraceId] = "trace-1";
        envelope.Metadata[EnvelopeMetadataKeys.TraceCausationId] = "cause-1";

        var scope = TracingContextHelpers.CreateLogScopeState(envelope);

        scope["trace_id"].Should().Be("trace-1");
        scope["correlation_id"].Should().Be("corr-1");
        scope["causation_id"].Should().Be("cause-1");
    }

    [Fact]
    public void BeginEnvelopeScope_ShouldAttachThreeKeysToLogs()
    {
        var provider = new ScopeCaptureLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var logger = factory.CreateLogger("runtime-tracing-test");

        var envelope = new EventEnvelope
        {
            CorrelationId = "corr-2",
        };
        envelope.Metadata[EnvelopeMetadataKeys.TraceId] = "trace-2";
        envelope.Metadata[EnvelopeMetadataKeys.TraceCausationId] = "cause-2";

        using (TracingContextHelpers.BeginEnvelopeScope(logger, envelope))
        {
            logger.LogInformation("runtime log line");
        }

        provider.Entries.Should().ContainSingle();
        var fields = provider.Entries[0];
        fields["trace_id"].Should().Be("trace-2");
        fields["correlation_id"].Should().Be("corr-2");
        fields["causation_id"].Should().Be("cause-2");
    }

    [Fact]
    public void PopulateTraceId_ShouldPopulateTraceAndSpanMetadata_FromCurrentActivity()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Aevatar.Agents",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = AevatarActivitySource.Source.StartActivity("populate-test");
        activity.Should().NotBeNull();
        var envelope = new EventEnvelope();

        TracingContextHelpers.PopulateTraceId(envelope);

        envelope.Metadata[EnvelopeMetadataKeys.TraceId].Should().Be(activity!.TraceId.ToString());
        envelope.Metadata[EnvelopeMetadataKeys.TraceSpanId].Should().Be(activity.SpanId.ToString());
        envelope.Metadata[EnvelopeMetadataKeys.TraceFlags].Should().Be(((byte)activity.ActivityTraceFlags).ToString("x2"));
    }

    [Fact]
    public void EventHandleScope_ShouldCreateActivityAndAttachScope()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Aevatar.Agents",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        var provider = new ScopeCaptureLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var logger = factory.CreateLogger("runtime-tracing-test");
        var envelope = new EventEnvelope
        {
            Id = "evt-3",
            CorrelationId = "corr-3",
            Direction = EventDirection.Self,
            PublisherId = "publisher-3",
        };

        using var scope = EventHandleScope.Begin(logger, "agent-3", envelope);
        scope.Activity.Should().NotBeNull();
        scope.Activity!.GetTagItem("aevatar.agent.id").Should().Be("agent-3");
        logger.LogInformation("runtime log line");

        provider.Entries.Should().ContainSingle();
        var fields = provider.Entries[0];
        fields["correlation_id"].Should().Be("corr-3");
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
