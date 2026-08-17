namespace Aevatar.Studio.Hosting.WorkflowDeliveries;

public sealed class WorkflowDeliveryContinuationWorkerOptions
{
    public const string SectionName = "Aevatar:Studio:WorkflowDeliveryContinuationWorker";

    public bool Enabled { get; set; } = true;

    public int PollIntervalSeconds { get; set; } = 5;

    public int PageSize { get; set; } = 100;

    public int ClaimDurationSeconds { get; set; } = 120;

    public string ClaimantId { get; set; } =
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Max(1, PollIntervalSeconds));

    public TimeSpan ClaimDuration => TimeSpan.FromSeconds(Math.Clamp(ClaimDurationSeconds, 5, 300));
}
