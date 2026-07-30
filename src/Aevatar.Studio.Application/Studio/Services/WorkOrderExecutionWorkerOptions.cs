namespace Aevatar.Studio.Application.Studio.Services;

public sealed class WorkOrderExecutionWorkerOptions
{
    public const string SectionName = "Aevatar:Studio:WorkOrderExecutionWorker";

    public int QueueCapacity { get; set; } = 1024;

    public int MaxConcurrency { get; set; } = 64;

    public int ShutdownDrainGraceSeconds { get; set; } = 10;

    public TimeSpan ShutdownDrainGrace => ShutdownDrainGraceSeconds > 0
        ? TimeSpan.FromSeconds(ShutdownDrainGraceSeconds)
        : TimeSpan.Zero;
}
