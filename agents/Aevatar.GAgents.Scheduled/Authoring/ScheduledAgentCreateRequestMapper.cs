using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Schedules;

namespace Aevatar.GAgents.Scheduled;

internal sealed class ScheduledAgentCreateRequestMapper
{
    private const string ScheduledAgentNyxProviderSlugHeader = "scheduled_agent.nyx_provider_slug";

    internal static readonly TimeSpan MinimumOneShotDelay = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan MaximumOneShotDelay = TimeSpan.FromDays(366);

    private static readonly HashSet<string> AllowedProperties = new(StringComparer.Ordinal)
    {
        "skill_ref",
        "schedule_cron",
        "schedule_timezone",
        "schedule_mode",
        "delay_seconds",
        "run_at_utc",
        "one_shot_message",
        "display_name",
        "execution_prompt",
        "provider_name",
        "model",
        "temperature",
        "max_tokens",
        "max_tool_rounds",
        "max_history_messages",
        "requires_nyxid_proxy_success",
        "required_nyx_services",
        "nyx_user_service_id",
        "nyx_provider_slug",
        "output_format",
        "external_trigger_sources",
        "run_immediately",
    };

    public ScheduledAgentCreatePlanResult Plan(string argumentsJson, OwnerScope caller, string agentId)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var args = CreatorArgs.Parse(argumentsJson);
        if (args.Error is not null)
            return ScheduledAgentCreatePlanResult.JsonError(args.Error);

        var unknown = args.Properties.Keys.Where(key => !AllowedProperties.Contains(key)).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
            return ScheduledAgentCreatePlanResult.Failed($"unsupported_fields:{string.Join(",", unknown)}");

        if (!TryParseScheduleMode(args.Str("schedule_mode"), out var scheduleMode, out var scheduleModeError))
            return ScheduledAgentCreatePlanResult.Failed(scheduleModeError);

        var skillRefText = Normalize(args.Str("skill_ref"));
        var hasSkillRef = skillRefText is not null;
        if (scheduleMode == ScheduledAgentScheduleMode.Cron && !hasSkillRef)
            return ScheduledAgentCreatePlanResult.Failed("skill_ref is required");

        var referenceParse = hasSkillRef
            ? ScheduledSkillReference.Parse(skillRefText)
            : new ScheduledSkillReferenceParseResult(new ScheduledSkillReference(string.Empty), null, null);
        if (referenceParse.ErrorJson is not null)
            return ScheduledAgentCreatePlanResult.RawError(referenceParse.ErrorJson);

        var reference = referenceParse.Reference!;
        var nowUtc = DateTimeOffset.UtcNow;
        var cron = Normalize(args.Str("schedule_cron")) ?? string.Empty;
        var timezone = Normalize(args.Str("schedule_timezone")) ?? ScheduledWorkflowAgentDefaults.DefaultTimezone;
        DateTimeOffset? oneShotRunAt = null;
        string? oneShotMessage = Normalize(args.Str("one_shot_message"));

        if (scheduleMode == ScheduledAgentScheduleMode.OneShot)
        {
            if (args.Bool("run_immediately") == true)
                return ScheduledAgentCreatePlanResult.Failed("run_immediately is not supported for one_shot schedules");

            if (!TryResolveOneShotRunAt(args, nowUtc, out var resolvedRunAt, out var runAtError))
                return ScheduledAgentCreatePlanResult.Failed(runAtError);
            oneShotRunAt = resolvedRunAt;

            if (string.IsNullOrWhiteSpace(oneShotMessage) && !hasSkillRef)
                return ScheduledAgentCreatePlanResult.Failed("one_shot_message is required when skill_ref is omitted");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(cron))
                return ScheduledAgentCreatePlanResult.Failed("schedule_cron is required");

            if (string.IsNullOrWhiteSpace(args.Str("schedule_timezone")))
                return ScheduledAgentCreatePlanResult.Failed("schedule_timezone is required");

            if (!ScheduledDispatchCalculator.TryResolveTimeZone(timezone, out _, out var timezoneError))
                return ScheduledAgentCreatePlanResult.Failed($"invalid_schedule_timezone: {timezoneError}");

