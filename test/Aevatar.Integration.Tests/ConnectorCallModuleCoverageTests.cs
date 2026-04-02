using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Connectors;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "ConnectorCallModule")]
public sealed class ConnectorCallModuleCoverageTests
{
    [Fact]
    public async Task HandleAsync_WhenNonConnectorStep_ShouldNoop()
    {
        var module = new ConnectorCallModule(new RegistryBackedWorkflowConnectorResolver(new ConfiguredConnectorRegistry()));
        var ctx = CreateContext();
        var request = new StepRequestEvent
        {
            StepId = "s1",
            StepType = "llm_call",
            Input = "input",
        };

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        ctx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenMissingConnectorParameter_ShouldFail()
    {
        var module = new ConnectorCallModule(new RegistryBackedWorkflowConnectorResolver(new ConfiguredConnectorRegistry()));
        var ctx = CreateContext();
        var request = new StepRequestEvent
        {
            StepId = "s-missing",
            StepType = "connector_call",
            Input = "input",
        };

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("missing required parameter: connector");
    }

    [Fact]
    public async Task HandleAsync_WhenConnectorMissingAndOptionalYes_ShouldSkip()
    {
        var module = new ConnectorCallModule(new RegistryBackedWorkflowConnectorResolver(new ConfiguredConnectorRegistry()));
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

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("payload");
        completed.Annotations["connector.skipped"].Should().Be("true");
        completed.Annotations["connector.skip_reason"].Should().Be("connector_not_found");
    }

    [Fact]
    public async Task HandleAsync_WhenFirstAttemptThrowsAndRetrySucceeds_ShouldPublishSuccess()
    {
        var registry = new ConfiguredConnectorRegistry();
        var connector = new ThrowThenSuccessConnector("retryable");
        registry.Register(connector);

        var module = new ConnectorCallModule(new RegistryBackedWorkflowConnectorResolver(registry));
        var ctx = CreateContext();
        var request = new StepRequestEvent
        {
            StepId = "s-retry",
            StepType = "connector_call",
            Input = "in",
            Parameters =
            {
                ["connector"] = "retryable",
                ["operation"] = "op",
                ["retry"] = "1",
            },
        };

        await module.HandleAsync(Envelope(request, correlationId: "corr-1"), ctx, CancellationToken.None);

        connector.Attempts.Should().Be(2);
        connector.LastRequest.Should().NotBeNull();
        connector.LastRequest!.RunId.Should().Be("corr-1");
        connector.LastRequest.StepId.Should().Be("s-retry");

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("ok");
        completed.Annotations["connector.attempts"].Should().Be("2");
        completed.Annotations["connector.name"].Should().Be("retryable");
    }

    [Fact]
    public async Task HandleAsync_WhenTimeoutAndContinue_ShouldKeepInput()
    {
        var registry = new ConfiguredConnectorRegistry();
        registry.Register(new DelayConnector("slow"));
        var module = new ConnectorCallModule(new RegistryBackedWorkflowConnectorResolver(registry));
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

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("original");
        completed.Annotations["connector.continued_on_error"].Should().Be("true");
        completed.Annotations["connector.timeout_ms"].Should().Be("100");
        completed.Annotations.Should().ContainKey("connector.error");
    }

    [Fact]
    public async Task HandleAsync_WhenSecureConnectorCallUsesTemplateDefault_ShouldResolveCapturedSecret()
    {
        var registry = new ConfiguredConnectorRegistry();
        var connector = new EchoConnector("secure");
        registry.Register(connector);
        var module = new ConnectorCallModule(new RegistryBackedWorkflowConnectorResolver(registry));
        var agent = new TestWorkflowRunAgent("connector-module-test-agent", "run-secure");
        var services = new ServiceCollection().BuildServiceProvider();
        var seedCtx = new TestEventHandlerContext(services, agent, NullLogger.Instance);

        await module.HandleAsync(
            Envelope(new SecureValueCapturedEvent
            {
                RunId = "run-secure",
                StepId = "capture-secret",
                Variable = "api_key",
                Value = "sk-secure",
            }),
            seedCtx,
            CancellationToken.None);

        var ctx = new TestEventHandlerContext(services, agent, NullLogger.Instance);

        var request = new StepRequestEvent
        {
            StepId = "s-secure",
            RunId = "run-secure",
            StepType = "secure_connector_call",
            Input = """{"providerName":"demo"}""",
            Parameters =
            {
                ["connector"] = "secure",
                ["stdin_template"] = """{"providerName":"demo","apiKey":"[[secure:api_key]]"}""",
            },
        };

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        connector.LastRequest.Should().NotBeNull();
        connector.LastRequest!.Payload.Should().Be("""{"providerName":"demo","apiKey":"sk-secure"}""");

        var completed = ctx.Published.Last().evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("ok");
        completed.Output.Should().NotContain("sk-secure");
        completed.Annotations.Values.Should().NotContain(value => value.Contains("sk-secure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleAsync_WhenSecureJsonPlaceholderUsed_ShouldEscapeSecretForJsonString()
    {
        var registry = new ConfiguredConnectorRegistry();
        var connector = new EchoConnector("secure-json");
        registry.Register(connector);
        var module = new ConnectorCallModule(new RegistryBackedWorkflowConnectorResolver(registry));
        var agent = new TestWorkflowRunAgent("connector-module-test-agent-json", "run-secure-json");
        var services = new ServiceCollection().BuildServiceProvider();
        var seedCtx = new TestEventHandlerContext(services, agent, NullLogger.Instance);

        await module.HandleAsync(
            Envelope(new SecureValueCapturedEvent
            {
                RunId = "run-secure-json",
                StepId = "capture-secret",
                Variable = "api_key",
                Value = "sk-\"line\ntwo",
            }),
            seedCtx,
            CancellationToken.None);

        var ctx = new TestEventHandlerContext(services, agent, NullLogger.Instance);

        var request = new StepRequestEvent
        {
            StepId = "s-secure-json",
            RunId = "run-secure-json",
            StepType = "secure_connector_call",
            Parameters =
            {
                ["connector"] = "secure-json",
                ["stdin_template"] = """{"providerName":"demo","apiKey":"[[secure_json:api_key]]"}""",
            },
        };

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        connector.LastRequest.Should().NotBeNull();
        connector.LastRequest!.Payload.Should().Be("""{"providerName":"demo","apiKey":"sk-\"line\ntwo"}""");
    }

    [Fact]
    public async Task HandleAsync_WhenAssertResponsePathPassesAndPassThroughEnabled_ShouldKeepOriginalInput()
    {
        var registry = new ConfiguredConnectorRegistry();
        registry.Register(new FixedResponseConnector("validator", """{"valid":true}"""));
        var module = new ConnectorCallModule(new RegistryBackedWorkflowConnectorResolver(registry));
        var ctx = CreateContext();
        var request = new StepRequestEvent
        {
            StepId = "s-assert-pass",
            StepType = "connector_call",
            Input = """{"nodes":[{"temp_id":"new_0"}]}""",
            Parameters =
            {
                ["connector"] = "validator",
                ["assert_response_path"] = "valid",
                ["pass_through_input"] = "true",
            },
        };

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be(request.Input);
    }

    [Fact]
    public async Task HandleAsync_WhenAssertResponsePathFails_ShouldPublishFailure()
    {
        var registry = new ConfiguredConnectorRegistry();
        registry.Register(new FixedResponseConnector("validator", """{"valid":false}"""));
        var module = new ConnectorCallModule(new RegistryBackedWorkflowConnectorResolver(registry));
        var ctx = CreateContext();
        var request = new StepRequestEvent
        {
            StepId = "s-assert-fail",
            StepType = "connector_call",
            Input = """{"nodes":[{"temp_id":"new_0"}]}""",
            Parameters =
            {
                ["connector"] = "validator",
                ["assert_response_path"] = "valid",
            },
        };

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("assertion failed");
        completed.Error.Should().Contain("valid");
    }

    private static TestEventHandlerContext CreateContext()
    {
        return new TestEventHandlerContext(
            new ServiceCollection().BuildServiceProvider(),
            new TestAgent("connector-module-test-agent"),
            NullLogger.Instance);
    }

    private static EventEnvelope Envelope(IMessage evt, string? correlationId = null)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test-publisher", TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = correlationId ?? string.Empty,
            },
        };
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

    private sealed class EchoConnector(string name) : IConnector
    {
        public string Name { get; } = name;
        public string Type => "test";
        public ConnectorRequest? LastRequest { get; private set; }

        public Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new ConnectorResponse
            {
                Success = true,
                Output = "ok",
            });
        }
    }

    private sealed class FixedResponseConnector(string name, string output) : IConnector
    {
        public string Name { get; } = name;
        public string Type => "test";

        public Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
        {
            _ = request;
            _ = ct;
            return Task.FromResult(new ConnectorResponse
            {
                Success = true,
                Output = output,
            });
        }
    }
}
