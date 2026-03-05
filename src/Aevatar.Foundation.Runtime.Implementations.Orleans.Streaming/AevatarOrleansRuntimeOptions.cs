namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;

public sealed class AevatarOrleansRuntimeOptions
{
    public const string StreamBackendInMemory = "InMemory";
    public const string StreamBackendMassTransitAdapter = "MassTransitAdapter";
    public const string PersistenceBackendInMemory = "InMemory";
    public const string PersistenceBackendGarnet = "Garnet";
    public const string DefaultGarnetConnectionString = "localhost:6379";
    public const string RuntimeCallbackSchedulingModeAuto = "Auto";
    public const string RuntimeCallbackSchedulingModeForceInline = "ForceInline";
    public const string RuntimeCallbackSchedulingModeForceDedicated = "ForceDedicated";
    public const string RuntimeCallbackDedicatedDeliveryModeAuto = "Auto";
    public const string RuntimeCallbackDedicatedDeliveryModeTimer = "Timer";
    public const string RuntimeCallbackDedicatedDeliveryModeReminder = "Reminder";

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
    public string RuntimeCallbackSchedulingMode { get; set; } = RuntimeCallbackSchedulingModeAuto;

    /// <summary>
    /// Dedicated scheduling delivery mode:
    /// Auto | Timer | Reminder
    /// </summary>
    public string RuntimeCallbackDedicatedDeliveryMode { get; set; } = RuntimeCallbackDedicatedDeliveryModeAuto;

    /// <summary>
    /// Auto mode threshold: when due_time >= threshold, dedicated path prefers reminder.
    /// Set <= 0 to disable reminder auto-selection.
    /// </summary>
    public int RuntimeCallbackReminderThresholdMs { get; set; } = 300_000;

    /// <summary>
    /// Auto mode threshold: inline path is used only when due_time <= threshold.
    /// Set <= 0 to allow inline regardless of due_time.
    /// </summary>
    public int RuntimeCallbackInlineMaxDueTimeMs { get; set; } = 60_000;
}
