using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.AI.ToolProviders.Lark.Tests;

public sealed class LarkWorkflowFileSubmitRegistrationTests
{

    [Fact]
    public void AddLarkTools_ShouldNotRegisterWorkflowFileSubmitAdapter()
    {
        var services = new ServiceCollection();
        services.AddLarkTools(options => options.ProviderSlug = "api-lark-bot");

        services.Should().NotContain(descriptor =>
            descriptor.ServiceType.FullName != null &&
            descriptor.ServiceType.FullName.Contains("WorkflowConnectedServiceFileSubmit", StringComparison.Ordinal));
    }
}
