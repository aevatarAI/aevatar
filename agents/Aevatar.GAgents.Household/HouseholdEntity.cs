using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Hooks;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Household;

/// <summary>
/// HouseholdEntity — autonomous home AI agent.
/// Implements Perceive-Reason-Act loop driven by stream events.
/// </summary>
public class HouseholdEntity : AIGAgentBase<HouseholdEntityState>
{
    public HouseholdEntity(
        ILLMProviderFactory? llmProviderFactory = null,
        IEnumerable<IAIGAgentExecutionHook>? additionalHooks = null,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<IToolCallMiddleware>? toolMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
        IEnumerable<IAgentToolSource>? toolSources = null)
        : base(llmProviderFactory, additionalHooks, agentMiddlewares, toolMiddlewares, llmMiddlewares, toolSources)
    {
    }

    // ─── Activation ───

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(State.ProviderName))
        {
            await PersistDomainEventAsync(new HouseholdInitializedEvent
            {
                ProviderName = HouseholdEntityDefaults.DefaultProviderName,
                SystemPrompt = HouseholdEntitySystemPrompt.Base,
                MaxToolRounds = HouseholdEntityDefaults.DefaultMaxToolRounds,
                MaxHistoryMessages = HouseholdEntityDefaults.DefaultMaxHistoryMessages,
            });
        }

        await base.OnActivateAsync(ct);
    }

    // ─── Config merge ───

    protected override AIAgentConfigStateOverrides ExtractStateConfigOverrides(HouseholdEntityState state)
    {
        return new AIAgentConfigStateOverrides
        {
            HasProviderName = !string.IsNullOrWhiteSpace(state.ProviderName),
            ProviderName = state.ProviderName,
            HasModel = !string.IsNullOrWhiteSpace(state.Model),
            Model = state.Model,
            HasSystemPrompt = !string.IsNullOrWhiteSpace(state.SystemPrompt),
            SystemPrompt = state.SystemPrompt,
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

    // ─── Dynamic system prompt (inject environment, actions, memories) ───

    protected override string DecorateSystemPrompt(string basePrompt)
    {
        var sb = new StringBuilder(basePrompt);

        // Current environment
        var env = State.Environment;
        if (env != null)
        {
            sb.AppendLine();
            sb.AppendLine("## Current Environment");
            sb.AppendLine($"- Temperature: {env.Temperature:F1}°C");
            sb.AppendLine($"- Humidity: {env.Humidity:F0}%");
            sb.AppendLine($"- Light level: {env.LightLevel:F0}%");
            sb.AppendLine($"- Motion detected: {env.MotionDetected}");
            sb.AppendLine($"- Time of day: {env.TimeOfDay}");
            sb.AppendLine($"- Day of week: {env.DayOfWeek}");

            if (!string.IsNullOrWhiteSpace(env.SceneDescription))
                sb.AppendLine($"- Scene: {env.SceneDescription}");

            if (env.DeviceStates.Count > 0)
            {
                sb.AppendLine("- Device states:");
                foreach (var kv in env.DeviceStates)
                    sb.AppendLine($"  - {kv.Key}: {kv.Value}");
            }
        }

        // Recent actions
        if (State.RecentActions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Recent Actions");
            foreach (var action in State.RecentActions)
                sb.AppendLine($"- [{action.Agent}] {action.Action}: {action.Detail} (reason: {action.Reasoning})");
        }

        // Memories
        if (State.Memories.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Your Memories");
            foreach (var mem in State.Memories)
                sb.AppendLine($"- {mem.Key}: {mem.Content} (reinforced {mem.Reinforcement}x)");
        }

        // Current mode
        sb.AppendLine();
        sb.AppendLine($"## Status: mode={State.CurrentMode ?? "active"}, reasoning_count_today={State.ReasoningCountToday}");

        return sb.ToString();
    }

    // ─── Event Handlers (Perceive) ───

    [EventHandler]
    public async Task HandleSensorData(SensorDataEvent evt)
    {
        var prev = State.Environment ?? new EnvironmentSnapshot();
        var shouldReason = ShouldTriggerOnSensorChange(prev, evt);

        await PersistDomainEventAsync(evt);

        if (shouldReason)
            await RunReasoningAsync("Sensor data changed significantly.");
    }

    [EventHandler]
    public async Task HandleCameraScene(CameraSceneEvent evt)
    {
        var prev = State.Environment?.SceneDescription ?? "";
        var changed = !string.Equals(prev, evt.SceneDescription, StringComparison.Ordinal);

        await PersistDomainEventAsync(evt);

        if (changed && !string.IsNullOrWhiteSpace(evt.SceneDescription))
            await RunReasoningAsync("Visual scene changed.");
    }

    [EventHandler]
    public async Task HandleChat(HouseholdChatEvent evt)
    {
        // Telegram/channel messages always trigger reasoning
        await RunReasoningAsync(
            $"Message from user: {evt.Prompt}",
            evt.Metadata.Count > 0
                ? new Dictionary<string, string>(evt.Metadata, StringComparer.Ordinal)
                : null);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleHeartbeat(HeartbeatEvent _)
    {
        await RunReasoningAsync("Periodic heartbeat — check if anything needs attention.");
    }

    [EventHandler]
    public Task HandleTimePeriodChanged(TimePeriodChangedEvent evt)
    {
        return PersistDomainEventAsync(evt);
    }

    [EventHandler]
    public Task HandleSafetyStateChanged(SafetyStateChangedEvent evt)
    {
        return PersistDomainEventAsync(evt);
    }

    // ─── Reasoning (Reason + Act) ───

    internal async Task RunReasoningAsync(
        string trigger,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        // Safety checks
        if (!CanReason(out var reason))
        {
            Logger.LogInformation("[Household] Reasoning blocked: {Reason}. trigger={Trigger}", reason, trigger);
            return;
        }

        Logger.LogInformation("[Household] Reasoning triggered: {Trigger}", trigger);

        var prompt = $"[Trigger: {trigger}]\nBased on the current environment and your memories, decide whether to act.";

        try
        {
            var response = await ChatAsync(prompt, requestId: null, metadata);

            var isNoAction = response != null &&
                             response.Contains("NO_ACTION", StringComparison.OrdinalIgnoreCase);

            await PersistDomainEventAsync(new ReasoningCompletedEvent
            {
                Decision = isNoAction ? "NO_ACTION" : "ACTION",
                Reasoning = response ?? "",
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });

            Logger.LogInformation(
                "[Household] Reasoning complete: decision={Decision}",
                isNoAction ? "NO_ACTION" : "ACTION");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Household] Reasoning failed. trigger={Trigger}", trigger);
        }
    }

    // ─── Trigger condition checks ───

    internal bool ShouldTriggerOnSensorChange(EnvironmentSnapshot prev, SensorDataEvent evt)
    {
        if (Math.Abs(evt.Temperature - prev.Temperature) > HouseholdEntityDefaults.TemperatureChangeThreshold)
            return true;

        if (prev.LightLevel > 0 &&
            Math.Abs(evt.LightLevel - prev.LightLevel) / prev.LightLevel > HouseholdEntityDefaults.LightLevelChangeThreshold)
            return true;

        if (!prev.MotionDetected && evt.MotionDetected)
            return true;

        return false;
    }

    internal bool CanReason(out string blockedReason)
    {
        blockedReason = "";

        // Kill switch
        if (State.Safety?.KillSwitch == true)
        {
            blockedReason = "kill_switch active";
            return false;
        }

        // Frozen mode
        if (string.Equals(State.CurrentMode, "frozen", StringComparison.OrdinalIgnoreCase))
        {
            blockedReason = "mode is frozen";
            return false;
        }

        // Sleeping mode
        if (string.Equals(State.CurrentMode, "sleeping", StringComparison.OrdinalIgnoreCase))
        {
            blockedReason = "mode is sleeping";
            return false;
        }

        // Rate limit: actions per minute
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var safety = State.Safety;
        if (safety != null)
        {
            var windowElapsed = now - safety.MinuteWindowStartTs;
            if (windowElapsed < 60 && safety.ActionsThisMinute >= HouseholdEntityDefaults.MaxActionsPerMinute)
            {
                blockedReason = "action rate limit exceeded";
                return false;
            }
        }

        // Debounce: too soon since last reasoning
        if (State.LastReasoningTs > 0 &&
            now - State.LastReasoningTs < HouseholdEntityDefaults.ReasoningDebounceSeconds)
        {
            blockedReason = "reasoning debounce";
            return false;
        }

        return true;
    }

    // ─── State transitions ───

    protected override HouseholdEntityState TransitionState(HouseholdEntityState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<HouseholdInitializedEvent>(ApplyInitialized)
            .On<SensorDataEvent>(ApplySensorData)
            .On<CameraSceneEvent>(ApplyCameraScene)
            .On<ReasoningCompletedEvent>(ApplyReasoningCompleted)
            .On<ActionExecutedEvent>(ApplyActionExecuted)
            .On<MemoryUpdatedEvent>(ApplyMemoryUpdated)
            .On<SafetyStateChangedEvent>(ApplySafetyChanged)
            .On<TimePeriodChangedEvent>(ApplyTimePeriodChanged)
            .OrCurrent();

    private static HouseholdEntityState ApplyInitialized(
        HouseholdEntityState current, HouseholdInitializedEvent evt)
    {
        var next = current.Clone();
        next.ProviderName = evt.ProviderName;
        next.Model = evt.Model;
        next.SystemPrompt = evt.SystemPrompt;
        if (evt.HasTemperature) next.Temperature = evt.Temperature;
        if (evt.HasMaxTokens) next.MaxTokens = evt.MaxTokens;
        if (evt.HasMaxToolRounds) next.MaxToolRounds = evt.MaxToolRounds;
        if (evt.HasMaxHistoryMessages) next.MaxHistoryMessages = evt.MaxHistoryMessages;
        next.CurrentMode = "active";
        next.Environment ??= new EnvironmentSnapshot();
        next.Safety ??= new SafetyState();
        return next;
    }

    private static HouseholdEntityState ApplySensorData(
        HouseholdEntityState current, SensorDataEvent evt)
    {
        var next = current.Clone();
        var env = next.Environment ?? new EnvironmentSnapshot();
        env.Temperature = evt.Temperature;
        env.Humidity = evt.Humidity;
        env.LightLevel = evt.LightLevel;
        env.MotionDetected = evt.MotionDetected;
        env.LastSensorUpdateTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        next.Environment = env;
        return next;
    }

    private static HouseholdEntityState ApplyCameraScene(
        HouseholdEntityState current, CameraSceneEvent evt)
    {
        var next = current.Clone();
        var env = next.Environment ?? new EnvironmentSnapshot();
        env.SceneDescription = evt.SceneDescription;
        env.LastCameraUpdateTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        next.Environment = env;
        return next;
    }

    private static HouseholdEntityState ApplyReasoningCompleted(
        HouseholdEntityState current, ReasoningCompletedEvent evt)
    {
        var next = current.Clone();
        next.LastReasoningTs = evt.Timestamp;
        next.ReasoningCountToday++;
        return next;
    }

    private static HouseholdEntityState ApplyActionExecuted(
        HouseholdEntityState current, ActionExecutedEvent evt)
    {
        var next = current.Clone();
        if (evt.Action != null)
        {
            next.RecentActions.Add(evt.Action);
            while (next.RecentActions.Count > HouseholdEntityDefaults.MaxRecentActions)
                next.RecentActions.RemoveAt(0);
        }

        // Update actions-per-minute counter
        var safety = next.Safety ?? new SafetyState();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now - safety.MinuteWindowStartTs >= 60)
        {
            safety.ActionsThisMinute = 1;
            safety.MinuteWindowStartTs = now;
        }
        else
        {
            safety.ActionsThisMinute++;
        }
        next.Safety = safety;

        // Update device state if action detail contains device info
        if (evt.Action != null && !string.IsNullOrWhiteSpace(evt.Action.Agent))
        {
            var env = next.Environment ?? new EnvironmentSnapshot();
            env.DeviceStates[evt.Action.Agent] = $"{evt.Action.Action}: {evt.Action.Detail}";
            next.Environment = env;
        }

        return next;
    }

    private static HouseholdEntityState ApplyMemoryUpdated(
        HouseholdEntityState current, MemoryUpdatedEvent evt)
    {
        var next = current.Clone();
        if (evt.Entry == null) return next;

        // Update existing or add new
        var existing = next.Memories.FirstOrDefault(m => m.Key == evt.Entry.Key);
        if (existing != null)
        {
            var idx = next.Memories.IndexOf(existing);
            next.Memories[idx] = evt.Entry;
        }
        else
        {
            next.Memories.Add(evt.Entry);
            while (next.Memories.Count > HouseholdEntityDefaults.MaxMemories)
                next.Memories.RemoveAt(0);
        }

        return next;
    }

    private static HouseholdEntityState ApplySafetyChanged(
        HouseholdEntityState current, SafetyStateChangedEvent evt)
    {
        var next = current.Clone();
        next.Safety = evt.Safety;
        return next;
    }

    private static HouseholdEntityState ApplyTimePeriodChanged(
        HouseholdEntityState current, TimePeriodChangedEvent evt)
    {
        var next = current.Clone();
        var env = next.Environment ?? new EnvironmentSnapshot();
        env.TimeOfDay = evt.NewPeriod;
        next.Environment = env;
        return next;
    }

    // ─── Description ───

    public override Task<string> GetDescriptionAsync() =>
        Task.FromResult($"HouseholdEntity[{State.CurrentMode ?? "active"}]:{Id}");
}
