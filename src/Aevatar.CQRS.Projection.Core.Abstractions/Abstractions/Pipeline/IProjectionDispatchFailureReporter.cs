namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Reports projection dispatch failures to an upstream channel.
/// </summary>
public interface IProjectionDispatchFailureReporter<in TContext>
{
    ValueTask ReportAsync(
        TContext context,
        EventEnvelope envelope,
        Exception exception,
        CancellationToken ct = default);
}
