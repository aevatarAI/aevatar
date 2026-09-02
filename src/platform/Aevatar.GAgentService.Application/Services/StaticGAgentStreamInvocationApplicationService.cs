using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.ServiceRuns;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.AGUI.Contracts;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Application.Services;

public sealed class StaticGAgentStreamInvocationApplicationService : IStaticGAgentStreamInvocationPort<AGUIEvent>
{
    private static readonly TimeSpan DefaultInteractionTimeout = TimeSpan.FromMinutes(2);

    private readonly IServiceInvocationResolutionPort _resolutionService;
    private readonly IInvokeAdmissionAuthorizer _admissionAuthorizer;
    private readonly IServiceRunRegistrationPort _serviceRunRegistrationPort;
    private readonly IGAgentDraftRunInteractionPort _interactionPort;

    public StaticGAgentStreamInvocationApplicationService(
        IServiceInvocationResolutionPort resolutionService,
        IInvokeAdmissionAuthorizer admissionAuthorizer,
        IServiceRunRegistrationPort serviceRunRegistrationPort,
        IGAgentDraftRunInteractionPort interactionPort)
    {
        _resolutionService = resolutionService ?? throw new ArgumentNullException(nameof(resolutionService));
        _admissionAuthorizer = admissionAuthorizer ?? throw new ArgumentNullException(nameof(admissionAuthorizer));
        _serviceRunRegistrationPort = serviceRunRegistrationPort
            ?? throw new ArgumentNullException(nameof(serviceRunRegistrationPort));
        _interactionPort = interactionPort ?? throw new ArgumentNullException(nameof(interactionPort));
    }

    public async Task<StaticGAgentStreamInvocationResult> InvokeAsync(
        StaticGAgentStreamInvocationRequest request,
        Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
        Func<StaticGAgentStreamAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(emitAsync);

        var input = request.Input ?? throw new InvalidOperationException("input is required.");
        var identity = NormalizeIdentity(request.Identity);
        var endpointId = NormalizeRequired(request.EndpointId, nameof(request.EndpointId));
        var prompt = input.Prompt?.Trim() ?? string.Empty;
        var preferredActorId = NormalizeOptional(input.PreferredActorId);
        var revisionId = NormalizeOptional(input.RevisionId);
        var headers = CopyHeaders(input.Headers);

        var invocationRequest = BuildInvocationRequest(identity, endpointId, prompt, revisionId, headers, input.Caller);
        var target = await _resolutionService.ResolveAsync(invocationRequest, ct);
        await _admissionAuthorizer.AuthorizeAsync(
            target.Service.ServiceKey,
            target.Service.DeploymentId,
            target.Artifact,
            target.Endpoint,
            invocationRequest,
            ct);

        EnsureStaticChatTarget(target, invocationRequest);
        var staticPlan = target.Artifact.DeploymentPlan.StaticPlan;
        var agentKind = staticPlan?.AgentKind?.Trim() ?? string.Empty;
        if (agentKind.Length == 0)
            throw new InvalidOperationException("Static GAgent service has no agent kind configured.");

        StaticGAgentStreamAcceptedReceipt? accepted = null;
        ServiceRunRegistrationResult? registeredRun = null;
        var terminalObservation = new ServiceRunTerminalAguiObservation();

        async ValueTask OnAcceptedAsync(GAgentDraftRunAcceptedReceipt gagentReceipt, CancellationToken token)
        {
            var serviceReceipt = CreateServiceReceipt(target, gagentReceipt);
            accepted = new StaticGAgentStreamAcceptedReceipt(serviceReceipt, gagentReceipt);
            registeredRun = await RegisterServiceRunAsync(target, invocationRequest, gagentReceipt, token);
            if (onAcceptedAsync != null)
                await onAcceptedAsync(accepted, token);
        }

        async ValueTask EmitObservedAsync(AGUIEvent aguiEvent, CancellationToken token)
        {
            terminalObservation.Observe(aguiEvent);
            await emitAsync(aguiEvent, token);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(input.Timeout ?? DefaultInteractionTimeout);

        // Refactor (iter1353/cluster-001): Old pattern: static invocation lowered trusted facts into headers before draft-run dispatch.
        // New principle: headers stay payload-only; typed ToolContext and LlmControl cross the command boundary unchanged.
        var interaction = await _interactionPort.ExecuteAsync(
            new GAgentDraftRunInteractionRequest(
                ScopeId: identity.TenantId,
                AgentKind: agentKind,
                Prompt: prompt,
                PreferredActorId: preferredActorId,
                SessionId: input.SessionId,
                Headers: headers,
                InputParts: input.InputParts,
                UseCorrelationIdAsFallbackSessionId: false,
                ToolContext: input.ToolContext,
                LlmControl: input.LlmControl),
            EmitObservedAsync,
            OnAcceptedAsync,
            timeoutCts.Token);

        var completion = interaction.FinalizeResult?.Completion ?? GAgentDraftRunCompletionStatus.Unknown;
        var completed = interaction.FinalizeResult?.Completed ?? false;
        await PersistTerminalStatusAsync(registeredRun, completion, completed, terminalObservation);
        return new StaticGAgentStreamInvocationResult(
            accepted,
            interaction.Error,
            completion,
            completed);
    }

    private async Task PersistTerminalStatusAsync(
        ServiceRunRegistrationResult? registeredRun,
        GAgentDraftRunCompletionStatus completion,
        bool completed,
        ServiceRunTerminalAguiObservation terminalObservation)
    {
        if (registeredRun == null ||
            !completed ||
            !TryMapTerminalStatus(completion, terminalObservation, out var status))
        {
            return;
        }

        await _serviceRunRegistrationPort.UpdateStatusAsync(
            registeredRun.RunActorId,
            registeredRun.RunId,
            status,
            terminalObservation.LastOutput,
            terminalObservation.LastError,
            CancellationToken.None);
    }

    private static bool TryMapTerminalStatus(
        GAgentDraftRunCompletionStatus completion,
        ServiceRunTerminalAguiObservation terminalObservation,
        out ServiceRunStatus status)
    {
        if (terminalObservation.HasTerminalObservation)
        {
            status = terminalObservation.Status;
            return status != ServiceRunStatus.Unspecified;
        }

        status = completion switch
        {
            GAgentDraftRunCompletionStatus.TextMessageCompleted => ServiceRunStatus.Completed,
            GAgentDraftRunCompletionStatus.RunFinished => ServiceRunStatus.Completed,
            GAgentDraftRunCompletionStatus.Failed => ServiceRunStatus.Failed,
            GAgentDraftRunCompletionStatus.OutcomeUncertain => ServiceRunStatus.OutcomeUncertain,
            _ => ServiceRunStatus.Unspecified,
        };
        return status != ServiceRunStatus.Unspecified;
    }

    private static ServiceIdentity NormalizeIdentity(ServiceIdentity? identity)
    {
        if (identity == null)
            throw new InvalidOperationException("service identity is required.");

        return new ServiceIdentity
        {
            TenantId = NormalizeRequired(identity.TenantId, nameof(identity.TenantId)),
            AppId = NormalizeRequired(identity.AppId, nameof(identity.AppId)),
            Namespace = NormalizeRequired(identity.Namespace, nameof(identity.Namespace)),
            ServiceId = NormalizeRequired(identity.ServiceId, nameof(identity.ServiceId)),
        };
    }

    private static ServiceInvocationRequest BuildInvocationRequest(
        ServiceIdentity identity,
        string endpointId,
        string prompt,
        string revisionId,
        IReadOnlyDictionary<string, string>? headers,
        ServiceInvocationCaller? caller)
    {
        var chatRequest = new ChatRequestEvent
        {
            Prompt = prompt,
            ScopeId = identity.TenantId,
        };
        CopyHeaders(headers, chatRequest.Metadata);

        return new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = endpointId,
            RevisionId = revisionId,
            Payload = Any.Pack(chatRequest),
            Caller = caller?.Clone(),
        };
    }

