using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection.ReadModels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aevatar.Mainnet.Host.Api.WorkflowAdmission;

internal sealed class NyxIdWorkflowAdmissionEnforcementStartupGuard(
    IOptions<NyxIdToolOptions> options,
    IProjectionDocumentReader<WorkflowActorBindingDocument, string> definitionReader,
    IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string> runReader,
    IProjectionDocumentReader<ServiceDeploymentCatalogReadModel, string> deploymentReader) : IHostedService
{
    private const int PageSize = 200;
    private const int SampleLimit = 8;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.ManagedWorkflowAdmissionMode != NyxIdManagedWorkflowAdmissionMode.Enforce)
            return;

        var deactivatedServiceDefinitions = await ScanDeactivatedServiceDefinitionsAsync(cancellationToken);
        var invalidDefinitions = await ScanDefinitionsAsync(deactivatedServiceDefinitions, cancellationToken);
        var invalidActiveRuns = await ScanActiveRunsAsync(cancellationToken);
        if (invalidDefinitions.Count == 0 && invalidActiveRuns.Count == 0)
            return;

        throw new InvalidOperationException(
            $"{WorkflowCapabilityAdmissionPlanIntegrity.RebindRequiredCode}: " +
            $"definitions={invalidDefinitions.Count} active_runs={invalidActiveRuns.Count} " +
            $"definition_samples=[{string.Join(',', invalidDefinitions.Samples)}] " +
            $"active_run_samples=[{string.Join(',', invalidActiveRuns.Samples)}]");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<InventoryFailures> ScanDefinitionsAsync(
        IReadOnlySet<string> deactivatedServiceDefinitions,
        CancellationToken ct)
    {
        var failures = new InventoryFailures();
        string? cursor = null;
        do
        {
            // ponytail: the binding read model has no typed serving flag, so Enforce treats every
            // definition as potentially serving; narrow this only after that relationship is projected.
            var page = await definitionReader.QueryAsync(new ProjectionDocumentQuery
            {
                Cursor = cursor,
                Take = PageSize,
                Filters =
                [
                    new ProjectionDocumentFilter
                    {
                        FieldPath = nameof(WorkflowActorBindingDocument.ActorKindValue),
                        Operator = ProjectionDocumentFilterOperator.Eq,
                        Value = ProjectionDocumentValue.FromInt64((int)WorkflowActorKind.Definition),
                    },
                ],
                Sorts =
                [
                    new ProjectionDocumentSort
                    {
                        FieldPath = nameof(WorkflowActorBindingDocument.ActorId),
                        Direction = ProjectionDocumentSortDirection.Asc,
                    },
                ],
            }, ct);
            foreach (var document in page.Items)
            {
                if (deactivatedServiceDefinitions.Contains(document.ActorId))
                    continue;

                if (!HasValidV3Plan(
                        document.CapabilityAdmissionPlan,
                        document.WorkflowYaml,
                        document.InlineWorkflowYamlEntries))
                {
                    failures.Add(document.ActorId);
                }
            }

            cursor = page.NextCursor;
        } while (!string.IsNullOrWhiteSpace(cursor));

        return failures;
    }

    private async Task<IReadOnlySet<string>> ScanDeactivatedServiceDefinitionsAsync(CancellationToken ct)
    {
        var deactivated = new HashSet<string>(StringComparer.Ordinal);
        var nonDeactivated = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await deploymentReader.QueryAsync(new ProjectionDocumentQuery
            {
                Cursor = cursor,
                Take = PageSize,
                Sorts =
                [
                    new ProjectionDocumentSort
                    {
                        FieldPath = nameof(ServiceDeploymentCatalogReadModel.Id),
                        Direction = ProjectionDocumentSortDirection.Asc,
                    },
                ],
            }, ct);
            foreach (var deployment in page.Items.SelectMany(static document => document.Deployments))
            {
                var actorId = deployment.PrimaryActorId?.Trim();
                if (string.IsNullOrWhiteSpace(actorId))
                    continue;

                if (string.Equals(
                        deployment.Status,
                        ServiceDeploymentStatus.Deactivated.ToString(),
                        StringComparison.Ordinal))
                {
                    deactivated.Add(actorId);
                }
                else
                {
                    nonDeactivated.Add(actorId);
                }
            }

            cursor = page.NextCursor;
        } while (!string.IsNullOrWhiteSpace(cursor));

        deactivated.ExceptWith(nonDeactivated);
        return deactivated;
    }

    private async Task<InventoryFailures> ScanActiveRunsAsync(CancellationToken ct)
    {
        var failures = new InventoryFailures();
        string? cursor = null;
        do
        {
            var page = await runReader.QueryAsync(new ProjectionDocumentQuery
            {
                Cursor = cursor,
                Take = PageSize,
                Sorts =
                [
                    new ProjectionDocumentSort
                    {
                        FieldPath = nameof(WorkflowExecutionCurrentStateDocument.RootActorId),
                        Direction = ProjectionDocumentSortDirection.Asc,
                    },
                ],
            }, ct);
            foreach (var document in page.Items)
            {
                if (!IsTerminal(document.Status) &&
                    !HasValidV3Plan(
                        document.CapabilityAdmissionPlan,
                        document.WorkflowYaml,
                        document.InlineWorkflowYamlEntries))
                {
                    failures.Add(document.RootActorId);
                }
            }

            cursor = page.NextCursor;
        } while (!string.IsNullOrWhiteSpace(cursor));

        return failures;
    }

    private static bool HasValidV3Plan(
        WorkflowCapabilityAdmissionPlan? plan,
        string? workflowYaml,
        IReadOnlyDictionary<string, string> inlineWorkflowYamls)
    {
        if (plan is null ||
            !string.Equals(plan.SchemaVersion, WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            WorkflowCapabilityAdmissionPlanIntegrity.ValidateOrThrow(
                plan,
                workflowYaml ?? string.Empty,
                inlineWorkflowYamls,
                plan.ExecutionMode,
                plan.InvocationAdmissions.Select(ToExpectedInvocation));
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static ExternalToolInvocationSpec ToExpectedInvocation(
        WorkflowCapabilityInvocationAdmission admission)
    {
        var selector = new ExternalWorkflowCapabilitySelector();
        switch (admission.Capability?.CapabilityCase)
        {
            case ExternalWorkflowCapabilityRef.CapabilityOneofCase.HostConnector:
                selector.HostConnector = admission.Capability.HostConnector.Clone();
                break;
            case ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService:
                selector.NyxIdOperation = new NyxIdOperationSelector
                {
                    UserServiceId = admission.Capability.NyxIdUserService.UserServiceId,
                    EndpointId = admission.Capability.NyxIdUserService.EndpointId,
                };
                break;
        }

        return new ExternalToolInvocationSpec
        {
            CallSiteId = admission.CallSiteId,
            ToolName = "inventory_validation",
            Selector = selector,
        };
    }

    private static bool IsTerminal(string? status) =>
        status?.Trim() is "completed" or "failed" or "stopped";

    private sealed class InventoryFailures
    {
        public int Count { get; private set; }

        public List<string> Samples { get; } = [];

        public void Add(string? actorId)
        {
            Count++;
            if (Samples.Count < SampleLimit && !string.IsNullOrWhiteSpace(actorId))
                Samples.Add(actorId.Trim());
        }
    }
}
