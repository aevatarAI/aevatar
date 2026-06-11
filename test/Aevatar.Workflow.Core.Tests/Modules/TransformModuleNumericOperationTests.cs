using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using System.Text.Json;

namespace Aevatar.Workflow.Core.Tests.Modules;

public sealed class TransformModuleNumericOperationTests
{
    [Theory]
    [InlineData("sum", "0.10,0.20,0.30", "0.6")]
    [InlineData("subtract", "10.50,0.25,0.25", "10")]
    [InlineData("multiply", "1.20,3.00", "3.6")]
    [InlineData("divide", "10.5,2.1", "5")]
    [InlineData("min", "4.20,4.10,4.30", "4.1")]
    [InlineData("max", "4.20,4.10,4.30", "4.3")]
    public async Task HandleAsync_WhenNumericScalarOpRequested_ShouldUseDecimalResult(
        string op,
        string input,
        string expectedOutput)
    {
        var completed = await ExecuteAsync(input, new Dictionary<string, string>
        {
            ["op"] = op,
        });

        completed.Success.Should().BeTrue();
        decimal.Parse(completed.Output, CultureInfo.InvariantCulture).Should().Be(
            decimal.Parse(expectedOutput, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task HandleAsync_WhenValuesParameterProvided_ShouldPreferItForNumericOperations()
    {
        var completed = await ExecuteAsync("not numeric input", new Dictionary<string, string>
        {
            ["op"] = "sum",
            ["values"] = """["1.10","2.20",3.30]""",
        });

        decimal.Parse(completed.Output, CultureInfo.InvariantCulture).Should().Be(6.6m);
    }

    [Fact]
    public async Task HandleAsync_WhenRoundRequested_ShouldUseAwayFromZeroDecimalRounding()
    {
        var completed = await ExecuteAsync("2.345", new Dictionary<string, string>
        {
            ["op"] = "round",
            ["digits"] = "2",
        });

        completed.Output.Should().Be("2.35");
    }

    [Fact]
    public async Task HandleAsync_WhenGroupByRequested_ShouldAggregateJsonArrayByDecimalField()
    {
        var input =
            """
            [
              { "currency": "USD", "amount": "1.10" },
              { "currency": "EUR", "amount": 2.05 },
              { "currency": "USD", "amount": "3.20" }
            ]
            """;

        var completed = await ExecuteAsync(input, new Dictionary<string, string>
        {
            ["op"] = "group_by",
            ["group_by"] = "currency",
            ["field"] = "amount",
            ["aggregate"] = "sum",
            ["precision"] = "2",
        });

        completed.Success.Should().BeTrue();
        using var output = JsonDocument.Parse(completed.Output);
        output.RootElement.GetArrayLength().Should().Be(2);
        output.RootElement[0].GetProperty("key").GetString().Should().Be("EUR");
        output.RootElement[0].GetProperty("value").GetDecimal().Should().Be(2.05m);
        output.RootElement[1].GetProperty("key").GetString().Should().Be("USD");
        output.RootElement[1].GetProperty("value").GetDecimal().Should().Be(4.30m);
    }

    [Fact]
    public async Task HandleAsync_WhenNumericOperationFails_ShouldKeepExplicitTransformErrorContract()
    {
        var completed = await ExecuteAsync("10,0", new Dictionary<string, string>
        {
            ["op"] = "divide",
        });

        completed.Success.Should().BeFalse();
        completed.Output.Should().BeEmpty();
        completed.Error.Should().NotBeNullOrWhiteSpace();
        completed.Error.Should().Contain("divide cannot divide by zero");
    }

    [Fact]
    public async Task HandleAsync_WhenOperationUnknown_ShouldReturnInput()
    {
        var completed = await ExecuteAsync("raw", new Dictionary<string, string>
        {
            ["op"] = "unknown_numeric",
        });

        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("raw");
    }

    private static async Task<StepCompletedEvent> ExecuteAsync(
        string input,
        IReadOnlyDictionary<string, string> parameters)
    {
        var module = new TransformModule();
        var context = new RecordingWorkflowContext();
        var request = new StepRequestEvent
        {
            StepId = "numeric-transform",
            StepType = "transform",
            RunId = "run-1",
            Input = input,
        };
        foreach (var (key, value) in parameters)
            request.Parameters.Add(key, value);

        await module.HandleAsync(Envelope(request), context, CancellationToken.None);

        return context.Published
            .Select(item => item.Event)
            .OfType<StepCompletedEvent>()
            .Should()
            .ContainSingle()
            .Subject;
    }

    private static EventEnvelope Envelope(IMessage message) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(message),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
        };

    private sealed class RecordingWorkflowContext : IWorkflowExecutionContext
    {
        public EventEnvelope InboundEnvelope { get; } = new();

        public string AgentId => "agent-1";

        public string RunId => "run-1";

        public IServiceProvider Services => EmptyServiceProvider.Instance;

        public ILogger Logger { get; } = NullLogger.Instance;

        public List<(IMessage Event, TopologyAudience Direction)> Published { get; } = [];

        public TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new() =>
            new();

        public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new() =>
            [];

        public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
            where TState : class, IMessage<TState>
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task ClearStateAsync(string scopeKey, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            _ = options;
            Published.Add((evt, audience));
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(System.Type serviceType) => null;
    }
}
