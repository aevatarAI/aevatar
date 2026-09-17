using Aevatar.Configuration;
using FluentAssertions;

namespace Aevatar.Integration.Tests;

public class MCPConfigTests
{
    [Fact]
    public void LoadServers_WhenFileMissing_ShouldReturnEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), "aevatar-mcp-missing-" + Guid.NewGuid().ToString("N") + ".json");

        var servers = AevatarMCPConfig.LoadServers(path);

        servers.Should().BeEmpty();
    }

    [Fact]
    public void LoadServers_ShouldParseMcpServersAndDefaults()
    {
        var path = WriteTempJson("""
            {
              "mcpServers": {
                "alpha": {
                  "command": "node",
                  "args": ["server.js", "--stdio"],
                  "env": { "TOKEN": "abc", "REGION": "us-east-1" },
                  "timeoutMs": 12345
                },
                "beta": {
                  "command": "python"
                },
                "remote": {
                  "url": "https://mcp.example.com/mcp",
                  "headers": { "x-tenant": "demo" },
                  "timeoutMs": 23456
                }
              }
            }
            """);

        try
        {
            var servers = AevatarMCPConfig.LoadServers(path);

            servers.Should().HaveCount(3);
            var alpha = servers.Single(x => x.Name == "alpha");
            alpha.Command.Should().Be("node");
            alpha.Args.Should().Equal("server.js", "--stdio");
            alpha.TimeoutMs.Should().Be(12345);
            alpha.Env.Should().ContainKey("TOKEN").WhoseValue.Should().Be("abc");
            alpha.Env.Should().ContainKey("REGION").WhoseValue.Should().Be("us-east-1");

            var beta = servers.Single(x => x.Name == "beta");
            beta.Command.Should().Be("python");
            beta.Args.Should().BeEmpty();
            beta.TimeoutMs.Should().Be(60000);
            beta.Env.Should().BeEmpty();

            var remote = servers.Single(x => x.Name == "remote");
            remote.Command.Should().BeEmpty();
            remote.Url.Should().Be("https://mcp.example.com/mcp");
            remote.Headers.Should().ContainKey("x-tenant").WhoseValue.Should().Be("demo");
            remote.TimeoutMs.Should().Be(23456);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadServers_WhenJsonInvalid_ShouldReturnEmpty()
    {
        var path = WriteTempJson("{ \"mcpServers\": { \"bad\": ");

        try
        {
            var servers = AevatarMCPConfig.LoadServers(path);
            servers.Should().BeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTempJson(string json)
    {
        var file = Path.Combine(Path.GetTempPath(), "aevatar-mcp-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, json);
        return file;
    }
}
