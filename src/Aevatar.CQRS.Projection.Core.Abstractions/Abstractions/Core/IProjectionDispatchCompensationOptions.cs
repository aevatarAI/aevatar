namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Runtime switches for durable projection dispatch compensation replay.
/// </summary>
public interface IProjectionDispatchCompensationOptions
{
    bool Enabled { get; }

    bool EnableDispatchCompensationReplay { get; }

    int DispatchCompensationReplayPollIntervalMs { get; }

    int DispatchCompensationReplayBatchSize { get; }

    int DispatchCompensationReplayBaseDelayMs { get; }

    int DispatchCompensationReplayMaxDelayMs { get; }
}
