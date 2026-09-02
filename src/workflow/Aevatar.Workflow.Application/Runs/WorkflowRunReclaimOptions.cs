namespace Aevatar.Workflow.Application.Runs;

// 06-20-observatory-run-state-feed (R2): bounds the confirmed-materialization wait before reclaiming the
// throwaway actors an ad-hoc /api/chat run creates. The wait is a bounded poll of the durable
// materialization watermark; on timeout the gate defers (never destroys on unconfirmed materialization).
public sealed class WorkflowRunReclaimOptions
{
    /// <summary>Maximum number of watermark polls before the reclaim gate gives up and defers.</summary>
    public int MaxWatermarkPolls { get; init; } = 200;

    /// <summary>Delay between watermark polls.</summary>
    public TimeSpan WatermarkPollInterval { get; init; } = TimeSpan.FromMilliseconds(50);
}
