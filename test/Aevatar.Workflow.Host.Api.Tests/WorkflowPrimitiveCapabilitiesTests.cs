using Aevatar.Configuration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Abstractions.Primitives;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Infrastructure.Capabilities;
using Aevatar.Workflow.Projection.Workflows;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

[Collection(ProcessEnvSerialCollection.Name)]
public sealed class WorkflowPrimitiveCapabilitiesTests
{
    [Fact]
    public async Task Capabilities_ShouldUseModuleContributionsAndDiscoverTheirStepTypeOptions()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "wf-authoring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var previousAevatarHome = Environment.GetEnvironmentVariable(AevatarPaths.HomeEnv);
        Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, tempDirectory);
        try
        {
            var provider = new WorkflowInfrastructureCapabilitiesProvider(
                [new ExtensionPack()],
                new WorkflowCatalogReadModelQueryPort(new EmptyCatalogReader(), new WorkflowCatalogReadModelMapper()),
                new WorkflowCatalogReadModelMapper());

            var document = await provider.GetCapabilitiesAsync();

            foreach (var name in new[] { "custom_assign", "assign" })
            {
                var primitive = document.Primitives.Single(primitive => primitive.Name == name);
                primitive.Description.Should().Be("Custom assignment contract.");
                primitive.Parameters.Select(parameter => parameter.Name).Should().Equal("settings", "attempts");
                var parameter = primitive.Parameters.Single(parameter => parameter.Name == "settings");
                parameter.Name.Should().Be("settings");
                parameter.Type.Should().Be("object");
                parameter.Control.Should().Be(WorkflowPrimitiveParameterControl.Json);
                parameter.Label.Should().Be("Settings");
                var inferredNumber = primitive.Parameters.Single(parameter => parameter.Name == "attempts");
                inferredNumber.Type.Should().Be("int");
                inferredNumber.Control.Should().Be(WorkflowPrimitiveParameterControl.Number);
                inferredNumber.Integer.Should().BeTrue();
            }
            document.Primitives.Single(primitive => primitive.Name == "foreach").Parameters
                .Single(parameter => parameter.Name == "sub_step_type").Enum.Should().Contain(["custom_assign", "loop", "fan_out"]);
            document.Primitives.Single(primitive => primitive.Name == "wait_signal").Parameters
                .Single(parameter => parameter.Name == "timeout_ms").Maximum.Should().Be(86_400_000);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, previousAevatarHome);
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private sealed class ExtensionPack : IWorkflowModulePack
    {
        public string Name => "custom";
        public IReadOnlyList<WorkflowModuleRegistration> Modules =>
        [
            WorkflowModuleRegistration.Create<ExtensionModule>(new WorkflowPrimitiveDefinition("Custom assignment contract.",
                [
                    new("settings", WorkflowPrimitiveParameterType.Object, "Settings", Control: WorkflowPrimitiveParameterControl.Json),
                    new("attempts", WorkflowPrimitiveParameterType.Integer, "Attempts"),
                ]),
                "custom_assign", "assign"),
        ];
        public IReadOnlyList<IWorkflowModuleDependencyExpander> DependencyExpanders => [];
        public IReadOnlyList<IWorkflowModuleConfigurator> Configurators => [];
    }

    private sealed class ExtensionModule : IEventModule<IWorkflowExecutionContext>
    {
        public string Name => "custom_assign";
        public int Priority => 0;
        public bool CanHandle(EventEnvelope envelope) => false;
        public Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class EmptyCatalogReader : IProjectionDocumentReader<WorkflowCatalogCurrentStateDocument, string>
    {
        public Task<WorkflowCatalogCurrentStateDocument?> GetAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<WorkflowCatalogCurrentStateDocument?>(null);

        public Task<ProjectionDocumentQueryResult<WorkflowCatalogCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query, CancellationToken ct = default) =>
            Task.FromResult(new ProjectionDocumentQueryResult<WorkflowCatalogCurrentStateDocument>());
    }
}
