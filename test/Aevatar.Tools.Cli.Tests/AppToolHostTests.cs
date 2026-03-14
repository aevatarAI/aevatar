using Aevatar.Bootstrap.Connectors;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Tools.Cli.Hosting;
using Aevatar.Workflow.Core.Connectors;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Tools.Cli.Tests;

public class AppToolHostTests
{
    [Fact]
    public void LoadNamedConnectors_WhenConnectorsJsonContainsCliConnector_ShouldRegisterConnector()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "aevatar-app-toolhost-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var connectorsPath = Path.Combine(tempDir, "connectors.json");

        try
        {
            File.WriteAllText(
                connectorsPath,
                """
                {
                  "connectors": [
                    {
                      "name": "aevatar_oc_gateway_status",
                      "type": "cli",
                      "enabled": true,
                      "timeoutMs": 15000,
                      "cli": {
                        "command": "aevatar",
                        "fixedArguments": ["config", "doctor", "--json"],
                        "allowedInputKeys": []
                      }
                    }
                  ]
                }
                """);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConnectorRegistry, InMemoryConnectorRegistry>();
            services.AddSingleton<IConnectorBuilder, CliConnectorBuilder>();
            using var provider = services.BuildServiceProvider();

            var names = AppToolHost.LoadNamedConnectors(provider, connectorsPath);

            names.Should().Contain("aevatar_oc_gateway_status");
            var registry = provider.GetRequiredService<IConnectorRegistry>();
            registry.TryGet("aevatar_oc_gateway_status", out var connector).Should().BeTrue();
            connector.Should().NotBeNull();
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // ignore cleanup failures in test temp directory
            }
        }
    }

    [Fact]
    public void LoadNamedConnectors_WhenConnectorsJsonMissing_ShouldReturnEmptyConnectorList()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "aevatar-app-toolhost-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var missingConnectorsPath = Path.Combine(tempDir, "missing.connectors.json");

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConnectorRegistry, InMemoryConnectorRegistry>();
            services.AddSingleton<IConnectorBuilder, CliConnectorBuilder>();
            using var provider = services.BuildServiceProvider();

            var names = AppToolHost.LoadNamedConnectors(provider, missingConnectorsPath);

            names.Should().BeEmpty();
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // ignore cleanup failures in test temp directory
            }
        }
    }
}
