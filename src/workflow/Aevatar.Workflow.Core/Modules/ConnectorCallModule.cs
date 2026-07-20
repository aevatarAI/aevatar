using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.Connectors;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Abstractions.Credentials;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Connector invocation module.
/// Handles step_type == "connector_call" and delegates execution to a named connector.
/// </summary>
public sealed partial class ConnectorCallModule : IEventModule<IWorkflowExecutionContext>
{
    private const string ModuleStateKey = "connector_call";
    private const string TimeoutCallbackPrefix = "workflow-connector-timeout";
    private readonly IWorkflowConnectorResolver _connectorResolver;
    private readonly IWorkflowCallerAccessTokenProvider? _callerAccessTokenProvider;
    private readonly IRemoteToolApprovalPort? _remoteToolApprovalPort;
    private readonly IOutboundHttpRequestExecutor? _outboundHttpRequestExecutor;
    private readonly ICredentialProvider? _credentialProvider;

    public ConnectorCallModule(
        IWorkflowConnectorResolver connectorResolver,
        IWorkflowCallerAccessTokenProvider? callerAccessTokenProvider = null,
        IRemoteToolApprovalPort? remoteToolApprovalPort = null,
        IOutboundHttpRequestExecutor? outboundHttpRequestExecutor = null,
        ICredentialProvider? credentialProvider = null)
    {
        _connectorResolver = connectorResolver ?? throw new ArgumentNullException(nameof(connectorResolver));
        _callerAccessTokenProvider = callerAccessTokenProvider;
        _remoteToolApprovalPort = remoteToolApprovalPort;
        _outboundHttpRequestExecutor = outboundHttpRequestExecutor;
        _credentialProvider = credentialProvider;
    }

