using System.Diagnostics;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Capabilities.Tests;

public sealed class MainnetBootScriptTests
{
    [Fact]
    public async Task AppSettings_ShouldUseProvisionedConsoleOAuthClient()
    {
        var repoRoot = FindRepoRoot();
        var appSettingsPath = Path.Combine(
            repoRoot,
            "src",
            "Aevatar.Mainnet.Host.Api",
            "appsettings.json");

        await using var stream = File.OpenRead(appSettingsPath);
        using var document = await JsonDocument.ParseAsync(stream);

        document.RootElement
            .GetProperty("Aevatar")
            .GetProperty("BackendConsole")
            .GetProperty("OidcClientId")
            .GetString()
            .Should()
            .Be("a6ff2946-f02f-4c35-8203-1ec46132b660");
    }

    [Fact]
    public async Task DistributedAppSettings_ShouldDisableGraphProvidersByDefault()
    {
        var appSettingsPath = Path.Combine(
            FindRepoRoot(),
            "src",
            "Aevatar.Mainnet.Host.Api",
            "appsettings.Distributed.json");

        await using var stream = File.OpenRead(appSettingsPath);
        using var document = await JsonDocument.ParseAsync(stream);
        var providers = document.RootElement
            .GetProperty("Projection")
            .GetProperty("Graph")
            .GetProperty("Providers");
        var neo4j = providers.GetProperty("Neo4j");

        neo4j.GetProperty("Enabled").GetBoolean().Should().BeFalse();
        neo4j.TryGetProperty("Uri", out _).Should().BeFalse();
        neo4j.TryGetProperty("Username", out _).Should().BeFalse();
        neo4j.TryGetProperty("Password", out _).Should().BeFalse();
        providers.GetProperty("InMemory").GetProperty("Enabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task MainnetClusterCompose_ShouldNotDependOnOrConfigureNeo4j()
    {
        var compose = await File.ReadAllTextAsync(
            Path.Combine(FindRepoRoot(), "docker-compose.mainnet-cluster.yml"));

        compose.Should().NotContain("\n      neo4j:");
        compose.Should().NotContain("Projection__Graph__Providers__Neo4j__Uri");
        compose.Should().NotContain("Projection__Graph__Providers__Neo4j__Username");
        compose.Should().NotContain("Projection__Graph__Providers__Neo4j__Password");
        compose.Split("Projection__Graph__Providers__Neo4j__Enabled: \"false\"")
            .Should().HaveCount(4, "all three mainnet nodes must explicitly keep Neo4j disabled");
    }

    [Fact]
    public void AppSettings_DefaultNyxIdTransport_ShouldUsePublicApi()
    {
        var configuration = BuildMainnetConfiguration();
        var services = new ServiceCollection();

        services.AddNyxIdApiAccess(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<NyxIdToolOptions>();
        options.InternalApiBaseUrl.Should().BeNull();
        options.EffectiveTransportBaseUrl.Should().Be("https://nyx-api.chrono-ai.fun");
        options.PublicTransportFallbackBaseUrl.Should().BeNull();
    }

    [Fact]
    public void AppSettings_DefaultNyxIdChatProfile_ShouldSealDiningPreferenceMergePrecedence()
    {
        var configuration = BuildMainnetConfiguration();
        var instructions = configuration["AgentProfiles:SystemDefaultNyxIdChat:Instructions"];

        instructions.Should().Contain("current user message always overrides recovered defaults");
        instructions.Should().Contain("Interpret recovered context semantically against the selected workflow's expected input");
        instructions.Should().Contain("recovered context fills only missing task inputs");
        instructions.Should().Contain("asks the user only for inputs still missing");
        instructions.Should().Contain("any available current-user read-only profile, preference, or context tool that is relevant");
        instructions.Should().Contain("dinner reservation or dinner date requests");
        instructions.Should().Contain("instead of asking for planning details up front");
        instructions.Should().Contain("workflow start dispatcher may enrich a sparse JSON object");
        instructions.Should().Contain("one companion and no party size");
        instructions.Should().Contain("published dinner_date input contract");
        instructions.Should().Contain("Map semantic values from the current request and recovered context into the selected workflow's contract fields");
        instructions.Should().Contain("preserve nested contract object structure only when that nesting exists in the published contract");
        instructions.Should().Contain("do not create new grouping objects outside the contract shape");
        instructions.Should().Contain("do not wrap them in a new schema");
        instructions.Should().NotContain("home_location");
        instructions.Should().NotContain("preferred_cuisines");
        instructions.Should().NotContain("contact_phone_number");
        instructions.Should().NotContain("raw_user_request");
        instructions.Should().NotContain("restaurant_type");
        instructions.Should().NotContain("preference_context_source_tools");
    }

    [Fact]
    public async Task BootScript_LocalMode_ShouldPassCompleteDevelopmentStartupBoundary()
    {
        var repoRoot = FindRepoRoot();
        var sourceDir = Path.Combine(repoRoot, "src", "Aevatar.Mainnet.Host.Api");

        using var tempDir = new TemporaryDirectory();
        var scriptPath = Path.Combine(tempDir.Path, "boot.sh");
        var projectPath = Path.Combine(tempDir.Path, "Aevatar.Mainnet.Host.Api.csproj");
        var fakeDotnetPath = Path.Combine(tempDir.Path, "record-dotnet-env.sh");
        var recordedEnvironmentPath = Path.Combine(tempDir.Path, "dotnet-env.txt");
        File.Copy(Path.Combine(sourceDir, "boot.sh"), scriptPath);
        File.Copy(Path.Combine(sourceDir, "Aevatar.Mainnet.Host.Api.csproj"), projectPath);
        File.WriteAllText(
            fakeDotnetPath,
            $"#!/usr/bin/env bash\nenv > '{recordedEnvironmentPath}'\nexit 1\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                fakeDotnetPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using var process = Process.Start(CreateProcessStartInfo(
            scriptPath,
            tempDir.Path,
            overrides: new Dictionary<string, string?>
            {
                ["DOTNET_CMD"] = fakeDotnetPath,
                ["AEVATAR_Audit__ActorIdentityHasher__ActiveKeyId"] = null,
                ["AEVATAR_Audit__ActorIdentityHasher__Keys__0__KeyId"] = null,
                ["AEVATAR_Audit__ActorIdentityHasher__Keys__0__Key"] = null,
            }));
        process.Should().NotBeNull();

        await process!.WaitForExitAsync();

        process.ExitCode.Should().NotBe(0);
        var environment = await File.ReadAllTextAsync(recordedEnvironmentPath);
        environment.Should().Contain("AEVATAR_Aevatar__Authentication__Enabled=false");
        environment.Should().Contain("AEVATAR_Audit__ActorIdentityHasher__ActiveKeyId=local-development-key");
        environment.Should().Contain("AEVATAR_Audit__ActorIdentityHasher__Keys__0__KeyId=local-development-key");
        environment.Should().Contain("AEVATAR_Audit__ActorIdentityHasher__Keys__0__Key=local-development-audit-identity-key");
        environment.Should().Contain("AEVATAR_ActorRuntime__Provider=InMemory");
        environment.Should().Contain("AEVATAR_ActorRuntime__SecretStoreBackend=InMemory");
        environment.Should().Contain("AEVATAR_ChannelIdentity__OAuthClient__Bootstrap__Enabled=false");
    }

    [Fact]
    public async Task BootScript_LocalMode_ShouldRespectExplicitConsoleLoginBoundary()
    {
        var repoRoot = FindRepoRoot();
        var sourceDir = Path.Combine(repoRoot, "src", "Aevatar.Mainnet.Host.Api");

        using var tempDir = new TemporaryDirectory();
        var scriptPath = Path.Combine(tempDir.Path, "boot.sh");
        var projectPath = Path.Combine(tempDir.Path, "Aevatar.Mainnet.Host.Api.csproj");
        var fakeDotnetPath = Path.Combine(tempDir.Path, "record-dotnet-env.sh");
        var recordedEnvironmentPath = Path.Combine(tempDir.Path, "dotnet-env.txt");
        File.Copy(Path.Combine(sourceDir, "boot.sh"), scriptPath);
        File.Copy(Path.Combine(sourceDir, "Aevatar.Mainnet.Host.Api.csproj"), projectPath);
        File.WriteAllText(
            fakeDotnetPath,
            $"#!/usr/bin/env bash\nenv > '{recordedEnvironmentPath}'\nexit 1\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                fakeDotnetPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using var process = Process.Start(CreateProcessStartInfo(
            scriptPath,
            tempDir.Path,
            overrides: new Dictionary<string, string?>
            {
                ["DOTNET_CMD"] = fakeDotnetPath,
                ["AEVATAR_Aevatar__Authentication__Enabled"] = "true",
                ["AEVATAR_ChannelIdentity__OAuthClient__Bootstrap__Enabled"] = "true",
            }));
        process.Should().NotBeNull();

        await process!.WaitForExitAsync();

        process.ExitCode.Should().NotBe(0);
        var environment = await File.ReadAllTextAsync(recordedEnvironmentPath);
        environment.Should().Contain("AEVATAR_Aevatar__Authentication__Enabled=true");
        environment.Should().Contain("AEVATAR_ChannelIdentity__OAuthClient__Bootstrap__Enabled=true");
    }

    [Fact]
    public async Task BootScript_OrleansMemoryMode_ShouldSetPureInMemoryBoundary()
    {
        var repoRoot = FindRepoRoot();
        var sourceDir = Path.Combine(repoRoot, "src", "Aevatar.Mainnet.Host.Api");

        using var tempDir = new TemporaryDirectory();
        var scriptPath = Path.Combine(tempDir.Path, "boot.sh");
        var projectPath = Path.Combine(tempDir.Path, "Aevatar.Mainnet.Host.Api.csproj");
        var fakeDotnetPath = Path.Combine(tempDir.Path, "record-dotnet-env.sh");
        var recordedEnvironmentPath = Path.Combine(tempDir.Path, "dotnet-env.txt");
        var recordedArgumentsPath = Path.Combine(tempDir.Path, "dotnet-args.txt");
        File.Copy(Path.Combine(sourceDir, "boot.sh"), scriptPath);
        File.Copy(Path.Combine(sourceDir, "Aevatar.Mainnet.Host.Api.csproj"), projectPath);
        File.WriteAllText(
            fakeDotnetPath,
            $"#!/usr/bin/env bash\nenv > '{recordedEnvironmentPath}'\nprintf '%s\\n' \"$@\" > '{recordedArgumentsPath}'\nexit 1\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                fakeDotnetPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using var process = Process.Start(CreateProcessStartInfo(
            scriptPath,
            tempDir.Path,
            "orleans-memory",
            new Dictionary<string, string?>
            {
                ["DOTNET_CMD"] = fakeDotnetPath,
                ["AEVATAR_ActorRuntime__OrleansGarnetConnectionString"] = "localhost:6379",
                ["AEVATAR_ActorRuntime__KafkaBootstrapServers"] = "localhost:9092",
                ["AEVATAR_Projection__Graph__Providers__Neo4j__Password"] = "stale-password",
            }));
        process.Should().NotBeNull();

        await process!.WaitForExitAsync();

        process.ExitCode.Should().NotBe(0);
        var environment = await File.ReadAllTextAsync(recordedEnvironmentPath);
        environment.Should().Contain("ASPNETCORE_ENVIRONMENT=Development");
        environment.Should().Contain("DOTNET_ENVIRONMENT=Development");
        environment.Should().Contain("AEVATAR_Aevatar__Authentication__Enabled=false");
        environment.Should().Contain("AEVATAR_Aevatar__NyxId__AssistantActions__Enabled=false");
        environment.Should().Contain("AEVATAR_Aevatar__Status__UseBuiltInTargets=false");
        environment.Should().Contain("AEVATAR_ActorRuntime__Provider=Orleans");
        environment.Should().Contain("AEVATAR_ActorRuntime__OrleansStreamBackend=InMemory");
        environment.Should().Contain("AEVATAR_ActorRuntime__OrleansPersistenceBackend=InMemory");
        environment.Should().Contain("AEVATAR_ActorRuntime__SecretStoreBackend=InMemory");
        environment.Should().Contain("AEVATAR_Orleans__ClusteringMode=Localhost");
        environment.Should().Contain("AEVATAR_Projection__Document__Providers__InMemory__Enabled=true");
        environment.Should().Contain("AEVATAR_Projection__Document__Providers__Elasticsearch__Enabled=false");
        environment.Should().Contain("AEVATAR_Projection__Graph__Providers__InMemory__Enabled=true");
        environment.Should().Contain("AEVATAR_Projection__Graph__Providers__Neo4j__Enabled=false");
        environment.Should().Contain("AEVATAR_GAgentService__Demo__Enabled=false");
        environment.Should().NotContain("AEVATAR_ActorRuntime__OrleansGarnetConnectionString=");
        environment.Should().NotContain("AEVATAR_ActorRuntime__KafkaBootstrapServers=");
        environment.Should().NotContain("AEVATAR_Projection__Graph__Providers__Neo4j__Password=");

        var arguments = await File.ReadAllLinesAsync(recordedArgumentsPath);
        arguments.Should().ContainInOrder("run", "--nologo", "--project");
    }

