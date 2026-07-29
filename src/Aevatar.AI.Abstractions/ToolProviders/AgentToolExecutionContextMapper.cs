using Aevatar.AI.Abstractions.LLMProviders;

namespace Aevatar.AI.Abstractions.ToolProviders;

// Refactor (iter24/cluster-002-agent-tool-context-generic-metadata-bag):
//   Old pattern: any tool could parse control keys from Metadata directly.
//   New principle: Metadata contributes only external annotations; tool control flow uses typed context.
public static class AgentToolExecutionContextMapper
{
    private static readonly HashSet<string> OwnedControlKeys = new(StringComparer.Ordinal)
    {
        LLMRequestMetadataKeys.RequestId,
        LLMRequestMetadataKeys.CallId,
        LLMRequestMetadataKeys.ScopeId,
        "scope_id",
        LLMRequestMetadataKeys.OwnerScopeId,
        LLMRequestMetadataKeys.OwnerSubject,
        LLMRequestMetadataKeys.ResponseId,
        LLMRequestMetadataKeys.NyxIdAccessToken,
        LLMRequestMetadataKeys.NyxIdOrgToken,
        LLMRequestMetadataKeys.NyxIdRoutePreference,
        LLMRequestMetadataKeys.ModelOverride,
        LLMRequestMetadataKeys.MaxToolRoundsOverride,
        LLMRequestMetadataKeys.UserMemoryPrompt,
        LLMRequestMetadataKeys.ConnectedServicesContext,
        LLMRequestMetadataKeys.SenderBindingId,
        LLMRequestMetadataKeys.SenderNyxIdAccessToken,
        LLMRequestMetadataKeys.SenderNyxUserId,
        "sender_nyx_user_id",
        "workflow.parent_actor_id",
        "workflow.parent_run_id",
        "workflow.parent_step_id",
        "workflow.root_run_id",
        "workflow.depth",
        "workflow_call.parent_actor_id",
        "workflow_call.parent_run_id",
        "workflow_call.parent_step_id",
        "workflow_call.root_run_id",
        "workflow_call.depth",
        "aevatar.workflow.parent_actor_id",
        "aevatar.workflow.parent_run_id",
        "aevatar.workflow.parent_step_id",
        "aevatar.workflow.root_run_id",
        "aevatar.workflow.depth",
        "platform",
        "channel.platform",
        "sender_id",
        "channel.sender_id",
        "registration_scope_id",
        "delivery_target_id",
        "channel.delivery_target_id",
        "channel.durable_reply_credential_ref",
        "message_id",
        "channel.message_id",
        "platform_message_id",
        "channel.platform_message_id",
        "lark.message_id",
        "lark.open_id",
        "lark.receive_id",
        "telegram.chat_id",
    };

    public static AgentToolExecutionContext FromRequest(LLMRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ToolContext is { } typedContext)
        {
            var mergedContext = MergeExternalMetadata(typedContext, request.Metadata);
            return request.LlmControl?.ToToolContext(mergedContext) ?? mergedContext;
        }

