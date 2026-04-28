using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.LLMProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Platform.Lark;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Scheduled;

[GAgent("scheduled.skill-execution")]
public sealed class SkillExecutionGAgent : AIGAgentBase<SkillExecutionState>
{
    private readonly NyxIdApiClient? _nyxIdApiClient;
    private readonly IOwnerLlmConfigSource? _ownerLlmConfigSource;
    private readonly SkillRunnerToolFailureCounter _toolFailureCounter;

    public SkillExecutionGAgent(
        ILLMProviderFactory? llmProviderFactory = null,
        IEnumerable<IAIGAgentExecutionHook>? additionalHooks = null,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<IToolCallMiddleware>? toolMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
        IEnumerable<IAgentToolSource>? toolSources = null,
        NyxIdApiClient? nyxIdApiClient = null,
        IOwnerLlmConfigSource? ownerLlmConfigSource = null)
        : this(
            BuildToolMiddlewareChain(toolMiddlewares),
            llmProviderFactory,
            additionalHooks,
            agentMiddlewares,
            llmMiddlewares,
            toolSources,
            nyxIdApiClient,
            ownerLlmConfigSource)
    {
    }

    private SkillExecutionGAgent(
        ToolMiddlewareChain toolMiddlewareChain,
        ILLMProviderFactory? llmProviderFactory,
        IEnumerable<IAIGAgentExecutionHook>? additionalHooks,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares,
        IEnumerable<IAgentToolSource>? toolSources,
        NyxIdApiClient? nyxIdApiClient,
        IOwnerLlmConfigSource? ownerLlmConfigSource)
        : base(
            llmProviderFactory,
            additionalHooks,
            agentMiddlewares,
            toolMiddlewareChain.Middlewares,
            llmMiddlewares,
            toolSources)
    {
        _nyxIdApiClient = nyxIdApiClient;
        _ownerLlmConfigSource = ownerLlmConfigSource;
        _toolFailureCounter = toolMiddlewareChain.Counter;
    }

    private readonly record struct ToolMiddlewareChain(
        IReadOnlyList<IToolCallMiddleware> Middlewares,
        SkillRunnerToolFailureCounter Counter);

    internal SkillRunnerToolFailureCounter ToolFailureCounterForTesting => _toolFailureCounter;

    private static ToolMiddlewareChain BuildToolMiddlewareChain(
        IEnumerable<IToolCallMiddleware>? input)
    {
        var counter = new SkillRunnerToolFailureCounter();
        var combined = (input ?? Array.Empty<IToolCallMiddleware>()).ToList();
        combined.Add(new NyxIdProxyToolFailureCountingMiddleware(counter));
        return new ToolMiddlewareChain(combined, counter);
    }

    protected override AIAgentConfigStateOverrides ExtractStateConfigOverrides(SkillExecutionState state)
    {
        return new AIAgentConfigStateOverrides
        {
            HasProviderName = !string.IsNullOrWhiteSpace(state.ProviderName),
            ProviderName = state.ProviderName,
            HasModel = !string.IsNullOrWhiteSpace(state.Model),
            Model = state.Model,
            HasSystemPrompt = !string.IsNullOrWhiteSpace(state.SkillContent),
            SystemPrompt = state.SkillContent,
            HasTemperature = state.HasTemperature,
            Temperature = state.HasTemperature ? state.Temperature : null,
            HasMaxTokens = state.HasMaxTokens,
            MaxTokens = state.HasMaxTokens ? state.MaxTokens : null,
            HasMaxToolRounds = state.HasMaxToolRounds,
            MaxToolRounds = state.HasMaxToolRounds ? state.MaxToolRounds : null,
            HasMaxHistoryMessages = state.HasMaxHistoryMessages,
            MaxHistoryMessages = state.HasMaxHistoryMessages ? state.MaxHistoryMessages : null,
        };
    }

