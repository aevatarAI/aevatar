using Aevatar.Tools.Cli.Studio.Domain.Models;

namespace Aevatar.Tools.Cli.Studio.Application.Abstractions;

public interface IWorkflowBundleRepository
{
    Task<IReadOnlyList<ProjectIndexEntry>> ListAsync(CancellationToken cancellationToken = default);

    Task<WorkflowBundle?> GetAsync(string bundleId, CancellationToken cancellationToken = default);

    Task<WorkflowBundle> UpsertAsync(WorkflowBundle bundle, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string bundleId, CancellationToken cancellationToken = default);
}
