using Aevatar.Configuration;
using FluentAssertions;

namespace Aevatar.Integration.Tests;

public class ConnectorConfigTests
{
    [Fact]
    public void LoadConnectors_ShouldParseArrayShape()
    {
        var path = WriteTempJson("""
            {
              "connectors": [
                {
                  "name": "api_demo",
                  "type": "http",
                  "timeoutMs": 12000,
                  "http": {
                    "baseUrl": "https://api.example.com",
                    "allowedMethods": ["POST"],
                    "allowedPaths": ["/v1/analyze"]
                  }
                },
                {
                  "name": "cli_demo",
                  "type": "cli",
                  "cli": {
                    "command": "echo",
                    "allowedOperations": ["summarize"]
                  }
                }
              ]
            }
            """);

        try
        {
            var connectors = AevatarConnectorConfig.LoadConnectors(path);
            connectors.Should().HaveCount(2);

            var http = connectors.Single(x => x.Name == "api_demo");
            http.Type.Should().Be("http");
            http.TimeoutMs.Should().Be(12000);
            http.Http.BaseUrl.Should().Be("https://api.example.com");
            http.Http.AllowedMethods.Should().ContainSingle().Which.Should().Be("POST");
            http.Http.AllowedPaths.Should().Contain("/v1/analyze");

            var cli = connectors.Single(x => x.Name == "cli_demo");
            cli.Type.Should().Be("cli");
            cli.Cli.Command.Should().Be("echo");
            cli.Cli.AllowedOperations.Should().Contain("summarize");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadConnectors_ShouldParseNamedObjectShape()
    {
        var path = WriteTempJson("""
            {
              "connectors": {
                "maker_post": {
                  "type": "cli",
                  "timeoutMs": 8000,
                  "cli": {
                    "command": "cat"
                  }
                }
              }
            }
            """);

        try
        {
            var connectors = AevatarConnectorConfig.LoadConnectors(path);
            connectors.Should().ContainSingle();
            connectors[0].Name.Should().Be("maker_post");
            connectors[0].Type.Should().Be("cli");
            connectors[0].TimeoutMs.Should().Be(8000);
            connectors[0].Cli.Command.Should().Be("cat");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTempJson(string json)
    {
        var file = Path.Combine(Path.GetTempPath(), "aevatar-connectors-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, json);
        return file;
    }
}