    [Fact]
    public async Task BootScript_LocalMode_ShouldNotRequireNeo4jPasswordFromInheritedDistributedEnv()
    {
        var repoRoot = FindRepoRoot();
        var sourceDir = Path.Combine(repoRoot, "src", "Aevatar.Mainnet.Host.Api");

        using var tempDir = new TemporaryDirectory();
        var scriptPath = Path.Combine(tempDir.Path, "boot.sh");
        var projectPath = Path.Combine(tempDir.Path, "Aevatar.Mainnet.Host.Api.csproj");
        File.Copy(Path.Combine(sourceDir, "boot.sh"), scriptPath);
        File.Copy(Path.Combine(sourceDir, "Aevatar.Mainnet.Host.Api.csproj"), projectPath);

        using var process = Process.Start(CreateProcessStartInfo(scriptPath, tempDir.Path));
        process.Should().NotBeNull();

        var stdoutTask = process!.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        process.ExitCode.Should().NotBe(0);
        stderr.Should().NotContain("Distributed mode with Neo4j enabled requires an explicit Neo4j password.");
        stderr.Should().Contain("Aevatar.Mainnet.Host.Api failed to start.");
        stdout.Should().Contain("==> Starting Aevatar.Mainnet.Host.Api");
    }

    [Fact]
    public async Task BootScript_DistributedMode_ShouldNotRequireNeo4jPassword_WhenNeo4jDisabled()
    {
        var repoRoot = FindRepoRoot();
        var sourceDir = Path.Combine(repoRoot, "src", "Aevatar.Mainnet.Host.Api");

        using var tempDir = new TemporaryDirectory();
        var scriptPath = Path.Combine(tempDir.Path, "boot.sh");
        var projectPath = Path.Combine(tempDir.Path, "Aevatar.Mainnet.Host.Api.csproj");
        File.Copy(Path.Combine(sourceDir, "boot.sh"), scriptPath);
        File.Copy(Path.Combine(sourceDir, "Aevatar.Mainnet.Host.Api.csproj"), projectPath);

        using var process = Process.Start(CreateProcessStartInfo(
            scriptPath,
            tempDir.Path,
            "distributed",
            new Dictionary<string, string?>
            {
                ["AEVATAR_Projection__Graph__Providers__Neo4j__Enabled"] = "false",
            }));
        process.Should().NotBeNull();

        var stdoutTask = process!.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        process.ExitCode.Should().NotBe(0);
        stderr.Should().NotContain("Distributed mode with Neo4j enabled requires an explicit Neo4j password.");
        stderr.Should().Contain("Aevatar.Mainnet.Host.Api failed to start.");
        stdout.Should().Contain("==> Mode: distributed");
    }

