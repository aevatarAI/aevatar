using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.StatusDashboard;
using Aevatar.GAgents.StatusDashboard.Executors;
using Aevatar.Presentation.AGUI;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Mainnet.Host.Api.Status;

public sealed class ResponsesForwardTeamInternalProbeExecutor : IHealthProbeExecutor
{
    public const string ProbeKind = "responses_forward_team_internal";

    private readonly IChatRoutePolicyQueryPort _chatRoutePolicyQueryPort;
    private readonly ChatRouteResolver _chatRouteResolver;
    private readonly IStudioTeamQueryPort _teamQueryPort;
    private readonly IStudioMemberQueryPort _memberQueryPort;
    private readonly ITeamEntryMemberResolver _teamEntryMemberResolver;
    private readonly IStaticGAgentStreamInvocationPort<AGUIEvent> _teamInvocationPort;
    private readonly StatusProbeAuthorizationResolver _authorizationResolver;

    public ResponsesForwardTeamInternalProbeExecutor(
        IChatRoutePolicyQueryPort chatRoutePolicyQueryPort,
        ChatRouteResolver chatRouteResolver,
        IStudioTeamQueryPort teamQueryPort,
        IStudioMemberQueryPort memberQueryPort,
        ITeamEntryMemberResolver teamEntryMemberResolver,
        IStaticGAgentStreamInvocationPort<AGUIEvent> teamInvocationPort,
        StatusProbeAuthorizationResolver authorizationResolver)
    {
        _chatRoutePolicyQueryPort = chatRoutePolicyQueryPort
            ?? throw new ArgumentNullException(nameof(chatRoutePolicyQueryPort));
        _chatRouteResolver = chatRouteResolver ?? throw new ArgumentNullException(nameof(chatRouteResolver));
        _teamQueryPort = teamQueryPort ?? throw new ArgumentNullException(nameof(teamQueryPort));
        _memberQueryPort = memberQueryPort ?? throw new ArgumentNullException(nameof(memberQueryPort));
        _teamEntryMemberResolver = teamEntryMemberResolver
            ?? throw new ArgumentNullException(nameof(teamEntryMemberResolver));
        _teamInvocationPort = teamInvocationPort ?? throw new ArgumentNullException(nameof(teamInvocationPort));
        _authorizationResolver = authorizationResolver
            ?? throw new ArgumentNullException(nameof(authorizationResolver));
    }

    public string Kind => ProbeKind;

