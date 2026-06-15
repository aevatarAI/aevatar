using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
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
    public async Task SetRequestMetadata_ShouldNotPromoteConnectorAuthorization_AndGuardPassthrough()
    {
        var host = new RecordingStateHost();

        await WorkflowRequestMetadataRuntimeContextAccess.SetRequestMetadataAsync(
            host,
            new Dictionary<string, string>
            {
                [" trace-id "] = "  abc  ",
                ["llm.model_override"] = " model ",
                ["model_override"] = " model ",
                ["llm.max_tool_rounds"] = "3",
                ["llm.user_memory_prompt"] = " memory ",
                ["connector.http.authorization"] = " Bearer secret ",
                [" "] = "ignored",
                ["empty"] = " ",
            });

        host.ExecutionContextState.Connector.Should().BeNull();
        host.ExecutionContextState.Llm.Should().BeNull();
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().ContainKey("trace-id");
        host.RuntimeContext.RequestPassthroughMetadata.Values["trace-id"].Should().Be("abc");
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().NotContainKey("connector.http.authorization");
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().NotContainKey("llm.model_override");
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().NotContainKey("model_override");
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().NotContainKey("llm.max_tool_rounds");
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().NotContainKey("llm.user_memory_prompt");
    }

    [Fact]
    public async Task SetLlmControl_ShouldPromoteOnlyDurableLlmValuesToTypedState()
    {
        var host = new RecordingStateHost();

        await WorkflowRequestMetadataRuntimeContextAccess.SetLlmControlAsync(
            host,
            new WorkflowLlmControlContext
            {
                ModelOverride = " model ",
                MaxToolRoundsOverride = 3,
                UserMemoryPrompt = " memory ",
            });

        host.ExecutionContextState.Llm.ModelOverride.Should().Be("model");
        host.ExecutionContextState.Llm.MaxToolRoundsOverride.Should().Be(3);
        host.ExecutionContextState.Llm.UserMemoryPrompt.Should().Be("memory");
    }

    [Fact]
    public async Task SetRequestMetadata_ShouldOnlyClearPassthroughWhenMetadataIsNullEmptyOrInvalid()
    {
        var host = new RecordingStateHost();
        await ConnectorAuthorizationRuntimeContextAccess.SetAuthorizationAsync(host, "Bearer typed");
        await WorkflowRequestMetadataRuntimeContextAccess.SetRequestMetadataAsync(
            host,
            new Dictionary<string, string>
            {
                ["trace-id"] = "abc",
                ["connector.http.authorization"] = "Bearer secret",
            });

        await WorkflowRequestMetadataRuntimeContextAccess.SetRequestMetadataAsync(host, null);

        host.ExecutionContextState.Connector!.HttpAuthorization.Should().Be("Bearer typed");
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().BeEmpty();

        await WorkflowRequestMetadataRuntimeContextAccess.SetRequestMetadataAsync(
            host,
            new Dictionary<string, string>
            {
                [" "] = " ",
            });

        host.ExecutionContextState.Connector!.HttpAuthorization.Should().Be("Bearer typed");
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task SetRequestMetadata_ShouldThrowAndNotMutatePassthroughWhenCancellationRequested()
    {
        var host = new RecordingStateHost();
        await WorkflowRequestMetadataRuntimeContextAccess.SetRequestMetadataAsync(
            host,
            new Dictionary<string, string>
            {
                ["trace-id"] = "abc",
            });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => WorkflowRequestMetadataRuntimeContextAccess.SetRequestMetadataAsync(
                host,
                new Dictionary<string, string>
                {
                    ["trace-id"] = "changed",
                    ["request-id"] = "request-1",
                },
                cts.Token))
            .Should()
            .ThrowAsync<OperationCanceledException>();

        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().ContainSingle();
        host.RuntimeContext.RequestPassthroughMetadata.Values["trace-id"].Should().Be("abc");
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().NotContainKey("request-id");
    }

    [Fact]
    public async Task RemoveRequestMetadata_ShouldValidateAndClearTypedExecutionContext()
    {
        var host = new RecordingStateHost();
        await WorkflowRequestMetadataRuntimeContextAccess.SetRequestMetadataAsync(
            host,
            new Dictionary<string, string>
            {
                ["trace-id"] = "abc",
                ["connector.http.authorization"] = "Bearer secret",
            });
        await WorkflowRequestMetadataRuntimeContextAccess.SetLlmControlAsync(
            host,
            new WorkflowLlmControlContext
            {
                ModelOverride = "model",
            });

        await WorkflowRequestMetadataRuntimeContextAccess.RemoveRequestMetadataAsync(host);

        host.ExecutionContextState.Llm.Should().BeNull();
        host.ExecutionContextState.Connector.Should().BeNull();
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().BeEmpty();

        await FluentActions.Awaiting(() => WorkflowRequestMetadataRuntimeContextAccess.RemoveRequestMetadataAsync(null!))
            .Should()
            .ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void BuildConnectorAuthorizationDelta_ShouldPromoteOnlyTypedConnectorAuthorization()
    {
        var delta = WorkflowRunExecutionContextStateAccess.BuildConnectorAuthorizationDelta(" Bearer secret ");

        delta.ClearConnector.Should().BeTrue();
        delta.Connector!.HttpAuthorization.Should().Be("Bearer secret");

        var emptyDelta = WorkflowRunExecutionContextStateAccess.BuildConnectorAuthorizationDelta(" ");
        emptyDelta.ClearConnector.Should().BeTrue();
        emptyDelta.Connector.Should().BeNull();
    }

    [Fact]
    public async Task ConnectorAuthorizationRuntimeAccess_ShouldTrimSetAndClearTypedState()
    {
        var host = new RecordingStateHost();

        await ConnectorAuthorizationRuntimeContextAccess.SetAuthorizationAsync(host, " Bearer secret ");

        host.ExecutionContextState.Connector!.HttpAuthorization.Should().Be("Bearer secret");

        await ConnectorAuthorizationRuntimeContextAccess.SetAuthorizationAsync(host, " ");

        host.ExecutionContextState.Connector.Should().BeNull();

        await ConnectorAuthorizationRuntimeContextAccess.SetAuthorizationAsync(host, "Bearer secret");
        await ConnectorAuthorizationRuntimeContextAccess.RemoveAuthorizationAsync(host);

        host.ExecutionContextState.Connector.Should().BeNull();
        await FluentActions.Awaiting(() => ConnectorAuthorizationRuntimeContextAccess.SetAuthorizationAsync(null!, "secret"))
            .Should()
            .ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => ConnectorAuthorizationRuntimeContextAccess.RemoveAuthorizationAsync(null!))
            .Should()
            .ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void ConnectorAuthorizationRuntimeAccess_ShouldReadFromTypedStateHost()
    {
        var context = new RecordingWorkflowExecutionContext();
        context.ExecutionContextState.Connector = new WorkflowConnectorExecutionContextState
        {
            HttpAuthorization = " Bearer secret ",
        };

        ConnectorAuthorizationRuntimeContextAccess.TryGetAuthorization(context, out var authorization)
            .Should()
            .BeTrue();
        authorization.Should().Be("Bearer secret");

        context.ExecutionContextState.Connector.HttpAuthorization = " ";
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
                ["llm.model_override"] = "model",
                ["connector.http.authorization"] = "Bearer secret",
                ["empty"] = " ",
            });
        var target = new Dictionary<string, string>(StringComparer.Ordinal);

        var copied = WorkflowRequestMetadataRuntimeContextAccess.CopyRequestMetadata(context, target);

        copied.Should().Be(1);
        target.Should().HaveCount(1);
        target["trace-id"].Should().Be("abc");
        target.Should().NotContainKey("llm.model_override");
        target.Should().NotContainKey("connector.http.authorization");
    }

    [Fact]
    public async Task CopyRequestMetadata_ShouldValidateArguments()
    {
        var context = new RecordingWorkflowExecutionContext();
        var target = new Dictionary<string, string>(StringComparer.Ordinal);

        FluentActions.Invoking(() => WorkflowRequestMetadataRuntimeContextAccess.CopyRequestMetadata(null!, target))
            .Should()
            .Throw<ArgumentNullException>();
        FluentActions.Invoking(() => WorkflowRequestMetadataRuntimeContextAccess.CopyRequestMetadata(context, null!))
            .Should()
            .Throw<ArgumentNullException>();
        await FluentActions.Awaiting(() => WorkflowRequestMetadataRuntimeContextAccess.SetRequestMetadataAsync(null!, target))
            .Should()
            .ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task SecureInputRuntimeAccess_ShouldStoreRemoveAndClearTypedCapturedValues()
    {
        var context = new RecordingWorkflowExecutionContext();

        await SecureInputRuntimeContextAccess.SetCapturedValueAsync(
            context,
            " run-1 ",
            " api_key ",
            "secret",
            CancellationToken.None);

        SecureInputRuntimeContextAccess.TryGetCapturedValue(context, "run-1", "api_key", out var value)
            .Should()
            .BeTrue();
        value.Should().Be("secret");
        context.SecureInputState.Captured.Should().ContainKey("run-1::api_key");

        (await SecureInputRuntimeContextAccess.RemoveCapturedValueAsync(
            context,
            "run-1",
            "api_key",
            CancellationToken.None)).Should().BeTrue();
        SecureInputRuntimeContextAccess.TryGetCapturedValue(context, "run-1", "api_key", out _)
            .Should()
            .BeFalse();

        await SecureInputRuntimeContextAccess.SetCapturedValueAsync(context, "run-1", "api_key", "secret", CancellationToken.None);
        await SecureInputRuntimeContextAccess.SetCapturedValueAsync(context, "run-2", "api_key", "other", CancellationToken.None);
        await SecureInputRuntimeContextAccess.RemoveRunAsync(context, "run-1", CancellationToken.None);

        context.SecureInputState.Captured.Should().NotContainKey("run-1::api_key");
        context.SecureInputState.Captured.Should().ContainKey("run-2::api_key");
    }

    private sealed class RecordingStateHost : IWorkflowExecutionStateHost
    {
        private readonly Dictionary<string, Any> _states = new(StringComparer.Ordinal);

        public string RunId => "run-1";

        public WorkflowExecutionRuntimeContext RuntimeContext { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextState { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextSnapshot => ExecutionContextState.Clone();

        public Task UpdateExecutionContextAsync(
            WorkflowRunExecutionContextDelta delta,
            CancellationToken ct = default)
        {
            ApplyDelta(ExecutionContextState, delta);
            return Task.CompletedTask;
        }

        public Task ClearExecutionContextAsync(CancellationToken ct = default)
        {
            ExecutionContextState.Llm = null;
            ExecutionContextState.Connector = null;
            return Task.CompletedTask;
        }

        public Any? GetExecutionState(string scopeKey) =>
            _states.TryGetValue(scopeKey, out var state) ? state : null;

        public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() => _states.ToList();

        public Task UpsertExecutionStateAsync(string scopeKey, Any state, CancellationToken ct = default)
        {
            _states[scopeKey] = state;
            return Task.CompletedTask;
        }

        public Task ClearExecutionStateAsync(string scopeKey, CancellationToken ct = default)
        {
            _states.Remove(scopeKey);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingWorkflowExecutionContext :
        ContextWithoutRuntimeAccessor,
        IWorkflowExecutionRuntimeContextAccessor,
        IWorkflowExecutionStateHost
    {
        private readonly Dictionary<string, Any> _states = new(StringComparer.Ordinal);

        public WorkflowExecutionRuntimeContext RuntimeContext { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextState { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextSnapshot => ExecutionContextState.Clone();

        public SecureInputModuleState SecureInputState =>
            _states.TryGetValue(SecureInputStateAccess.ModuleStateKey, out var state) &&
            state.Is(SecureInputModuleState.Descriptor)
                ? state.Unpack<SecureInputModuleState>()
                : new SecureInputModuleState();

        public override TState LoadState<TState>(string scopeKey)
        {
            if (_states.TryGetValue(scopeKey, out var state) &&
                state.Is(new TState().Descriptor))
            {
                return state.Unpack<TState>() ?? new TState();
            }

            return new TState();
        }

        public override IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
        {
            return _states
                .Where(x => string.IsNullOrEmpty(scopeKeyPrefix) || x.Key.StartsWith(scopeKeyPrefix, StringComparison.Ordinal))
                .Where(x => x.Value.Is(new TState().Descriptor))
                .Select(x => new KeyValuePair<string, TState>(x.Key, x.Value.Unpack<TState>() ?? new TState()))
                .ToList();
        }

        public override Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
        {
            _states[scopeKey] = Any.Pack(state);
            return Task.CompletedTask;
        }

        public override Task ClearStateAsync(string scopeKey, CancellationToken ct = default)
        {
            _states.Remove(scopeKey);
            return Task.CompletedTask;
        }

        public Any? GetExecutionState(string scopeKey) =>
            _states.TryGetValue(scopeKey, out var state) ? state : null;

        public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() => _states.ToList();

        public Task UpsertExecutionStateAsync(string scopeKey, Any state, CancellationToken ct = default)
        {
            _states[scopeKey] = state;
            return Task.CompletedTask;
        }

        public Task ClearExecutionStateAsync(string scopeKey, CancellationToken ct = default)
        {
            _states.Remove(scopeKey);
            return Task.CompletedTask;
        }

        public Task UpdateExecutionContextAsync(
            WorkflowRunExecutionContextDelta delta,
            CancellationToken ct = default)
        {
            ApplyDelta(ExecutionContextState, delta);
            return Task.CompletedTask;
        }

        public Task ClearExecutionContextAsync(CancellationToken ct = default)
        {
            ExecutionContextState.Llm = null;
            ExecutionContextState.Connector = null;
            return Task.CompletedTask;
        }
    }

    private static void ApplyDelta(
        WorkflowRunExecutionContextState state,
        WorkflowRunExecutionContextDelta delta)
    {
        if (delta.ClearLlm)
            state.Llm = null;
        if (delta.ClearConnector)
            state.Connector = null;
        if (delta.Llm != null)
        {
            state.Llm = new WorkflowLlmExecutionContextState
            {
                ModelOverride = delta.Llm.ModelOverride,
                UserMemoryPrompt = delta.Llm.UserMemoryPrompt,
            };
            if (delta.Llm.HasMaxToolRoundsOverride)
                state.Llm.MaxToolRoundsOverride = delta.Llm.MaxToolRoundsOverride;
        }

        if (delta.Connector != null)
        {
            state.Connector = new WorkflowConnectorExecutionContextState
            {
                HttpAuthorization = delta.Connector.HttpAuthorization,
            };
        }
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

        public virtual TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new() => new();

        public virtual IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new() => [];

        public virtual Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
            where TState : class, IMessage<TState> => Task.CompletedTask;

        public virtual Task ClearStateAsync(string scopeKey, CancellationToken ct = default) => Task.CompletedTask;

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
