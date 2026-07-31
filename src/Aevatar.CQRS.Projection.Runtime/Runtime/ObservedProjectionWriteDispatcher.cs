using System.Diagnostics;
using Aevatar.Foundation.Runtime.Observability;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ObservedProjectionWriteDispatcher<TReadModel>
    : IProjectionWriteDispatcher<TReadModel>
    where TReadModel : class, IProjectionReadModel
{
    private readonly ProjectionStoreDispatcher<TReadModel> _inner;

    public ObservedProjectionWriteDispatcher(ProjectionStoreDispatcher<TReadModel> inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        ct.ThrowIfCancellationRequested();

        using var activity = AevatarActivitySource.StartReadmodelUpsert(
            typeof(TReadModel).Name,
            readModel.StateVersion);

        try
        {
            var result = await _inner.UpsertAsync(readModel, ct);
            AevatarActivitySource.SafeSetStatus(activity, ActivityStatusCode.Ok);
            return result;
        }
        catch (Exception ex)
        {
            AevatarActivitySource.SafeSetStatus(activity, ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    public async Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ct.ThrowIfCancellationRequested();

        using var activity = AevatarActivitySource.StartReadmodelDelete(
            typeof(TReadModel).Name,
            id);

        try
        {
            var result = await _inner.DeleteAsync(id, ct);
            AevatarActivitySource.SafeSetStatus(activity, ActivityStatusCode.Ok);
            return result;
        }
        catch (Exception ex)
        {
            AevatarActivitySource.SafeSetStatus(activity, ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    public async Task<ProjectionWriteResult> DeleteAsync(
        ProjectionDocumentDeleteMarker marker,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(marker);
        ArgumentException.ThrowIfNullOrWhiteSpace(marker.Id);
        ct.ThrowIfCancellationRequested();

        using var activity = AevatarActivitySource.StartReadmodelDelete(
            typeof(TReadModel).Name,
            marker.Id);

        try
        {
            var result = await _inner.DeleteAsync(marker, ct);
            AevatarActivitySource.SafeSetStatus(activity, ActivityStatusCode.Ok);
            return result;
        }
        catch (Exception ex)
        {
            AevatarActivitySource.SafeSetStatus(activity, ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }
}