    public async Task<HealthProbeOutcome> ProbeAsync(
        HealthProbeTargetDescriptor descriptor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        try
        {
            var stage = ReadRequiredParam(descriptor, "Stage");
            return stage switch
            {
                "route-policy" => await ProbeRoutePolicyAsync(descriptor, ct),
                "team-entry-member" => await ProbeTeamEntryMemberAsync(descriptor, ct),
                "member-binding" => await ProbeMemberBindingAsync(descriptor, ct),
                "direct-team-invoke" => await ProbeDirectTeamInvokeAsync(descriptor, ct),
                _ => Down("unknown_stage", $"Unknown Responses ForwardToTeam internal probe stage '{stage}'."),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Down("exception", ex.Message);
        }
    }

    private async Task<HealthProbeOutcome> ProbeRoutePolicyAsync(
        HealthProbeTargetDescriptor descriptor,
        CancellationToken ct)
    {
        var scopeId = ReadRequiredParam(descriptor, "ScopeId");
        var expectedTeamId = ReadRequiredParam(descriptor, "TeamId");
        var expectedEndpointId = ReadRequiredParam(descriptor, "EndpointId");
        var model = ReadParam(descriptor, "Model") ?? string.Empty;
        var prompt = ReadParam(descriptor, "Prompt") ?? string.Empty;
        var ownerScope = OwnerScope.ForNyxIdNative(scopeId);

        var snapshot = await _chatRoutePolicyQueryPort.LookupForCallerAsync(ownerScope, ct);
        if (snapshot is null)
        {
            return Down(
                "route_policy_missing",
                $"No chat route policy readmodel exists for scope '{scopeId}'.");
        }

        var decision = _chatRouteResolver.Resolve(snapshot, new ChatRouteInput
        {
            SourceKind = ChatSourceKind.NyxResponses,
            CallerScope = new ChatRouteCallerScope
            {
                NyxUserId = ownerScope.NyxUserId,
                Platform = ownerScope.Platform,
                RegistrationScopeId = ownerScope.RegistrationScopeId,
                SenderId = ownerScope.SenderId,
            },
            Channel = string.Empty,
            CommandName = string.Empty,
            ContentHint = BuildContentHint(prompt),
            ToolMode = ToolMode.None,
            Model = model,
        });

        if (!TryReadForwardToTeamToolHint(decision.Action, out var routeTeamId, out var routeEndpointId))
        {
            return Down(
                "route_not_forward_to_team_tool",
                $"Route resolved to {decision.Action.ActionCase}, not ForwardToModel + aevatar_invoke_team.");
        }

        if (!string.Equals(routeTeamId, expectedTeamId, StringComparison.Ordinal) ||
            !string.Equals(routeEndpointId, expectedEndpointId, StringComparison.Ordinal))
        {
            return Down(
                "route_target_mismatch",
                $"Route target is team '{routeTeamId}' endpoint '{routeEndpointId}', expected team '{expectedTeamId}' endpoint '{expectedEndpointId}'.");
        }

        return Ok($"aevatar_invoke_team:{expectedTeamId}/{expectedEndpointId}");
    }

    private static bool TryReadForwardToTeamToolHint(
        ChatRouteAction action,
        out string teamId,
        out string endpointId)
    {
        teamId = string.Empty;
        endpointId = string.Empty;
        if (action.ForwardToModel?.ToolChoiceHint is not { } hint ||
            !string.Equals(hint.ToolName, "aevatar_invoke_team", StringComparison.Ordinal))
        {
            return false;
        }

        teamId = ReadString(hint.PrefilledArguments, "team_id");
        endpointId = ReadString(hint.PrefilledArguments, "endpoint_id");
        return !string.IsNullOrWhiteSpace(teamId) && !string.IsNullOrWhiteSpace(endpointId);
    }

    private static string ReadString(Struct? arguments, string key)
    {
        if (arguments?.Fields.TryGetValue(key, out var value) != true ||
            value.KindCase != Value.KindOneofCase.StringValue)
        {
            return string.Empty;
        }

        return value.StringValue?.Trim() ?? string.Empty;
    }

    private async Task<HealthProbeOutcome> ProbeTeamEntryMemberAsync(
        HealthProbeTargetDescriptor descriptor,
        CancellationToken ct)
    {
        var scopeId = ReadRequiredParam(descriptor, "ScopeId");
        var teamId = ReadRequiredParam(descriptor, "TeamId");
        var expectedMemberId = ReadRequiredParam(descriptor, "MemberId");
        var expectedPublishedServiceId = ReadRequiredParam(descriptor, "PublishedServiceId");

        var team = await _teamQueryPort.GetAsync(scopeId, teamId, ct);
        if (team is null)
            return Down("team_not_found", $"Team '{teamId}' was not found in scope '{scopeId}'.");
        if (string.Equals(team.LifecycleStage, TeamLifecycleStageNames.Archived, StringComparison.Ordinal))
            return Down("team_archived", $"Team '{teamId}' is archived.");
        if (!string.Equals(team.EntryMemberId, expectedMemberId, StringComparison.Ordinal))
        {
            return Down(
                "entry_member_mismatch",
                $"Team '{teamId}' entry member is '{team.EntryMemberId ?? string.Empty}', expected '{expectedMemberId}'.");
        }

        try
        {
            var resolution = await _teamEntryMemberResolver.ResolveAsync(scopeId, teamId, ct);
            if (!string.Equals(resolution.PublishedServiceId, expectedPublishedServiceId, StringComparison.Ordinal))
            {
                return Down(
                    "published_service_mismatch",
                    $"Entry member published service is '{resolution.PublishedServiceId}', expected '{expectedPublishedServiceId}'.");
            }

            return Ok($"entry_member:{resolution.EntryMemberId}");
        }
        catch (TeamEntryMemberResolutionException ex)
        {
            return Down(ex.Code, ex.Message);
        }
    }

    private async Task<HealthProbeOutcome> ProbeMemberBindingAsync(
        HealthProbeTargetDescriptor descriptor,
        CancellationToken ct)
    {
        var scopeId = ReadRequiredParam(descriptor, "ScopeId");
        var memberId = ReadRequiredParam(descriptor, "MemberId");
        var expectedPublishedServiceId = ReadRequiredParam(descriptor, "PublishedServiceId");

        var member = await _memberQueryPort.GetAsync(scopeId, memberId, ct);
        if (member is null)
            return Down("member_not_found", $"Member '{memberId}' was not found in scope '{scopeId}'.");
        if (string.IsNullOrWhiteSpace(member.Summary.PublishedServiceId))
            return Down("member_missing_published_service", $"Member '{memberId}' has no published service id.");
        if (!string.Equals(member.Summary.PublishedServiceId, expectedPublishedServiceId, StringComparison.Ordinal))
        {
            return Down(
                "member_published_service_mismatch",
                $"Member published service is '{member.Summary.PublishedServiceId}', expected '{expectedPublishedServiceId}'.");
        }
        if (!string.Equals(member.Summary.LifecycleStage, MemberLifecycleStageNames.BindReady, StringComparison.Ordinal))
        {
            return Down(
                "member_not_bind_ready",
                $"Member '{memberId}' lifecycle stage is '{member.Summary.LifecycleStage}', expected '{MemberLifecycleStageNames.BindReady}'.");
        }

        var lastBinding = member.LastBinding;
        if (lastBinding is null)
            return Down("member_binding_missing", $"Member '{memberId}' has no completed binding.");
        if (!string.Equals(lastBinding.PublishedServiceId, expectedPublishedServiceId, StringComparison.Ordinal))
        {
            return Down(
                "member_binding_mismatch",
                $"Member last binding is '{lastBinding.PublishedServiceId}', expected '{expectedPublishedServiceId}'.");
        }

        return Ok($"binding:{lastBinding.RevisionId}");
    }

    private async Task<HealthProbeOutcome> ProbeDirectTeamInvokeAsync(
        HealthProbeTargetDescriptor descriptor,
        CancellationToken ct)
    {
        var scopeId = ReadRequiredParam(descriptor, "ScopeId");
        var teamId = ReadRequiredParam(descriptor, "TeamId");
        var endpointId = ReadRequiredParam(descriptor, "EndpointId");
        var prompt = ReadParam(descriptor, "Prompt") ?? string.Empty;

        TeamEntryMemberResolution resolution;
        try
        {
            resolution = await _teamEntryMemberResolver.ResolveAsync(scopeId, teamId, ct);
        }
        catch (TeamEntryMemberResolutionException ex)
        {
            return Down(ex.Code, ex.Message);
        }

        var responseId = $"status-probe-{Guid.NewGuid():N}";
        var headers = await BuildInvocationHeadersAsync(descriptor, scopeId, responseId, ct);
        var result = await _teamInvocationPort.InvokeAsync(
            new StaticGAgentStreamInvocationRequest(
                new ServiceIdentity
                {
                    TenantId = resolution.ScopeId,
                    AppId = ScopeServiceIdentityDefaults.ServiceAppId,
                    Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
                    ServiceId = resolution.PublishedServiceId,
                },
                endpointId,
                new StaticGAgentStreamInvocationInput(
                    Prompt: prompt,
                    SessionId: responseId,
                    Headers: headers,
                    Timeout: TimeSpan.FromSeconds(30))),
            emitAsync: static (frame, token) =>
            {
                if (frame.RunError is not null)
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(frame.RunError.Message)
                            ? "Team invocation emitted run_error."
                            : frame.RunError.Message);
                return ValueTask.CompletedTask;
            },
            onAcceptedAsync: null,
            ct);

        if (!result.Succeeded)
        {
            return Down(
                $"invoke_start_{result.StartError.ToString().ToLowerInvariant()}",
                "Team invocation could not be accepted.");
        }

        if (result.CompletionStatus == GAgentDraftRunCompletionStatus.Failed)
            return Down("invoke_failed", "Team invocation completed with Failed status.");

        return Ok(result.CompletionObserved
            ? $"invoke:{result.CompletionStatus.ToString().ToLowerInvariant()}"
            : "invoke:accepted");
    }

