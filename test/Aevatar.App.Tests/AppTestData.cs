namespace Aevatar.App.Tests;

internal static class AppTestData
{
    public const string ValidScriptSource =
        """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Scripting.Abstractions;
        using Aevatar.Scripting.Abstractions.Behaviors;
        using Aevatar.Tools.Cli.Hosting;

        public sealed class DraftBehavior : ScriptBehavior<AppScriptReadModel, AppScriptReadModel>
        {
            protected override void Configure(IScriptBehaviorBuilder<AppScriptReadModel, AppScriptReadModel> builder)
            {
                builder
                    .OnCommand<AppScriptCommand>(HandleAsync)
                    .OnEvent<AppScriptUpdated>(
                        apply: static (_, evt, _) => evt.Current == null ? new AppScriptReadModel() : evt.Current.Clone())
                    .ProjectState(static (state, _) => state == null ? new AppScriptReadModel() : state.Clone());
            }

            private static Task HandleAsync(
                AppScriptCommand input,
                ScriptCommandContext<AppScriptReadModel> context,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();

                var commandId = context.CommandId ?? input?.CommandId ?? string.Empty;
                var text = input?.Input ?? string.Empty;
                var current = AppScriptProtocol.CreateState(
                    text,
                    text.Trim().ToUpperInvariant(),
                    "ok",
                    commandId,
                    new[]
                    {
                        "trimmed",
                        "uppercased",
                    });

                context.Emit(new AppScriptUpdated
                {
                    CommandId = commandId,
                    Current = current,
                });
                return Task.CompletedTask;
            }
        }
        """;

    public const string InvalidScriptSource =
        """
        using Aevatar.Scripting.Abstractions.Behaviors;
        using Aevatar.Tools.Cli.Hosting;

        public sealed class BrokenBehavior : ScriptBehavior<AppScriptReadModel, AppScriptReadModel>
        {
            protected override void Configure(IScriptBehaviorBuilder<AppScriptReadModel, AppScriptReadModel> builder)
            {
                builder
                    .OnCommand<AppScriptCommand>(HandleAsync)
                    .ProjectState(static (state, _) => state == null ? new AppScriptReadModel() : state.Clone())
            }
        }
        """;

    public static object CreateConnector(string name) => new
    {
        name,
        type = "http",
        enabled = true,
        timeoutMs = 5_000,
        retry = 1,
        http = new
        {
            baseUrl = "https://example.com",
            allowedMethods = new[] { "GET" },
            allowedPaths = new[] { "/health" },
            allowedInputKeys = Array.Empty<string>(),
            defaultHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["x-test-suite"] = "aevatar-app",
            },
        },
        cli = new
        {
            command = string.Empty,
            fixedArguments = Array.Empty<string>(),
            allowedOperations = Array.Empty<string>(),
            allowedInputKeys = Array.Empty<string>(),
            workingDirectory = string.Empty,
            environment = new Dictionary<string, string>(StringComparer.Ordinal),
        },
        mcp = new
        {
            serverName = string.Empty,
            command = string.Empty,
            arguments = Array.Empty<string>(),
            environment = new Dictionary<string, string>(StringComparer.Ordinal),
            defaultTool = string.Empty,
            allowedTools = Array.Empty<string>(),
            allowedInputKeys = Array.Empty<string>(),
        },
    };

    public static string CreateConnectorImportJson(string name) =>
        $$"""
        {
          "connectors": [
            {
              "name": "{{name}}",
              "type": "http",
              "enabled": true,
              "timeoutMs": 5000,
              "retry": 1,
              "http": {
                "baseUrl": "https://example.com",
                "allowedMethods": ["GET"],
                "allowedPaths": ["/health"],
                "allowedInputKeys": [],
                "defaultHeaders": {
                  "x-test-suite": "aevatar-app"
                }
              }
            }
          ]
        }
        """;

    public static object CreateRole(string id) => new
    {
        id,
        name = "Support Agent",
        systemPrompt = "Help the user with support requests.",
        provider = "meai",
        model = "gpt-4.1-mini",
        connectors = new[] { "sample-http" },
    };

    public static string CreateRoleImportJson(string id) =>
        $$"""
        {
          "roles": [
            {
              "id": "{{id}}",
              "name": "Imported Support Agent",
              "systemPrompt": "Handle imported support requests.",
              "provider": "meai",
              "model": "gpt-4.1-mini",
              "connectors": ["sample-http"]
            }
          ]
        }
        """;
}
