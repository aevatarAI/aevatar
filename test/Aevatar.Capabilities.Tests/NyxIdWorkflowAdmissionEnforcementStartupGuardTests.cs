using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.Mainnet.Host.Api.WorkflowAdmission;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Capabilities.Tests;

public sealed class NyxIdWorkflowAdmissionEnforcementStartupGuardTests
{
    [Theory]
    [InlineData(NyxIdManagedWorkflowAdmissionMode.Shadow)]
    [InlineData(NyxIdManagedWorkflowAdmissionMode.Enforce)]
    public async Task AddNyxIdTools_ShouldUseTheSameModeForProxyAndStartupGuard(
        NyxIdManagedWorkflowAdmissionMode mode)
    {
        var definitions = new PagedReader<WorkflowActorBindingDocument>(
            [[Definition("wf-v2", LegacyPlan())]]);
        var runs = new PagedReader<WorkflowExecutionCurrentStateDocument>([]);
        var deployments = new PagedReader<ServiceDeploymentCatalogReadModel>([]);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNyxIdTools(options =>
        {
            options.BaseUrl = "https://nyxid.invalid";
            options.ManagedWorkflowAdmissionMode = mode;
        });
        services.AddSingleton<IWorkflowDefinitionParser, TestWorkflowDefinitionParser>();
        services.AddSingleton<IProjectionDocumentReader<WorkflowActorBindingDocument, string>>(definitions);
        services.AddSingleton<IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string>>(runs);
        services.AddSingleton<IProjectionDocumentReader<ServiceDeploymentCatalogReadModel, string>>(deployments);
        services.AddSingleton<NyxIdWorkflowAdmissionEnforcementStartupGuard>();

        await using var provider = services.BuildServiceProvider();
        var source = provider.GetRequiredService<NyxIdAgentToolSource>();
        var proxy = (NyxIdProxyTool)(await source.DiscoverToolsAsync())
            .Single(static tool => tool.Name == "nyxid_proxy");
        using var context = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            WorkflowRuntime = new AgentWorkflowRuntimeContext(
                "workflow-run-actor-alpha",
                "run-alpha",
                "llm-alpha",
                "run-alpha",
                1),
            InvocationSurface = AgentToolInvocationSurface.WorkflowLlmToolLoop,
        });

        var proxyResult = await proxy.ExecuteAsync("{}");
        var startGuard = () => provider
            .GetRequiredService<NyxIdWorkflowAdmissionEnforcementStartupGuard>()
            .StartAsync(CancellationToken.None);

        if (mode == NyxIdManagedWorkflowAdmissionMode.Enforce)
        {
            proxyResult.Should().Contain("NYXID_OPERATION_ADMISSION_REQUIRED");
            await startGuard.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*CAPABILITY_ADMISSION_REBIND_REQUIRED*wf-v2*");
        }
        else
        {
            proxyResult.Should().NotContain("NYXID_OPERATION_ADMISSION_REQUIRED");
            await startGuard.Should().NotThrowAsync();
        }
    }

    [Fact]
    public async Task StartAsync_WhenShadow_ShouldNotReadInventory()
    {
        var definitions = new PagedReader<WorkflowActorBindingDocument>([]);
        var runs = new PagedReader<WorkflowExecutionCurrentStateDocument>([]);
        var deployments = new PagedReader<ServiceDeploymentCatalogReadModel>([]);
        var guard = CreateGuard(NyxIdManagedWorkflowAdmissionMode.Shadow, definitions, runs, deployments);

        await guard.StartAsync(CancellationToken.None);

        definitions.Queries.Should().BeEmpty();
        runs.Queries.Should().BeEmpty();
        deployments.Queries.Should().BeEmpty();
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
        var guard = CreateGuard(
            NyxIdManagedWorkflowAdmissionMode.Enforce,
            definitions,
            runs,
            new PagedReader<ServiceDeploymentCatalogReadModel>([]));

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
        var guard = CreateGuard(
            NyxIdManagedWorkflowAdmissionMode.Enforce,
            definitions,
            runs,
            new PagedReader<ServiceDeploymentCatalogReadModel>([]));

        await guard.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_WhenEnforce_ShouldAllowIntegrityValidV5InventoryDuringV6Rollout()
    {
        var validV5 = AsV5(ValidV3Plan());
        var definitions = new PagedReader<WorkflowActorBindingDocument>(
            [[Definition("wf-v5", validV5)]]);
        var runs = new PagedReader<WorkflowExecutionCurrentStateDocument>(
            [[Run("run-v5-active", "running", validV5)]]);
        var guard = CreateGuard(
            NyxIdManagedWorkflowAdmissionMode.Enforce,
            definitions,
            runs,
            new PagedReader<ServiceDeploymentCatalogReadModel>([]));

        await guard.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_WhenEnforce_ShouldRejectV5InventoryWithInvalidDigest()
    {
        var invalidV5 = AsV5(ValidV3Plan());
        invalidV5.AdmissionDigest = "tampered";
        var definitions = new PagedReader<WorkflowActorBindingDocument>(
            [[Definition("wf-v5-invalid", invalidV5)]]);
        var guard = CreateGuard(
            NyxIdManagedWorkflowAdmissionMode.Enforce,
            definitions,
            new PagedReader<WorkflowExecutionCurrentStateDocument>([]),
            new PagedReader<ServiceDeploymentCatalogReadModel>([]));

        var act = () => guard.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*CAPABILITY_ADMISSION_REBIND_REQUIRED*definitions=1*wf-v5-invalid*");
    }

    [Fact]
    public async Task StartAsync_WhenEnforce_ShouldAllowProoflessInventoryWithoutExternalCallSites()
    {
        var definitions = new PagedReader<WorkflowActorBindingDocument>(
            [[Definition("wf-internal", plan: null, "name: wf-internal\nsteps: []\n")]]);
        var runs = new PagedReader<WorkflowExecutionCurrentStateDocument>(
            [[Run("run-internal", "running", plan: null, "name: wf-internal\nsteps: []\n")]]);
        var guard = CreateGuard(
            NyxIdManagedWorkflowAdmissionMode.Enforce,
            definitions,
            runs,
            new PagedReader<ServiceDeploymentCatalogReadModel>([]));

        await guard.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_WhenEnforce_ShouldRejectPlanThatDoesNotMatchParsedYamlInvocations()
    {
        const string yaml = """
            name: wf-v3
            roles: []
            steps:
              - id: read-alpha
                type: tool_call
                capability:
                  nyxid_operation:
                    user_service_id: us-yaml-alpha
                    endpoint_id: read-yaml-alpha
                parameters:
                  tool: nyxid_proxy
                  arguments: '{"query":{}}'
            """;
        var definitions = new PagedReader<WorkflowActorBindingDocument>(
            [[Definition("wf-v3", ValidNyxIdPlan(yaml), yaml)]]);
        var guard = CreateGuard(
            NyxIdManagedWorkflowAdmissionMode.Enforce,
            definitions,
            new PagedReader<WorkflowExecutionCurrentStateDocument>([]),
            new PagedReader<ServiceDeploymentCatalogReadModel>([]));

        var act = () => guard.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*CAPABILITY_ADMISSION_REBIND_REQUIRED*definitions=1*wf-v3*");
    }

    [Fact]
    public async Task StartAsync_WhenEnforce_ShouldIgnoreOnlyExplicitlyDeactivatedServiceDefinitions()
    {
        var definitions = new PagedReader<WorkflowActorBindingDocument>(
        [[
            Definition("service-definition-retired", LegacyPlan()),
            Definition("service-definition-active", LegacyPlan()),
        ]]);
        var runs = new PagedReader<WorkflowExecutionCurrentStateDocument>([]);
        var deployments = new PagedReader<ServiceDeploymentCatalogReadModel>(
        [[new ServiceDeploymentCatalogReadModel
        {
            Id = "service-alpha",
            Deployments =
            {
                Deployment(
                    "deployment-retired",
                    "service-definition-retired",
                    ServiceDeploymentStatus.Deactivated),
                Deployment(
                    "deployment-active",
                    "service-definition-active",
                    ServiceDeploymentStatus.Active),
            },
        }]]);
        var guard = CreateGuard(
            NyxIdManagedWorkflowAdmissionMode.Enforce,
            definitions,
            runs,
            deployments);

        var act = () => guard.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*definitions=1*service-definition-active*")
            .Where(exception => !exception.Message.Contains("service-definition-retired", StringComparison.Ordinal));
        deployments.Queries.Should().ContainSingle();
    }

    private static NyxIdWorkflowAdmissionEnforcementStartupGuard CreateGuard(
        NyxIdManagedWorkflowAdmissionMode mode,
        IProjectionDocumentReader<WorkflowActorBindingDocument, string> definitions,
        IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string> runs,
        IProjectionDocumentReader<ServiceDeploymentCatalogReadModel, string> deployments) =>
        new(
            new NyxIdToolOptions { ManagedWorkflowAdmissionMode = mode },
            new TestWorkflowDefinitionParser(),
            definitions,
            runs,
            deployments);

    private static WorkflowActorBindingDocument Definition(
        string id,
        WorkflowCapabilityAdmissionPlan? plan,
        string workflowYaml = "name: wf-v3") =>
        new()
        {
            Id = id,
            ActorId = id,
            ActorKind = WorkflowActorKind.Definition,
            WorkflowYaml = workflowYaml,
            CapabilityAdmissionPlan = plan?.Clone(),
        };

    private static WorkflowExecutionCurrentStateDocument Run(
        string id,
        string status,
        WorkflowCapabilityAdmissionPlan? plan,
        string workflowYaml = "name: wf-v3") =>
        new()
        {
            Id = id,
            RootActorId = id,
            RunId = id,
            Status = status,
            WorkflowYaml = workflowYaml,
            CapabilityAdmissionPlan = plan?.Clone(),
        };

    private static WorkflowCapabilityAdmissionPlan ValidV3Plan() =>
        WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: wf-v3",
            inlineWorkflowYamls: null,
            ExternalCapabilityExecutionMode.Interactive,
            invocationAdmissions: [],
            sourceStamps: []);

    private static WorkflowCapabilityAdmissionPlan AsV5(WorkflowCapabilityAdmissionPlan plan)
    {
        var v5 = plan.Clone();
        v5.SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.CodeRouteSchemaVersion;
        v5.AdmissionDigest = WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(v5);
        return v5;
    }

    private static WorkflowCapabilityAdmissionPlan LegacyPlan() =>
        new()
        {
            SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.LegacySchemaVersion,
        };

    private static ServiceDeploymentReadModel Deployment(
        string deploymentId,
        string actorId,
        ServiceDeploymentStatus status) =>
        new()
        {
            DeploymentId = deploymentId,
            PrimaryActorId = actorId,
            Status = status.ToString(),
        };

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
                    EndpointId = "read-alpha",
                },
            },
        });
        return plan;
    }

    private static WorkflowCapabilityAdmissionPlan ValidNyxIdPlan(string workflowYaml)
    {
        var observedAt = new DateTimeOffset(2026, 7, 29, 0, 0, 0, TimeSpan.Zero);
        return WorkflowCapabilityAdmissionPlanIntegrity.Create(
            workflowYaml,
            inlineWorkflowYamls: null,
            ExternalCapabilityExecutionMode.Interactive,
            [new WorkflowCapabilityInvocationAdmission
            {
                CallSiteId = "wf-v3/read-alpha",
                Capability = new ExternalWorkflowCapabilityRef
                {
                    NyxIdUserService = new NyxIdUserServiceCapabilityRef
                    {
                        UserServiceId = "us-plan-alpha",
                        ServiceSlugSnapshot = "service-alpha",
                        EndpointId = "read-plan-alpha",
                        HttpMethod = "GET",
                        PathTemplate = "/items",
                        ContractDigest = "sha256:plan-alpha",
                        ExecutionPolicy = new NyxIdOperationExecutionPolicy
                        {
                            Risk = NyxIdOperationRisk.ReadOnly,
                            Approval = NyxIdOperationApproval.None,
                            EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
                            AllowedExecutionModes =
                            {
                                ExternalCapabilityExecutionMode.Interactive,
                                ExternalCapabilityExecutionMode.Durable,
                            },
                        },
                    },
                },
            }],
            [
                Source(ExternalCapabilitySourceKind.NyxIdUserServices, "nyxid-user-services:caller", observedAt),
                Source(ExternalCapabilitySourceKind.NyxIdOpenApi, "us-plan-alpha", observedAt),
            ]);
    }

    private static ExternalCapabilitySourceStamp Source(
        ExternalCapabilitySourceKind kind,
        string sourceId,
        DateTimeOffset observedAt) =>
        new()
        {
            SourceKind = kind,
            SourceId = sourceId,
            ObservedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(observedAt),
            FreshUntil = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(observedAt.AddMinutes(5)),
            ContentDigest = "sha256:source-alpha",
        };

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

    private sealed class TestWorkflowDefinitionParser : IWorkflowDefinitionParser
    {
        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var workflow = new WorkflowParser().Parse(workflowYaml);
                return Task.FromResult(WorkflowYamlParseResult.Success(
                    workflow.Name,
                    WorkflowAuthorizationDependencyEvaluator.Evaluate(workflow)));
            }
            catch (Exception exception)
            {
                return Task.FromResult(WorkflowYamlParseResult.Invalid(exception.Message));
            }
        }

        public Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default) =>
            Task.FromResult(WorkflowInlineYamlBundleParseResult.Invalid("Not used by the startup guard."));
    }
}