    protected override SkillExecutionState TransitionState(SkillExecutionState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<SkillExecutionStartedEvent>(ApplyStarted)
            .On<SkillExecutionRetryStartedEvent>(ApplyRetryStarted)
            .On<SkillExecutionCompletedEvent>(ApplyCompleted)
            .On<SkillExecutionFailedEvent>(ApplyFailed)
            .OrCurrent();

    [EventHandler]
    public async Task HandleStartAsync(StartSkillExecutionCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.SkillContent))
        {
            Logger.LogWarning("Skill execution {ActorId} ignored because skill_content is empty", Id);
            return;
        }

        if (!string.IsNullOrWhiteSpace(State.Status))
        {
            Logger.LogInformation(
                "Skill execution {ActorId} ignored duplicate start because status is {Status}",
                Id,
                State.Status);
            return;
        }

        var started = new SkillExecutionStartedEvent
        {
            DefinitionId = command.DefinitionId ?? string.Empty,
            ScheduledAt = command.ScheduledAt,
            StartedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Reason = command.Reason ?? string.Empty,
            SkillContent = command.SkillContent ?? string.Empty,
            ExecutionPrompt = command.ExecutionPrompt ?? string.Empty,
            OutboundConfig = command.OutboundConfig?.Clone() ?? new SkillRunnerOutboundConfig(),
            ScopeId = command.ScopeId ?? string.Empty,
            ProviderName = command.ProviderName ?? string.Empty,
            Model = command.Model ?? string.Empty,
        };
        if (command.HasTemperature) started.Temperature = command.Temperature;
        if (command.HasMaxTokens) started.MaxTokens = command.MaxTokens;
        if (command.HasMaxToolRounds) started.MaxToolRounds = command.MaxToolRounds;
        if (command.HasMaxHistoryMessages) started.MaxHistoryMessages = command.MaxHistoryMessages;

        await PersistDomainEventAsync(started);
        if (SkillDefinitionDefaults.MaxRetryAttempts > 0)
            await ScheduleRetryAsync(State.RetryAttempts + 1, CancellationToken.None);