        // Refactor (issue1574): Old pattern: core request mapping promoted owned control keys from Metadata.
        // New principle: core LLMRequest control is typed; Metadata contributes only scrubbed annotations.
        var mapped = AgentToolExecutionContext.Empty with
        {
            ExternalMetadata = StripOwnedControlKeys(request.Metadata),
        };
        mapped = request.LlmControl?.ToToolContext(mapped) ?? mapped;
        var caller = request.CallerContext;
        return mapped with
        {
            Request = new AgentToolRequestIdentity(
                AgentToolExecutionContext.Normalize(request.RequestId) ?? mapped.Request.RequestId,
                mapped.Request.CallId),
            Caller = caller == null
                ? mapped.Caller
                : new AgentToolCallerContext(
                    AgentToolExecutionContext.Normalize(caller.ScopeId) ?? mapped.Caller.ScopeId,
                    AgentToolExecutionContext.Normalize(caller.OwnerSubject) ?? mapped.Caller.OwnerSubject,
                    AgentToolExecutionContext.Normalize(caller.ResponseId) ?? mapped.Caller.ResponseId,
                    mapped.Caller.OwnerScopeId),
            Credentials = caller?.Credentials == null
                ? mapped.Credentials
                : mapped.Credentials with
                {
                    NyxIdAccessToken = AgentToolExecutionContext.Normalize(caller.Credentials.NyxIdBearer)
                        ?? mapped.Credentials.NyxIdAccessToken,
                },
            Routing = request.RoutingContext ?? mapped.Routing,
            ExternalMetadata = StripOwnedControlKeys(request.Metadata),
        };
    }

    public static AgentToolExecutionContext FromRequestWithCallId(LLMRequest request, string? callId)
    {
        ArgumentNullException.ThrowIfNull(request);

        return FromRequest(request).WithCallId(callId);
    }

    public static AgentToolExecutionContext MergeExternalMetadata(
        AgentToolExecutionContext context,
        IReadOnlyDictionary<string, string>? metadata)
    {
        ArgumentNullException.ThrowIfNull(context);

        var incoming = StripOwnedControlKeys(metadata);
        if (incoming.Count == 0)
            return context;

        var merged = new Dictionary<string, string>(incoming, StringComparer.Ordinal);
        foreach (var pair in context.ExternalMetadata)
            merged[pair.Key] = pair.Value;

        return context with { ExternalMetadata = merged };
    }

    public static AgentToolExecutionContext FromPayload(AgentToolExecutionContextPayload? payload)
    {
        if (payload == null)
            return AgentToolExecutionContext.Empty;

        var context = new AgentToolExecutionContext(
            new AgentToolRequestIdentity(
                AgentToolExecutionContext.Normalize(payload.Request?.RequestId),
                AgentToolExecutionContext.Normalize(payload.Request?.CallId),
                AgentToolExecutionContext.Normalize(payload.Request?.IdempotencyKey)),
            new AgentToolCredentials(
                AgentToolExecutionContext.Normalize(payload.Credentials?.NyxIdAccessToken),
                AgentToolExecutionContext.Normalize(payload.Credentials?.NyxIdOrgToken),
                AgentToolExecutionContext.Normalize(payload.Credentials?.SenderNyxIdAccessToken)),
            new AgentToolCallerContext(
                AgentToolExecutionContext.Normalize(payload.Caller?.ScopeId),
                AgentToolExecutionContext.Normalize(payload.Caller?.OwnerSubject),
                AgentToolExecutionContext.Normalize(payload.Caller?.ResponseId),
                AgentToolExecutionContext.Normalize(payload.Caller?.OwnerScopeId)),
            FromChannelPayload(payload.Channel),
            new AgentToolSenderBindingContext(
                AgentToolExecutionContext.Normalize(payload.SenderBinding?.BindingId),
                AgentToolExecutionContext.Normalize(payload.SenderBinding?.NyxUserId),
                AgentToolExecutionContext.Normalize(payload.SenderBinding?.SenderTenant)),
            FromRoutingPayload(payload.Routing),
            new AgentToolConnectedServicesContext(AgentToolExecutionContext.Normalize(payload.ConnectedServices?.ContextJson)),
            FromWorkflowRuntimePayload(payload.WorkflowRuntime),
            new AgentToolScheduleContext(AgentToolExecutionContext.Normalize(payload.Schedule?.ScheduleId)),
            FromCredentialSourcePayload(payload.CredentialSource),
            FromSkillRecoveryPayload(payload.SkillRecovery),
            StripOwnedControlKeys(payload.ExternalMetadata));
        return context with
        {
            ToolVisibility = FromToolVisibilityPayload(payload.ToolVisibility),
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                AgentToolExecutionContext.Normalize(payload.NyxIdAuthority?.Platform),
                AgentToolExecutionContext.Normalize(payload.NyxIdAuthority?.Tenant),
                AgentToolExecutionContext.Normalize(payload.NyxIdAuthority?.ExternalUserId),
                AgentToolExecutionContext.Normalize(payload.NyxIdAuthority?.Scope)),
            InvocationSurface = FromInvocationSurfacePayload(payload.InvocationSurface),
        };
    }

    public static AgentToolExecutionContextPayload ToPayload(this AgentToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var payload = new AgentToolExecutionContextPayload
        {
            Request = new AgentToolRequestIdentityPayload
            {
                RequestId = context.Request.RequestId ?? string.Empty,
                CallId = context.Request.CallId ?? string.Empty,
                IdempotencyKey = context.Request.IdempotencyKey ?? string.Empty,
            },
            Credentials = new AgentToolCredentialsPayload
            {
                NyxIdAccessToken = context.Credentials.NyxIdAccessToken ?? string.Empty,
                NyxIdOrgToken = context.Credentials.NyxIdOrgToken ?? string.Empty,
                SenderNyxIdAccessToken = context.Credentials.SenderNyxIdAccessToken ?? string.Empty,
            },
            Caller = new AgentToolCallerContextPayload
            {
                ScopeId = context.Caller.ScopeId ?? string.Empty,
                OwnerSubject = context.Caller.OwnerSubject ?? string.Empty,
                ResponseId = context.Caller.ResponseId ?? string.Empty,
                OwnerScopeId = context.Caller.OwnerScopeId ?? string.Empty,
            },
            Channel = ToChannelPayload(context.Channel),
            SenderBinding = new AgentToolSenderBindingContextPayload
            {
                BindingId = context.SenderBinding.BindingId ?? string.Empty,
                NyxUserId = context.SenderBinding.NyxUserId ?? string.Empty,
                SenderTenant = context.SenderBinding.SenderTenant ?? string.Empty,
            },
            Routing = new LLMRequestRoutingContextPayload
            {
                ModelOverride = context.Routing.ModelOverride ?? string.Empty,
                NyxIdRoutePreference = context.Routing.NyxIdRoutePreference ?? string.Empty,
                UserMemoryPrompt = context.Routing.UserMemoryPrompt ?? string.Empty,
            },
            ConnectedServices = new AgentToolConnectedServicesContextPayload
            {
                ContextJson = context.ConnectedServices.ContextJson ?? string.Empty,
            },
            WorkflowRuntime = ToWorkflowRuntimePayload(context.WorkflowRuntime),
            CredentialSource = ToCredentialSourcePayload(context.CredentialSource),
            InvocationSurface = ToInvocationSurfacePayload(context.InvocationSurface),
            SkillRecovery = ToSkillRecoveryPayload(context.SkillRecovery),
        };

        ApplyOptionalPayloads(context, payload);
        CopyExternalMetadata(context.ExternalMetadata, payload);

        return payload;
    }

    public static AgentToolExecutionContext FromMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata == null || metadata.Count == 0)
            return AgentToolExecutionContext.Empty;

        return AgentToolExecutionContext.Empty with
        {
            ExternalMetadata = StripOwnedControlKeys(metadata),
        };
    }

    private static AgentToolChannelContext FromChannelPayload(AgentToolChannelContextPayload? payload) =>
        new(
            AgentToolExecutionContext.Normalize(payload?.Platform),
            AgentToolExecutionContext.Normalize(payload?.SenderId),
            AgentToolExecutionContext.Normalize(payload?.RegistrationScopeId),
            AgentToolExecutionContext.Normalize(payload?.MessageId),
            AgentToolExecutionContext.Normalize(payload?.PlatformMessageId),
            AgentToolExecutionContext.Normalize(payload?.DeliveryTargetId),
            FromWorkflowResultDeliveryCredentialPayload(payload?.WorkflowResultDeliveryCredential),
            AgentToolExecutionContext.Normalize(payload?.BotRegistrationId),
            FromIdentityHintPayloads(payload?.IdentityHints));

    private static LLMRequestRoutingContext FromRoutingPayload(LLMRequestRoutingContextPayload? payload) =>
        new(
            AgentToolExecutionContext.Normalize(payload?.ModelOverride),
            AgentToolExecutionContext.Normalize(payload?.NyxIdRoutePreference),
            payload?.HasMaxToolRoundsOverride == true ? payload.MaxToolRoundsOverride : null,
            AgentToolExecutionContext.Normalize(payload?.UserMemoryPrompt));

    private static AgentToolChannelContextPayload ToChannelPayload(AgentToolChannelContext context)
    {
        var payload = new AgentToolChannelContextPayload
        {
            Platform = context.Platform ?? string.Empty,
            SenderId = context.SenderId ?? string.Empty,
            RegistrationScopeId = context.RegistrationScopeId ?? string.Empty,
            MessageId = context.MessageId ?? string.Empty,
            PlatformMessageId = context.PlatformMessageId ?? string.Empty,
            DeliveryTargetId = context.DeliveryTargetId ?? string.Empty,
            WorkflowResultDeliveryCredential = context.WorkflowResultDeliveryCredential?.Clone(),
            BotRegistrationId = context.BotRegistrationId ?? string.Empty,
        };
        AddIdentityHintPayloads(context.IdentityHints, payload);
        return payload;
    }

    private static void AddIdentityHintPayloads(
        IReadOnlyList<AgentToolChannelIdentityHint>? hints,
        AgentToolChannelContextPayload payload)
    {
        if (hints is null)
            return;

        foreach (var hint in hints)
        {
            var subject = AgentToolExecutionContext.Normalize(hint.Subject);
            var kind = AgentToolExecutionContext.Normalize(hint.Kind);
            var value = AgentToolExecutionContext.Normalize(hint.Value);
            if (subject is null || kind is null || value is null)
                continue;

            payload.IdentityHints.Add(new AgentToolChannelIdentityHintPayload
            {
                Subject = subject,
                Kind = kind,
                Value = value,
            });
        }
    }

    private static void ApplyOptionalPayloads(
        AgentToolExecutionContext context,
        AgentToolExecutionContextPayload payload)
    {
        if (context.NyxIdAuthority.IsComplete)
            payload.NyxIdAuthority = ToNyxIdAuthorityPayload(context.NyxIdAuthority);

        if (context.Routing.MaxToolRoundsOverride.HasValue)
            payload.Routing.MaxToolRoundsOverride = context.Routing.MaxToolRoundsOverride.Value;

        if (!string.IsNullOrWhiteSpace(context.Schedule.ScheduleId))
            payload.Schedule = new AgentToolScheduleContextPayload
            {
                ScheduleId = context.Schedule.ScheduleId.Trim(),
            };

        if (context.ToolVisibility.IsRestricted)
            payload.ToolVisibility = ToToolVisibilityPayload(context.ToolVisibility);
    }

    private static AgentToolNyxIdAuthorityContextPayload ToNyxIdAuthorityPayload(
        AgentToolNyxIdAuthorityContext context) =>
        new()
        {
            Platform = context.Platform ?? string.Empty,
            Tenant = context.Tenant ?? string.Empty,
            ExternalUserId = context.ExternalUserId ?? string.Empty,
            Scope = context.Scope ?? string.Empty,
        };

    private static void CopyExternalMetadata(
        IReadOnlyDictionary<string, string> metadata,
        AgentToolExecutionContextPayload payload)
    {
        foreach (var pair in StripOwnedControlKeys(metadata))
            payload.ExternalMetadata[pair.Key] = pair.Value;
    }

    private static ChannelWorkflowResultDeliveryCredential? FromWorkflowResultDeliveryCredentialPayload(
        ChannelWorkflowResultDeliveryCredential? payload) =>
        string.IsNullOrWhiteSpace(payload?.SecretReference?.Ref) ? null : payload.Clone();

    private static IReadOnlyList<AgentToolChannelIdentityHint> FromIdentityHintPayloads(
        IEnumerable<AgentToolChannelIdentityHintPayload>? payloads)
    {
        if (payloads is null)
            return [];

        var hints = new List<AgentToolChannelIdentityHint>();
        foreach (var payload in payloads)
        {
            var subject = AgentToolExecutionContext.Normalize(payload.Subject);
            var kind = AgentToolExecutionContext.Normalize(payload.Kind);
            var value = AgentToolExecutionContext.Normalize(payload.Value);
            if (subject is null || kind is null || value is null)
                continue;

            hints.Add(new AgentToolChannelIdentityHint(subject, kind, value));
        }

        return hints;
    }

    private static AgentSkillRecoveryContext FromSkillRecoveryPayload(AgentSkillRecoveryContextPayload? payload)
    {
        if (payload == null)
            return AgentSkillRecoveryContext.Empty;

        return new AgentSkillRecoveryContext(
            payload.RequireInitialOrnnSearch,
            payload.RequireOrnnSearchOnBlocker,
            AgentToolExecutionContext.Normalize(payload.CommandName),
            AgentToolExecutionContext.Normalize(payload.OriginalCommand),
            AgentToolExecutionContext.Normalize(payload.PrimarySkillName),
            payload.MaxOrnnSearchAttempts,
            AgentToolExecutionContext.Normalize(payload.CommandArguments),
            payload.DiscoveryRequested);
    }

    private static AgentToolCredentialSource FromCredentialSourcePayload(AgentToolCredentialSourcePayload source) =>
        source switch
        {
            AgentToolCredentialSourcePayload.NyxidAssertion => AgentToolCredentialSource.NyxIdAssertion,
            AgentToolCredentialSourcePayload.BearerToken => AgentToolCredentialSource.BearerToken,
            AgentToolCredentialSourcePayload.ChannelRegistration => AgentToolCredentialSource.ChannelRegistration,
            AgentToolCredentialSourcePayload.ScheduledRun => AgentToolCredentialSource.ScheduledRun,
            AgentToolCredentialSourcePayload.System => AgentToolCredentialSource.System,
            AgentToolCredentialSourcePayload.ServiceAccount => AgentToolCredentialSource.ServiceAccount,
            _ => AgentToolCredentialSource.Unspecified,
        };

    private static AgentToolCredentialSourcePayload ToCredentialSourcePayload(AgentToolCredentialSource source) =>
        source switch
        {
            AgentToolCredentialSource.NyxIdAssertion => AgentToolCredentialSourcePayload.NyxidAssertion,
            AgentToolCredentialSource.BearerToken => AgentToolCredentialSourcePayload.BearerToken,
            AgentToolCredentialSource.ChannelRegistration => AgentToolCredentialSourcePayload.ChannelRegistration,
            AgentToolCredentialSource.ScheduledRun => AgentToolCredentialSourcePayload.ScheduledRun,
            AgentToolCredentialSource.System => AgentToolCredentialSourcePayload.System,
            AgentToolCredentialSource.ServiceAccount => AgentToolCredentialSourcePayload.ServiceAccount,
            _ => AgentToolCredentialSourcePayload.Unspecified,
        };

    private static AgentToolInvocationSurface FromInvocationSurfacePayload(
        AgentToolInvocationSurfacePayload surface) =>
        surface switch
        {
            AgentToolInvocationSurfacePayload.HumanSession => AgentToolInvocationSurface.HumanSession,
            AgentToolInvocationSurfacePayload.WorkflowToolCall => AgentToolInvocationSurface.WorkflowToolCall,
            AgentToolInvocationSurfacePayload.WorkflowLlmToolLoop => AgentToolInvocationSurface.WorkflowLlmToolLoop,
            _ => AgentToolInvocationSurface.Unspecified,
        };

    private static AgentToolInvocationSurfacePayload ToInvocationSurfacePayload(
        AgentToolInvocationSurface surface) =>
        surface switch
        {
            AgentToolInvocationSurface.HumanSession => AgentToolInvocationSurfacePayload.HumanSession,
            AgentToolInvocationSurface.WorkflowToolCall => AgentToolInvocationSurfacePayload.WorkflowToolCall,
            AgentToolInvocationSurface.WorkflowLlmToolLoop => AgentToolInvocationSurfacePayload.WorkflowLlmToolLoop,
            _ => AgentToolInvocationSurfacePayload.Unspecified,
        };

    private static AgentWorkflowRuntimeContext FromWorkflowRuntimePayload(AgentWorkflowRuntimeContextPayload? payload)
    {
        if (payload == null)
            return AgentWorkflowRuntimeContext.Empty;

        return new AgentWorkflowRuntimeContext(
            AgentToolExecutionContext.Normalize(payload.ParentActorId),
            AgentToolExecutionContext.Normalize(payload.ParentRunId),
            AgentToolExecutionContext.Normalize(payload.ParentStepId),
            AgentToolExecutionContext.Normalize(payload.RootRunId),
            Math.Max(0, payload.Depth));
    }

    private static AgentWorkflowRuntimeContextPayload ToWorkflowRuntimePayload(AgentWorkflowRuntimeContext context) =>
        new()
        {
            ParentActorId = context.ParentActorId ?? string.Empty,
            ParentRunId = context.ParentRunId ?? string.Empty,
            ParentStepId = context.ParentStepId ?? string.Empty,
            RootRunId = context.RootRunId ?? string.Empty,
            Depth = Math.Max(0, context.Depth),
        };

    private static AgentSkillRecoveryContextPayload ToSkillRecoveryPayload(AgentSkillRecoveryContext context) =>
        new()
        {
            RequireInitialOrnnSearch = context.RequireInitialOrnnSearch,
            RequireOrnnSearchOnBlocker = context.RequireOrnnSearchOnBlocker,
            CommandName = context.CommandName ?? string.Empty,
            OriginalCommand = context.OriginalCommand ?? string.Empty,
            PrimarySkillName = context.PrimarySkillName ?? string.Empty,
            MaxOrnnSearchAttempts = context.MaxOrnnSearchAttempts,
            CommandArguments = context.CommandArguments ?? string.Empty,
            DiscoveryRequested = context.DiscoveryRequested,
        };

    private static AgentToolVisibilityScope FromToolVisibilityPayload(AgentToolVisibilityScopePayload? payload)
    {
        if (payload == null)
            return AgentToolVisibilityScope.Unrestricted;

        return AgentToolVisibilityScope.FromAllowedToolNames(payload.AllowedToolNames);
    }

    private static AgentToolVisibilityScopePayload ToToolVisibilityPayload(AgentToolVisibilityScope scope)
    {
        var payload = new AgentToolVisibilityScopePayload();
        if (scope.AllowedToolNames is null)
            return payload;

        foreach (var toolName in scope.AllowedToolNames.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase))
            payload.AllowedToolNames.Add(toolName);

        return payload;
    }

    public static IReadOnlyDictionary<string, string> StripOwnedControlKeys(IReadOnlyDictionary<string, string>? metadata)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (metadata == null)
            return result;

        foreach (var (key, value) in metadata)
        {
            if (OwnedControlKeys.Contains(key))
                continue;

            result[key] = value;
        }

        return result;
    }

}
