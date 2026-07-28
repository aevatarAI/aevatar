namespace Aevatar.Architecture.Tests.Rules;

public sealed class ManagedCodexDependencyBoundaryTests
{
    [Fact]
    public void ManagedCodexLifecycleOrchestrationMustLiveInApplication()
    {
        var root = FindRepositoryRoot();
        var applicationDirectory = Path.Combine(root, "src", "Aevatar.AI.Application.CodexExecution");
        var applicationProject = Path.Combine(
            applicationDirectory,
            "Aevatar.AI.Application.CodexExecution.csproj");

        Assert.True(File.Exists(applicationProject),
            "Managed Codex lifecycle orchestration requires a dedicated Application project.");

        var lifecycleDefinitions = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(static path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains(
                "class ManagedCodexCredentialLifecycle",
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        Assert.Single(lifecycleDefinitions);
        Assert.StartsWith(
            "src/Aevatar.AI.Application.CodexExecution/",
            lifecycleDefinitions[0].Replace(Path.DirectorySeparatorChar, '/'),
            StringComparison.Ordinal);

        var projectText = File.ReadAllText(applicationProject);
        var forbiddenDependencies = new[]
        {
            "Aevatar.AI.Infrastructure.ChronoSandbox",
            "Aevatar.AI.ToolProviders.NyxId",
            "StackExchange.Redis",
            "Microsoft.AspNetCore",
            "Aevatar.Mainnet.Host",
        };
        var violations = forbiddenDependencies
            .Where(dependency => projectText.Contains(dependency, StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void ManagedCodexExecutionPortMustLiveInApplication()
    {
        var root = FindRepositoryRoot();
        var managedExecutionPorts = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(static path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return System.Text.RegularExpressions.Regex.IsMatch(
                           source,
                           @":\s*ICodexExecutionPort\b",
                           System.Text.RegularExpressions.RegexOptions.CultureInvariant) &&
                       source.Contains(
                           "CodexExecutionTarget.TargetOneofCase.ManagedSandbox",
                           StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        Assert.Single(managedExecutionPorts);
        Assert.StartsWith(
            "src/Aevatar.AI.Application.CodexExecution/",
            managedExecutionPorts[0].Replace(Path.DirectorySeparatorChar, '/'),
            StringComparison.Ordinal);

        var transportSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Aevatar.AI.Infrastructure.ChronoSandbox",
            "NyxIdManagedCodexChronoTransport.cs"));
        Assert.DoesNotContain(
            "IManagedCodexCredentialQueryPort",
            transportSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryMustNotRestoreTheDirectOpenSandboxSdkPath()
    {
        var root = FindRepositoryRoot();
        var forbiddenTerms = new[]
        {
            "Alibaba." + "OpenSandbox",
            "OpenSandboxCodexExecution" + "Adapter",
            "AddOpenSandboxCodex" + "Execution",
            "tools/" + "opensandbox-codex-smoke",
        };
        var files = new[] { "src", "tools", ".github" }
            .SelectMany(directory => Directory.EnumerateFiles(
                Path.Combine(root, directory),
                "*",
                SearchOption.AllDirectories))
            .Where(static path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                Path.GetExtension(path) is ".cs" or ".csproj" or ".props" or ".yml" or ".yaml")
            .Append(Path.Combine(root, "aevatar.slnx"))
            .Append(Path.Combine(root, "Directory.Packages.props"));

        var violations = files
            .SelectMany(path => forbiddenTerms
                .Where(term => File.ReadAllText(path).Contains(term, StringComparison.Ordinal))
                .Select(term => $"{Path.GetRelativePath(root, path)}: {term}"))
            .ToArray();

        Assert.Empty(violations);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "aevatar.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