    private static void EnsureStaticChatTarget(
        ServiceInvocationResolvedTarget target,
        ServiceInvocationRequest invocationRequest)
    {
        if (target.Artifact.ImplementationKind != ServiceImplementationKind.Static)
        {
            throw new InvalidOperationException(
                "Only static GAgent services support stream invocation.");
        }

        if (target.Endpoint.Kind != ServiceEndpointKind.Chat)
        {
            throw new InvalidOperationException(
                "Only chat endpoints support static GAgent stream invocation.");
        }

        if (!string.IsNullOrWhiteSpace(target.Endpoint.RequestTypeUrl) &&
            !string.Equals(target.Endpoint.RequestTypeUrl, invocationRequest.Payload?.TypeUrl, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Endpoint '{target.Endpoint.EndpointId}' expects payload '{target.Endpoint.RequestTypeUrl}', but got '{invocationRequest.Payload?.TypeUrl}'.");
        }
    }

    private async Task<ServiceRunRegistrationResult> RegisterServiceRunAsync(
        ServiceInvocationResolvedTarget target,
        ServiceInvocationRequest invocationRequest,
        GAgentDraftRunAcceptedReceipt receipt,
        CancellationToken ct)
    {
        var record = new ServiceRunRecord
        {
            ScopeId = invocationRequest.Identity?.TenantId ?? string.Empty,
            ServiceId = invocationRequest.Identity?.ServiceId ?? string.Empty,
            ServiceKey = target.Service.ServiceKey ?? string.Empty,
            RunId = receipt.CommandId,
            CommandId = receipt.CommandId,
            CorrelationId = receipt.CorrelationId,
            EndpointId = target.Endpoint.EndpointId ?? string.Empty,
            ImplementationKind = ServiceImplementationKind.Static,
            TargetActorId = receipt.ActorId,
            RevisionId = target.Service.RevisionId ?? string.Empty,
            DeploymentId = target.Service.DeploymentId ?? string.Empty,
            Status = ServiceRunStatus.Accepted,
            Identity = invocationRequest.Identity?.Clone(),
        };
        return await _serviceRunRegistrationPort.RegisterAsync(record, ct);
    }

    private static ServiceInvocationAcceptedReceipt CreateServiceReceipt(
        ServiceInvocationResolvedTarget target,
        GAgentDraftRunAcceptedReceipt receipt) =>
        new()
        {
            RequestId = receipt.CommandId,
            ServiceKey = target.Service.ServiceKey,
            DeploymentId = target.Service.DeploymentId,
            TargetActorId = receipt.ActorId,
            EndpointId = target.Endpoint.EndpointId,
            CommandId = receipt.CommandId,
            CorrelationId = receipt.CorrelationId,
        };

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{fieldName} is required.");

        return normalized;
    }

    private static string NormalizeOptional(string? value) => value?.Trim() ?? string.Empty;

    private static IReadOnlyDictionary<string, string>? CopyHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is not { Count: > 0 })
            return null;

        return headers
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .ToDictionary(x => x.Key.Trim(), x => x.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static void CopyHeaders(
        IReadOnlyDictionary<string, string>? source,
        IDictionary<string, string> target)
    {
        if (source == null)
            return;

        foreach (var (key, value) in source)
            target[key] = value;
    }
}