    private async Task<Dictionary<string, string>> BuildInvocationHeadersAsync(
        HealthProbeTargetDescriptor descriptor,
        string scopeId,
        string responseId,
        CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.RequestId] = responseId,
            [LLMRequestMetadataKeys.ResponseId] = responseId,
            [LLMRequestMetadataKeys.ScopeId] = scopeId,
            [LLMRequestMetadataKeys.OwnerSubject] = scopeId,
            [ChannelMetadataKeys.RegistrationScopeId] = scopeId,
        };

        var bearerToken = await _authorizationResolver.ResolveBearerTokenAsync(descriptor, ct);
        if (bearerToken is not null)
        {
            headers[LLMRequestMetadataKeys.NyxIdAccessToken] = bearerToken.AccessToken;
            headers[ConnectorRequest.HttpAuthorizationMetadataKey] =
                $"{bearerToken.TokenType} {bearerToken.AccessToken}";
        }

        return headers;
    }

    private static string BuildContentHint(string? content)
    {
        var normalized = content?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;
        const int maxContentHintLength = 160;
        return normalized.Length <= maxContentHintLength
            ? normalized
            : normalized[..maxContentHintLength];
    }

    private static string? ReadParam(HealthProbeTargetDescriptor descriptor, string key)
    {
        foreach (var (k, v) in descriptor.Parameters)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                return v;
        }

        return null;
    }

    private static string ReadRequiredParam(HealthProbeTargetDescriptor descriptor, string key)
    {
        var value = ReadParam(descriptor, key)?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Parameter '{key}' is required.");
        return value;
    }

    private static HealthProbeOutcome Ok(string detail) =>
        new()
        {
            Status = HealthOutcomeStatus.Ok,
            Detail = detail,
            ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

    private static HealthProbeOutcome Down(string detail, string error) =>
        new()
        {
            Status = HealthOutcomeStatus.Down,
            Detail = detail,
            ErrorMessage = error,
            ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
}
