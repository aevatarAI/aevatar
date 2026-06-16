using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Core.Schedules;
using Aevatar.Workflow.Extensions.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowPlatformScheduledDispatchTests
{
    [Fact]
    public void AddAevatarPlatform_WithWorkflowCapability_ShouldRegisterScheduledDispatchServices()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

        builder.AddAevatarPlatform(options =>
        {
            options.EnableAIFeatures = false;
            options.EnableScriptingCapability = false;
        });

        builder.Services.Should().Contain(x => x.ServiceType == typeof(IScheduledDispatchApplicationService));

        using var provider = builder.Services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IAgentKindRegistry>();

        registry.TryGetKindForAgentType(typeof(ScheduledDispatchGAgent), out var kind).Should().BeTrue();
        kind.Should().Be("gagent.service.scheduled-dispatch");
    }
}