    [Fact]
    public async Task BootScript_DistributedMode_ShouldNotRequireNeo4jPassword_ByDefault()
    {
        var repoRoot = FindRepoRoot();
        var sourceDir = Path.Combine(repoRoot, "src", "Aevatar.Mainnet.Host.Api");

        using var tempDir = new TemporaryDirectory();
        var scriptPath = Path.Combine(tempDir.Path, "boot.sh");
        var projectPath = Path.Combine(tempDir.Path, "Aevatar.Mainnet.Host.Api.csproj");
        File.Copy(Path.Combine(sourceDir, "boot.sh"), scriptPath);
        File.Copy(Path.Combine(sourceDir, "Aevatar.Mainnet.Host.Api.csproj"), projectPath);

        using var process = Process.Start(CreateProcessStartInfo(
            scriptPath,
            tempDir.Path,
            "distributed",
            new Dictionary<string, string?>
            {
                ["AEVATAR_Projection__Graph__Providers__Neo4j__Enabled"] = null,
            }));
        process.Should().NotBeNull();

        var stdoutTask = process!.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        process.ExitCode.Should().NotBe(0);
        stderr.Should().NotContain("Distributed mode with Neo4j enabled requires an explicit Neo4j password.");
        stderr.Should().Contain("Aevatar.Mainnet.Host.Api failed to start.");
        stdout.Should().Contain("==> Mode: distributed");
    }

