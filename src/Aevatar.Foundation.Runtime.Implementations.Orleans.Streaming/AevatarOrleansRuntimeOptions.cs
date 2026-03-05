namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;

public sealed class AevatarOrleansRuntimeOptions
{
    public const string StreamBackendInMemory = "InMemory";
    public const string StreamBackendMassTransitAdapter = "MassTransitAdapter";
    public const string PersistenceBackendInMemory = "InMemory";
    public const string PersistenceBackendGarnet = "Garnet";
    public const string DefaultGarnetConnectionString = "localhost:6379";
    public const string AsyncCallbackSchedulingModeAuto = "Auto";
    public const string AsyncCallbackSchedulingModeForceInline = "ForceInline";
    public const string AsyncCallbackSchedulingModeForceDedicated = "ForceDedicated";
    public const string AsyncCallbackDedicatedDeliveryModeAuto = "Auto";
    public const string AsyncCallbackDedicatedDeliveryModeTimer = "Timer";
    public const string AsyncCallbackDedicatedDeliveryModeReminder = "Reminder";

    public string StreamBackend { get; set; } = StreamBackendInMemory;

    public string StreamProviderName { get; set; } = OrleansRuntimeConstants.DefaultOrleansStreamProviderName;

    public string ActorEventNamespace { get; set; } = OrleansRuntimeConstants.ActorEventStreamNamespace;

    public string PersistenceBackend { get; set; } = PersistenceBackendInMemory;

    public string GarnetConnectionString { get; set; } = DefaultGarnetConnectionString;

    public int QueueCount { get; set; } = 8;

    public int QueueCacheSize { get; set; } = 4096;

    /// <summary>
    /// Scheduling strategy selection:
    /// Auto | ForceInline | ForceDedicated
    /// </summary>
    public string AsyncCallbackSchedulingMode { get; set; } = AsyncCallbackSchedulingModeAuto;

    /// <summary>
    /// Dedicated scheduling delivery mode:
    /// Auto | Timer | Reminder
    /// </summary>
    public string AsyncCallbackDedicatedDeliveryMode { get; set; } = AsyncCallbackDedicatedDeliveryModeAuto;

    /// <summary>
    /// Auto mode threshold: when due_time >= threshold, dedicated path prefers reminder.
    /// Set <= 0 to disable reminder auto-selection.
    /// </summary>
    public int AsyncCallbackReminderThresholdMs { get; set; } = 300_000;

    /// <summary>
    /// Auto mode threshold: inline path is used only when due_time <= threshold.
    /// Set <= 0 to allow inline regardless of due_time.
    /// </summary>
    public int AsyncCallbackInlineMaxDueTimeMs { get; set; } = 60_000;
}
