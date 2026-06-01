using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Execution;

public sealed class WorkflowExecutionRuntimeContextTests
{
    [Fact]
    public void SetRequestMetadata_ShouldKeepLlmControlAsPassthroughOnly()
    {
        var host = new RecordingStateHost();

        WorkflowRequestMetadataRuntimeContextAccess.SetRequestMetadata(
            host,
            new Dictionary<string, string>
            {
                [" trace-id "] = "  abc  ",
                [LLMRequestMetadataKeys.NyxIdAccessToken] = " token ",
                [LLMRequestMetadataKeys.ModelOverride] = " model ",
                [LLMRequestMetadataKeys.NyxIdRoutePreference] = " route ",
                [ConnectorRequest.HttpAuthorizationMetadataKey] = " Bearer secret ",
                [" "] = "ignored",
                ["empty"] = " ",
            });

        host.RuntimeContext.LlmOverrides.NyxIdAccessToken.Should().BeNull();
        host.RuntimeContext.LlmOverrides.ModelOverride.Should().BeNull();
        host.RuntimeContext.LlmOverrides.NyxIdRoutePreference.Should().BeNull();
        host.RuntimeContext.Connector.Authorization.Should().Be("Bearer secret");
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().ContainKeys(
            "trace-id",
            LLMRequestMetadataKeys.NyxIdAccessToken,
            LLMRequestMetadataKeys.ModelOverride,
            LLMRequestMetadataKeys.NyxIdRoutePreference);
        host.RuntimeContext.RequestPassthroughMetadata.Values["trace-id"].Should().Be("abc");
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().NotContainKey(ConnectorRequest.HttpAuthorizationMetadataKey);
    }

    [Fact]
    public void SetToolContext_ShouldPromoteLlmRuntimeValuesFromTypedContext()
    {
        var host = new RecordingStateHost();
        var toolContext = AgentToolExecutionContext.Empty with
        {
            Credentials = AgentToolCredentials.Empty with
            {
                NyxIdAccessToken = " token ",
                NyxIdOrgToken = " org-token ",
            },
            Routing = LLMRequestRoutingContext.Empty with
            {
                ModelOverride = " model ",
                NyxIdRoutePreference = " route ",
            },
        };

        WorkflowToolExecutionRuntimeContextAccess.SetToolContext(
            host,
            toolContext);

        host.RuntimeContext.ToolContext.Should().BeSameAs(toolContext);
        host.RuntimeContext.LlmOverrides.NyxIdAccessToken.Should().Be("token");
        host.RuntimeContext.LlmOverrides.ModelOverride.Should().Be("model");
        host.RuntimeContext.LlmOverrides.NyxIdRoutePreference.Should().Be("route");
    }

    [Fact]
    public void SetToolContext_WithNull_ShouldClearStoredToolContext()
    {
        var host = new RecordingStateHost();
        WorkflowToolExecutionRuntimeContextAccess.SetToolContext(
            host,
            AgentToolExecutionContext.Empty with
            {
                Credentials = AgentToolCredentials.Empty with
                {
                    NyxIdAccessToken = "token",
                },
            });

        WorkflowToolExecutionRuntimeContextAccess.SetToolContext(host, null);

        host.RuntimeContext.ToolContext.Should().BeNull();
        host.RuntimeContext.LlmOverrides.NyxIdAccessToken.Should().BeNull();
    }

    [Fact]
    public void GetToolContext_ShouldReadOnlyFromRuntimeAccessor()
    {
        var context = new RecordingWorkflowExecutionContext();
        var toolContext = AgentToolExecutionContext.Empty with
        {
            Credentials = AgentToolCredentials.Empty with
            {
                NyxIdAccessToken = "token",
            },
        };
        context.RuntimeContext.ApplyToolContext(toolContext);

        WorkflowToolExecutionRuntimeContextAccess.GetToolContext(context).Should().BeSameAs(toolContext);
        WorkflowToolExecutionRuntimeContextAccess.GetToolContext(new ContextWithoutRuntimeAccessor()).Should().BeNull();
        FluentActions.Invoking(() => WorkflowToolExecutionRuntimeContextAccess.GetToolContext(null!))
            .Should()
            .Throw<ArgumentNullException>();
    }

