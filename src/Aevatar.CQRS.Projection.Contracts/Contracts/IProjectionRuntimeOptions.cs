namespace Aevatar.CQRS.Projection.Contracts;

/// <summary>
/// Runtime switches for projection lifecycle and query/report capabilities.
/// </summary>
public interface IProjectionRuntimeOptions
{
    bool Enabled { get; }

    bool EnableRunQueryEndpoints { get; }

    bool EnableRunReportArtifacts { get; }

    int RunProjectionCompletionWaitTimeoutMs { get; }
}
