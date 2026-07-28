using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Mainnet.Host.Api.WorkflowAdmission;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Aevatar.Capabilities.Tests;

public sealed class NyxIdWorkflowAdmissionEnforcementStartupGuardTests
{
    [Fact]
    public async Task StartAsync_WhenShadow_ShouldNotReadInventory()
    {
        var definitions = new PagedReader<WorkflowActorBindingDocument>([]);
        var runs = new PagedReader<WorkflowExecutionCurrentStateDocument>([]);
        var guard = CreateGuard(NyxIdManagedWorkflowAdmissionMode.Shadow, definitions, runs);

        await guard.StartAsync(CancellationToken.None);

        definitions.Queries.Should().BeEmpty();
        runs.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_WhenEnforce_ShouldRejectLegacyDefinitionsAndNonTerminalRunsAcrossPages()
    {
        var definitions = new PagedReader<WorkflowActorBindingDocument>(
        [
            [Definition("wf-v3", ValidV3Plan())],
            [Definition("wf-v2", new WorkflowCapabilityAdmissionPlan
            {
                SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.LegacySchemaVersion,
            })],
            [Definition("wf-v3-invalid-policy", InvalidPolicyV3Plan())],
        ]);
        var runs = new PagedReader<WorkflowExecutionCurrentStateDocument>(
        [
            [Run("run-v2-complete", "completed", new WorkflowCapabilityAdmissionPlan
            {
                SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.LegacySchemaVersion,
            })],
            [Run("run-v2-active", "running", new WorkflowCapabilityAdmissionPlan
            {
                SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.LegacySchemaVersion,
            })],
        ]);
        var guard = CreateGuard(NyxIdManagedWorkflowAdmissionMode.Enforce, definitions, runs);

        var act = () => guard.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(
                "*CAPABILITY_ADMISSION_REBIND_REQUIRED*definitions=2*active_runs=1*" +
                "wf-v2*wf-v3-invalid-policy*run-v2-active*");
        definitions.Queries.Should().HaveCount(3);
        runs.Queries.Should().HaveCount(2);
        foreach (var query in definitions.Queries)
        {
            var filter = query.Filters.Should().ContainSingle().Which;
            filter.FieldPath.Should().Be(nameof(WorkflowActorBindingDocument.ActorKindValue));
            filter.Operator.Should().Be(ProjectionDocumentFilterOperator.Eq);
            filter.Value.RawValue.Should().Be((long)WorkflowActorKind.Definition);
        }
        definitions.Queries.Should().OnlyContain(query => query.Sorts.Count > 0);
        runs.Queries.Should().OnlyContain(query => query.Sorts.Count > 0);
    }

    [Fact]
    public async Task StartAsync_WhenEnforce_ShouldAllowOnlyValidV3ServingInventory()
    {
        var valid = ValidV3Plan();
        var definitions = new PagedReader<WorkflowActorBindingDocument>([[Definition("wf-v3", valid)]]);
        var runs = new PagedReader<WorkflowExecutionCurrentStateDocument>(
        [[
            Run("run-v3-active", "running", valid),
            Run("run-v2-terminal", "failed", new WorkflowCapabilityAdmissionPlan
            {
                SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.LegacySchemaVersion,
            }),
        ]]);
        var guard = CreateGuard(NyxIdManagedWorkflowAdmissionMode.Enforce, definitions, runs);

        await guard.StartAsync(CancellationToken.None);
    }

    private static NyxIdWorkflowAdmissionEnforcementStartupGuard CreateGuard(
        NyxIdManagedWorkflowAdmissionMode mode,
        IProjectionDocumentReader<WorkflowActorBindingDocument, string> definitions,
        IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string> runs) =>
        new(Options.Create(new NyxIdToolOptions { ManagedWorkflowAdmissionMode = mode }), definitions, runs);

    private static WorkflowActorBindingDocument Definition(string id, WorkflowCapabilityAdmissionPlan plan) =>
        new()
        {
            Id = id,
            ActorId = id,
            ActorKind = WorkflowActorKind.Definition,
            WorkflowYaml = "name: wf-v3",
            CapabilityAdmissionPlan = plan.Clone(),
        };

    private static WorkflowExecutionCurrentStateDocument Run(
        string id,
        string status,
        WorkflowCapabilityAdmissionPlan plan) =>
        new()
        {
            Id = id,
            RootActorId = id,
            RunId = id,
            Status = status,
            WorkflowYaml = "name: wf-v3",
            CapabilityAdmissionPlan = plan.Clone(),
        };

    private static WorkflowCapabilityAdmissionPlan ValidV3Plan() =>
        WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: wf-v3",
            inlineWorkflowYamls: null,
            ExternalCapabilityExecutionMode.Interactive,
            invocationAdmissions: [],
            sourceStamps: []);

    private static WorkflowCapabilityAdmissionPlan InvalidPolicyV3Plan()
    {
        var plan = ValidV3Plan();
        plan.InvocationAdmissions.Add(new WorkflowCapabilityInvocationAdmission
        {
            CallSiteId = "wf-v3/read-alpha",
            Capability = new ExternalWorkflowCapabilityRef
            {
                NyxIdUserService = new NyxIdUserServiceCapabilityRef
                {
                    UserServiceId = "us-alpha",
                    OperationId = "read-alpha",
                },
            },
        });
        return plan;
    }

    private sealed class PagedReader<T>(IReadOnlyList<IReadOnlyList<T>> pages)
        : IProjectionDocumentReader<T, string>
        where T : class, IProjectionReadModel
    {
        public List<ProjectionDocumentQuery> Queries { get; } = [];

        public Task<T?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<T?>(null);

        public Task<ProjectionDocumentQueryResult<T>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            Queries.Add(query);
            var index = string.IsNullOrEmpty(query.Cursor) ? 0 : int.Parse(query.Cursor);
            return Task.FromResult(new ProjectionDocumentQueryResult<T>
            {
                Items = index < pages.Count ? pages[index] : [],
                NextCursor = index + 1 < pages.Count ? (index + 1).ToString() : null,
            });
        }
    }
}
