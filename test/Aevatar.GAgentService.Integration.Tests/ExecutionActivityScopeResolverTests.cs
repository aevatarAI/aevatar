using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.Capabilities.ExecutionActivity;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class ExecutionActivityScopeResolverTests
{
    private readonly ExecutionActivityScopeResolver _resolver = new();

    [Fact]
    public void Resolve_WhenEnvelopeIsNull_ReturnsNull()
    {
        var scopeId = _resolver.Resolve(null);

        scopeId.Should().BeNull();
    }

    [Fact]
    public void Resolve_WhenPayloadIsNull_ReturnsNull()
    {
        var envelope = new EventEnvelope();

        var scopeId = _resolver.Resolve(envelope);

        scopeId.Should().BeNull();
    }

    [Fact]
    public void Resolve_WhenChatRequestHasScopeId_ReturnsScopeId()
    {
        var envelope = CreateEnvelope(new ChatRequestEvent
        {
            ScopeId = "scope-chat",
            ToolContext = new AgentToolExecutionContextPayload
            {
                Caller = new AgentToolCallerContextPayload
                {
                    ScopeId = "scope-caller",
                },
            },
        });

        var scopeId = _resolver.Resolve(envelope);

        scopeId.Should().Be("scope-chat");
    }

    [Fact]
    public void Resolve_WhenChatRequestHasCallerScopeOnly_ReturnsCallerScopeId()
    {
        var envelope = CreateEnvelope(new ChatRequestEvent
        {
            ScopeId = " ",
            ToolContext = new AgentToolExecutionContextPayload
            {
                Caller = new AgentToolCallerContextPayload
                {
                    ScopeId = "scope-caller",
                },
            },
        });

        var scopeId = _resolver.Resolve(envelope);

        scopeId.Should().Be("scope-caller");
    }

    [Fact]
    public void Resolve_WhenChatRequestScopeIdIsWhitespace_ReturnsNull()
    {
        var envelope = CreateEnvelope(new ChatRequestEvent
        {
            ScopeId = "   ",
        });

        var scopeId = _resolver.Resolve(envelope);

        scopeId.Should().BeNull();
    }

    [Fact]
    public void Resolve_WhenServiceRunRecordHasScopeId_ReturnsScopeId()
    {
        var envelope = CreateEnvelope(new ServiceRunRecord
        {
            ScopeId = "scope-service",
            Identity = new ServiceIdentity
            {
                TenantId = "tenant-ignored",
            },
        });

        var scopeId = _resolver.Resolve(envelope);

        scopeId.Should().Be("scope-service");
    }

    [Fact]
    public void Resolve_WhenServiceRunRecordHasTenantOnly_ReturnsTenantId()
    {
        var envelope = CreateEnvelope(new ServiceRunRecord
        {
            ScopeId = " ",
            Identity = new ServiceIdentity
            {
                TenantId = "tenant-service",
            },
        });

        var scopeId = _resolver.Resolve(envelope);

        scopeId.Should().Be("tenant-service");
    }

    [Fact]
    public void Resolve_WhenBindWorkflowRunDefinitionEventHasScopeId_ReturnsScopeId()
    {
        var envelope = CreateEnvelope(new BindWorkflowRunDefinitionEvent
        {
            ScopeId = "scope-workflow-run-definition",
        });

        var scopeId = _resolver.Resolve(envelope);

        scopeId.Should().Be("scope-workflow-run-definition");
    }

    [Fact]
    public void Resolve_WhenBindWorkflowDefinitionEventHasScopeId_ReturnsScopeId()
    {
        var envelope = CreateEnvelope(new BindWorkflowDefinitionEvent
        {
            ScopeId = "scope-workflow-definition",
        });

        var scopeId = _resolver.Resolve(envelope);

        scopeId.Should().Be("scope-workflow-definition");
    }

    [Fact]
    public void Resolve_WhenWorkflowRunExecutionStartedEventHasScopeId_ReturnsScopeId()
    {
        var envelope = CreateEnvelope(new WorkflowRunExecutionStartedEvent
        {
            ScopeId = "scope-workflow-started",
        });

        var scopeId = _resolver.Resolve(envelope);

        scopeId.Should().Be("scope-workflow-started");
    }

    [Fact]
    public void Resolve_WhenSubWorkflowInvocationRegisteredEventHasScopeId_ReturnsScopeId()
    {
        var envelope = CreateEnvelope(new SubWorkflowInvocationRegisteredEvent
        {
            ScopeId = "scope-subworkflow",
        });

        var scopeId = _resolver.Resolve(envelope);

        scopeId.Should().Be("scope-subworkflow");
    }

    [Fact]
    public void Resolve_WhenUnmappedPayloadHasTypedScopeIdField_ReturnsScopeId()
    {
        var envelope = CreateEnvelope(new WorkflowChatRequestEvent
        {
            ScopeId = "scope-descriptor",
        });

        var scopeId = _resolver.Resolve(envelope);

        scopeId.Should().Be("scope-descriptor");
    }

    [Fact]
    public void Resolve_WhenUnmappedPayloadHasEmptyTypedScopeIdField_ReturnsNull()
    {
        var envelope = CreateEnvelope(new WorkflowChatRequestEvent
        {
            ScopeId = string.Empty,
        });

        var scopeId = _resolver.Resolve(envelope);

        scopeId.Should().BeNull();
    }

    [Fact]
    public void Resolve_WhenUnmappedPayloadHasNoScopeIdField_ReturnsNull()
    {
        var envelope = CreateEnvelope(new ChatResponseEvent
        {
            SessionId = "session-1",
        });

        var scopeId = _resolver.Resolve(envelope);

        scopeId.Should().BeNull();
    }

    private static EventEnvelope CreateEnvelope(IMessage payload)
    {
        return new EventEnvelope
        {
            Payload = Any.Pack(payload),
        };
    }
}
