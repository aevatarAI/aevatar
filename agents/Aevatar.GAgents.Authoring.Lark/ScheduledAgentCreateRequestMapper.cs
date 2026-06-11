using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.GAgents.Authoring.Lark;

internal sealed class ScheduledAgentCreateRequestMapper
{
    private static readonly TimeZoneResolver ScheduleTimeZoneResolver = new();

    private static readonly HashSet<string> AllowedProperties = new(StringComparer.Ordinal)
    {
        "skill_ref",
        "schedule_cron",
        "schedule_timezone",
        "display_name",
        "execution_prompt",
        "provider_name",
        "model",
        "temperature",
        "max_tokens",
        "max_tool_rounds",
        "max_history_messages",
        "requires_nyxid_proxy_success",
        "required_service_slugs",
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

        var referenceParse = ScheduledSkillReference.Parse(args.Str("skill_ref"));
        if (referenceParse.ErrorJson is not null)
            return ScheduledAgentCreatePlanResult.RawError(referenceParse.ErrorJson);

        var reference = referenceParse.Reference!;
        var cron = Normalize(args.Str("schedule_cron"));
        if (cron is null)
            return ScheduledAgentCreatePlanResult.Failed("schedule_cron is required");

        var timezone = Normalize(args.Str("schedule_timezone"));
        if (timezone is null)
            return ScheduledAgentCreatePlanResult.Failed("schedule_timezone is required");

        if (!ScheduleTimeZoneResolver.TryResolve(timezone, out var scheduleTimeZone, out var timezoneError))
            return ScheduledAgentCreatePlanResult.Failed($"invalid_schedule_timezone: {timezoneError}");

        if (!ChannelScheduleCalculator.TryGetNextOccurrence(cron, scheduleTimeZone, DateTimeOffset.UtcNow, out _, out var cronError))
            return ScheduledAgentCreatePlanResult.Failed($"invalid_schedule_cron: {cronError}");

        var scopeId = Normalize(AgentToolRequestContext.ScopeId ?? AgentToolRequestContext.ChannelRegistrationScopeId);
        if (scopeId is null)
            return ScheduledAgentCreatePlanResult.Failed("scope_id_unavailable");

        if (!TryParseOutputFormat(args.Str("output_format"), out var outputFormat, out var outputFormatError))
            return ScheduledAgentCreatePlanResult.Failed(outputFormatError);

        if (!args.TryStringArray("required_service_slugs", out var requiredServiceSlugs, out var requiredServiceSlugsError))
            return ScheduledAgentCreatePlanResult.Failed(requiredServiceSlugsError);

        var conversationId = Normalize(AgentToolRequestContext.TryGetExternalMetadata(ChannelMetadataKeys.ConversationId));
        if (conversationId is null)
            return ScheduledAgentCreatePlanResult.Failed("conversation_id_unavailable");

        var primarySlug = Normalize(AgentToolRequestContext.TryGetExternalMetadata(ChannelMetadataKeys.LarkOutboundProxySlug));
        if (primarySlug is null)
            return ScheduledAgentCreatePlanResult.Failed("lark_outbound_provider_slug_unavailable");

        var target = LarkConversationTargets.BuildFromInboundWithFallback(
            AgentToolRequestContext.TryGetExternalMetadata(ChannelMetadataKeys.ChatType),
            conversationId,
            AgentToolRequestContext.ChannelSenderId,
            AgentToolRequestContext.TryGetExternalMetadata(ChannelMetadataKeys.LarkUnionId),
            AgentToolRequestContext.TryGetExternalMetadata(ChannelMetadataKeys.LarkChatId));

        if (string.IsNullOrWhiteSpace(target.Primary.ReceiveId) ||
            string.IsNullOrWhiteSpace(target.Primary.ReceiveIdType))
        {
            return ScheduledAgentCreatePlanResult.Failed("lark_receive_target_unavailable");
        }

        var failureSlug = Normalize(AgentToolRequestContext.TryGetExternalMetadata(ChannelMetadataKeys.InboundChannelBotProxySlug));
        if (string.Equals(failureSlug, primarySlug, StringComparison.Ordinal))
            failureSlug = null;

        return new ScheduledAgentCreatePlanResult(
            Success: true,
            Request: new ScheduledAgentCreatePlannedRequest(
                Reference: reference,
                DisplayName: Normalize(args.Str("display_name")),
                ExecutionPrompt: Normalize(args.Str("execution_prompt")),
                ScheduleCron: cron,
                ScheduleTimezone: timezone,
                ScopeId: scopeId,
                ProviderName: Normalize(args.Str("provider_name")),
                Model: Normalize(args.Str("model")),
                Temperature: args.TryDouble("temperature", out var temperature) ? temperature : null,
                MaxTokens: args.TryInt("max_tokens", out var maxTokens) ? maxTokens : null,
                MaxToolRounds: args.TryInt("max_tool_rounds", out var maxToolRounds) ? maxToolRounds : null,
                MaxHistoryMessages: args.TryInt("max_history_messages", out var maxHistoryMessages) ? maxHistoryMessages : null,
                RequiresNyxidProxySuccess: args.Bool("requires_nyxid_proxy_success") ?? false,
                OutputFormat: outputFormat,
                ExternalTriggerSources: args.ExternalTriggerSources("external_trigger_sources"),
                RunImmediately: args.Bool("run_immediately") ?? false,
                ConversationId: conversationId,
                PrimaryOutboundSlug: primarySlug,
                FailureNotificationSlug: failureSlug,
                ReceiveTarget: target,
                Caller: caller.Clone()),
            ServiceSlugs: new ScheduledAgentServiceSlugs(primarySlug, failureSlug, requiredServiceSlugs),
            ErrorJson: null);
    }

