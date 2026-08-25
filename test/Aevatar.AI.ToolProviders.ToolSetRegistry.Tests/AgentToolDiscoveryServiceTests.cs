using Aevatar.AI.Abstractions.ToolProviders;
using FluentAssertions;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Aevatar.AI.ToolProviders.ToolSetRegistry.Tests;

public sealed class AgentToolDiscoveryServiceTests
{
    private static readonly AgentToolExecutionContext Context = AgentToolExecutionContext.Empty with
    {
        Caller = new AgentToolCallerContext("scope-1", "owner-1", "response-1"),
    };

    [Fact]
    public async Task DiscoverAsync_ShouldPublishTypedContextAndReturnDeterministicExactTools()
    {
        var source = new CapturingSource([
            new StaticTool("zeta"),
            new StaticTool("Alpha"),
        ]);

        var result = await AgentToolDiscoveryService.Instance.DiscoverAsync([source], Context);

        result.IsSuccess.Should().BeTrue();
        result.Tools.Select(static tool => tool.Name).Should().Equal("Alpha", "zeta");
        source.CapturedScopeId.Should().Be("scope-1");
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task DiscoverAsync_ShouldAllowTheSameExactObjectFromRepeatedTopologyIncludes()
    {
        var tool = new StaticTool("read_state");

        var result = await AgentToolDiscoveryService.Instance.DiscoverAsync(
            [new StaticSource([tool]), new StaticSource([tool])],
            Context);

        result.IsSuccess.Should().BeTrue();
        result.Tools.Should().ContainSingle().Which.Should().BeSameAs(tool);
    }

    [Fact]
    public async Task DiscoverAsync_ShouldFailClosedOnCaseInsensitiveDifferentObjectCollision()
    {
        var result = await AgentToolDiscoveryService.Instance.DiscoverAsync(
            [
                new StaticSource([new StaticTool("web_search")]),
                new StaticSource([new StaticTool("Web_Search")]),
            ],
            Context);

        result.IsSuccess.Should().BeFalse();
        result.Tools.Should().BeEmpty();
        result.Failure.Should().NotBeNull();
        result.Failure!.Code.Should().Be(AgentToolDiscoveryFailureCode.ToolNameCollision);
        result.Failure.ToolName.Should().Be("web_search");
    }

    [Fact]
    public async Task DiscoverAsync_ShouldReturnTypedSourceFailureWithoutLeakingExceptionText()
    {
        var result = await AgentToolDiscoveryService.Instance.DiscoverAsync(
            [new FaultingSource()],
            Context);

        result.IsSuccess.Should().BeFalse();
        result.Failure!.Code.Should().Be(AgentToolDiscoveryFailureCode.SourceFailed);
        result.Failure.Detail.Should().Contain(nameof(InvalidOperationException));
        result.Failure.Detail.Should().NotContain("secret-value");
    }

    [Fact]
    public async Task DiscoverAsync_ShouldPropagateCallerCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => AgentToolDiscoveryService.Instance.DiscoverAsync(
            [new CancellationSource()],
            Context,
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DiscoverAsync_ShouldEmitRegisteredDiscoveredAndRejectedTelemetry()
    {
        var measurements = new ConcurrentDictionary<string, ConcurrentBag<long>>(StringComparer.Ordinal);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (string.Equals(
                    instrument.Meter.Name,
                    AgentTurnToolCatalogTelemetry.MeterName,
                    StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            measurements.GetOrAdd(instrument.Name, static _ => []).Add(measurement));
        listener.Start();

        var result = await AgentToolDiscoveryService.Instance.DiscoverAsync(
            [
                new StaticSource([new StaticTool("read_state")]),
                new StaticSource([new StaticTool("READ_STATE")]),
            ],
            Context);

        result.IsSuccess.Should().BeFalse();
        measurements[AgentTurnToolCatalogTelemetry.RegisteredCounterName].Should().Contain(2);
        measurements[AgentTurnToolCatalogTelemetry.DiscoveredCounterName].Should().Contain(1);
        measurements[AgentTurnToolCatalogTelemetry.RejectedCounterName].Should().Contain(1);
    }

    [Fact]
    public void DurableCancellationContract_ShouldRequireAnExplicitImplementation()
    {
        var method = typeof(IAgentToolOperationCanceller).GetMethod(
            nameof(IAgentToolOperationCanceller.CancelOperationAsync));

        method.Should().NotBeNull();
        method!.IsAbstract.Should().BeTrue();
    }

    private sealed class CapturingSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public string? CapturedScopeId { get; private set; }

        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            CapturedScopeId = AgentToolRequestContext.ScopeId;
            return Task.FromResult(tools);
        }
    }

    private sealed class StaticSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult(tools);
    }

    private sealed class FaultingSource : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<IAgentTool>>(
                new InvalidOperationException("secret-value"));
    }

    private sealed class CancellationSource : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);
        }
    }

    private sealed class StaticTool(string name) : IAgentTool
    {
        public string Name { get; } = name;

        public string Description => Name;

        public string ParametersSchema => "{}";

        public bool IsReadOnly => true;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }

}
