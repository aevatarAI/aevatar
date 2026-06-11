using FluentAssertions;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class SharedOrleansPortAllocatorSourceRegressionTests
{
    [Fact]
    public void SharedOrleansHostStartupSources_ShouldNotReintroducePerTestTcpPortReservation()
    {
        var repositoryRoot = FindRepositoryRoot();
        var migratedSources = new[]
        {
            Path.Combine(
                repositoryRoot,
                "test",
                "Aevatar.Foundation.Runtime.Hosting.Tests",
                "AgentKindGrainActivationIntegrationTests.cs"),
            Path.Combine(
                repositoryRoot,
                "test",
                "Aevatar.Foundation.Runtime.Hosting.Tests",
                "OrleansGarnetPersistenceIntegrationTests.cs"),
            Path.Combine(
                repositoryRoot,
                "test",
                "Aevatar.Foundation.Runtime.Hosting.Tests",
                "OrleansRuntimeActorStateStoreIntegrationTests.cs"),
            Path.Combine(
                repositoryRoot,
                "test",
                "Aevatar.Foundation.Runtime.Hosting.Tests",
                "RuntimeCallbackSchedulerGrainCredentialGuardIntegrationTests.cs"),
            Path.Combine(
                repositoryRoot,
                "test",
                "Aevatar.GAgents.ChannelRuntime.Tests",
                "RuntimeCallbackSchedulerGrainTestHarness.cs"),
        };

        foreach (var sourcePath in migratedSources)
        {
            var source = File.ReadAllText(sourcePath);

            source.Should().NotContain("ReserveTcpPort", sourcePath);
            source.Should().NotContain("TcpListener(IPAddress.Loopback, 0)", sourcePath);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
    }
}
