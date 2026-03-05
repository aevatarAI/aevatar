namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;

public sealed class RuntimeCallbackSchedulerGrainState
{
    public Dictionary<string, ReminderScheduledCallbackState> ReminderCallbacks { get; set; } = [];
}

public sealed class ReminderScheduledCallbackState
{
    public long Generation { get; set; }

    public bool Periodic { get; set; }

    public int PeriodMs { get; set; }

    public byte[] EnvelopeBytes { get; set; } = [];

    public int FireIndex { get; set; }
}
