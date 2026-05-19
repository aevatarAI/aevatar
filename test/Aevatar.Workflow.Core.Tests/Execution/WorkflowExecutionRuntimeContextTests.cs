using Aevatar.AI.Abstractions.LLMProviders;
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
    public void SetRequestMetadata_ShouldPromoteTypedRuntimeValuesAndFilterPassthrough()
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

        host.RuntimeContext.LlmOverrides.NyxIdAccessToken.Should().Be("token");
        host.RuntimeContext.LlmOverrides.ModelOverride.Should().Be("model");
        host.RuntimeContext.LlmOverrides.NyxIdRoutePreference.Should().Be("route");
        host.RuntimeContext.Connector.Authorization.Should().Be("Bearer secret");
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().ContainSingle();
        host.RuntimeContext.RequestPassthroughMetadata.Values["trace-id"].Should().Be("abc");
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdAccessToken);
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().NotContainKey(ConnectorRequest.HttpAuthorizationMetadataKey);
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

        copied.Should().Be(1);
        target.Should().ContainSingle();
        target["trace-id"].Should().Be("abc");
        target.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdAccessToken);
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
