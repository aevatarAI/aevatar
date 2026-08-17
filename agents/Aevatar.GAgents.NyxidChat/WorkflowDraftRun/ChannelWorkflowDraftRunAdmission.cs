using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.NyxidChat.WorkflowDraftRun;

public sealed record ChannelWorkflowDraftRunAdmissionResult(
    bool Matched,
    NeedsWorkflowDraftRunEvent? Request = null,
    MessageContent? Rejection = null)
{
    public static ChannelWorkflowDraftRunAdmissionResult NotMatched { get; } = new(false);

    public static ChannelWorkflowDraftRunAdmissionResult Accepted(NeedsWorkflowDraftRunEvent request) =>
        new(true, request);

    public static ChannelWorkflowDraftRunAdmissionResult Rejected(string text) =>
        new(true, null, new MessageContent { Text = text });
}

public sealed class ChannelWorkflowDraftRunAdmission
{
    private readonly ChannelWorkflowDraftRunIntentParser _parser;
    private readonly IScopeWorkflowQueryPort? _workflowQueryPort;
    private readonly IChannelWorkflowAuthorizedScopeResolver? _authorizedScopeResolver;

    public ChannelWorkflowDraftRunAdmission(
        ChannelWorkflowDraftRunIntentParser parser,
        IScopeWorkflowQueryPort? workflowQueryPort = null,
        IChannelWorkflowAuthorizedScopeResolver? authorizedScopeResolver = null)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _workflowQueryPort = workflowQueryPort;
        _authorizedScopeResolver = authorizedScopeResolver;
    }

    public async Task<ChannelWorkflowDraftRunAdmissionResult> TryAdmitAsync(
        ChatActivity activity,
        ChannelBotRegistrationEntry registration,
        ChannelInboundEvent inboundEvent,
        ConversationTurnRuntimeContext runtimeContext,
        ExternalSubjectRef? senderSubject,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(inboundEvent);

        if (!_parser.TryParse(inboundEvent.Text, out var intent))
            return ChannelWorkflowDraftRunAdmissionResult.NotMatched;

        var authorization = _authorizedScopeResolver is null
            ? ChannelWorkflowAuthorizedScopeResolution.Denied(
                ChannelWorkflowScopeAuthorizationFailure.AuthorityUnavailable)
            : await _authorizedScopeResolver
                .ResolveAsync(senderSubject, registration.ScopeId, ct)
                .ConfigureAwait(false);
        if (!authorization.IsAuthorized)
        {
            return authorization.Failure switch
            {
                ChannelWorkflowScopeAuthorizationFailure.RegistrationScopeMissing =>
                    ChannelWorkflowDraftRunAdmissionResult.Rejected("无法确定当前 NyxID scope,暂不能运行 workflow。"),
                ChannelWorkflowScopeAuthorizationFailure.BindingUnavailable =>
                    ChannelWorkflowDraftRunAdmissionResult.Rejected("请先完成 NyxID 绑定,再运行 workflow。"),
                _ => ChannelWorkflowDraftRunAdmissionResult.Rejected(
                    "当前 NyxID 身份无权运行该 bot scope 的 workflow。"),
            };
        }

        var scopeId = authorization.AuthorizedScopeId;

        var userAccessToken = NormalizeOptional(runtimeContext.NyxUserAccessToken) ??
                              NormalizeOptional(activity.TransportExtras?.NyxUserAccessToken);
        if (userAccessToken is null)
            return ChannelWorkflowDraftRunAdmissionResult.Rejected("缺少当前用户的 NyxID 授权,暂不能运行 workflow。");

        if (_workflowQueryPort is null)
            return ChannelWorkflowDraftRunAdmissionResult.Rejected("Workflow 查询服务暂不可用,请稍后重试。");

        var lookup = await _workflowQueryPort.LookupByWorkflowIdAsync(scopeId, intent.WorkflowId, ct)
            .ConfigureAwait(false);
        if (lookup.Status == ScopeWorkflowLookupStatus.NotFound)
            return ChannelWorkflowDraftRunAdmissionResult.Rejected($"未找到 workflow `{intent.WorkflowId}`。");

        if (!lookup.IsRunnable)
            return ChannelWorkflowDraftRunAdmissionResult.Rejected($"Workflow `{intent.WorkflowId}` 暂未绑定可运行的 actor,请稍后重试。");

        var workflow = lookup.Workflow!;

        var request = new NeedsWorkflowDraftRunEvent
        {
            CorrelationId = activity.Id ?? string.Empty,
            RegistrationId = registration.Id ?? string.Empty,
            Activity = activity.Clone(),
            WorkflowSource = new ChannelWorkflowDraftRunSource
            {
                Kind = ChannelWorkflowDraftRunSourceKind.DefinitionActor,
                ScopeId = workflow.ScopeId,
                WorkflowId = workflow.WorkflowId,
                WorkflowName = workflow.WorkflowName,
                DefinitionActorId = workflow.ActorId,
            },
            Prompt = intent.Prompt,
            RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            RunId = ChannelWorkflowDraftRunId.NewValue(),
            NyxUserAccessToken = userAccessToken,
        };

        request.Headers["channel"] = inboundEvent.Platform ?? string.Empty;
        request.Headers["registration_id"] = registration.Id ?? string.Empty;
        request.Headers["activity_id"] = activity.Id ?? string.Empty;
        request.Headers["message_id"] = inboundEvent.MessageId ?? string.Empty;
        request.Headers["conversation_id"] = inboundEvent.ConversationId ?? string.Empty;
        request.Headers["scope_id"] = scopeId;
        request.Headers["workflow_id"] = workflow.WorkflowId;

        return ChannelWorkflowDraftRunAdmissionResult.Accepted(request);
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
