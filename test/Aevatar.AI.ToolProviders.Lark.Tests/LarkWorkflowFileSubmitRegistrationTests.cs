using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.AI.ToolProviders.Lark.Tests;

public sealed class LarkWorkflowFileSubmitRegistrationTests
{
    [Fact]
    public void LarkToolOptions_ShouldNotExposeWorkflowFileSubmitToggle()
    {
        typeof(LarkToolOptions).GetProperty("EnableWorkflowFileSubmit")
            .Should()
            .BeNull();
    }

    [Fact]
    public void AddLarkTools_ShouldNotRegisterWorkflowFileSubmitAdapter()
    {
        var services = new ServiceCollection();
        services.AddLarkTools(options => options.ProviderSlug = "api-lark-bot");

        services.Should().NotContain(descriptor =>
            descriptor.ServiceType.FullName != null &&
            descriptor.ServiceType.FullName.Contains("WorkflowConnectedServiceFileSubmit", StringComparison.Ordinal));
    }

    [Fact]
    public void LarkNyxTransport_ShouldNotExposeProductionFileUploadSurfaces()
    {
        typeof(ILarkNyxClient).GetMethod("UploadDriveMediaAsync")
            .Should()
            .BeNull();
        typeof(ILarkNyxClient).GetMethod("UploadApprovalFileAsync")
            .Should()
            .BeNull();
        typeof(ILarkNyxClient).Assembly.GetType("Aevatar.AI.ToolProviders.Lark.LarkDriveMediaUploadRequest")
            .Should()
            .BeNull();
        typeof(ILarkNyxClient).Assembly.GetType("Aevatar.AI.ToolProviders.Lark.LarkApprovalFileUploadRequest")
            .Should()
            .BeNull();
    }
}
