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

    [Fact]
    public void LoadConnectors_ShouldParseDefinitionsShape_AndClampRanges()
    {
        var path = WriteTempJson("""
            {
              "connectors": {
                "definitions": [
                  {
                    "name": "mcp_demo",
                    "type": "mcp",
                    "enabled": "true",
                    "timeoutMs": 999999,
                    "retry": 9,
                    "mcp": {
                      "serverName": "local-mcp",
                      "command": "node",
                      "arguments": ["server.js", 1],
                      "environment": { "TOKEN": "abc", "PORT": 3000 },
                      "defaultTool": "search",
                      "allowedTools": ["search", "", 12],
                      "allowedInputKeys": ["query", null]
                    }
                  },
                  {
                    "name": "disabled_one",
                    "type": "http",
                    "enabled": false
                  }
                ]
              }
            }
            """);

        try
        {
            var connectors = AevatarConnectorConfig.LoadConnectors(path);
            connectors.Should().ContainSingle();

            var mcp = connectors[0];
            mcp.Name.Should().Be("mcp_demo");
            mcp.Type.Should().Be("mcp");
            mcp.TimeoutMs.Should().Be(300000);
            mcp.Retry.Should().Be(5);
            mcp.MCP.ServerName.Should().Be("local-mcp");
            mcp.MCP.Command.Should().Be("node");
            mcp.MCP.Arguments.Should().Equal("server.js");
            mcp.MCP.Environment.Should().ContainKey("TOKEN").WhoseValue.Should().Be("abc");
            mcp.MCP.Environment.Should().ContainKey("PORT").WhoseValue.Should().Be("3000");
            mcp.MCP.DefaultTool.Should().Be("search");
            mcp.MCP.AllowedTools.Should().Equal("search");
            mcp.MCP.AllowedInputKeys.Should().Equal("query");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadConnectors_ShouldSupportCaseInsensitiveKeys_AndFilterInvalidEntries()
    {
        var path = WriteTempJson("""
            {
              "CONNECTORS": {
                "NoType": {
                  "Name": "bad"
                },
                "MixedCase": {
                  "TyPe": "http",
                  "ENABLED": "true",
                  "TIMEOUTMS": "80",
                  "RETRY": "-3",
                  "Http": {
                    "BaseUrl": "https://example.com",
                    "AllowedMethods": ["POST", "GET"],
                    "AllowedPaths": ["/a", "/b"],
                    "AllowedInputKeys": ["q"],
                    "DefaultHeaders": { "X-Token": "abc" }
                  }
                },
                "DisabledAsString": {
                  "Type": "cli",
                  "Enabled": "false"
                }
              }
            }
            """);

        try
        {
            var connectors = AevatarConnectorConfig.LoadConnectors(path);
            connectors.Should().ContainSingle();

            var http = connectors[0];
            http.Name.Should().Be("MixedCase");
            http.Type.Should().Be("http");
            http.TimeoutMs.Should().Be(100);
            http.Retry.Should().Be(0);
            http.Http.BaseUrl.Should().Be("https://example.com");
            http.Http.AllowedMethods.Should().Contain(["POST", "GET"]);
            http.Http.AllowedPaths.Should().Contain(["/a", "/b"]);
            http.Http.AllowedInputKeys.Should().ContainSingle().Which.Should().Be("q");
            http.Http.DefaultHeaders.Should().ContainKey("X-Token").WhoseValue.Should().Be("abc");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadConnectors_ShouldExpandEnvironmentVariables_InStringFields()
    {
        const string tokenVariable = "AEVATAR_TEST_BRAVE_TOKEN";
        const string baseUrlVariable = "AEVATAR_TEST_BRAVE_BASEURL";
        Environment.SetEnvironmentVariable(tokenVariable, "token-from-env");
        Environment.SetEnvironmentVariable(baseUrlVariable, "https://api.search.brave.com");
        var path = WriteTempJson($$"""
            {
              "connectors": [
                {
                  "name": "brave_search",
                  "type": "http",
                  "http": {
                    "baseUrl": "%{{baseUrlVariable}}%",
                    "defaultHeaders": {
                      "X-Subscription-Token": "%{{tokenVariable}}%"
                    }
                  }
                }
              ]
            }
            """);

        try
        {
            var connectors = AevatarConnectorConfig.LoadConnectors(path);
            connectors.Should().ContainSingle();
            connectors[0].Http.BaseUrl.Should().Be("https://api.search.brave.com");
            connectors[0].Http.DefaultHeaders.Should()
                .ContainKey("X-Subscription-Token").WhoseValue.Should().Be("token-from-env");
        }
        finally
        {
            Environment.SetEnvironmentVariable(tokenVariable, null);
            Environment.SetEnvironmentVariable(baseUrlVariable, null);
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadConnectors_WhenJsonInvalid_ShouldReturnEmpty()
    {
        var path = WriteTempJson("{ \"connectors\": [");

        try
        {
            var connectors = AevatarConnectorConfig.LoadConnectors(path);
            connectors.Should().BeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadConnectors_ShouldParseTelegramUserConnectorConfig()
    {
        var path = WriteTempJson("""
            {
              "connectors": [
                {
                  "name": "telegram_user_main",
                  "type": "telegram_user",
                  "timeoutMs": 45000,
                  "telegramUser": {
                    "apiId": "123456",
                    "apiHash": "hash-abc",
                    "phoneNumber": "+8613800000000",
                    "sessionPath": "telegram-user/main.session",
                    "allowedOperations": ["/sendMessage", "/getUpdates"]
                  }
                }
              ]
            }
            """);

        try
        {
            var connectors = AevatarConnectorConfig.LoadConnectors(path);
            connectors.Should().ContainSingle();

            var telegramUser = connectors[0];
            telegramUser.Name.Should().Be("telegram_user_main");
            telegramUser.Type.Should().Be("telegram_user");
            telegramUser.TimeoutMs.Should().Be(45000);
            telegramUser.TelegramUser.ApiId.Should().Be("123456");
            telegramUser.TelegramUser.ApiHash.Should().Be("hash-abc");
            telegramUser.TelegramUser.PhoneNumber.Should().Be("+8613800000000");
            telegramUser.TelegramUser.SessionPath.Should().Be("telegram-user/main.session");
            telegramUser.TelegramUser.AllowedOperations.Should().Equal("/sendMessage", "/getUpdates");
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