    public string Name => "connector_call";
    public int Priority => 9;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        return payload != null &&
               (payload.Is(StepRequestEvent.Descriptor) ||
                payload.Is(SecureValueCapturedEvent.Descriptor) ||
                payload.Is(WorkflowConnectorTimeoutFiredEvent.Descriptor) ||
                payload.Is(WorkflowConnectorAttemptCompletedEvent.Descriptor) ||
                payload.Is(WorkflowConnectorApprovalStatusCheckFiredEvent.Descriptor) ||
                payload.Is(WorkflowRunStoppedEvent.Descriptor) ||
                payload.Is(WorkflowCompletedEvent.Descriptor));
    }

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        if (envelope.Payload == null)
            return;

        if (envelope.Payload.Is(SecureValueCapturedEvent.Descriptor))
        {
            var captured = envelope.Payload.Unpack<SecureValueCapturedEvent>();
            if (!string.IsNullOrWhiteSpace(captured.Variable) && !string.IsNullOrEmpty(captured.Value))
            {
                await SecureInputRuntimeContextAccess.SetCapturedValueAsync(
                    ctx,
                    captured.RunId,
                    captured.Variable,
                    captured.Value,
                    ct);
            }
            return;
        }

        if (envelope.Payload.Is(WorkflowCompletedEvent.Descriptor))
        {
            var completed = envelope.Payload.Unpack<WorkflowCompletedEvent>();
            await SecureInputRuntimeContextAccess.RemoveRunAsync(
                ctx,
                completed.RunId,
                ct);
            await HandleApprovalRunTerminatedAsync(completed.RunId, ctx, ct);
            return;
        }

        if (envelope.Payload.Is(WorkflowRunStoppedEvent.Descriptor))
        {
            await HandleApprovalRunTerminatedAsync(
                envelope.Payload.Unpack<WorkflowRunStoppedEvent>().RunId,
                ctx,
                ct);
            return;
        }

        if (envelope.Payload.Is(WorkflowConnectorApprovalStatusCheckFiredEvent.Descriptor))
        {
            await HandleApprovalStatusCheckAsync(
                envelope.Payload.Unpack<WorkflowConnectorApprovalStatusCheckFiredEvent>(),
                envelope,
                ctx,
                ct);
            return;
        }

        if (envelope.Payload.Is(WorkflowConnectorTimeoutFiredEvent.Descriptor))
        {
            await HandleTimeoutFiredAsync(envelope.Payload.Unpack<WorkflowConnectorTimeoutFiredEvent>(), envelope, ctx, ct);
            return;
        }

        if (envelope.Payload.Is(WorkflowConnectorAttemptCompletedEvent.Descriptor))
        {
            await HandleAttemptCompletedAsync(envelope.Payload.Unpack<WorkflowConnectorAttemptCompletedEvent>(), ctx, ct);
            return;
        }

        var request = envelope.Payload.Unpack<StepRequestEvent>();
        var canonicalStepType = WorkflowPrimitiveCatalog.ToCanonicalType(request.StepType);
        var isSecureStep = string.Equals(canonicalStepType, "secure_connector_call", StringComparison.OrdinalIgnoreCase);
        var isHttpRequestStep = string.Equals(canonicalStepType, "http_request", StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(canonicalStepType, "connector_call", StringComparison.OrdinalIgnoreCase) &&
            !isSecureStep &&
            !isHttpRequestStep)
        {
            return;
        }

        if (isHttpRequestStep)
        {
            await HandleHttpRequestAsync(envelope, request, ctx, ct);
            return;
        }

        var connectorName = WorkflowParameterValueParser.GetString(
            request.Parameters,
            string.Empty,
            "connector",
            "connector_name").Trim();
        var operation = WorkflowParameterValueParser.GetString(request.Parameters, string.Empty, "operation", "action");
        var retry = ParseBoundedInt(request.Parameters.GetValueOrDefault("retry", "0"), 0, 5, 0);
        var timeoutMs = ParseBoundedInt(request.Parameters.GetValueOrDefault("timeout_ms", "30000"), 100, 300_000, 30_000);
        var optional = ParseBool(request.Parameters.GetValueOrDefault("optional", "false"));
        var onMissing = request.Parameters.GetValueOrDefault("on_missing", "fail");
        var onError = request.Parameters.GetValueOrDefault("on_error", "fail");

        if (string.IsNullOrWhiteSpace(connectorName))
        {
            await PublishFailureAsync(ctx, request, "connector_call missing required parameter: connector", ct);
            return;
        }

        var connector = await _connectorResolver.ResolveAsync(ctx, connectorName, ct);
        if (connector == null)
        {
            if (optional || string.Equals(onMissing, "skip", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Logger.LogWarning("ConnectorCall: step={StepId} connector={Connector} not found, skip", request.StepId, connectorName);
                await PublishSkippedAsync(ctx, request, connectorName, operation, "connector_not_found", timeoutMs, ct);
                return;
            }

            await PublishFailureAsync(ctx, request, $"connector '{connectorName}' not found", ct);
            return;
        }

        // 当步骤带有 role 且该 role 配置了 connectors 允许列表时，校验当前 connector 是否在列表中
        var allowedKey = request.Parameters.GetValueOrDefault("allowed_connectors", "").Trim();
        if (!string.IsNullOrEmpty(allowedKey))
        {
            var allowed = WorkflowParameterValueParser.ParseStringList(allowedKey)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (allowed.Count > 0 && !allowed.Contains(connectorName))
            {
                await PublishFailureAsync(ctx, request,
                    $"connector '{connectorName}' is not allowed for this role (allowed: {string.Join(", ", allowed)})", ct);
                return;
            }
        }

        // Refactor (iter89/cluster-089-workflow-module-clock-state):
        //   Old: Connector elapsed metadata used Stopwatch directly inside
        //        the module.
        //   New: Connector duration is measured through the workflow context
        //        monotonic elapsed API.
        var attempts = Math.Max(1, retry + 1);
        var runId = string.IsNullOrEmpty(request.RunId)
            ? envelope.Propagation?.CorrelationId ?? string.Empty
            : request.RunId;
        if (RequiresConnectorApproval(request))
        {
            await BeginConnectorApprovalAsync(
                request,
                runId,
                connectorName,
                operation,
                connector,
                attempts,
                timeoutMs,
                string.Equals(onError, "continue", StringComparison.OrdinalIgnoreCase),
                isSecureStep,
                ctx,
                ct);
            return;
        }

        await StartAttemptAsync(
            envelope,
            request,
            runId,
            connectorName,
            operation,
            connector,
            attempt: 1,
            attempts,
            timeoutMs,
            string.Equals(onError, "continue", StringComparison.OrdinalIgnoreCase),
            isSecureStep,
            ctx,
            ct);
    }

    private async Task HandleHttpRequestAsync(
        EventEnvelope envelope,
        StepRequestEvent request,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var httpRequest = request.StepParameters?.HttpRequest;
        if (httpRequest == null)
        {
            await PublishFailureAsync(ctx, request, "http_request missing typed parameters", ct);
            return;
        }

        if (ContainsRawAuthorizationHeader(httpRequest.Headers))
        {
            await PublishFailureAsync(ctx, request, "http_request authentication must use authentication.secret_ref", ct);
            return;
        }

        var retry = ParseBoundedInt(request.Parameters.GetValueOrDefault("retry", "0"), 0, 5, 0);
        var timeoutMs = ParseBoundedInt(
            httpRequest.TimeoutMs > 0 ? httpRequest.TimeoutMs.ToString() : request.Parameters.GetValueOrDefault("timeout_ms", "30000"),
            100,
            300_000,
            30_000);
        var attempts = Math.Max(1, retry + 1);
        var runId = string.IsNullOrEmpty(request.RunId)
            ? envelope.Propagation?.CorrelationId ?? string.Empty
            : request.RunId;
        var onErrorContinue = string.Equals(
            request.Parameters.GetValueOrDefault("on_error", "fail"),
            "continue",
            StringComparison.OrdinalIgnoreCase);
        var normalizedHttpRequest = httpRequest.Clone();
        normalizedHttpRequest.TimeoutMs = timeoutMs;
        var connector = new HttpRequestWorkflowConnector(
            ResolveOutboundHttpRequestExecutor(ctx),
            ResolveCredentialProvider(ctx),
            normalizedHttpRequest);
        await StartAttemptAsync(
            envelope,
            request,
            runId,
            "http_request",
            BuildHttpRequestOperation(httpRequest),
            connector,
            attempt: 1,
            attempts,
            timeoutMs,
            onErrorContinue,
            isSecureStep: false,
            ctx,
            ct,
            stepType: "http_request",
            httpRequest: normalizedHttpRequest);
    }

    private async Task HandleTimeoutFiredAsync(
        WorkflowConnectorTimeoutFiredEvent evt,
        EventEnvelope envelope,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.OperationId))
            return;

        var state = WorkflowExecutionStateAccess.Load<ConnectorCallModuleState>(ctx, ModuleStateKey);
        if (!state.PendingByOperationId.TryGetValue(evt.OperationId, out var pending))
            return;

        if (!MatchesPendingTimeout(envelope, pending))
        {
            ctx.Logger.LogDebug(
                "ConnectorCall: ignore timeout without matching lease operation={OperationId}",
                evt.OperationId);
            return;
        }

        if (!string.IsNullOrWhiteSpace(pending.ApprovalActionId))
        {
            await HandleApprovedTimeoutAsync(pending, evt, state, ctx, ct);
            return;
        }

        // Refactor (iter158/cluster-157-004-timeout-cts):
        //   Old: connector timeout used inline CTS/call-stack cancellation, so late
        //        connector completions raced with in-memory continuation state.
        //   New: the actor owns a typed durable timeout event keyed by operation id;
        //        timeout and late completion both reconcile through persisted module state.
        var completion = new StepCompletedEvent
        {
            StepId = pending.StepId,
            RunId = pending.RunId,
            Success = pending.OnErrorContinue,
            Output = pending.OnErrorContinue ? pending.Input : string.Empty,
            Error = pending.OnErrorContinue ? string.Empty : $"connector call timed out after {evt.TimeoutMs}ms",
            ExecutionId = pending.ExecutionId,
        };
        completion.Annotations["connector.name"] = pending.ConnectorName;
        completion.Annotations["connector.step_id"] = pending.StepId;
        completion.Annotations["connector.run_id"] = pending.RunId;
        completion.Annotations["connector.type"] = pending.ConnectorType;
        completion.Annotations["connector.operation"] = pending.Operation;
        completion.Annotations["connector.attempts"] = pending.Attempt.ToString();
        completion.Annotations["connector.timeout_ms"] = evt.TimeoutMs.ToString();
        completion.Annotations["connector.duration_ms"] = evt.TimeoutMs.ToString("F2");
        completion.Annotations["connector.timeout_fired"] = "true";
        if (pending.OnErrorContinue)
        {
            completion.Annotations["connector.continued_on_error"] = "true";
            completion.Annotations["connector.error"] = completion.Error;
        }

        await ctx.PublishAsync(completion, TopologyAudience.Self, ct);
        RemovePending(state, pending);
        await SaveStateAsync(state, ctx, ct);
    }

    private async Task HandleAttemptCompletedAsync(
        WorkflowConnectorAttemptCompletedEvent evt,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.OperationId))
            return;

        var state = WorkflowExecutionStateAccess.Load<ConnectorCallModuleState>(ctx, ModuleStateKey);
        if (!state.PendingByOperationId.TryGetValue(evt.OperationId, out var pending))
            return;

        if (!string.IsNullOrWhiteSpace(pending.ApprovalActionId))
        {
            await HandleApprovedAttemptCompletedAsync(evt, pending, state, ctx, ct);
            return;
        }

        RemovePending(state, pending);
        await SaveStateAsync(state, ctx, ct);
        await WorkflowRuntimeCallbackLeaseSupport.CancelAsync(ctx, pending.TimeoutLease, CancellationToken.None);

        var durationMs = ParseDuration(evt);
        if (evt.Success)
        {
            var resolvedOutput = evt.Output ?? string.Empty;
            if (!TryAssertResponseOutput(pending.Parameters, resolvedOutput, out var assertionError))
            {
                await PublishPendingCompletionAsync(pending, false, string.Empty, assertionError, durationMs, evt.Annotations, ctx, ct);
                return;
            }

            if (ParseBool(pending.Parameters.GetValueOrDefault("pass_through_input", "false")))
                resolvedOutput = pending.Input ?? string.Empty;

            await PublishPendingCompletionAsync(pending, true, resolvedOutput, string.Empty, durationMs, evt.Annotations, ctx, ct);
            return;
        }

        var errorText = string.IsNullOrWhiteSpace(evt.Error) ? "connector call failed" : evt.Error;
        if (pending.Attempt < pending.Attempts)
        {
            ctx.Logger.LogWarning(
                "ConnectorCall: step={StepId} connector={Connector} attempt={Attempt}/{Attempts} failed: {Error}",
                pending.StepId, pending.ConnectorName, pending.Attempt, pending.Attempts, errorText);
            var nextRequest = BuildRetryStepRequest(pending);
            var connector = string.Equals(pending.StepType, "http_request", StringComparison.OrdinalIgnoreCase)
                ? new HttpRequestWorkflowConnector(
                    ResolveOutboundHttpRequestExecutor(ctx),
                    ResolveCredentialProvider(ctx),
                    pending.HttpRequest?.Clone() ?? new WorkflowHttpRequestOptions())
                : await _connectorResolver.ResolveAsync(ctx, pending.ConnectorName, ct);
            if (connector == null)
            {
                await PublishPendingCompletionAsync(
                    pending,
                    false,
                    string.Empty,
                    $"connector '{pending.ConnectorName}' not found",
                    durationMs,
                    evt.Annotations,
                    ctx,
                    ct);
                return;
            }

            await StartAttemptAsync(
                new EventEnvelope { Id = pending.OperationId },
                nextRequest,
                pending.RunId,
                pending.ConnectorName,
                pending.Operation,
                connector,
                pending.Attempt + 1,
                pending.Attempts,
                pending.TimeoutMs,
                pending.OnErrorContinue,
                pending.SecureStep,
                ctx,
                ct,
                stepType: pending.StepType,
                httpRequest: pending.HttpRequest);
            return;
        }

        await PublishPendingCompletionAsync(
            pending,
            false,
            string.Empty,
            errorText,
            durationMs,
            evt.Annotations,
            ctx,
            ct);
    }

    private async Task StartAttemptAsync(
        EventEnvelope envelope,
        StepRequestEvent request,
        string runId,
        string connectorName,
        string operation,
        IConnector connector,
        int attempt,
        int attempts,
        int timeoutMs,
        bool onErrorContinue,
        bool isSecureStep,
        IWorkflowExecutionContext ctx,
        CancellationToken ct,
        string approvalActionId = "",
        string stepType = "",
        WorkflowHttpRequestOptions? httpRequest = null)
    {
        var pending = await RegisterPendingAsync(
            envelope,
            request,
            runId,
            connectorName,
            operation,
            connector.Type,
            attempt,
            attempts,
            timeoutMs,
            onErrorContinue,
            isSecureStep,
            ctx,
            ct,
            approvalActionId,
            stepType,
            httpRequest);
        var requestMetadata = new Dictionary<string, string>(StringComparer.Ordinal);
        WorkflowRequestMetadataRuntimeContextAccess.CopyRequestMetadata(ctx, requestMetadata);
        var connectorRequest = new ConnectorRequest
        {
            HttpAuthorization = await ReconstructConnectorHttpAuthorizationAsync(ctx, ct),
            RunId = runId,
            StepId = request.StepId,
            Connector = connectorName,
            Operation = operation,
            Payload = await ResolvePayloadAsync(request, isSecureStep, ctx, ct) ?? string.Empty,
            Parameters = request.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value),
            IdempotencyKey = request.IdempotencyKey ?? string.Empty,
        };
        _ = ExecuteConnectorAndSignalAsync(ctx, connector, connectorRequest, pending);
    }

    private static async Task ExecuteConnectorAndSignalAsync(
        IWorkflowExecutionContext ctx,
        IConnector connector,
        ConnectorRequest request,
        PendingConnectorCallState pending)
    {
        var startedAt = ctx.GetTimestamp();
        var completed = new WorkflowConnectorAttemptCompletedEvent
        {
            RunId = pending.RunId,
            StepId = pending.StepId,
            OperationId = pending.OperationId,
            Attempt = pending.Attempt,
            ExecutionId = pending.ExecutionId,
        };
        try
        {
            var response = await connector.ExecuteAsync(request, CancellationToken.None);
            completed.Success = response.Success;
            completed.Output = response.Output ?? string.Empty;
            completed.Error = response.Error ?? string.Empty;
            foreach (var (key, value) in response.Metadata)
                completed.Annotations[key] = value;
        }
        catch (Exception ex)
        {
            completed.Success = false;
            completed.Error = ex.Message;
        }

        completed.Annotations["connector.duration_ms"] = ctx.GetElapsedTime(startedAt).TotalMilliseconds.ToString("F2");
        await ctx.PublishAsync(completed, TopologyAudience.Self, CancellationToken.None);
    }

    private async Task<PendingConnectorCallState> RegisterPendingAsync(
        EventEnvelope envelope,
        StepRequestEvent request,
        string runId,
        string connectorName,
        string operation,
        string connectorType,
        int attempt,
        int attempts,
        int timeoutMs,
        bool onErrorContinue,
        bool isSecureStep,
        IWorkflowExecutionContext ctx,
        CancellationToken ct,
        string approvalActionId = "",
        string stepType = "",
        WorkflowHttpRequestOptions? httpRequest = null)
    {
        var operationId = BuildOperationId(runId, request.StepId, attempt, request.ExecutionId, ResolveOriginEnvelopeId(envelope));
        var callbackId = RuntimeCallbackKeyComposer.BuildCallbackId(
            TimeoutCallbackPrefix,
            runId,
            request.StepId,
            operationId,
            attempt.ToString());
        var pending = new PendingConnectorCallState
        {
            StepId = request.StepId,
            RunId = runId,
            OperationId = operationId,
            Input = string.IsNullOrWhiteSpace(approvalActionId) ? request.Input ?? string.Empty : string.Empty,
            ConnectorName = connectorName,
            Operation = operation,
            Attempt = attempt,
            Attempts = attempts,
            TimeoutMs = timeoutMs,
            OnErrorContinue = onErrorContinue,
            TimeoutCallbackId = callbackId,
            ExecutionId = request.ExecutionId,
            SecureStep = isSecureStep,
            ConnectorType = connectorType,
            IdempotencyKey = request.IdempotencyKey ?? string.Empty,
            ApprovalActionId = approvalActionId,
            RequestDispatched = false,
            StepType = string.IsNullOrWhiteSpace(stepType)
                ? WorkflowPrimitiveCatalog.ToCanonicalType(request.StepType)
                : stepType,
            HttpRequest = httpRequest?.Clone(),
        };
        if (string.IsNullOrWhiteSpace(approvalActionId))
        {
            foreach (var (key, value) in request.Parameters)
                pending.Parameters[key] = value;
        }

        var state = WorkflowExecutionStateAccess.Load<ConnectorCallModuleState>(ctx, ModuleStateKey);
        RemovePendingForStep(state, runId, request.StepId);
        state.PendingByOperationId[operationId] = pending;
        state.PendingOperationIdByStepId[BuildStepKey(runId, request.StepId)] = operationId;
        await SaveStateAsync(state, ctx, ct);

        return await EnsurePendingTimeoutScheduledAsync(pending, ctx, ct);
    }

    private async Task<PendingConnectorCallState> EnsurePendingTimeoutScheduledAsync(
        PendingConnectorCallState pending,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (pending.TimeoutLease != null)
            return pending;

        var lease = await ctx.ScheduleSelfDurableTimeoutAsync(
            pending.TimeoutCallbackId,
            TimeSpan.FromMilliseconds(pending.TimeoutMs),
            new WorkflowConnectorTimeoutFiredEvent
            {
                RunId = pending.RunId,
                StepId = pending.StepId,
                OperationId = pending.OperationId,
                TimeoutMs = pending.TimeoutMs,
                Attempt = pending.Attempt,
            },
            ct: ct);

        var state = WorkflowExecutionStateAccess.Load<ConnectorCallModuleState>(ctx, ModuleStateKey);
        if (!state.PendingByOperationId.TryGetValue(pending.OperationId, out var persistedPending))
            return pending;

        persistedPending.TimeoutLease = WorkflowRuntimeCallbackLeaseStateCodec.ToState(lease);
        state.PendingByOperationId[persistedPending.OperationId] = persistedPending;
        await SaveStateAsync(state, ctx, ct);
        return persistedPending;
    }

    private static async Task MarkConnectorRequestDispatchedAsync(
        PendingConnectorCallState pending,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var state = WorkflowExecutionStateAccess.Load<ConnectorCallModuleState>(ctx, ModuleStateKey);
        if (!state.PendingByOperationId.TryGetValue(pending.OperationId, out var persistedPending))
            return;

        persistedPending.RequestDispatched = true;
        state.PendingByOperationId[persistedPending.OperationId] = persistedPending;
        await SaveStateAsync(state, ctx, ct);
    }

    private static bool MatchesPendingTimeout(EventEnvelope envelope, PendingConnectorCallState pending)
    {
        if (pending.TimeoutLease != null)
            return WorkflowRuntimeCallbackLeaseSupport.MatchesLease(envelope, pending.TimeoutLease);

        return RuntimeCallbackEnvelopeStateReader.TryRead(envelope, out var callbackState) &&
               string.Equals(callbackState.CallbackId, pending.TimeoutCallbackId, StringComparison.Ordinal);
    }

    private static void RemovePendingForStep(
        ConnectorCallModuleState state,
        string runId,
        string stepId)
    {
        var stepKey = BuildStepKey(runId, stepId);
        if (!state.PendingOperationIdByStepId.Remove(stepKey, out var operationId))
            return;

        state.PendingByOperationId.Remove(operationId);
    }

    private static void RemovePending(
        ConnectorCallModuleState state,
        PendingConnectorCallState pending)
    {
        state.PendingByOperationId.Remove(pending.OperationId);
        state.PendingOperationIdByStepId.Remove(BuildStepKey(pending.RunId, pending.StepId));
    }

    private static Task SaveStateAsync(
        ConnectorCallModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct) =>
        WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);

    private static string ResolveOriginEnvelopeId(EventEnvelope envelope) =>
        string.IsNullOrWhiteSpace(envelope.Id)
            ? Guid.NewGuid().ToString("N")
            : envelope.Id;

    private static string BuildOperationId(
        string runId,
        string stepId,
        int attempt,
        string executionId,
        string originEnvelopeId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId(
            "connector-operation",
            runId,
            stepId,
            string.IsNullOrWhiteSpace(executionId) ? originEnvelopeId : executionId,
            attempt.ToString());

    private static string BuildStepKey(string runId, string stepId) =>
        $"{runId}:{stepId}";

    private static StepRequestEvent BuildRetryStepRequest(PendingConnectorCallState pending)
    {
        var request = new StepRequestEvent
        {
            StepId = pending.StepId,
            StepType = string.IsNullOrWhiteSpace(pending.StepType)
                ? (pending.SecureStep ? "secure_connector_call" : "connector_call")
                : pending.StepType,
            RunId = pending.RunId,
            Input = pending.Input,
            ExecutionId = pending.ExecutionId,
            IdempotencyKey = pending.IdempotencyKey,
        };
        foreach (var (key, value) in pending.Parameters)
            request.Parameters[key] = value;
        if (string.Equals(pending.StepType, "http_request", StringComparison.OrdinalIgnoreCase) &&
            pending.HttpRequest != null)
        {
            request.StepParameters = new WorkflowStepParameters
            {
                HttpRequest = pending.HttpRequest.Clone(),
            };
        }
        return request;
    }

    private ICredentialProvider? ResolveCredentialProvider(IWorkflowExecutionContext ctx) =>
        _credentialProvider ?? ctx.Services.GetService<ICredentialProvider>();

    private IOutboundHttpRequestExecutor ResolveOutboundHttpRequestExecutor(IWorkflowExecutionContext ctx) =>
        _outboundHttpRequestExecutor ??
        ctx.Services.GetService<IOutboundHttpRequestExecutor>() ??
        new DefaultOutboundHttpRequestExecutor();

    private static bool ContainsRawAuthorizationHeader(IEnumerable<KeyValuePair<string, string>> headers) =>
        headers.Any(pair => string.Equals(pair.Key, "Authorization", StringComparison.OrdinalIgnoreCase));

    private static string BuildHttpRequestOperation(WorkflowHttpRequestOptions options)
    {
        var method = string.IsNullOrWhiteSpace(options.Method) ? "GET" : options.Method.Trim().ToUpperInvariant();
        if (!Uri.TryCreate(options.Url?.Trim(), UriKind.Absolute, out var uri))
            return method;

        return $"{method} {uri.GetLeftPart(UriPartial.Path)}";
    }

    private sealed class HttpRequestWorkflowConnector(
        IOutboundHttpRequestExecutor executor,
        ICredentialProvider? credentialProvider,
        WorkflowHttpRequestOptions options) : IConnector
    {
        public string Name => "http_request";

        public string Type => "http";

        public async Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
        {
            var headers = CopyHeadersWithoutContentType(options.Headers, out var contentType);
            if (ContainsRawAuthorizationHeader(headers))
                return Failure("http_request authentication must use authentication.secret_ref");

            var authorization = string.Empty;
            var redactions = new List<string>();
            var authResult = await ApplyAuthenticationAsync(headers, redactions, ct);
            if (!authResult.Success)
                return Failure(authResult.Error);
            authorization = authResult.Authorization;

            var response = await executor.ExecuteAsync(new OutboundHttpRequest
            {
                Method = options.Method,
                Url = options.Url,
                Query = options.Query.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
                Headers = headers,
                Authorization = authorization,
                IdempotencyKey = request.IdempotencyKey,
                Body = ResolveBody(request.Payload),
                ContentType = contentType,
                TimeoutMs = options.TimeoutMs,
                MaxResponseBytes = options.MaxResponseBytes,
                MaxRedirects = options.MaxRedirects,
                AllowInsecureHttp = options.AllowInsecureHttp,
                AllowPrivateNetwork = false,
            }, ct);

            return new ConnectorResponse
            {
                Success = response.Success,
                Output = RedactSecrets(response.Output, redactions),
                Error = RedactSecrets(response.Error, redactions),
                Metadata = response.Metadata.ToDictionary(
                    kv => kv.Key,
                    kv => RedactSecrets(kv.Value, redactions),
                    StringComparer.Ordinal),
            };
        }

        private async Task<(bool Success, string Authorization, string Error)> ApplyAuthenticationAsync(
            IDictionary<string, string> headers,
            List<string> redactions,
            CancellationToken ct)
        {
            var authentication = options.Authentication;
            if (authentication == null ||
                string.IsNullOrWhiteSpace(authentication.Scheme) &&
                string.IsNullOrWhiteSpace(authentication.SecretRef))
            {
                return (true, string.Empty, string.Empty);
            }

            var scheme = string.IsNullOrWhiteSpace(authentication.Scheme)
                ? "bearer"
                : authentication.Scheme.Trim().ToLowerInvariant();
            if (string.Equals(scheme, "none", StringComparison.Ordinal))
                return (true, string.Empty, string.Empty);

            if (string.IsNullOrWhiteSpace(authentication.SecretRef))
                return (false, string.Empty, "http_request authentication requires authentication.secret_ref");
            if (credentialProvider == null)
                return (false, string.Empty, "http_request credential provider is unavailable");

            var secret = await credentialProvider.ResolveAsync(authentication.SecretRef.Trim(), ct);
            if (string.IsNullOrEmpty(secret))
                return (false, string.Empty, "http_request authentication secret_ref could not be resolved");

            redactions.Add(secret);
            return scheme switch
            {
                "bearer" => (true, $"Bearer {secret}", string.Empty),
                "header" or "secret_ref_header" => ApplyHeaderAuthentication(headers, authentication, secret),
                _ => (false, string.Empty, $"unsupported http_request authentication scheme '{authentication.Scheme}'"),
            };
        }

        private static (bool Success, string Authorization, string Error) ApplyHeaderAuthentication(
            IDictionary<string, string> headers,
            WorkflowHttpRequestAuthentication authentication,
            string secret)
        {
            if (string.IsNullOrWhiteSpace(authentication.HeaderName))
                return (false, string.Empty, "http_request header authentication requires authentication.header_name");

            headers[authentication.HeaderName.Trim()] = string.Concat(
                authentication.HeaderValuePrefix ?? string.Empty,
                secret);
            return (true, string.Empty, string.Empty);
        }

        private string ResolveBody(string inheritedPayload)
        {
            var mode = options.BodyMode?.Trim();
            if (string.Equals(mode, "none", StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            if (string.Equals(mode, "input", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "inherit", StringComparison.OrdinalIgnoreCase))
            {
                return inheritedPayload ?? string.Empty;
            }

            return options.Body ?? string.Empty;
        }

        private static Dictionary<string, string> CopyHeadersWithoutContentType(
            IEnumerable<KeyValuePair<string, string>> source,
            out string contentType)
        {
            contentType = string.Empty;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in source)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                if (string.Equals(key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    contentType = value ?? string.Empty;
                    continue;
                }

                headers[key.Trim()] = value ?? string.Empty;
            }

            return headers;
        }

        private static ConnectorResponse Failure(string error) =>
            new()
            {
                Success = false,
                Error = error,
            };

        private static string RedactSecrets(string value, IReadOnlyCollection<string> secrets)
        {
            if (string.IsNullOrEmpty(value) || secrets.Count == 0)
                return value ?? string.Empty;

            var redacted = value;
            foreach (var secret in secrets.Where(secret => !string.IsNullOrEmpty(secret)))
                redacted = redacted.Replace(secret, "[redacted]", StringComparison.Ordinal);
            return redacted;
        }
    }

    private static double ParseDuration(WorkflowConnectorAttemptCompletedEvent evt)
    {
        return double.TryParse(
            evt.Annotations.GetValueOrDefault("connector.duration_ms", "0"),
            out var durationMs)
            ? durationMs
            : 0d;
    }

    private static async Task PublishPendingCompletionAsync(
        PendingConnectorCallState pending,
        bool success,
        string output,
        string error,
        double durationMs,
        IReadOnlyDictionary<string, string> responseAnnotations,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        await ctx.PublishAsync(
            BuildPendingCompletion(
                pending,
                success,
                output,
                error,
                durationMs,
                responseAnnotations),
            TopologyAudience.Self,
            ct);
    }

    private static StepCompletedEvent BuildPendingCompletion(
        PendingConnectorCallState pending,
        bool success,
        string output,
        string error,
        double durationMs,
        IReadOnlyDictionary<string, string> responseAnnotations)
    {
        var completed = new StepCompletedEvent
        {
            StepId = pending.StepId,
            RunId = pending.RunId,
            Success = success,
            Output = success ? output : string.Empty,
            Error = success ? string.Empty : error,
            ExecutionId = pending.ExecutionId,
        };
        AppendBaseMetadata(completed, pending, durationMs);
        foreach (var (key, value) in responseAnnotations)
        {
            if (!string.Equals(key, "connector.duration_ms", StringComparison.Ordinal))
                completed.Annotations[key] = value;
        }

        if (!success && pending.OnErrorContinue)
        {
            completed.Success = true;
            completed.Output = pending.Input;
            completed.Error = string.Empty;
            completed.Annotations["connector.continued_on_error"] = "true";
            completed.Annotations["connector.error"] = error ?? string.Empty;
        }

        return completed;
    }

    private static async Task PublishFailureAsync(
        IWorkflowExecutionContext ctx,
        StepRequestEvent request,
        string error,
        CancellationToken ct)
    {
        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = false,
            Error = error,
        }, TopologyAudience.Self, ct);
    }

    private static async Task PublishSkippedAsync(
        IWorkflowExecutionContext ctx,
        StepRequestEvent request,
        string connectorName,
        string operation,
        string reason,
        int timeoutMs,
        CancellationToken ct)
    {
        var skipped = new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = true,
            Output = request.Input,
        };
        skipped.Annotations["connector.skipped"] = "true";
        skipped.Annotations["connector.skip_reason"] = reason;
        skipped.Annotations["connector.name"] = connectorName;
        skipped.Annotations["connector.operation"] = operation;
        skipped.Annotations["connector.timeout_ms"] = timeoutMs.ToString();
        await ctx.PublishAsync(skipped, TopologyAudience.Self, ct);
    }

    private static int ParseBoundedInt(string raw, int min, int max, int fallback)
    {
        if (!int.TryParse(raw, out var parsed)) return fallback;
        if (parsed < min) return min;
        if (parsed > max) return max;
        return parsed;
    }

    private static bool ParseBool(string raw) =>
        string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase);

}