        var now = DateTimeOffset.UtcNow;
        try
        {
            var output = await ExecuteSkillAsync(now, command.Reason, CancellationToken.None);

            await PersistDomainEventAsync(new SkillExecutionCompletedEvent
            {
                CompletedAt = Timestamp.FromDateTimeOffset(now),
                Output = output,
            });

            await UpdateRegistryExecutionAsync(
                SkillDefinitionDefaults.StatusRunning,
                Timestamp.FromDateTimeOffset(now),
                0,
                string.Empty,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Skill execution {ActorId} failed (attempt={Attempt})",
                Id,
                State.RetryAttempts);

            if (State.RetryAttempts < SkillDefinitionDefaults.MaxRetryAttempts)
            {
                return;
            }

            await PersistDomainEventAsync(new SkillExecutionFailedEvent
            {
                FailedAt = Timestamp.FromDateTimeOffset(now),
                Error = ex.Message,
                RetryAttempt = State.RetryAttempts,
            });

            await TrySendFailureAsync(ex.Message, CancellationToken.None);
            await UpdateRegistryExecutionAsync(
                SkillDefinitionDefaults.StatusError,
                Timestamp.FromDateTimeOffset(now),
                1,
                ex.Message,
                CancellationToken.None);
        }
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleRetryAsync(RetrySkillExecutionCommand command)
    {
        if (!string.Equals(State.Status, "running", StringComparison.Ordinal))
        {
            Logger.LogInformation(
                "Skill execution {ActorId} ignored retry attempt {Attempt} because status is {Status}",
                Id,
                command.RetryAttempt,
                string.IsNullOrWhiteSpace(State.Status) ? "(empty)" : State.Status);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        await PersistDomainEventAsync(new SkillExecutionRetryStartedEvent
        {
            RetryAttempt = command.RetryAttempt,
            StartedAt = Timestamp.FromDateTimeOffset(now),
        });

        try
        {
            var output = await ExecuteSkillAsync(now, "retry", CancellationToken.None);

            await PersistDomainEventAsync(new SkillExecutionCompletedEvent
            {
                CompletedAt = Timestamp.FromDateTimeOffset(now),
                Output = output,
            });

            await UpdateRegistryExecutionAsync(
                SkillDefinitionDefaults.StatusRunning,
                Timestamp.FromDateTimeOffset(now),
                0,
                string.Empty,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Skill execution {ActorId} retry failed (attempt={Attempt})",
                Id,
                command.RetryAttempt);

            if (command.RetryAttempt < SkillDefinitionDefaults.MaxRetryAttempts)
            {
                await ScheduleRetryAsync(command.RetryAttempt + 1, CancellationToken.None);
                return;
            }

            await PersistDomainEventAsync(new SkillExecutionFailedEvent
            {
                FailedAt = Timestamp.FromDateTimeOffset(now),
                Error = ex.Message,
                RetryAttempt = command.RetryAttempt,
            });

            await TrySendFailureAsync(ex.Message, CancellationToken.None);
            await UpdateRegistryExecutionAsync(
                SkillDefinitionDefaults.StatusError,
                Timestamp.FromDateTimeOffset(now),
                command.RetryAttempt + 1,
                ex.Message,
                CancellationToken.None);
        }
    }

    private async Task ScheduleRetryAsync(int retryAttempt, CancellationToken ct)
    {
        await ScheduleSelfDurableTimeoutAsync(
            SkillDefinitionDefaults.RetryCallbackId,
            SkillDefinitionDefaults.RetryBackoff,
            new RetrySkillExecutionCommand { RetryAttempt = retryAttempt },
            ct: ct);
        Logger.LogInformation(
            "Skill execution {ActorId} scheduled retry attempt {Attempt} in {Backoff}",
            Id, retryAttempt, SkillDefinitionDefaults.RetryBackoff);
    }

    private async Task<string> ExecuteSkillAsync(DateTimeOffset now, string? reason, CancellationToken ct)
    {
        _toolFailureCounter.Reset();

        var prompt = BuildExecutionPrompt(now, reason);
        var metadata = await BuildExecutionMetadataAsync(ct);
        var requestId = Guid.NewGuid().ToString("N");
        var content = new StringBuilder();

        var sink = TryCreateStreamingSink();
        try
        {
            await foreach (var chunk in ChatStreamAsync(prompt, requestId, metadata, ct))
            {
                if (string.IsNullOrEmpty(chunk.DeltaContent))
                    continue;
                content.Append(chunk.DeltaContent);
                if (sink is not null)
                    await sink.OnDeltaAsync(content.ToString(), ct);
            }

            var output = content.ToString().Trim();
            if (string.IsNullOrWhiteSpace(output))
                output = "No update generated.";

            EnsureToolStatusAllowsCompletion(_toolFailureCounter.FailureCount, _toolFailureCounter.SuccessCount);

            var chunks = SkillRunnerOutputChunker.Split(output);
            await DispatchOutputChunksAsync(sink, chunks, ct);

            return output;
        }
        finally
        {
            sink?.Dispose();
        }
    }

    private async Task DispatchOutputChunksAsync(
        SkillRunnerStreamingReplySink? sink,
        IReadOnlyList<string> chunks,
        CancellationToken ct)
    {
        if (chunks.Count == 0)
            return;

        if (sink is not null)
            await sink.FinalizeAsync(chunks[0], ct);
        else
            await SendOutputAsync(chunks[0], ct);

        for (var i = 1; i < chunks.Count; i++)
            await SendOutputAsync(chunks[i], ct);
    }

    private SkillRunnerStreamingReplySink? TryCreateStreamingSink()
    {
        var client = _nyxIdApiClient ?? Services.GetService<NyxIdApiClient>();
        if (client is null)
        {
            Logger.LogWarning(
                "Skill execution {ActorId} has no NyxIdApiClient registered; streaming-edit delivery is disabled",
                Id);
            return null;
        }

        if (string.IsNullOrWhiteSpace(State.OutboundConfig?.NyxApiKey) ||
            string.IsNullOrWhiteSpace(State.OutboundConfig?.NyxProviderSlug) ||
            string.IsNullOrWhiteSpace(State.OutboundConfig?.ConversationId))
        {
            Logger.LogWarning(
                "Skill execution {ActorId} has incomplete outbound config; streaming-edit delivery is disabled",
                Id);
            return null;
        }

        var primary = LarkConversationTargets.Resolve(
            State.OutboundConfig.LarkReceiveId,
            State.OutboundConfig.LarkReceiveIdType,
            State.OutboundConfig.ConversationId);

        var fallbackId = State.OutboundConfig.LarkReceiveIdFallback?.Trim();
        var fallbackType = State.OutboundConfig.LarkReceiveIdTypeFallback?.Trim();
        LarkReceiveTarget? fallback = null;
        if (!string.IsNullOrEmpty(fallbackId) && !string.IsNullOrEmpty(fallbackType))
            fallback = new LarkReceiveTarget(fallbackId, fallbackType, FellBackToPrefixInference: false);

        return new SkillRunnerStreamingReplySink(
            client,
            State.OutboundConfig.NyxApiKey,
            State.OutboundConfig.NyxProviderSlug,
            primary,
            fallback,
            BuildLarkRejectionMessage,
            SkillDefinitionDefaults.StreamingEditThrottle,
            TimeProvider.System,
            Logger);
    }

    internal static void EnsureToolStatusAllowsCompletion(int failureCount, int successCount)
    {
        if (failureCount > 0 && successCount == 0)
        {
            throw new InvalidOperationException(
                $"All {failureCount} nyxid_proxy tool call(s) in this run failed; refusing to record an empty-day report as a successful execution. " +
                "Inspect the previous attempt's tool output for the underlying NyxID/upstream error envelope.");
        }
    }

    private Task SendOutputAsync(string output, CancellationToken ct) =>
        SendOutputAsync(output, providerSlugOverride: null, ct);

    private async Task SendOutputAsync(string output, string? providerSlugOverride, CancellationToken ct)
    {
        var client = _nyxIdApiClient ?? Services.GetService<NyxIdApiClient>();
        if (client is null)
        {
            Logger.LogWarning("Skill execution {ActorId} has no NyxIdApiClient registered; skipping outbound delivery", Id);
            return;
        }

        if (string.IsNullOrWhiteSpace(State.OutboundConfig?.NyxApiKey) ||
            string.IsNullOrWhiteSpace(State.OutboundConfig?.NyxProviderSlug) ||
            string.IsNullOrWhiteSpace(State.OutboundConfig?.ConversationId))
        {
            Logger.LogWarning("Skill execution {ActorId} has incomplete outbound config; skipping outbound delivery", Id);
            return;
        }

        var slug = string.IsNullOrWhiteSpace(providerSlugOverride)
            ? State.OutboundConfig.NyxProviderSlug
            : providerSlugOverride!;

        var deliveryTarget = LarkConversationTargets.Resolve(
            State.OutboundConfig.LarkReceiveId,
            State.OutboundConfig.LarkReceiveIdType,
            State.OutboundConfig.ConversationId);

        var outcome = await TrySendWithFallbackAsync(client, output, slug, deliveryTarget, ct);

        if (!outcome.Succeeded)
        {
            throw new InvalidOperationException(BuildLarkRejectionMessage(outcome.LarkCode, outcome.Detail));
        }
    }

    private readonly record struct SendOutcome(bool Succeeded, int? LarkCode, string Detail)
    {
        public static SendOutcome Success() => new(true, null, string.Empty);
        public static SendOutcome Failed(int? larkCode, string detail) => new(false, larkCode, detail);
    }

    private async Task<SendOutcome> TrySendWithFallbackAsync(
        NyxIdApiClient client,
        string output,
        string slug,
        LarkReceiveTarget primary,
        CancellationToken ct)
    {
        var primaryResponse = await SendOutboundAsync(client, output, slug, primary, ct);
        if (!LarkProxyResponse.TryGetError(primaryResponse, out var larkCode, out var detail))
            return SendOutcome.Success();

        if (larkCode != LarkBotErrorCodes.BotNotInChat)
            return SendOutcome.Failed(larkCode, detail);

        var fallbackId = State.OutboundConfig.LarkReceiveIdFallback?.Trim();
        var fallbackType = State.OutboundConfig.LarkReceiveIdTypeFallback?.Trim();
        if (string.IsNullOrEmpty(fallbackId) || string.IsNullOrEmpty(fallbackType))
            return SendOutcome.Failed(larkCode, detail);

        Logger.LogInformation(
            "Skill execution {ActorId} primary delivery target rejected as `bot not in chat` (code 230002); retrying with fallback",
            Id);

        var fallbackTarget = new LarkReceiveTarget(fallbackId, fallbackType, FellBackToPrefixInference: false);
        var fallbackResponse = await SendOutboundAsync(client, output, slug, fallbackTarget, ct);
        if (!LarkProxyResponse.TryGetError(fallbackResponse, out var fallbackCode, out var fallbackDetail))
            return SendOutcome.Success();

        return SendOutcome.Failed(fallbackCode, fallbackDetail);
    }

    private async Task<string> SendOutboundAsync(
        NyxIdApiClient client,
        string output,
        string slug,
        LarkReceiveTarget target,
        CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            receive_id = target.ReceiveId,
            msg_type = "text",
            content = JsonSerializer.Serialize(new { text = output }),
        });

