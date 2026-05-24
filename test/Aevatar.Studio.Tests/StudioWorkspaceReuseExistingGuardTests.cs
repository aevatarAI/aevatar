using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class StudioWorkspaceReuseExistingGuardTests
{
    [Fact]
    public void StudioWorkspaceScopedDraftRefactor_ShouldNotAddParallelScopedWorkspaceAuthority()
    {
        var source = ReadProductionSource();

        source.Should().NotContain("interface IScopedStudioWorkspacePort");
        source.Should().NotContain("class ScopedStudioWorkspacePort");
        source.Should().NotContain("class ScopedStudioWorkspaceGAgent");
        source.Should().NotContain("record ScopedWorkspaceEnvelope");
        source.Should().NotContain("class ScopedWorkspaceProjectionPhase");
    }

    private static string ReadProductionSource()
    {
        var repositoryRoot = FindRepositoryRoot();
        return string.Join(
            Environment.NewLine,
            Directory
                .EnumerateFiles(Path.Combine(repositoryRoot, "src"), "*.cs", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .Select(File.ReadAllText));
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

        throw new InvalidOperationException("Repository root could not be found.");
    }
}
