namespace Aevatar.CQRS.Sagas.Abstractions.Configuration;

public sealed class SagaRuntimeOptions
{
    public bool Enabled { get; set; } = true;

    public string WorkingDirectory { get; set; } = Path.Combine("artifacts", "cqrs", "sagas");

    public int ActorScanIntervalMs { get; set; } = 1000;

    public int MaxActionsPerEvent { get; set; } = 256;

    public int ConcurrencyRetryAttempts { get; set; } = 5;

    public int TimeoutDispatchIntervalMs { get; set; } = 500;

    public int TimeoutDispatchBatchSize { get; set; } = 128;
}