        return await client.ProxyRequestAsync(
            State.OutboundConfig.NyxApiKey,
            slug,
            $"open-apis/im/v1/messages?receive_id_type={target.ReceiveIdType}",
            "POST", body, null, ct);
    }

    private static string BuildLarkRejectionMessage(int? larkCode, string detail)
    {
        if (larkCode == LarkBotErrorCodes.OpenIdCrossApp)
        {
            return
                $"Lark message delivery rejected (code={larkCode}): {detail}. " +
                "This agent was created before cross-app union_id ingress existed; " +
                "delete and recreate it (`/agents` → Delete → `/daily`) to pick up the cross-app safe target.";
        }

        if (larkCode == LarkBotErrorCodes.UserIdCrossTenant)
        {
            return
                $"Lark message delivery rejected (code={larkCode}): {detail}. " +
                "The outbound Lark app is in a different tenant than the inbound app, so " +
                "user-id translation is impossible. Delete and recreate the agent " +
                "(`/agents` → Delete → `/daily`) so the new chat_id-preferred outbound path " +
                "takes effect, or align the NyxID `s/api-lark-bot` proxy with the channel-bot that " +
                "received the inbound event.";
        }

        return larkCode is { } code
            ? $"Lark message delivery rejected (code={code}): {detail}"
            : $"Lark message delivery rejected: {detail}";
    }

    private async Task TrySendFailureAsync(string error, CancellationToken ct)
    {
        var message = $"Skill execution failed: {error}";
        var failureSlug = State.OutboundConfig?.FailureNotificationProviderSlug?.Trim();
        var primarySlug = State.OutboundConfig?.NyxProviderSlug?.Trim();

        if (!string.IsNullOrEmpty(failureSlug) &&
            !string.Equals(failureSlug, primarySlug, StringComparison.Ordinal))
        {
            try
            {
                await SendOutputAsync(message, providerSlugOverride: failureSlug, ct);
                return;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    ex,
                    "Skill execution {ActorId} failed-notification via failure-notification slug rejected; falling back to primary slug",
                    Id);
            }
        }

        try { await SendOutputAsync(message, providerSlugOverride: null, ct); }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Skill execution {ActorId} failed to send failure notification", Id);
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> BuildExecutionMetadataAsync(CancellationToken ct)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = State.OutboundConfig?.NyxApiKey ?? string.Empty,
            [ChannelMetadataKeys.ConversationId] = State.OutboundConfig?.ConversationId ?? string.Empty,
        };
        if (!string.IsNullOrWhiteSpace(State.ScopeId))
            metadata["scope_id"] = State.ScopeId;

        await OwnerLlmConfigApplier.ApplyAsync(
            metadata,
            State.ScopeId,
            _ownerLlmConfigSource,
            Logger,
            actorLabel: "Skill execution",
            actorId: Id,
            ct);
        return metadata;
    }

    private string BuildExecutionPrompt(DateTimeOffset now, string? reason)
    {
        var prompt = string.IsNullOrWhiteSpace(State.ExecutionPrompt)
            ? "Execute the configured skill now and return plain text only."
            : State.ExecutionPrompt;
        return $"{prompt}\nCurrent UTC time: {now:O}\nTrigger reason: {(string.IsNullOrWhiteSpace(reason) ? "manual" : reason)}";
    }

    private async Task UpdateRegistryExecutionAsync(
        string status, Timestamp? lastRunAt,
        int errorCount, string? lastError, CancellationToken ct)
    {
        var definitionId = State.DefinitionId;
        if (string.IsNullOrWhiteSpace(definitionId))
            return;

        var command = new UserAgentCatalogExecutionUpdateCommand
        {
            AgentId = definitionId, Status = status,
            LastRunAt = lastRunAt,
            ErrorCount = errorCount, LastError = lastError ?? string.Empty,
        };
        await UserAgentCatalogStoreCommands.DispatchExecutionUpdateAsync(Services, definitionId, command, ct);
    }

    private static SkillExecutionState ApplyStarted(SkillExecutionState current, SkillExecutionStartedEvent evt)
    {
        var next = current.Clone();
        next.DefinitionId = evt.DefinitionId ?? string.Empty;
        next.ScheduledAt = evt.ScheduledAt;
        next.StartedAt = evt.StartedAt;
        next.Status = "running";
        next.SkillContent = evt.SkillContent ?? string.Empty;
        next.ExecutionPrompt = evt.ExecutionPrompt ?? string.Empty;
        next.OutboundConfig = evt.OutboundConfig?.Clone() ?? new SkillRunnerOutboundConfig();
        next.ScopeId = evt.ScopeId ?? string.Empty;
        next.ProviderName = evt.ProviderName ?? string.Empty;
        next.Model = evt.Model ?? string.Empty;
        if (evt.HasTemperature) next.Temperature = evt.Temperature;
        else next.ClearTemperature();
        if (evt.HasMaxTokens) next.MaxTokens = evt.MaxTokens;
        else next.ClearMaxTokens();
        next.MaxToolRounds = evt.HasMaxToolRounds ? evt.MaxToolRounds : SkillDefinitionDefaults.DefaultMaxToolRounds;
        next.MaxHistoryMessages = evt.HasMaxHistoryMessages ? evt.MaxHistoryMessages : SkillDefinitionDefaults.DefaultMaxHistoryMessages;
        return next;
    }

    private static SkillExecutionState ApplyRetryStarted(SkillExecutionState current, SkillExecutionRetryStartedEvent evt)
    {
        var next = current.Clone();
        next.StartedAt = evt.StartedAt;
        next.RetryAttempts = evt.RetryAttempt;
        next.Status = "running";
        return next;
    }

    private static SkillExecutionState ApplyCompleted(SkillExecutionState current, SkillExecutionCompletedEvent evt)
    {
        var next = current.Clone();
        next.CompletedAt = evt.CompletedAt;
        next.Output = evt.Output ?? string.Empty;
        next.Status = "completed";
        return next;
    }

    private static SkillExecutionState ApplyFailed(SkillExecutionState current, SkillExecutionFailedEvent evt)
    {
        var next = current.Clone();
        next.CompletedAt = evt.FailedAt;
        next.Errors.Add(evt.Error ?? string.Empty);
        next.RetryAttempts = evt.RetryAttempt;
        next.Status = "failed";
        return next;
    }
}
