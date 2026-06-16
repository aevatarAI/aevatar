using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.AI.ToolProviders.Lark.Tests;

public sealed class LarkConnectedServiceResourceFetchAdapterRegistrationTests
{
    [Fact]
    public void AddLarkTools_ShouldRegisterMessageResourceFetchAdapter()
    {
        var services = new ServiceCollection();

        services.AddLarkTools();

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IWorkflowConnectedServiceResourceFetchAdapter) &&
            descriptor.ImplementationType == typeof(LarkMessageResourceFetchAdapter));
    }
}
