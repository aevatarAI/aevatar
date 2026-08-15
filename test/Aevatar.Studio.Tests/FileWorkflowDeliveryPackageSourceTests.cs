using Aevatar.Studio.Application.Delivery;
using Aevatar.Studio.Infrastructure.Delivery;
using Aevatar.Studio.Infrastructure.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Tests;

public sealed class FileWorkflowDeliveryPackageSourceTests
{
    [Fact]
    public async Task ReadSourceYamlAsync_ShouldReadExactYamlFromConfiguredPackageDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"aevatar-delivery-package-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            const string yaml = "name: package_alpha\nsteps: []\n";
            await File.WriteAllTextAsync(Path.Combine(directory, "package_alpha.yaml"), yaml);
            var source = new FileWorkflowDeliveryPackageSource(
                Options.Create(new WorkflowDeliveryOptions { PackageDirectory = directory }));

            var result = await source.ReadSourceYamlAsync("package_alpha");

            result.Should().Be(yaml);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("../package_alpha")]
    [InlineData("nested/package_alpha")]
    [InlineData("")]
    public async Task ReadSourceYamlAsync_ShouldRejectNonFileNames(string workflowName)
    {
        var source = new FileWorkflowDeliveryPackageSource(
            Options.Create(new WorkflowDeliveryOptions { PackageDirectory = Path.GetTempPath() }));

        var result = await source.ReadSourceYamlAsync(workflowName);

        result.Should().BeNull();
    }

    [Fact]
    public void AddStudioInfrastructure_ShouldRegisterDeliveryPackageSource()
    {
        var services = new ServiceCollection();

        services.AddStudioInfrastructure(new ConfigurationBuilder().Build());

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IWorkflowDeliveryPackageSource) &&
            descriptor.ImplementationType == typeof(FileWorkflowDeliveryPackageSource));
    }
}
