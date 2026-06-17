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

        host.ExecutionContextState.CallerCredential.Should().BeNull();
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
                RoutePreference = " route-a ",
            });

        host.ExecutionContextState.Llm.ModelOverride.Should().Be("model");
        host.ExecutionContextState.Llm.MaxToolRoundsOverride.Should().Be(3);
        host.ExecutionContextState.Llm.UserMemoryPrompt.Should().Be("memory");
        host.ExecutionContextState.Llm.RoutePreference.Should().Be("route-a");
    }

    [Fact]
    public async Task SetRequestMetadata_ShouldOnlyClearPassthroughWhenMetadataIsNullEmptyOrInvalid()
    {
        var host = new RecordingStateHost();
        await WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
            host,
            new WorkflowCallerCredential { BearerToken = "typed" });
        await WorkflowRequestMetadataRuntimeContextAccess.SetRequestMetadataAsync(
            host,
            new Dictionary<string, string>
            {
                ["trace-id"] = "abc",
                ["connector.http.authorization"] = "Bearer secret",
            });

        await WorkflowRequestMetadataRuntimeContextAccess.SetRequestMetadataAsync(host, null);

        host.ExecutionContextState.CallerCredential!.BearerToken.Should().Be("typed");
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().BeEmpty();

        await WorkflowRequestMetadataRuntimeContextAccess.SetRequestMetadataAsync(
            host,
            new Dictionary<string, string>
            {
                [" "] = " ",
            });

        host.ExecutionContextState.CallerCredential!.BearerToken.Should().Be("typed");
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
        host.ExecutionContextState.CallerCredential.Should().BeNull();
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().BeEmpty();

        await FluentActions.Awaiting(() => WorkflowRequestMetadataRuntimeContextAccess.RemoveRequestMetadataAsync(null!))
            .Should()
            .ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void BuildCallerCredentialDelta_ShouldPromoteOnlyTypedCallerCredential()
    {
        var delta = WorkflowRunExecutionContextStateAccess.BuildCallerCredentialDelta(
            new WorkflowCallerCredential { BearerToken = " secret " });

        delta.ClearCallerCredential.Should().BeTrue();
        delta.CallerCredential!.BearerToken.Should().Be("secret");

        var emptyDelta = WorkflowRunExecutionContextStateAccess.BuildCallerCredentialDelta(
            new WorkflowCallerCredential { BearerToken = " " });
        emptyDelta.ClearCallerCredential.Should().BeTrue();
        emptyDelta.CallerCredential.Should().BeNull();

        FluentActions.Invoking(() => WorkflowRunExecutionContextStateAccess.BuildCallerCredentialDelta(
                new WorkflowCallerCredential { BearerToken = "Bearer secret" }))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*caller credential*invalid*");
    }

    [Fact]
    public async Task WorkflowCallerCredentialRuntimeAccess_ShouldTrimSetAndClearTypedState()
    {
        var host = new RecordingStateHost();

        await WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
            host,
            new WorkflowCallerCredential { BearerToken = " secret " });

        host.ExecutionContextState.CallerCredential!.BearerToken.Should().Be("secret");

        await WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
            host,
            new WorkflowCallerCredential { BearerToken = " " });

        host.ExecutionContextState.CallerCredential.Should().BeNull();

        await WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
            host,
            new WorkflowCallerCredential { BearerToken = "secret" });
        await WorkflowCallerCredentialRuntimeContextAccess.RemoveCredentialAsync(host);

        host.ExecutionContextState.CallerCredential.Should().BeNull();

        await FluentActions.Awaiting(() => WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
                host,
                new WorkflowCallerCredential { BearerToken = "Bearer secret" }))
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("*caller credential*invalid*");

        await FluentActions.Awaiting(() => WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
                null!,
                new WorkflowCallerCredential { BearerToken = "secret" }))
            .Should()
            .ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => WorkflowCallerCredentialRuntimeContextAccess.RemoveCredentialAsync(null!))
            .Should()
            .ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void WorkflowCallerCredentialRuntimeAccess_ShouldReadFromTypedStateHost()
    {
        var context = new RecordingWorkflowExecutionContext();
        context.ExecutionContextState.CallerCredential = new WorkflowCallerCredentialState
        {
            BearerToken = " secret ",
        };

        WorkflowCallerCredentialRuntimeContextAccess.TryGetCredential(context, out var credential)
            .Should()
            .BeTrue();
        credential.BearerToken.Should().Be("secret");

        context.ExecutionContextState.CallerCredential.BearerToken = " ";
        WorkflowCallerCredentialRuntimeContextAccess.TryGetCredential(context, out credential)
            .Should()
            .BeFalse();
        credential.BearerToken.Should().BeEmpty();

        context.ExecutionContextState.CallerCredential.BearerToken = "Bearer secret";
        WorkflowCallerCredentialRuntimeContextAccess.TryGetCredential(context, out credential)
            .Should()
            .BeFalse();
        credential.BearerToken.Should().BeEmpty();

        WorkflowCallerCredentialRuntimeContextAccess.TryGetCredential(new ContextWithoutRuntimeAccessor(), out credential)
            .Should()
            .BeFalse();
        credential.BearerToken.Should().BeEmpty();
        FluentActions.Invoking(() => WorkflowCallerCredentialRuntimeContextAccess.TryGetCredential(null!, out _))
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
            ExecutionContextState.CallerCredential = null;
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

        Task<WorkflowCompensationTransitionResult> IWorkflowExecutionStateHost.TryStartCompensationAsync(
            WorkflowCompletedEvent terminalFailure,
            StepCompletedEvent? terminalStep,
            CancellationToken ct)
        {
            _ = terminalFailure;
            _ = terminalStep;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        Task IWorkflowExecutionStateHost.RecordCompensableStepDispatchAsync(
            CompensableStepDispatchedEvent evt,
            CancellationToken ct)
        {
            _ = evt;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<WorkflowCompensationTransitionResult> RecordCompensationStepCompletionAsync(
            CompensationStepCompletedEvent completion,
            CancellationToken ct = default)
        {
            _ = completion;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        public Task<WorkflowCompensationTransitionResult> RecordCompensationPhaseDeadlineExceededAsync(
            string runId,
            string error,
            CancellationToken ct = default)
        {
            _ = runId;
            _ = error;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        private static WorkflowCompensationTransitionResult NoCompensableLedger() =>
            new(
                WorkflowCompensationTransitionStatus.NoCompensableLedger,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);
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

        Task<WorkflowCompensationTransitionResult> IWorkflowExecutionStateHost.TryStartCompensationAsync(
            WorkflowCompletedEvent terminalFailure,
            StepCompletedEvent? terminalStep,
            CancellationToken ct)
        {
            _ = terminalFailure;
            _ = terminalStep;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        Task IWorkflowExecutionStateHost.RecordCompensableStepDispatchAsync(
            CompensableStepDispatchedEvent evt,
            CancellationToken ct)
        {
            _ = evt;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<WorkflowCompensationTransitionResult> RecordCompensationStepCompletionAsync(
            CompensationStepCompletedEvent completion,
            CancellationToken ct = default)
        {
            _ = completion;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        public Task<WorkflowCompensationTransitionResult> RecordCompensationPhaseDeadlineExceededAsync(
            string runId,
            string error,
            CancellationToken ct = default)
        {
            _ = runId;
            _ = error;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        private static WorkflowCompensationTransitionResult NoCompensableLedger() =>
            new(
                WorkflowCompensationTransitionStatus.NoCompensableLedger,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);

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
            ExecutionContextState.CallerCredential = null;
            return Task.CompletedTask;
        }
    }

    private static void ApplyDelta(
        WorkflowRunExecutionContextState state,
        WorkflowRunExecutionContextDelta delta)
    {
        if (delta.ClearLlm)
            state.Llm = null;
        if (delta.ClearCallerCredential)
            state.CallerCredential = null;
        if (delta.Llm != null)
        {
            state.Llm = new WorkflowLlmExecutionContextState
            {
                ModelOverride = delta.Llm.ModelOverride,
                UserMemoryPrompt = delta.Llm.UserMemoryPrompt,
                RoutePreference = delta.Llm.RoutePreference,
            };
            if (delta.Llm.HasMaxToolRoundsOverride)
                state.Llm.MaxToolRoundsOverride = delta.Llm.MaxToolRoundsOverride;
        }

        if (delta.CallerCredential != null)
        {
            state.CallerCredential = new WorkflowCallerCredentialState
            {
                BearerToken = delta.CallerCredential.BearerToken,
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
