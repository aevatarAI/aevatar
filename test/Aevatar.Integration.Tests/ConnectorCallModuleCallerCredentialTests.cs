using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Abstractions.Credentials;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "ConnectorCallModule")]
public sealed class ConnectorCallModuleCallerCredentialTests
{
    [Fact]
    public async Task ConnectorCallModule_ShouldIssueFreshCallerTokenForEveryRequest()
    {
        var connector = new RecordingConnector();
        var tokenProvider = new RotatingCallerAccessTokenProvider();
        var module = new ConnectorCallModule(new FixedConnectorResolver(connector), tokenProvider);
        var services = new ServiceCollection().AddAevatarWorkflow().BuildServiceProvider();
        var agent = new TestAgent("workflow-connector-auth-agent", "run-runtime-auth");
        var ctx = new TestEventHandlerContext(services, agent, NullLogger.Instance);
        await WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
            agent,
            new WorkflowCallerCredential { NyxIdAuthority = CreateCallerAuthority() });

        await module.HandleAsync(Envelope(CreateRequest("connector-auth-1")), ctx, CancellationToken.None);
        await module.HandleAsync(Envelope(CreateRequest("connector-auth-2")), ctx, CancellationToken.None);

        connector.Requests.Select(request => request.HttpAuthorization)
            .Should().Equal("Bearer token-1", "Bearer token-2");
        tokenProvider.Authorities.Should().HaveCount(2);
    }

    private static StepRequestEvent CreateRequest(string stepId) =>
        new()
        {
            StepId = stepId,
            StepType = "connector_call",
            RunId = "run-runtime-auth",
            Input = "payload",
            Parameters =
            {
                ["connector"] = "runtime-auth",
                ["operation"] = "invoke",
            },
        };

    private static WorkflowCallerNyxIdAuthority CreateCallerAuthority() =>
        new()
        {
            Platform = "nyxid",
            Tenant = "tenant-1",
            ExternalUserId = "m-alpha",
            Scope = "invoke",
        };

    private static EventEnvelope Envelope(IMessage evt) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
        };

    private sealed class FixedConnectorResolver(IConnector connector) : IWorkflowConnectorResolver
    {
        public ValueTask<IConnector?> ResolveAsync(
            IWorkflowExecutionContext context,
            string connectorName,
            CancellationToken ct = default)
        {
            _ = context;
            _ = connectorName;
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IConnector?>(connector);
        }
    }

    private sealed class RecordingConnector : IConnector
    {
        public string Name => "runtime-auth";

        public string Type => "test";

        public List<ConnectorRequest> Requests { get; } = [];

        public Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new ConnectorResponse { Success = true, Output = "ok" });
        }
    }

    private sealed class RotatingCallerAccessTokenProvider : IWorkflowCallerAccessTokenProvider
    {
        public List<WorkflowCallerNyxIdAuthority> Authorities { get; } = [];

        public Task<string> IssueAsync(WorkflowCallerNyxIdAuthority authority, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Authorities.Add(authority.Clone());
            return Task.FromResult($"token-{Authorities.Count}");
        }
    }
}
