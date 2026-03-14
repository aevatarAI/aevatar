using System.CommandLine;
using System.Text.Json;
using Aevatar.Configuration;
using Aevatar.Tools.Cli;
using FluentAssertions;

namespace Aevatar.Integration.Tests;

public sealed class CliConfigCommandTests
{
    [Fact]
    public async Task ConfigPathsShow_ShouldReturnJsonEnvelope()
    {
        await WithTempHomeAsync(async _ =>
        {
            var result = await InvokeCliAsync(["config", "paths", "show", "--json"]);
            result.ExitCode.Should().Be(0);

            using var doc = ParseJson(result.StdOut);
            doc.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("code").GetString().Should().Be("OK");
            doc.RootElement.GetProperty("data").GetProperty("root").GetString().Should().NotBeNullOrWhiteSpace();
        });
    }

    [Fact]
    public async Task ConfigSecrets_SetGetRemove_ShouldRoundtrip()
    {
        await WithTempHomeAsync(async _ =>
        {
            var setResult = await InvokeCliAsync(["config", "secrets", "set", "Demo:ApiKey", "sk-demo", "--json"]);
            setResult.ExitCode.Should().Be(0);

            var getResult = await InvokeCliAsync(["config", "secrets", "get", "Demo:ApiKey", "--json"]);
            getResult.ExitCode.Should().Be(0);
            using (var getDoc = ParseJson(getResult.StdOut))
            {
                getDoc.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
                getDoc.RootElement.GetProperty("data").GetProperty("value").GetString().Should().Be("sk-demo");
            }

            var removeResult = await InvokeCliAsync(["config", "secrets", "remove", "Demo:ApiKey", "--yes", "--json"]);
            removeResult.ExitCode.Should().Be(0);

            var getAfterDelete = await InvokeCliAsync(["config", "secrets", "get", "Demo:ApiKey", "--json"]);
            getAfterDelete.ExitCode.Should().Be(3);
            using var notFoundDoc = ParseJson(getAfterDelete.StdOut);
            notFoundDoc.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
            notFoundDoc.RootElement.GetProperty("code").GetString().Should().Be("NOT_FOUND");
        });
    }

    [Fact]
    public async Task ConfigConfigJson_SetGetRemove_ShouldRoundtrip()
    {
        await WithTempHomeAsync(async _ =>
        {
            var setResult = await InvokeCliAsync(["config", "config-json", "set", "App:Mode", "demo", "--json"]);
            setResult.ExitCode.Should().Be(0);

            var getResult = await InvokeCliAsync(["config", "config-json", "get", "App:Mode", "--json"]);
            getResult.ExitCode.Should().Be(0);
            using (var getDoc = ParseJson(getResult.StdOut))
            {
                getDoc.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
                getDoc.RootElement.GetProperty("data").GetProperty("value").GetString().Should().Be("demo");
            }

            var removeResult = await InvokeCliAsync(["config", "config-json", "remove", "App:Mode", "--yes", "--json"]);
            removeResult.ExitCode.Should().Be(0);

            var getAfterDelete = await InvokeCliAsync(["config", "config-json", "get", "App:Mode", "--json"]);
            getAfterDelete.ExitCode.Should().Be(3);
        });
    }

    [Fact]
    public async Task ConfigWorkflows_PutListGetDelete_ShouldRoundtrip()
    {
        await WithTempHomeAsync(async tempHome =>
        {
            var yamlPath = Path.Combine(tempHome, "sample.yaml");
            await File.WriteAllTextAsync(yamlPath, """
                name: sample
                description: sample workflow
                roles: []
                steps: []
                """);

            var putResult = await InvokeCliAsync(["config", "workflows", "put", "sample.yaml", "--file", yamlPath, "--source", "home", "--json"]);
            putResult.ExitCode.Should().Be(0);

            var listResult = await InvokeCliAsync(["config", "workflows", "list", "--source", "home", "--json"]);
            listResult.ExitCode.Should().Be(0);
            using (var listDoc = ParseJson(listResult.StdOut))
            {
                var items = listDoc.RootElement.GetProperty("data").GetProperty("items");
                items.EnumerateArray().Select(x => x.GetProperty("filename").GetString()).Should().Contain("sample.yaml");
            }

            var getResult = await InvokeCliAsync(["config", "workflows", "get", "sample.yaml", "--source", "home", "--json"]);
            getResult.ExitCode.Should().Be(0);
            using (var getDoc = ParseJson(getResult.StdOut))
            {
                getDoc.RootElement.GetProperty("data").GetProperty("content").GetString().Should().Contain("sample workflow");
            }

            var deleteResult = await InvokeCliAsync(["config", "workflows", "delete", "sample.yaml", "--source", "home", "--yes", "--json"]);
            deleteResult.ExitCode.Should().Be(0);
        });
    }

    [Fact]
    public async Task ConfigConnectorsAndMcp_PutGetDelete_ShouldRoundtrip()
    {
        await WithTempHomeAsync(async _ =>
        {
            var connectorJson = """{"type":"http","timeoutMs":3000,"http":{"baseUrl":"https://example.com","allowedMethods":["GET"],"allowedPaths":["/"]}}""";
            var connectorPut = await InvokeCliAsync(["config", "connectors", "put", "demo-http", "--entry-json", connectorJson, "--json"]);
            connectorPut.ExitCode.Should().Be(0);

            var connectorGet = await InvokeCliAsync(["config", "connectors", "get", "demo-http", "--json"]);
            connectorGet.ExitCode.Should().Be(0);

            var connectorDelete = await InvokeCliAsync(["config", "connectors", "delete", "demo-http", "--yes", "--json"]);
            connectorDelete.ExitCode.Should().Be(0);

            var mcpJson = """{"command":"npx","args":["-y","demo-server"],"env":{},"timeoutMs":1000}""";
            var mcpPut = await InvokeCliAsync(["config", "mcp", "put", "demo-mcp", "--entry-json", mcpJson, "--json"]);
            mcpPut.ExitCode.Should().Be(0);

            var mcpGet = await InvokeCliAsync(["config", "mcp", "get", "demo-mcp", "--json"]);
            mcpGet.ExitCode.Should().Be(0);

            var mcpDelete = await InvokeCliAsync(["config", "mcp", "delete", "demo-mcp", "--yes", "--json"]);
            mcpDelete.ExitCode.Should().Be(0);
        });
    }

    private static async Task WithTempHomeAsync(Func<string, Task> action)
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"aevatar-cli-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);
        using var homeScope = new EnvironmentVariableScope(AevatarPaths.HomeEnv, tempHome);
        using var secretsScope = new EnvironmentVariableScope(AevatarPaths.SecretsPathEnv, null);
        try
        {
            await action(tempHome);
        }
        finally
        {
            if (Directory.Exists(tempHome))
                Directory.Delete(tempHome, recursive: true);
        }
    }

    private static async Task<CliInvokeResult> InvokeCliAsync(string[] args)
    {
        var root = RootCommandFactory.Create();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exitCode = await root.InvokeAsync(args);
            return new CliInvokeResult(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    private static JsonDocument ParseJson(string text)
    {
        var trimmed = text.Trim();
        trimmed.Should().NotBeNullOrWhiteSpace();
        return JsonDocument.Parse(trimmed);
    }

    private sealed record CliInvokeResult(int ExitCode, string StdOut, string StdErr);

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _previous);
        }
    }
}