            var validation = ScheduledDispatchCalculator.Validate(cron, timezone, nowUtc);
            if (!validation.Succeeded)
            {
                var cronError = validation.Error;
                return ScheduledAgentCreatePlanResult.Failed($"invalid_schedule_cron: {cronError}");
            }
        }

        var scopeId = Normalize(AgentToolRequestContext.ScopeId ?? AgentToolRequestContext.ChannelRegistrationScopeId);
        if (scopeId is null)
            return ScheduledAgentCreatePlanResult.Failed("scope_id_unavailable");

        if (!TryParseOutputFormat(args.Str("output_format"), out var outputFormat, out var outputFormatError))
            return ScheduledAgentCreatePlanResult.Failed(outputFormatError);

        if (args.HasNonEmptyArray("external_trigger_sources"))
            return ScheduledAgentCreatePlanResult.Failed("external_trigger_sources are not supported for scheduled workflow agents");

        if (!args.TryNyxIdServices("required_nyx_services", out var requiredNyxServices, out var requiredNyxServicesError))
            return ScheduledAgentCreatePlanResult.Failed(requiredNyxServicesError);

        var requestedOutboundSlug = Normalize(args.Str("nyx_provider_slug"));
        var primaryOutboundUserServiceId = Normalize(args.Str("nyx_user_service_id")) ?? string.Empty;
        if (requestedOutboundSlug is not null && scheduleMode != ScheduledAgentScheduleMode.OneShot)
            return ScheduledAgentCreatePlanResult.Failed("nyx_provider_slug is only supported for one_shot schedules");

        var conversationId = Normalize(AgentToolRequestContext.TryGetExternalMetadata(ChannelMetadataKeys.ConversationId));
        if (conversationId is null)
            return ScheduledAgentCreatePlanResult.Failed("conversation_id_unavailable");

        var contextOutboundSlug = Normalize(AgentToolRequestContext.TryGetExternalMetadata(ChannelMetadataKeys.OutboundProviderSlug));
        var contextScheduledSlug = Normalize(AgentToolRequestContext.TryGetExternalMetadata(ScheduledAgentNyxProviderSlugHeader));
        var contextPrimarySlug = contextScheduledSlug ?? contextOutboundSlug;
        var primarySlug = requestedOutboundSlug ?? contextPrimarySlug;
        if (primarySlug is null)
            return ScheduledAgentCreatePlanResult.Failed("channel_outbound_provider_slug_unavailable");

        var target = BuildChannelTarget(conversationId, primarySlug);
        if (string.IsNullOrWhiteSpace(target.PrimaryAddressId))
            return ScheduledAgentCreatePlanResult.Failed("channel_delivery_address_unavailable");

        var failureSlug = Normalize(AgentToolRequestContext.TryGetExternalMetadata(ChannelMetadataKeys.InboundChannelBotProxySlug));
        if (string.Equals(failureSlug, primarySlug, StringComparison.Ordinal))
            failureSlug = null;

        return new ScheduledAgentCreatePlanResult(
            Success: true,
            Request: new ScheduledAgentCreatePlannedRequest(
                AgentId: agentId,
                Reference: reference,
                DisplayName: Normalize(args.Str("display_name")),
                ExecutionPrompt: Normalize(args.Str("execution_prompt")),
                ScheduleCron: cron,
                ScheduleTimezone: timezone,
                ScheduleMode: scheduleMode,
                OneShotRunAtUtc: oneShotRunAt,
                OneShotMessage: oneShotMessage,
                ScopeId: scopeId,
                ProviderName: Normalize(args.Str("provider_name")),
                Model: Normalize(args.Str("model")),
                Temperature: args.TryDouble("temperature", out var temperature) ? temperature : null,
                MaxTokens: args.TryInt("max_tokens", out var maxTokens) ? maxTokens : null,
                MaxToolRounds: args.TryInt("max_tool_rounds", out var maxToolRounds) ? maxToolRounds : null,
                MaxHistoryMessages: args.TryInt("max_history_messages", out var maxHistoryMessages) ? maxHistoryMessages : null,
                RequiresNyxidProxySuccess: args.Bool("requires_nyxid_proxy_success") ?? false,
                OutputFormat: outputFormat,
                RunImmediately: args.Bool("run_immediately") ?? false,
                ConversationId: conversationId,
                PrimaryOutboundSlug: primarySlug,
                FailureNotificationSlug: failureSlug,
                ChannelTarget: target,
                Caller: caller.Clone()),
            ServiceRequirements: new ScheduledAgentServiceRequirements(
                primarySlug,
                primaryOutboundUserServiceId,
                failureSlug,
                requiredNyxServices,
                RequiresOrnnService: hasSkillRef),
            ErrorJson: null);
    }

    public ScheduledAgentCreateMapResult Map(
        ScheduledAgentCreatePlannedRequest request,
        ScheduledAgentApiKeyIssueResult issuedKey,
        SecretReference secretReference)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(issuedKey);
        ArgumentNullException.ThrowIfNull(secretReference);

        if (!issuedKey.Success || string.IsNullOrWhiteSpace(issuedKey.ApiKeyId))
            return ScheduledAgentCreateMapResult.Failed("api_key_unavailable");

        var schedule = new WorkflowScheduleConfiguration(
            ScheduleId: request.AgentId,
            DisplayName: request.DisplayName ?? request.Reference.Name,
            WorkflowName: ResolveWorkflowName(request),
            Prompt: ResolveWorkflowPrompt(request),
            CronExpression: request.ScheduleMode == ScheduledAgentScheduleMode.OneShot
                ? string.Empty
                : request.ScheduleCron,
            Timezone: request.ScheduleTimezone,
            Enabled: true,
            Headers: BuildWorkflowHeaders(request, issuedKey),
            ScopeId: request.ScopeId,
            Auth: BuildWorkflowScheduleAuth(secretReference, issuedKey),
            ScheduleMode: request.ScheduleMode == ScheduledAgentScheduleMode.OneShot
                ? WorkflowScheduleMode.OneShotAtUtc
                : WorkflowScheduleMode.RecurringCron,
            OneShotFireAt: request.OneShotRunAtUtc);

        var catalog = BuildCatalogUpsertCommand(request, issuedKey, secretReference);

        return new ScheduledAgentCreateMapResult(
            Success: true,
            Request: new ScheduledWorkflowAgentCreateRequest(schedule, catalog, request.RunImmediately),
            ErrorJson: null);
    }

    private static ScheduledAgentChannelTarget BuildChannelTarget(
        string conversationId,
        string primarySlug)
    {
        var platform = Normalize(AgentToolRequestContext.ChannelPlatform)
            ?? Normalize(AgentToolRequestContext.TryGetExternalMetadata(ChannelMetadataKeys.Platform))
            ?? string.Empty;
        var primaryAddressId = Normalize(AgentToolRequestContext.TryGetExternalMetadata(ChannelMetadataKeys.DeliveryAddressId))
            ?? Normalize(AgentToolRequestContext.ChannelDeliveryTargetId)
            ?? Normalize(conversationId)
            ?? string.Empty;
        var primaryAddressType = Normalize(AgentToolRequestContext.TryGetExternalMetadata(ChannelMetadataKeys.DeliveryAddressType))
            ?? string.Empty;
        var fallbackAddressId = Normalize(AgentToolRequestContext.TryGetExternalMetadata(ChannelMetadataKeys.DeliveryFallbackAddressId));
        var fallbackAddressType = Normalize(AgentToolRequestContext.TryGetExternalMetadata(ChannelMetadataKeys.DeliveryFallbackAddressType));

        return new ScheduledAgentChannelTarget(
            Platform: platform,
            ProviderSlug: primarySlug,
            ConversationId: conversationId,
            PrimaryAddressId: primaryAddressId,
            PrimaryAddressType: primaryAddressType,
            FallbackAddressId: fallbackAddressId,
            FallbackAddressType: fallbackAddressType);
    }

    internal static string BuildScheduledNyxApiKeyOwnerScopeKey(
        OwnerScope caller,
        string scopeId,
        string conversationId,
        string primaryAddressId)
    {
        var platform = Normalize(caller.Platform) ?? OwnerScope.NyxIdPlatform;
        var nyxUserId = Normalize(caller.NyxUserId) ?? string.Empty;
        var registrationScopeId = Normalize(caller.RegistrationScopeId) ?? string.Empty;
        var senderId = Normalize(caller.SenderId) ?? string.Empty;
        return string.Join(
            ":",
            "scheduled",
            Escape(platform),
            Escape(nyxUserId),
            Escape(registrationScopeId),
            Escape(senderId),
            Escape(Normalize(scopeId) ?? string.Empty),
            Escape(Normalize(conversationId) ?? string.Empty),
            Escape(Normalize(primaryAddressId) ?? string.Empty));
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal);

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static bool TryParseScheduleMode(
        string? value,
        out ScheduledAgentScheduleMode scheduleMode,
        out string error)
    {
        error = string.Empty;
        var normalized = Normalize(value);
        if (normalized is null || normalized.Equals("cron", StringComparison.OrdinalIgnoreCase))
        {
            scheduleMode = ScheduledAgentScheduleMode.Cron;
            return true;
        }

        if (normalized.Equals("one_shot", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("one-shot", StringComparison.OrdinalIgnoreCase))
        {
            scheduleMode = ScheduledAgentScheduleMode.OneShot;
            return true;
        }

        scheduleMode = ScheduledAgentScheduleMode.Unspecified;
        error = "schedule_mode must be one of: cron, one_shot";
        return false;
    }

    private static bool TryResolveOneShotRunAt(
        CreatorArgs args,
        DateTimeOffset nowUtc,
        out DateTimeOffset runAtUtc,
        out string error)
    {
        runAtUtc = default;
        error = string.Empty;

        var hasDelay = args.HasProperty("delay_seconds");
        var hasRunAt = args.HasProperty("run_at_utc");
        if (hasDelay == hasRunAt)
        {
            error = "provide exactly one of delay_seconds or run_at_utc for one_shot";
            return false;
        }

        if (hasDelay)
        {
            if (!args.TryLong("delay_seconds", out var seconds) || seconds <= 0)
            {
                error = "delay_seconds must be a positive integer";
                return false;
            }

            if (seconds < (long)MinimumOneShotDelay.TotalSeconds)
            {
                error = $"one-shot delay must be at least {(int)MinimumOneShotDelay.TotalSeconds} seconds";
                return false;
            }

            if (seconds > (long)MaximumOneShotDelay.TotalSeconds)
            {
                error = $"one-shot delay must be at most {(int)MaximumOneShotDelay.TotalDays} days";
                return false;
            }

            var delay = TimeSpan.FromSeconds(seconds);
            runAtUtc = nowUtc.Add(delay);
            return true;
        }

        var raw = Normalize(args.Str("run_at_utc"));
        if (raw is null || !DateTimeOffset.TryParse(raw, out var parsed))
        {
            error = "run_at_utc must be an ISO-8601 UTC timestamp";
            return false;
        }

        if (parsed.Offset != TimeSpan.Zero)
        {
            error = "run_at_utc must use UTC offset Z or +00:00";
            return false;
        }

        runAtUtc = parsed.ToUniversalTime();
        return ValidateOneShotDelay(runAtUtc - nowUtc, out error);
    }

    private static bool ValidateOneShotDelay(TimeSpan delay, out string error)
    {
        if (delay < MinimumOneShotDelay)
        {
            error = $"one-shot delay must be at least {(int)MinimumOneShotDelay.TotalSeconds} seconds";
            return false;
        }

        if (delay > MaximumOneShotDelay)
        {
            error = $"one-shot delay must be at most {(int)MaximumOneShotDelay.TotalDays} days";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static string ResolveWorkflowName(ScheduledAgentCreatePlannedRequest request) =>
        string.IsNullOrWhiteSpace(request.Reference.Name)
            ? ScheduledWorkflowAgentDefaults.DefaultWorkflowName
            : request.Reference.Name;

    private static string ResolveWorkflowPrompt(ScheduledAgentCreatePlannedRequest request) =>
        request.ScheduleMode == ScheduledAgentScheduleMode.OneShot && string.IsNullOrWhiteSpace(request.Reference.Name)
            ? request.OneShotMessage ?? string.Empty
            : request.ExecutionPrompt ?? "Execute the configured workflow and return plain text only.";

    private static WorkflowScheduleAuth BuildWorkflowScheduleAuth(
        SecretReference secretReference,
        ScheduledAgentApiKeyIssueResult issuedKey) =>
        new(ScheduledInvocationAgentKey: new WorkflowScheduleAgentKeyCredentialReference(
            secretReference.Clone(),
            issuedKey.ApiKeyId ?? string.Empty,
            issuedKey.KeyExpiresAtUnixMs));

    private static IReadOnlyDictionary<string, string> BuildWorkflowHeaders(
        ScheduledAgentCreatePlannedRequest request,
        ScheduledAgentApiKeyIssueResult issuedKey)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["scheduled_agent.agent_type"] = ScheduledWorkflowAgentDefaults.AgentType,
            ["scheduled_agent.conversation_id"] = request.ConversationId,
            ["scheduled_agent.output_format"] = request.OutputFormat.ToString(),
            ["scheduled_agent.api_key_id"] = issuedKey.ApiKeyId ?? string.Empty,
            [ScheduledAgentNyxProviderSlugHeader] = request.PrimaryOutboundSlug,
        };

        if (!string.IsNullOrWhiteSpace(request.Model))
            headers["workflow.llm.model"] = request.Model;
        if (!string.IsNullOrWhiteSpace(request.ProviderName))
            headers["workflow.llm.provider"] = request.ProviderName;
        if (request.MaxToolRounds.HasValue)
            headers["workflow.llm.max_tool_rounds"] = request.MaxToolRounds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (request.MaxHistoryMessages.HasValue)
            headers["workflow.llm.max_history_messages"] = request.MaxHistoryMessages.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return headers;
    }

    private static UserAgentCatalogUpsertCommand BuildCatalogUpsertCommand(
        ScheduledAgentCreatePlannedRequest request,
        ScheduledAgentApiKeyIssueResult issuedKey,
        SecretReference secretReference) =>
        new()
        {
            AgentId = request.AgentId,
            ConversationId = request.ConversationId,
            NyxProviderSlug = request.PrimaryOutboundSlug,
            NyxApiKey = string.Empty,
            NyxApiKeyReference = secretReference.Clone(),
            AgentType = ScheduledWorkflowAgentDefaults.AgentType,
            TemplateName = request.DisplayName ?? request.Reference.Name,
            ScopeId = request.ScopeId,
            ApiKeyId = issuedKey.ApiKeyId ?? string.Empty,
            ScheduleCron = request.ScheduleCron,
            ScheduleTimezone = request.ScheduleTimezone,
            TargetPlatform = request.ChannelTarget.Platform,
            ChannelAddress = UserAgentCatalogChannelAddress.FromParts(
                request.ChannelTarget.Platform,
                request.ChannelTarget.ProviderSlug,
                request.ConversationId,
                request.ChannelTarget.PrimaryAddressId,
                request.ChannelTarget.PrimaryAddressType,
                request.ChannelTarget.FallbackAddressId,
                request.ChannelTarget.FallbackAddressType),
            OwnerScope = request.Caller.Clone(),
            OutputFormat = request.OutputFormat,
        };

    private static bool TryParseOutputFormat(
        string? value,
        out ScheduledAgentOutputFormat outputFormat,
        out string error)
    {
        outputFormat = ScheduledAgentOutputFormat.Auto;
        error = string.Empty;
        var normalized = Normalize(value);
        if (normalized is null)
            return true;

        switch (normalized.ToLowerInvariant())
        {
            case "auto":
                outputFormat = ScheduledAgentOutputFormat.Auto;
                return true;
            case "text":
                outputFormat = ScheduledAgentOutputFormat.Text;
                return true;
            case "feishu_doc":
            case "feishu-doc":
                outputFormat = ScheduledAgentOutputFormat.FeishuDoc;
                return true;
            default:
                error = "output_format must be one of: auto, text, feishu_doc";
                return false;
        }
    }

    private sealed class CreatorArgs
    {
        private CreatorArgs(Dictionary<string, JsonElement> properties, string? error)
        {
            Properties = properties;
            Error = error;
        }

        public IReadOnlyDictionary<string, JsonElement> Properties { get; }
        public string? Error { get; }

        public static CreatorArgs Parse(string? json)
        {
            try
            {
                using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return new CreatorArgs([], "arguments must be a JSON object");

                var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var property in document.RootElement.EnumerateObject())
                    properties[property.Name] = property.Value.Clone();

                return new CreatorArgs(properties, null);
            }
            catch (JsonException ex)
            {
                return new CreatorArgs([], ex.Message);
            }
        }

        public string? Str(string name)
        {
            if (!Properties.TryGetValue(name, out var value))
                return null;

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };
        }

        // A JSON null or blank string is "not provided": tool-calling models routinely emit
        // every schema field and clear the unused ones (e.g. delay_seconds=180 alongside
        // run_at_utc=null or ""). Treating an empty unused key as provided made the one-shot
        // "exactly one of delay_seconds or run_at_utc" guard reject valid reminders.
        public bool HasProperty(string name) =>
            Properties.TryGetValue(name, out var value) && value.ValueKind switch
            {
                JsonValueKind.Null => false,
                JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
                _ => true,
            };

        public bool? Bool(string name)
        {
            if (!Properties.TryGetValue(name, out var value))
                return null;

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
                _ => null,
            };
        }

        public bool TryInt(string name, out int value)
        {
            value = 0;
            if (!Properties.TryGetValue(name, out var element))
                return false;

            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value))
                return true;

            return element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out value);
        }

        public bool TryLong(string name, out long value)
        {
            value = 0;
            if (!Properties.TryGetValue(name, out var element))
                return false;

            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value))
                return true;

            return element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), out value);
        }

        public bool TryDouble(string name, out double value)
        {
            value = 0;
            if (!Properties.TryGetValue(name, out var element))
                return false;

            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value))
                return true;

            return element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), out value);
        }

        public bool TryNyxIdServices(
            string name,
            out IReadOnlyList<NyxIdUserServiceCapabilityRef> values,
            out string error)
        {
            values = [];
            error = string.Empty;
            if (!Properties.TryGetValue(name, out var element))
                return true;
            if (element.ValueKind != JsonValueKind.Array)
            {
                error = $"{name} must be an array of exact NyxID service objects";
                return false;
            }

            var normalized = new List<NyxIdUserServiceCapabilityRef>();
            var seen = new HashSet<(string Id, string Slug)>();
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    item.EnumerateObject().Any(property =>
                        property.Name is not ("user_service_id" or "service_slug_snapshot")) ||
                    !item.TryGetProperty("user_service_id", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("service_slug_snapshot", out var slugElement) ||
                    slugElement.ValueKind != JsonValueKind.String)
                {
                    error = $"{name} must contain only user_service_id and service_slug_snapshot strings";
                    return false;
                }

                var id = idElement.GetString()?.Trim() ?? string.Empty;
                var slug = slugElement.GetString()?.Trim() ?? string.Empty;
                if (id.Length == 0 || slug.Length == 0)
                {
                    error = $"{name} requires non-empty user_service_id and service_slug_snapshot";
                    return false;
                }
                if (!seen.Add((id, slug)))
                    continue;
                normalized.Add(new NyxIdUserServiceCapabilityRef
                {
                    UserServiceId = id,
                    ServiceSlugSnapshot = slug,
                });
            }

            values = normalized;
            return true;
        }

        public bool HasNonEmptyArray(string name) =>
            Properties.TryGetValue(name, out var element) &&
            element.ValueKind == JsonValueKind.Array &&
            element.GetArrayLength() > 0;

    }
}