    [Fact]
    public async Task BootScript_DistributedMode_ShouldRequireNeo4jPassword_WhenExplicitlyEnabled()
    {
        var repoRoot = FindRepoRoot();
        var sourceDir = Path.Combine(repoRoot, "src", "Aevatar.Mainnet.Host.Api");

        using var tempDir = new TemporaryDirectory();
        var scriptPath = Path.Combine(tempDir.Path, "boot.sh");
        var projectPath = Path.Combine(tempDir.Path, "Aevatar.Mainnet.Host.Api.csproj");
        File.Copy(Path.Combine(sourceDir, "boot.sh"), scriptPath);
        File.Copy(Path.Combine(sourceDir, "Aevatar.Mainnet.Host.Api.csproj"), projectPath);

        using var process = Process.Start(CreateProcessStartInfo(
            scriptPath,
            tempDir.Path,
            "distributed"));
        process.Should().NotBeNull();

        var stdoutTask = process!.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        process.ExitCode.Should().NotBe(0);
        stderr.Should().Contain("Distributed mode with Neo4j enabled requires an explicit Neo4j password.");
        stderr.Should().NotContain("Aevatar.Mainnet.Host.Api failed to start.");
        stdout.Should().Contain("==> Mode: distributed");
    }

    [Fact]
    public async Task BootScript_DistributedMode_ShouldHonorBareNeo4jFlagPrecedence()
    {
        var repoRoot = FindRepoRoot();
        var sourceDir = Path.Combine(repoRoot, "src", "Aevatar.Mainnet.Host.Api");

        using var tempDir = new TemporaryDirectory();
        var scriptPath = Path.Combine(tempDir.Path, "boot.sh");
        var projectPath = Path.Combine(tempDir.Path, "Aevatar.Mainnet.Host.Api.csproj");
        File.Copy(Path.Combine(sourceDir, "boot.sh"), scriptPath);
        File.Copy(Path.Combine(sourceDir, "Aevatar.Mainnet.Host.Api.csproj"), projectPath);

        using var process = Process.Start(CreateProcessStartInfo(
            scriptPath,
            tempDir.Path,
            "distributed",
            new Dictionary<string, string?>
            {
                ["AEVATAR_Projection__Graph__Providers__Neo4j__Enabled"] = "false",
                ["Projection__Graph__Providers__Neo4j__Enabled"] = "true",
            }));
        process.Should().NotBeNull();

        var stdoutTask = process!.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        process.ExitCode.Should().NotBe(0);
        stderr.Should().Contain("Distributed mode with Neo4j enabled requires an explicit Neo4j password.");
        stderr.Should().NotContain("Aevatar.Mainnet.Host.Api failed to start.");
        stdout.Should().Contain("==> Mode: distributed");
    }

