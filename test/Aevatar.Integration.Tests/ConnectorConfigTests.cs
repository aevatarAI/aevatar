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

    [Fact]
    public void LoadConnectors_ShouldSupportDirectRootShape_EnvironmentExpansion_AndNyxidFields()
    {
        const string proxyBaseUrl = "https://nyxid.example.com";
        const string authTokenUrl = "https://auth.example.com/oauth/token";
        const string serviceAccountClientId = "svc-client";
        const string serviceAccountClientSecret = "svc-secret";
        const string mcpUrl = "https://nyxid.example.com/mcp";
        const string validatorBaseUrl = "https://validator.example.com";

        var previousValues = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["NYXID_PROXY_BASE_URL"] = Environment.GetEnvironmentVariable("NYXID_PROXY_BASE_URL"),
            ["NYXID_AUTH_TOKEN_URL"] = Environment.GetEnvironmentVariable("NYXID_AUTH_TOKEN_URL"),
            ["NYXID_SA_CLIENT_ID"] = Environment.GetEnvironmentVariable("NYXID_SA_CLIENT_ID"),
            ["NYXID_SA_CLIENT_SECRET"] = Environment.GetEnvironmentVariable("NYXID_SA_CLIENT_SECRET"),
            ["NYXID_MCP_URL"] = Environment.GetEnvironmentVariable("NYXID_MCP_URL"),
            ["FORMAT_VALIDATOR_BASE_URL"] = Environment.GetEnvironmentVariable("FORMAT_VALIDATOR_BASE_URL"),
        };

        Environment.SetEnvironmentVariable("NYXID_PROXY_BASE_URL", proxyBaseUrl);
        Environment.SetEnvironmentVariable("NYXID_AUTH_TOKEN_URL", authTokenUrl);
        Environment.SetEnvironmentVariable("NYXID_SA_CLIENT_ID", serviceAccountClientId);
        Environment.SetEnvironmentVariable("NYXID_SA_CLIENT_SECRET", serviceAccountClientSecret);
        Environment.SetEnvironmentVariable("NYXID_MCP_URL", mcpUrl);
        Environment.SetEnvironmentVariable("FORMAT_VALIDATOR_BASE_URL", validatorBaseUrl);

        var path = WriteTempJson("""
            {
              "chrono_graph": {
                "type": "http",
                "enabled": true,
                "http": {
                  "baseUrl": "${NYXID_PROXY_BASE_URL}",
                  "allowedMethods": ["GET", "POST"],
                  "allowedPaths": ["/api/v1/proxy/s/chrono-graph/*"],
                  "defaultHeaders": {
                    "Content-Type": "application/json"
                  },
                  "auth": {
                    "type": "client_credentials",
                    "tokenUrl": "${NYXID_AUTH_TOKEN_URL}",
                    "clientId": "${NYXID_SA_CLIENT_ID}",
                    "clientSecret": "${NYXID_SA_CLIENT_SECRET}",
                    "scope": "proxy:*"
                  }
                }
              },
              "nyxid_mcp": {
                "type": "mcp",
                "enabled": true,
                "mcp": {
                  "serverName": "nyxid",
                  "url": "${NYXID_MCP_URL}",
                  "auth": {
                    "type": "client_credentials",
                    "tokenUrl": "${NYXID_AUTH_TOKEN_URL}",
                    "clientId": "${NYXID_SA_CLIENT_ID}",
                    "clientSecret": "${NYXID_SA_CLIENT_SECRET}",
                    "scope": "proxy:*"
                  },
                  "allowedTools": [
                    "chrono-graph__get_snapshot",
                    "chrono-graph__create_nodes"
                  ]
                }
              },
              "format_validator": {
                "type": "http",
                "enabled": true,
                "http": {
                  "baseUrl": "${FORMAT_VALIDATOR_BASE_URL}",
                  "allowedMethods": ["POST"],
                  "allowedPaths": ["/validate/*"]
                }
              }
            }
            """);

        try
        {
            var connectors = AevatarConnectorConfig.LoadConnectors(path);
            connectors.Should().HaveCount(3);

            var chronoGraph = connectors.Single(x => x.Name == "chrono_graph");
            chronoGraph.Http.BaseUrl.Should().Be(proxyBaseUrl);
            chronoGraph.Http.AllowedPaths.Should().ContainSingle().Which.Should().Be("/api/v1/proxy/s/chrono-graph/*");
            chronoGraph.Http.Auth.Type.Should().Be("client_credentials");
            chronoGraph.Http.Auth.TokenUrl.Should().Be(authTokenUrl);
            chronoGraph.Http.Auth.ClientId.Should().Be(serviceAccountClientId);
            chronoGraph.Http.Auth.ClientSecret.Should().Be(serviceAccountClientSecret);
            chronoGraph.Http.Auth.Scope.Should().Be("proxy:*");

            var nyxidMcp = connectors.Single(x => x.Name == "nyxid_mcp");
            nyxidMcp.MCP.ServerName.Should().Be("nyxid");
            nyxidMcp.MCP.Url.Should().Be(mcpUrl);
            nyxidMcp.MCP.Command.Should().BeEmpty();
            nyxidMcp.MCP.AllowedTools.Should().Equal("chrono-graph__get_snapshot", "chrono-graph__create_nodes");
            nyxidMcp.MCP.Auth.Type.Should().Be("client_credentials");
            nyxidMcp.MCP.Auth.TokenUrl.Should().Be(authTokenUrl);

            var formatValidator = connectors.Single(x => x.Name == "format_validator");
            formatValidator.Http.BaseUrl.Should().Be(validatorBaseUrl);
            formatValidator.Http.AllowedPaths.Should().ContainSingle().Which.Should().Be("/validate/*");
        }
        finally
        {
            foreach (var (key, value) in previousValues)
                Environment.SetEnvironmentVariable(key, value);

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