internal sealed record ScheduledAgentCreateMapResult(
    bool Success,
    ScheduledWorkflowAgentCreateRequest? Request,
    string? ErrorJson)
{
    public static ScheduledAgentCreateMapResult Failed(string error) =>
        new(false, null, JsonSerializer.Serialize(new { error = "validation_error", detail = error }));

    public static ScheduledAgentCreateMapResult JsonError(string error) =>
        new(false, null, JsonSerializer.Serialize(new { error = error }));

    public static ScheduledAgentCreateMapResult RawError(string json) =>
        new(false, null, json);
}

internal sealed record ScheduledAgentCreatePlanResult(
    bool Success,
    ScheduledAgentCreatePlannedRequest? Request,
    ScheduledAgentServiceRequirements? ServiceRequirements,
    string? ErrorJson)
{
    public static ScheduledAgentCreatePlanResult Failed(string error) =>
        new(false, null, null, JsonSerializer.Serialize(new { error = "validation_error", detail = error }));

    public static ScheduledAgentCreatePlanResult JsonError(string error) =>
        new(false, null, null, JsonSerializer.Serialize(new { error = error }));

    public static ScheduledAgentCreatePlanResult RawError(string json) =>
        new(false, null, null, json);
}

internal sealed record ScheduledAgentCreatePlannedRequest(
    string AgentId,
    ScheduledSkillReference Reference,
    string? DisplayName,
    string? ExecutionPrompt,
    string ScheduleCron,
    string ScheduleTimezone,
    ScheduledAgentScheduleMode ScheduleMode,
    DateTimeOffset? OneShotRunAtUtc,
    string? OneShotMessage,
    string ScopeId,
    string? ProviderName,
    string? Model,
    double? Temperature,
    int? MaxTokens,
    int? MaxToolRounds,
    int? MaxHistoryMessages,
    bool RequiresNyxidProxySuccess,
    ScheduledAgentOutputFormat OutputFormat,
    bool RunImmediately,
    string ConversationId,
    string PrimaryOutboundSlug,
    string? FailureNotificationSlug,
    ScheduledAgentChannelTarget ChannelTarget,
    OwnerScope Caller);

internal sealed record ScheduledAgentChannelTarget(
    string Platform,
    string ProviderSlug,
    string ConversationId,
    string PrimaryAddressId,
    string PrimaryAddressType,
    string? FallbackAddressId,
    string? FallbackAddressType);