    [Fact]
    public async Task BootScript_DistributedMode_ShouldLetBareDisableOverrideStalePrefixedEnable()
    {
        var repoRoot = FindRepoRoot();
        var sourceDir = Path.Combine(repoRoot, "src", "Aevatar.Mainnet.Host.Api");

        using var tempDir = new TemporaryDirectory();
        var scriptPath = Path.Combine(tempDir.Path, "boot.sh");
        var projectPath = Path.Combine(tempDir.Path, "Aevatar.Mainnet.Host.Api.csproj");
        File.Copy(Path.Combine(sourceDir, "boot.sh"), scriptPath);
        File.Copy(Path.Combine(sourceDir, "Aevatar.Mainnet.Host.Api.csproj"), projectPath);

        using var process = Process.Start(CreateProcessStartInfo(
            scriptPath,
            tempDir.Path,
            "distributed",
            new Dictionary<string, string?>
            {
                ["AEVATAR_Projection__Graph__Providers__Neo4j__Enabled"] = "true",
                ["Projection__Graph__Providers__Neo4j__Enabled"] = "false",
            }));
        process.Should().NotBeNull();

        var stdoutTask = process!.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        process.ExitCode.Should().NotBe(0);
        stderr.Should().NotContain("Distributed mode with Neo4j enabled requires an explicit Neo4j password.");
        stderr.Should().Contain("Aevatar.Mainnet.Host.Api failed to start.");
        stdout.Should().Contain("==> Mode: distributed");
    }

