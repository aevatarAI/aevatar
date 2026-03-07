using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.PrimitiveExecutors;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "ConnectorCallPrimitiveExecutor")]
public sealed class ConnectorCallPrimitiveExecutorCoverageTests
{
    [Fact]
    public async Task HandleAsync_WhenNonConnectorStep_ShouldNoop()
    {
        var module = new ConnectorCallPrimitiveExecutor(StaticConnectorCatalog.Empty);
        var ctx = CreateContext();
        var request = new StepRequestEvent
        {
            StepId = "s1",
            StepType = "llm_call",
            Input = "input",
        };

        await module.HandleAsync(request, ctx.CreatePrimitiveContext(), CancellationToken.None);

        ctx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenMissingConnectorParameter_ShouldFail()
    {
        var module = new ConnectorCallPrimitiveExecutor(StaticConnectorCatalog.Empty);
        var ctx = CreateContext();
        var request = new StepRequestEvent
        {
            StepId = "s-missing",
            StepType = "connector_call",
            Input = "input",
        };

        await module.HandleAsync(request, ctx.CreatePrimitiveContext(), CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("missing required parameter: connector");
    }

    [Fact]
    public async Task HandleAsync_WhenConnectorMissingAndOptionalYes_ShouldSkip()
    {
        var module = new ConnectorCallPrimitiveExecutor(StaticConnectorCatalog.Empty);
        var ctx = CreateContext();
        var request = new StepRequestEvent
        {
            StepId = "s-skip",
            StepType = "connector_call",
            Input = "payload",
            Parameters =
            {
                ["connector"] = "missing",
                ["optional"] = "yes",
            },
        };

        await module.HandleAsync(request, ctx.CreatePrimitiveContext(), CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("payload");
        completed.Metadata["connector.skipped"].Should().Be("true");
        completed.Metadata["connector.skip_reason"].Should().Be("connector_not_found");
    }

    [Fact]
    public async Task HandleAsync_WhenFirstAttemptThrowsAndRetrySucceeds_ShouldPublishSuccess()
    {
        var connector = new ThrowThenSuccessConnector("retryable");
        var module = new ConnectorCallPrimitiveExecutor(new StaticConnectorCatalog([connector]));
        var ctx = CreateContext();
        var request = new StepRequestEvent
        {
            StepId = "s-retry",
            StepType = "connector_call",
            RunId = "corr-1",
            Input = "in",
            Parameters =
            {
                ["connector"] = "retryable",
                ["operation"] = "op",
                ["retry"] = "1",
            },
        };

        await module.HandleAsync(request, ctx.CreatePrimitiveContext(), CancellationToken.None);

        connector.Attempts.Should().Be(2);
        connector.LastRequest.Should().NotBeNull();
        connector.LastRequest!.RunId.Should().Be("corr-1");
        connector.LastRequest.StepId.Should().Be("s-retry");

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("ok");
        completed.Metadata["connector.attempts"].Should().Be("2");
        completed.Metadata["connector.name"].Should().Be("retryable");
    }

    [Fact]
    public async Task HandleAsync_WhenTimeoutAndContinue_ShouldKeepInput()
    {
        var module = new ConnectorCallPrimitiveExecutor(new StaticConnectorCatalog([
            new DelayConnector("slow"),
        ]));
        var ctx = CreateContext();
        var request = new StepRequestEvent
        {
            StepId = "s-timeout",
            StepType = "connector_call",
            Input = "original",
            Parameters =
            {
                ["connector"] = "slow",
                ["timeout_ms"] = "1",
                ["on_error"] = "continue",
            },
        };

        await module.HandleAsync(request, ctx.CreatePrimitiveContext(), CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("original");
        completed.Metadata["connector.continued_on_error"].Should().Be("true");
        completed.Metadata["connector.timeout_ms"].Should().Be("100");
        completed.Metadata.Should().ContainKey("connector.error");
    }

    private static TestEventHandlerContext CreateContext()
    {
        return new TestEventHandlerContext(
            new ServiceCollection().BuildServiceProvider(),
            new TestAgent("connector-module-test-agent"),
            NullLogger.Instance);
    }

    private sealed class ThrowThenSuccessConnector(string name) : IConnector
    {
        public int Attempts { get; private set; }
        public ConnectorRequest? LastRequest { get; private set; }

        public string Name { get; } = name;
        public string Type => "test";

        public Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
        {
            Attempts++;
            LastRequest = request;
            if (Attempts == 1)
                throw new InvalidOperationException("transient failure");

            return Task.FromResult(new ConnectorResponse
            {
                Success = true,
                Output = "ok",
            });
        }
    }

    private sealed class DelayConnector(string name) : IConnector
    {
        public string Name { get; } = name;
        public string Type => "test";

        public async Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
        {
            _ = request;
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await pending.Task.WaitAsync(ct);
            return new ConnectorResponse
            {
                Success = true,
                Output = "late",
            };
        }
    }

}
