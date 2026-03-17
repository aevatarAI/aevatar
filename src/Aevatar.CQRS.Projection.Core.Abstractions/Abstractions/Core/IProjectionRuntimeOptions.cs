namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Runtime switches for projection lifecycle and query/report capabilities.
/// </summary>
public interface IProjectionRuntimeOptions
{
    bool Enabled { get; }

    bool EnableRunQueryEndpoints { get; }

    bool EnableRunReportDocuments { get; }

    int RunProjectionCompletionWaitTimeoutMs { get; }
}
