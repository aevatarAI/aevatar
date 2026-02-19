namespace Aevatar.CQRS.Runtime.Abstractions.Configuration;

public sealed class CqrsRuntimeOptions
{
    public string Runtime { get; set; } = "Wolverine";

    public string WorkingDirectory { get; set; } =
        Path.Combine("artifacts", "cqrs");

    public int MaxRetryAttempts { get; set; } = 3;

    public int RetryBaseDelayMs { get; set; } = 200;

    public int QueueCapacity { get; set; } = 4096;

    public int OutboxDispatchIntervalMs { get; set; } = 500;

    public int OutboxDispatchBatchSize { get; set; } = 128;
}
