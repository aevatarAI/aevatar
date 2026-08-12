using Aevatar.Workflow.Abstractions;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class WorkflowInteractiveActionParamsContractTests
{
    [Fact]
    public void ActionVariants_ShouldShareOneofAndReplaceTheActiveVariant()
    {
        var catalogServiceField = WorkflowInteractiveActionParams.Descriptor
            .FindFieldByName("catalog_service");
        var keyCreateField = WorkflowInteractiveActionParams.Descriptor
            .FindFieldByName("key_create");

        catalogServiceField.ContainingOneof.Should().NotBeNull();
        keyCreateField.ContainingOneof.Should().BeSameAs(catalogServiceField.ContainingOneof);
        catalogServiceField.FieldNumber.Should().Be(1);
        catalogServiceField.JsonName.Should().Be("catalogService");
        keyCreateField.FieldNumber.Should().Be(2);
        keyCreateField.JsonName.Should().Be("keyCreate");

        var actionParams = new WorkflowInteractiveActionParams
        {
            CatalogService = new WorkflowInteractiveCatalogServiceActionParams
            {
                ServiceSlug = "api-github",
            },
        };
        actionParams.ActionParamsCase.Should().Be(
            WorkflowInteractiveActionParams.ActionParamsOneofCase.CatalogService);

        actionParams.KeyCreate = new WorkflowInteractiveKeyCreateActionParams
        {
            Name = "agent-alpha",
            Platform = "codex",
            AllowedServiceIds = { "m-github" },
        };

        actionParams.ActionParamsCase.Should().Be(
            WorkflowInteractiveActionParams.ActionParamsOneofCase.KeyCreate);
        actionParams.CatalogService.Should().BeNull();
    }
}