    [Fact]
    public void SetRequestMetadata_ShouldClearRuntimeValuesWhenMetadataIsNullEmptyOrInvalid()
    {
        var host = new RecordingStateHost();
        WorkflowRequestMetadataRuntimeContextAccess.SetRequestMetadata(
            host,
            new Dictionary<string, string>
            {
                ["trace-id"] = "abc",
                [LLMRequestMetadataKeys.ModelOverride] = "model",
            });

        WorkflowRequestMetadataRuntimeContextAccess.SetRequestMetadata(host, null);

        host.RuntimeContext.LlmOverrides.ModelOverride.Should().BeNull();
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().BeEmpty();

        WorkflowRequestMetadataRuntimeContextAccess.SetRequestMetadata(
            host,
            new Dictionary<string, string>
            {
                [" "] = " ",
            });

        host.RuntimeContext.LlmOverrides.ModelOverride.Should().BeNull();
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().BeEmpty();
    }

    [Fact]
    public void RemoveRequestMetadata_ShouldValidateAndClearRuntimeContext()
    {
        var host = new RecordingStateHost();
        WorkflowRequestMetadataRuntimeContextAccess.SetRequestMetadata(
            host,
            new Dictionary<string, string>
            {
                ["trace-id"] = "abc",
                [ConnectorRequest.HttpAuthorizationMetadataKey] = "Bearer secret",
            });

        WorkflowRequestMetadataRuntimeContextAccess.RemoveRequestMetadata(host);

        host.RuntimeContext.Connector.Authorization.Should().BeNull();
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().BeEmpty();

        FluentActions.Invoking(() => WorkflowRequestMetadataRuntimeContextAccess.RemoveRequestMetadata(null!))
            .Should()
            .Throw<ArgumentNullException>();
    }

    [Fact]
    public void ConnectorAuthorizationRuntimeAccess_ShouldTrimSetAndClearAuthorization()
    {
        var host = new RecordingStateHost();

        ConnectorAuthorizationRuntimeContextAccess.SetAuthorization(host, " Bearer secret ");

        host.RuntimeContext.Connector.Authorization.Should().Be("Bearer secret");

        ConnectorAuthorizationRuntimeContextAccess.SetAuthorization(host, " ");

        host.RuntimeContext.Connector.Authorization.Should().BeNull();

        ConnectorAuthorizationRuntimeContextAccess.SetAuthorization(host, "Bearer secret");
        ConnectorAuthorizationRuntimeContextAccess.RemoveAuthorization(host);

        host.RuntimeContext.Connector.Authorization.Should().BeNull();
        FluentActions.Invoking(() => ConnectorAuthorizationRuntimeContextAccess.SetAuthorization(null!, "secret"))
            .Should()
            .Throw<ArgumentNullException>();
        FluentActions.Invoking(() => ConnectorAuthorizationRuntimeContextAccess.RemoveAuthorization(null!))
            .Should()
            .Throw<ArgumentNullException>();
    }

    [Fact]
    public void ConnectorAuthorizationRuntimeAccess_ShouldReadOnlyFromRuntimeAccessor()
    {
        var context = new RecordingWorkflowExecutionContext();
        context.RuntimeContext.Connector.Authorization = " Bearer secret ";

        ConnectorAuthorizationRuntimeContextAccess.TryGetAuthorization(context, out var authorization)
            .Should()
            .BeTrue();
        authorization.Should().Be("Bearer secret");

        context.RuntimeContext.Connector.Authorization = " ";
        ConnectorAuthorizationRuntimeContextAccess.TryGetAuthorization(context, out authorization)
            .Should()
            .BeFalse();
        authorization.Should().BeEmpty();

        ConnectorAuthorizationRuntimeContextAccess.TryGetAuthorization(new ContextWithoutRuntimeAccessor(), out authorization)
            .Should()
            .BeFalse();
        authorization.Should().BeEmpty();
        FluentActions.Invoking(() => ConnectorAuthorizationRuntimeContextAccess.TryGetAuthorization(null!, out _))
            .Should()
            .Throw<ArgumentNullException>();
    }

    [Fact]
    public void CopyRequestMetadata_ShouldReturnZeroWhenRuntimeContextIsMissingOrEmpty()
    {
        var target = new Dictionary<string, string>(StringComparer.Ordinal);

        WorkflowRequestMetadataRuntimeContextAccess.CopyRequestMetadata(new ContextWithoutRuntimeAccessor(), target)
            .Should()
            .Be(0);

        WorkflowRequestMetadataRuntimeContextAccess.CopyRequestMetadata(new RecordingWorkflowExecutionContext(), target)
            .Should()
            .Be(0);
    }

