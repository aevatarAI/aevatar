using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Connectors;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "ConnectorCallModuleHttpRequest")]
public sealed class ConnectorCallModuleHttpRequestTests
{
    [Fact]
    public async Task HandleAsync_WhenHttpRequestUsesTypedOptions_ShouldExecuteWithoutConnectorRegistry()
    {
        var executor = new RecordingOutboundHttpRequestExecutor(new OutboundHttpResponse
        {
            Success = true,
            Output = """{"ok":true}""",
            Metadata = new Dictionary<string, string>
            {
                ["connector.http.status_code"] = "200",
                ["connector.http.method"] = "GET",
                ["connector.http.url"] = "https://api.example.com/q1000",
            },
        });
        var module = new ConnectorCallModule(new RegistryBackedWorkflowConnectorResolver(new ConfiguredConnectorRegistry()));
        var ctx = CreateContext(executor, new StubCredentialProvider(("scope-secret:q1000-token", "sk-http-secret")));
        var request = new StepRequestEvent
        {
            StepId = "s-http",
            RunId = "run-http",
            StepType = "http_request",
            ExecutionId = "exec-http",
            IdempotencyKey = "idem-http",
            StepParameters = new WorkflowStepParameters
            {
                HttpRequest = new WorkflowHttpRequestOptions
                {
                    Method = "GET",
                    Url = "https://api.example.com/q1000",
                    TimeoutMs = 20_000,
                    MaxResponseBytes = 65_536,
                    MaxRedirects = 2,
                    Authentication = new WorkflowHttpRequestAuthentication
                    {
                        Scheme = "bearer",
                        SecretRef = "scope-secret:q1000-token",
                    },
                },
            },
        };
        request.StepParameters.HttpRequest.Headers["X-Trace"] = "trace-123";

        await HandleAndDrainAsync(module, Envelope(request), ctx);

        executor.Requests.Should().ContainSingle();
        var outbound = executor.Requests.Single();
        outbound.Method.Should().Be("GET");
        outbound.Url.Should().Be("https://api.example.com/q1000");
        outbound.Authorization.Should().Be("Bearer sk-http-secret");
        outbound.Headers.Should().Contain("X-Trace", "trace-123");
        outbound.Headers.Should().NotContainKey("Authorization");
        outbound.IdempotencyKey.Should().Be("idem-http");

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("""{"ok":true}""");
        completed.Error.Should().BeEmpty();
        completed.Annotations["connector.name"].Should().Be("http_request");
        completed.Annotations["connector.type"].Should().Be("http");
        completed.Annotations["connector.http.status_code"].Should().Be("200");
        completed.Annotations.Values.Should().NotContain(value => value.Contains("sk-http-secret", StringComparison.Ordinal));

        var state = ctx.LoadState<ConnectorCallModuleState>("connector_call");
        state.PendingByOperationId.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenHttpRequestUsesRawAuthorizationHeader_ShouldFailBeforeExecution()
    {
        var executor = new RecordingOutboundHttpRequestExecutor(new OutboundHttpResponse
        {
            Success = true,
            Output = """{"ok":true}""",
        });
        var module = new ConnectorCallModule(new RegistryBackedWorkflowConnectorResolver(new ConfiguredConnectorRegistry()));
        var ctx = CreateContext(executor, new StubCredentialProvider());
        var request = new StepRequestEvent
        {
            StepId = "s-raw-auth",
            RunId = "run-raw-auth",
            StepType = "http_request",
            StepParameters = new WorkflowStepParameters
            {
                HttpRequest = new WorkflowHttpRequestOptions
                {
                    Method = "GET",
                    Url = "https://api.example.com/q1000",
                },
            },
        };
        request.StepParameters.HttpRequest.Headers["Authorization"] = "Bearer raw-token";

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        executor.Requests.Should().BeEmpty();
        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeFalse();
        completed.Error.Should().Be("http_request authentication must use authentication.secret_ref");
        completed.Error.Should().NotContain("raw-token");
    }

    [Fact]
    public async Task HandleAsync_WhenHttpRequestRetries_ShouldPreservePrimitiveIdentityAndTypedOptions()
    {
        var executor = new RecordingOutboundHttpRequestExecutor(
            new OutboundHttpResponse
            {
                Success = false,
                Error = "503 Service Unavailable",
            },
            new OutboundHttpResponse
            {
                Success = true,
                Output = """{"ok":true}""",
            });
        var module = new ConnectorCallModule(new RegistryBackedWorkflowConnectorResolver(new ConfiguredConnectorRegistry()));
        var ctx = CreateContext(executor, new StubCredentialProvider(("retry-secret", "retry-token")));
        var request = new StepRequestEvent
        {
            StepId = "s-http-retry",
            RunId = "run-http-retry",
            StepType = "http_request",
            Input = "payload",
            StepParameters = new WorkflowStepParameters
            {
                HttpRequest = new WorkflowHttpRequestOptions
                {
                    Method = "POST",
                    Url = "https://api.example.com/retry",
                    Body = """{"source":"test"}""",
                    BodyMode = "raw",
                    TimeoutMs = 10_000,
                    MaxResponseBytes = 1024,
                    MaxRedirects = 1,
                    Authentication = new WorkflowHttpRequestAuthentication
                    {
                        Scheme = "bearer",
                        SecretRef = "retry-secret",
                    },
                },
            },
        };
        request.Parameters["retry"] = "1";

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        var pending = ctx.LoadState<ConnectorCallModuleState>("connector_call")
            .PendingByOperationId
            .Values
            .Should()
            .ContainSingle()
            .Subject;
        pending.StepType.Should().Be("http_request");
        pending.HttpRequest.Should().NotBeNull();
        pending.HttpRequest.Url.Should().Be("https://api.example.com/retry");

        await DrainConnectorContinuationsAsync(module, ctx);

        executor.Requests.Should().HaveCount(2);
        executor.Requests.Select(sent => sent.Url).Should().OnlyContain(url => url == "https://api.example.com/retry");
        executor.Requests.Select(sent => sent.Authorization).Should().OnlyContain(value => value == "Bearer retry-token");

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("""{"ok":true}""");
        completed.Annotations["connector.name"].Should().Be("http_request");
        completed.Annotations["connector.attempts"].Should().Be("2");
        completed.Annotations.Values.Should().NotContain(value => value.Contains("retry-token", StringComparison.Ordinal));
    }

    private static TestEventHandlerContext CreateContext(
        IOutboundHttpRequestExecutor outboundHttpRequestExecutor,
        ICredentialProvider credentialProvider)
    {
        var services = new ServiceCollection()
            .AddSingleton(outboundHttpRequestExecutor)
            .AddSingleton(credentialProvider)
            .BuildServiceProvider();
        return new TestEventHandlerContext(
            services,
            new TestAgent("connector-http-request-test-agent"),
            NullLogger.Instance);
    }

    private static EventEnvelope Envelope(IMessage evt, string? correlationId = null) =>
        new()
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

    private static async Task HandleAndDrainAsync(
        ConnectorCallModule module,
        EventEnvelope envelope,
        TestEventHandlerContext ctx)
    {
        await module.HandleAsync(envelope, ctx, CancellationToken.None);
        await DrainConnectorContinuationsAsync(module, ctx);
    }

    private static async Task DrainConnectorContinuationsAsync(
        ConnectorCallModule module,
        TestEventHandlerContext ctx)
    {
        for (var index = 0; index < ctx.Published.Count; index++)
        {
            if (ctx.Published[index].evt is not WorkflowConnectorAttemptCompletedEvent completed)
                continue;

            ctx.Published.RemoveAt(index);
            index--;
            await module.HandleAsync(Envelope(completed), ctx, CancellationToken.None);
        }
    }

    private sealed class RecordingOutboundHttpRequestExecutor(params OutboundHttpResponse[] responses)
        : IOutboundHttpRequestExecutor
    {
        private readonly Queue<OutboundHttpResponse> _responses = new(responses);

        public List<OutboundHttpRequest> Requests { get; } = [];

        public Task<OutboundHttpResponse> ExecuteAsync(
            OutboundHttpRequest request,
            CancellationToken ct = default)
        {
            _ = ct;
            Requests.Add(request);
            return Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()
                : new OutboundHttpResponse
                {
                    Success = true,
                    Output = "{}",
                });
        }
    }

    private sealed class StubCredentialProvider(params (string Ref, string Secret)[] credentials) : ICredentialProvider
    {
        private readonly Dictionary<string, string> _credentials = credentials.ToDictionary(
            entry => entry.Ref,
            entry => entry.Secret,
            StringComparer.Ordinal);

        public Task<string?> ResolveAsync(string credentialRef, CancellationToken ct = default)
        {
            _ = ct;
            return Task.FromResult(
                _credentials.TryGetValue(credentialRef, out var secret)
                    ? secret
                    : null);
        }
    }
}