    public ScheduledAgentCreateMapResult Map(
        ScheduledAgentCreatePlannedRequest request,
        ScheduledAgentApiKeyIssueResult issuedKey)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(issuedKey);

        if (!issuedKey.Success || string.IsNullOrWhiteSpace(issuedKey.ApiKeyId) || string.IsNullOrWhiteSpace(issuedKey.FullKey))
            return ScheduledAgentCreateMapResult.Failed("api_key_unavailable");

        var command = new InitializeSkillRunnerCommand
        {
            SkillName = request.Reference.Name,
            TemplateName = request.DisplayName ?? request.Reference.Name,
            SkillRef = new SkillRunnerSkillReference
            {
                Name = request.Reference.Name,
                Source = SkillRunnerSkillSource.Ornn,
            },
            ExecutionPrompt = request.ExecutionPrompt ??
                              "Execute the configured Ornn skill and return plain text only.",
            ScheduleCron = request.ScheduleCron,
            ScheduleTimezone = request.ScheduleTimezone,
            Enabled = true,
            ScopeId = request.ScopeId,
            ProviderName = request.ProviderName ?? string.Empty,
            Model = request.Model ?? string.Empty,
            RequiresNyxidProxySuccess = request.RequiresNyxidProxySuccess,
            OutputFormat = request.OutputFormat,
            OutboundConfig = new SkillRunnerOutboundConfig
            {
                ConversationId = request.ConversationId,
                NyxProviderSlug = request.PrimaryOutboundSlug,
                NyxApiKey = issuedKey.FullKey,
                ApiKeyId = issuedKey.ApiKeyId,
                LarkReceiveId = request.ReceiveTarget.Primary.ReceiveId,
                LarkReceiveIdType = request.ReceiveTarget.Primary.ReceiveIdType,
                LarkReceiveIdFallback = request.ReceiveTarget.Fallback?.ReceiveId ?? string.Empty,
                LarkReceiveIdTypeFallback = request.ReceiveTarget.Fallback?.ReceiveIdType ?? string.Empty,
                OwnerScope = request.Caller.Clone(),
                FailureNotificationProviderSlug = request.FailureNotificationSlug ?? string.Empty,
                OutputFormat = request.OutputFormat,
            },
        };

        if (request.Temperature.HasValue)
            command.Temperature = request.Temperature.Value;
        if (request.MaxTokens.HasValue)
            command.MaxTokens = request.MaxTokens.Value;
        if (request.MaxToolRounds.HasValue)
            command.MaxToolRounds = request.MaxToolRounds.Value;
        if (request.MaxHistoryMessages.HasValue)
            command.MaxHistoryMessages = request.MaxHistoryMessages.Value;
        command.ExternalTriggerSources.AddRange(request.ExternalTriggerSources);