    [Fact]
    public void CopyRequestMetadata_ShouldCopyOnlyPassthroughEntries()
    {
        var context = new RecordingWorkflowExecutionContext();
        context.RuntimeContext.ApplyRequestMetadata(
            new Dictionary<string, string>
            {
                ["trace-id"] = "abc",
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "token",
                [ConnectorRequest.HttpAuthorizationMetadataKey] = "Bearer secret",
                ["empty"] = " ",
            });
        var target = new Dictionary<string, string>(StringComparer.Ordinal);

        var copied = WorkflowRequestMetadataRuntimeContextAccess.CopyRequestMetadata(context, target);

        copied.Should().Be(2);
        target.Should().HaveCount(2);
        target["trace-id"].Should().Be("abc");
        target.Should().ContainKey(LLMRequestMetadataKeys.NyxIdAccessToken);
        target.Should().NotContainKey(ConnectorRequest.HttpAuthorizationMetadataKey);
    }

    [Fact]
    public void CopyRequestMetadata_ShouldValidateArguments()
    {
        var context = new RecordingWorkflowExecutionContext();
        var target = new Dictionary<string, string>(StringComparer.Ordinal);

        FluentActions.Invoking(() => WorkflowRequestMetadataRuntimeContextAccess.CopyRequestMetadata(null!, target))
            .Should()
            .Throw<ArgumentNullException>();
        FluentActions.Invoking(() => WorkflowRequestMetadataRuntimeContextAccess.CopyRequestMetadata(context, null!))
            .Should()
            .Throw<ArgumentNullException>();
        FluentActions.Invoking(() => WorkflowRequestMetadataRuntimeContextAccess.SetRequestMetadata(null!, target))
            .Should()
            .Throw<ArgumentNullException>();
    }

    [Fact]
    public void SecureInputRuntimeAccess_ShouldStoreRemoveAndClearTypedCapturedValues()
    {
        var context = new RecordingWorkflowExecutionContext();

        SecureInputRuntimeContextAccess.SetCapturedValue(context, " run-1 ", " api_key ", "secret");

        SecureInputRuntimeContextAccess.TryGetCapturedValue(context, "run-1", "api_key", out var value)
            .Should()
            .BeTrue();
        value.Should().Be("secret");
        context.RuntimeContext.CapturedSecureInputs.Values.Should().ContainKey(new CapturedSecureInputKey("run-1", "api_key"));

        SecureInputRuntimeContextAccess.RemoveCapturedValue(context, "run-1", "api_key").Should().BeTrue();
        SecureInputRuntimeContextAccess.TryGetCapturedValue(context, "run-1", "api_key", out _)
            .Should()
            .BeFalse();

        SecureInputRuntimeContextAccess.SetCapturedValue(context, "run-1", "api_key", "secret");
        SecureInputRuntimeContextAccess.SetCapturedValue(context, "run-2", "api_key", "other");
        SecureInputRuntimeContextAccess.RemoveRun(context, "run-1");

        context.RuntimeContext.CapturedSecureInputs.Values.Should().NotContainKey(new CapturedSecureInputKey("run-1", "api_key"));
        context.RuntimeContext.CapturedSecureInputs.Values.Should().ContainKey(new CapturedSecureInputKey("run-2", "api_key"));
    }

    private sealed class RecordingStateHost : IWorkflowExecutionStateHost
    {
        public string RunId => "run-1";

        public WorkflowExecutionRuntimeContext RuntimeContext { get; } = new();

        public Any? GetExecutionState(string scopeKey) => null;

        public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() => [];

        public Task UpsertExecutionStateAsync(string scopeKey, Any state, CancellationToken ct = default) => Task.CompletedTask;

        public Task ClearExecutionStateAsync(string scopeKey, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingWorkflowExecutionContext : ContextWithoutRuntimeAccessor, IWorkflowExecutionRuntimeContextAccessor
    {
        public WorkflowExecutionRuntimeContext RuntimeContext { get; } = new();
    }

    private class ContextWithoutRuntimeAccessor : IWorkflowExecutionContext
    {
        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "agent-1";

        public IServiceProvider Services { get; } = new EmptyServiceProvider();

        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;

        public string RunId => "run-1";

        public TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new() => new();

        public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new() => [];

        public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
            where TState : class, IMessage<TState> => Task.CompletedTask;

        public Task ClearStateAsync(string scopeKey, CancellationToken ct = default) => Task.CompletedTask;

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default) => Task.CompletedTask;

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage => Task.CompletedTask;

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage => Task.CompletedTask;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }
}
