using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.Workflow.Extensions.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class AgentToolAdmissionHostCompositionTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void AddWorkflowAgentToolAdmission_ForLocalEnvironment_ShouldResolveAvailableLedger(
        string environmentName)
    {
        var builder = CreateBuilder(environmentName);
        builder.AddAevatarPlatform(options => options.EnableWorkflowCapability = false);

        builder.AddWorkflowAgentToolAdmission();

        using var provider = builder.Services.BuildServiceProvider();
        provider.GetRequiredService<IAgentToolAdmissionLedger>()
            .Should().NotBeOfType<UnavailableAgentToolAdmissionLedger>();
    }

    [Fact]
    public void AddWorkflowAgentToolAdmission_InProductionWithoutRedis_ShouldFailAtComposition()
    {
        var builder = CreateBuilder(Environments.Production);
        builder.AddAevatarPlatform(options => options.EnableWorkflowCapability = false);

        var action = () => builder.AddWorkflowAgentToolAdmission();

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*AgentToolAdmission:RedisConnectionString*");
    }

    [Fact]
    public void AddWorkflowAgentToolAdmission_InProductionWithRedis_ShouldReplaceUnavailableLedger()
    {
        var builder = CreateBuilder(
            Environments.Production,
            new Dictionary<string, string?>
            {
                ["AgentToolAdmission:RedisConnectionString"] = "127.0.0.1:6379,abortConnect=false",
                ["AgentToolAdmission:KeyPrefix"] = "aevatar:workflow-test:agent-tool-admission:v1:",
            });
        builder.AddAevatarPlatform(options => options.EnableWorkflowCapability = false);

        builder.AddWorkflowAgentToolAdmission();

        var descriptor = builder.Services.Last(service =>
            service.ServiceType == typeof(IAgentToolAdmissionLedger));
        descriptor.ImplementationType.Should().NotBe(typeof(UnavailableAgentToolAdmissionLedger));
    }

    [Fact]
    public void AddWorkflowAgentToolAdmission_InProduction_ShouldOwnDistinctDefaultKeyPrefix()
    {
        var builder = CreateBuilder(
            Environments.Production,
            new Dictionary<string, string?>
            {
                ["AgentToolAdmission:RedisConnectionString"] = "127.0.0.1:6379,abortConnect=false",
            });
        builder.AddAevatarPlatform(options => options.EnableWorkflowCapability = false);

        builder.AddWorkflowAgentToolAdmission();

        var descriptor = builder.Services.Last(service =>
            service.ServiceType.FullName ==
            "Aevatar.AI.Infrastructure.ToolExecution.AgentToolAdmissionLedgerOptions");
        descriptor.ImplementationInstance.Should().NotBeNull();
        var options = descriptor.ImplementationInstance!;
        var keyPrefix = options.GetType().GetProperty("KeyPrefix")!.GetValue(options);
        keyPrefix.Should().Be("aevatar:workflow:agent-tool-admission:v1:");
        keyPrefix.Should().NotBe("aevatar:mainnet:agent-tool-admission:v1:");
    }

    private static WebApplicationBuilder CreateBuilder(
        string environmentName,
        IReadOnlyDictionary<string, string?>? values = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environmentName,
        });
        if (values is not null)
            builder.Configuration.AddInMemoryCollection(values);
        return builder;
    }
}
