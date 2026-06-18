using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.AI.ToolProviders.Lark.Tests;

public sealed class LarkWorkflowFileSubmitAdapterRegistrationTests
{
    [Fact]
    public void AddLarkTools_ShouldRegisterFileSubmitAdapterOnlyWhenEnabled()
    {
        var services = new ServiceCollection();
        services.AddLarkTools(options =>
        {
            options.ProviderSlug = "api-lark-bot";
            options.EnableWorkflowFileSubmit = true;
        });

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IWorkflowConnectedServiceFileSubmitAdapter) &&
            descriptor.ImplementationType == typeof(LarkWorkflowFileSubmitAdapter));

        var disabled = new ServiceCollection();
        disabled.AddLarkTools(options =>
        {
            options.ProviderSlug = "api-lark-bot";
            options.EnableWorkflowFileSubmit = false;
        });

        disabled.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(IWorkflowConnectedServiceFileSubmitAdapter));
    }
}
