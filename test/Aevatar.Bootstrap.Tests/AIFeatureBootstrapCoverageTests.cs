using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.MCP;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.Bootstrap.Connectors;
using Aevatar.Bootstrap.Extensions.AI;
using Aevatar.Bootstrap.Extensions.AI.Connectors;
using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.Connectors;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Bootstrap.Tests;

public class AIFeatureBootstrapCoverageTests
{
    [Fact]
    public void AddAevatarAIFeatures_ShouldRegisterCoreServicesWithExplicitApiKey()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        services.AddAevatarAIFeatures(config, options =>
        {
            options.ApiKey = "demo-key";
            options.DefaultProvider = "deepseek";
            options.EnableMEAIProviders = true;
            options.EnableSkills = true;
            options.SkillDirectories.Add("./skills-a");
            options.EnableMCPTools = false;
        });

        using var provider = services.BuildServiceProvider();
        provider.GetService<IRoleAgentTypeResolver>().Should().NotBeNull();

        var llmFactory = provider.GetRequiredService<ILLMProviderFactory>();
        llmFactory.GetDefault().Name.Should().Be("deepseek");

        var skillOptions = provider.GetRequiredService<SkillsOptions>();
        skillOptions.Directories.Should().ContainSingle().Which.Should().Be("./skills-a");
        provider.GetServices<IAgentToolSource>().Should().ContainSingle(x => x is SkillsAgentToolSource);
    }

    [Fact]
    public void AddAevatarAIFeatures_WhenMEAIDisabled_ShouldNotRegisterFactory()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        services.AddAevatarAIFeatures(config, options =>
        {
            options.EnableMEAIProviders = false;
            options.EnableMCPTools = false;
            options.EnableSkills = false;
        });

        using var provider = services.BuildServiceProvider();
        provider.GetService<ILLMProviderFactory>().Should().BeNull();
        provider.GetService<IRoleAgentTypeResolver>().Should().NotBeNull();
    }

    [Fact]
    public async Task AddAevatarAIFeatures_WhenMCPEnabledAndConfigured_ShouldRegisterMCPToolSourceAndConnectorBuilder()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"ai-feature-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);
        var previousHome = Environment.GetEnvironmentVariable(AevatarPaths.HomeEnv);
        Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, tempHome);

        try
        {
            File.WriteAllText(Path.Combine(tempHome, "mcp.json"),
                """
                {
                  "mcpServers": {
                    "local": {
                      "command": "echo",
                      "args": ["hello"],
                      "env": { "K": "V" }
                    }
                  }
                }
                """);

            var services = new ServiceCollection();
            var config = new ConfigurationBuilder().Build();

            services.AddAevatarAIFeatures(config, options =>
            {
                options.EnableMEAIProviders = false;
                options.EnableMCPTools = true;
                options.EnableSkills = false;
            });

            await using var provider = services.BuildServiceProvider();

            provider.GetServices<IAgentToolSource>().Should().ContainSingle(x => x is MCPAgentToolSource);
            provider.GetServices<IConnectorBuilder>().Should().ContainSingle(x => x is MCPConnectorBuilder);

            var mcpOptions = provider.GetRequiredService<MCPToolsOptions>();
            mcpOptions.Servers.Should().ContainSingle();
            mcpOptions.Servers[0].Name.Should().Be("local");
            mcpOptions.Servers[0].Environment["K"].Should().Be("V");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, previousHome);
            Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public void MCPConnectorBuilder_ShouldValidateCommandAndBuildConnector()
    {
        var builder = new MCPConnectorBuilder();

        var invalidEntry = new ConnectorConfigEntry
        {
            Name = "mcp-a",
            Type = "mcp",
            MCP = new MCPConnectorConfig { Command = "" },
        };

        var okInvalid = builder.TryBuild(invalidEntry, NullLogger.Instance, out var invalidConnector);
        okInvalid.Should().BeFalse();
        invalidConnector.Should().BeNull();

        var validEntry = new ConnectorConfigEntry
        {
            Name = "mcp-b",
            Type = "mcp",
            MCP = new MCPConnectorConfig
            {
                ServerName = "server-a",
                Command = "echo",
                Arguments = ["hello"],
                Environment = new Dictionary<string, string> { ["A"] = "B" },
                DefaultTool = "tool-a",
                AllowedTools = ["tool-a"],
                AllowedInputKeys = ["q"],
            },
        };

        var okValid = builder.TryBuild(validEntry, NullLogger.Instance, out var connector);
        okValid.Should().BeTrue();
        connector.Should().NotBeNull();
        connector!.Type.Should().Be("mcp");
        connector.Name.Should().Be("mcp-b");
    }
}