    private static ProcessStartInfo CreateProcessStartInfo(
        string scriptPath,
        string workingDirectory,
        string mode = "local",
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var startInfo = new ProcessStartInfo("/bin/bash")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("--mode");
        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add("5187");

        startInfo.Environment["DOTNET_CMD"] = "/usr/bin/false";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Distributed";
        startInfo.Environment["AEVATAR_Projection__Graph__Providers__Neo4j__Enabled"] = "true";
        startInfo.Environment.Remove("Projection__Graph__Providers__Neo4j__Enabled");
        startInfo.Environment.Remove("AEVATAR_Projection__Graph__Providers__Neo4j__Password");
        startInfo.Environment.Remove("NEO4J_PASSWORD");

        if (overrides is not null)
        {
            foreach (var entry in overrides)
            {
                if (entry.Value is null)
                    startInfo.Environment.Remove(entry.Key);
                else
                    startInfo.Environment[entry.Key] = entry.Value;
            }
        }

        return startInfo;
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "aevatar.slnx")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }

    private static IConfigurationRoot BuildMainnetConfiguration()
    {
        var appSettingsPath = Path.Combine(
            FindRepoRoot(),
            "src",
            "Aevatar.Mainnet.Host.Api",
            "appsettings.json");
        using var stream = File.OpenRead(appSettingsPath);
        return new ConfigurationBuilder().AddJsonStream(stream).Build();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aevatar-mainnet-boot-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
