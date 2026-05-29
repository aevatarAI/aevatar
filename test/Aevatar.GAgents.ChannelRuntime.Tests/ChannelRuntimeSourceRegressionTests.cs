using System.Text;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelRuntimeSourceRegressionTests
{
    [Fact]
    public void Channel_runtime_must_not_reintroduce_process_local_io_queue_or_lease_shell()
    {
        var source = ReadRepositorySources(
            "agents/Aevatar.GAgents.Channel.Runtime",
            "agents/Aevatar.GAgents.NyxidChat");

        source.Should().NotContain("LongRunningBusinessIoExecutor",
            "deleted per iter107/cluster-1 - actor-owned operation state + self-continuation");
        source.Should().NotContain("Channel<LongRunningBusinessIoWorkItem>",
            "deleted per iter107/cluster-1 - no process-local IO work queue");
        source.Should().NotContain("IDisposableProviderIoLease",
            "no-op lease abstraction deleted per Phase 8 r1 reject");
        source.Should().NotContain("DisposableProviderIoLeaseFactory",
            "no-op lease abstraction deleted per Phase 8 r1 reject");
    }

    [Fact]
    public void Startup_projection_activation_must_not_reintroduce_delay_backed_retry_loop()
    {
        foreach (var relativeFile in new[]
                 {
                     "agents/Aevatar.GAgents.Channel.Runtime/ChannelBotRegistrationStartupService.cs",
                     "agents/Aevatar.GAgents.Device/DeviceRegistrationStartupService.cs",
                     "agents/Aevatar.GAgents.Scheduled/UserAgentCatalogStartupService.cs",
                 })
        {
            var source = ReadRepositoryFile(relativeFile);

            source.Should().NotContain("await Task." + "Delay(",
                $"{relativeFile} should dispatch one activation attempt; retry/backoff belongs to actor/runtime scheduling infrastructure");
            source.Should().NotContain("MaxRetries",
                $"{relativeFile} should not own projection activation retry state in the hosted service");
        }
    }

    private static string ReadRepositorySources(params string[] relativeDirectories)
    {
        var repositoryRoot = GetRepositoryRoot();
        var builder = new StringBuilder();
        foreach (var relativeDirectory in relativeDirectories)
        {
            var directory = Path.Combine(repositoryRoot, relativeDirectory);
            foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                         .Where(static file =>
                             file.EndsWith(".cs", StringComparison.Ordinal) ||
                             file.EndsWith(".proto", StringComparison.Ordinal)))
            {
                builder.AppendLine(File.ReadAllText(file, Encoding.UTF8));
            }
        }

        return builder.ToString();
    }

    private static string ReadRepositoryFile(string relativeFile)
    {
        var repositoryRoot = GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(repositoryRoot, relativeFile), Encoding.UTF8);
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }
}