        return new ScheduledAgentCreateMapResult(
            Success: true,
            Command: command,
            RunImmediately: request.RunImmediately,
            ErrorJson: null);
    }

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static bool TryParseOutputFormat(
        string? value,
        out SkillRunnerOutputFormat outputFormat,
        out string error)
    {
        outputFormat = SkillRunnerOutputFormat.Auto;
        error = string.Empty;
        var normalized = Normalize(value);
        if (normalized is null)
            return true;

        switch (normalized.ToLowerInvariant())
        {
            case "auto":
                outputFormat = SkillRunnerOutputFormat.Auto;
                return true;
            case "text":
                outputFormat = SkillRunnerOutputFormat.Text;
                return true;
            case "feishu_doc":
            case "feishu-doc":
                outputFormat = SkillRunnerOutputFormat.FeishuDoc;
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

        public bool TryDouble(string name, out double value)
        {
            value = 0;
            if (!Properties.TryGetValue(name, out var element))
                return false;

            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value))
                return true;

            return element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), out value);
        }

        public bool TryStringArray(
            string name,
            out IReadOnlyList<string> values,
            out string error)
        {
            values = [];
            error = string.Empty;
            if (!Properties.TryGetValue(name, out var element))
                return true;

            if (element.ValueKind != JsonValueKind.Array)
            {
                error = $"{name} must be an array of strings";
                return false;
            }

            var normalized = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    error = $"{name} must contain only strings";
                    return false;
                }

                var value = item.GetString()?.Trim();
                if (string.IsNullOrEmpty(value) || !seen.Add(value))
                    continue;

                normalized.Add(value);
            }

            values = normalized;
            return true;
        }

        public IReadOnlyList<ExternalTriggerSource> ExternalTriggerSources(string name)
        {
            if (!Properties.TryGetValue(name, out var element) ||
                element.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var sources = new List<ExternalTriggerSource>();
            foreach (var sourceElement in element.EnumerateArray())
            {
                if (sourceElement.ValueKind != JsonValueKind.Object)
                    continue;

                var sourceId = ReadString(sourceElement, "source_id")?.Trim();
                if (string.IsNullOrWhiteSpace(sourceId))
                    continue;

                sources.Add(new ExternalTriggerSource
                {
                    SourceId = sourceId,
                    Kind = ParseKind(ReadString(sourceElement, "kind")),
                    Enabled = ReadBool(sourceElement, "enabled") ?? true,
                    DisplayName = ReadString(sourceElement, "display_name")?.Trim() ?? string.Empty,
                });
            }

            return sources;
        }

        private static ExternalTriggerSourceKind ParseKind(string? value) =>
            value?.Trim().ToLowerInvariant() switch
            {
                "channel_inbound" or "channel-inbound" => ExternalTriggerSourceKind.ChannelInbound,
                "webhook" => ExternalTriggerSourceKind.Webhook,
                _ => ExternalTriggerSourceKind.Webhook,
            };

        private static string? ReadString(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value))
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

        private static bool? ReadBool(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value))
                return null;

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
                _ => null,
            };
        }
    }
}

internal sealed record ScheduledAgentCreateMapResult(
    bool Success,
    InitializeSkillRunnerCommand? Command,
    bool RunImmediately,
    string? ErrorJson)
{
    public static ScheduledAgentCreateMapResult Failed(string error) =>
        new(false, null, false, JsonSerializer.Serialize(new { error = "validation_error", detail = error }));

    public static ScheduledAgentCreateMapResult JsonError(string error) =>
        new(false, null, false, JsonSerializer.Serialize(new { error = error }));

    public static ScheduledAgentCreateMapResult RawError(string json) =>
        new(false, null, false, json);
}

internal sealed record ScheduledAgentCreatePlanResult(
    bool Success,
    ScheduledAgentCreatePlannedRequest? Request,
    ScheduledAgentServiceSlugs? ServiceSlugs,
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
    ScheduledSkillReference Reference,
    string? DisplayName,
    string? ExecutionPrompt,
    string ScheduleCron,
    string ScheduleTimezone,
    string ScopeId,
    string? ProviderName,
    string? Model,
    double? Temperature,
    int? MaxTokens,
    int? MaxToolRounds,
    int? MaxHistoryMessages,
    bool RequiresNyxidProxySuccess,
    SkillRunnerOutputFormat OutputFormat,
    IReadOnlyList<ExternalTriggerSource> ExternalTriggerSources,
    bool RunImmediately,
    string ConversationId,
    string PrimaryOutboundSlug,
    string? FailureNotificationSlug,
    LarkReceiveTargetWithFallback ReceiveTarget,
    OwnerScope Caller);
