using System.CommandLine;
using System.CommandLine.Invocation;
using Aevatar.Tools.Cli.Hosting;

namespace Aevatar.Tools.Cli.Commands;

internal static class ConfigCommand
{
    public static Command Create()
    {
        var command = new Command("config", "Manage Aevatar config via pure CLI.");
        var jsonOption = new Option<bool>("--json", "Output machine-readable JSON.");
        var quietOption = new Option<bool>("--quiet", "Suppress human-readable output.");
        var yesOption = new Option<bool>("--yes", "Assume yes for destructive operations.");

        command.AddGlobalOption(jsonOption);
        command.AddGlobalOption(quietOption);
        command.AddGlobalOption(yesOption);

        command.AddCommand(CreateUiCommand(jsonOption, quietOption));
        command.AddCommand(CreateDoctorCommand(jsonOption, quietOption));
        command.AddCommand(CreatePathsCommand(jsonOption, quietOption));
        command.AddCommand(CreateSecretsCommand(jsonOption, quietOption, yesOption));
        command.AddCommand(CreateConfigJsonCommand(jsonOption, quietOption, yesOption));
        command.AddCommand(CreateLlmCommand(jsonOption, quietOption, yesOption));
        command.AddCommand(CreateWorkflowsCommand(jsonOption, quietOption, yesOption));
        command.AddCommand(CreateConnectorsCommand(jsonOption, quietOption, yesOption));
        command.AddCommand(CreateMcpCommand(jsonOption, quietOption, yesOption));

        return command;
    }

    private static Command CreateUiCommand(Option<bool> jsonOption, Option<bool> quietOption)
    {
        var command = new Command("ui", "Open local Aevatar config UI.");
        var portOption = new Option<int>("--port", () => 6677, "Port for config UI.");
        var noBrowserOption = new Option<bool>("--no-browser", "Do not auto-open browser.");
        var ensureCommand = new Command("ensure", "Ensure local Aevatar config UI is running.");
        var ensurePortOption = new Option<int>("--port", () => 6677, "Port for config UI.");
        var ensureNoBrowserOption = new Option<bool>("--no-browser", () => true, "Start without auto-opening browser.");

        command.AddOption(portOption);
        command.AddOption(noBrowserOption);
        command.SetHandler((int port, bool noBrowser) =>
            ConfigCommandHandler.RunUiAsync(port, noBrowser, CancellationToken.None), portOption, noBrowserOption);

        ensureCommand.AddOption(ensurePortOption);
        ensureCommand.AddOption(ensureNoBrowserOption);
        SetCommandHandler(ensureCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.UiEnsureAsync(
                context.ParseResult.GetValueForOption(ensurePortOption),
                context.ParseResult.GetValueForOption(ensureNoBrowserOption),
                ct)));

        command.AddCommand(ensureCommand);
        return command;
    }

    private static Command CreateDoctorCommand(Option<bool> jsonOption, Option<bool> quietOption)
    {
        var command = new Command("doctor", "Check config paths, env overrides, and file permissions.");
        SetCommandHandler(command, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.DoctorAsync(ct)));
        return command;
    }

    private static Command CreatePathsCommand(Option<bool> jsonOption, Option<bool> quietOption)
    {
        var command = new Command("paths", "Show resolved config paths.");
        var showCommand = new Command("show", "Show all resolved paths.");
        SetCommandHandler(showCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.PathsShowAsync(ct)));
        command.AddCommand(showCommand);
        return command;
    }

    private static Command CreateSecretsCommand(Option<bool> jsonOption, Option<bool> quietOption, Option<bool> yesOption)
    {
        var command = new Command("secrets", "Manage ~/.aevatar/secrets.json.");

        var listCommand = new Command("list", "List flattened secret keys.");
        SetCommandHandler(listCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.SecretsListAsync(ct)));

        var getCommand = new Command("get", "Get secret value by key.");
        var getKeyArg = new Argument<string>("key", "Secret key path.");
        getCommand.AddArgument(getKeyArg);
        SetCommandHandler(getCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.SecretsGetAsync(context.ParseResult.GetValueForArgument(getKeyArg), ct)));

        var setCommand = new Command("set", "Set secret value by key.");
        var setKeyArg = new Argument<string>("key", "Secret key path.");
        var setValueArg = new Argument<string?>("value", () => null, "Secret value (omit when using --stdin).");
        var setStdinOption = new Option<bool>("--stdin", "Read value from stdin.");
        setCommand.AddArgument(setKeyArg);
        setCommand.AddArgument(setValueArg);
        setCommand.AddOption(setStdinOption);
        SetCommandHandler(setCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.SecretsSetAsync(
                context.ParseResult.GetValueForArgument(setKeyArg),
                context.ParseResult.GetValueForArgument(setValueArg),
                context.ParseResult.GetValueForOption(setStdinOption),
                ct)));

        var removeCommand = new Command("remove", "Remove secret key.");
        var removeKeyArg = new Argument<string>("key", "Secret key path.");
        removeCommand.AddArgument(removeKeyArg);
        SetCommandHandler(removeCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.SecretsRemoveAsync(
                context.ParseResult.GetValueForArgument(removeKeyArg),
                context.ParseResult.GetValueForOption(yesOption),
                ct)));

        var importCommand = new Command("import", "Import secrets from nested JSON.");
        var importFileOption = new Option<string?>("--file", "Input JSON file path.");
        var importStdinOption = new Option<bool>("--stdin", "Read JSON from stdin.");
        importCommand.AddOption(importFileOption);
        importCommand.AddOption(importStdinOption);
        SetCommandHandler(importCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.SecretsImportAsync(
                context.ParseResult.GetValueForOption(importFileOption),
                context.ParseResult.GetValueForOption(importStdinOption),
                context.ParseResult.GetValueForOption(yesOption),
                ct)));

        var exportCommand = new Command("export", "Export secrets as nested JSON.");
        var exportFileOption = new Option<string?>("--file", "Output file path.");
        exportCommand.AddOption(exportFileOption);
        SetCommandHandler(exportCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.SecretsExportAsync(
                context.ParseResult.GetValueForOption(exportFileOption), ct)));

        command.AddCommand(listCommand);
        command.AddCommand(getCommand);
        command.AddCommand(setCommand);
        command.AddCommand(removeCommand);
        command.AddCommand(importCommand);
        command.AddCommand(exportCommand);
        return command;
    }

    private static Command CreateConfigJsonCommand(Option<bool> jsonOption, Option<bool> quietOption, Option<bool> yesOption)
    {
        var command = new Command("config-json", "Manage ~/.aevatar/config.json.");

        var listCommand = new Command("list", "List flattened config keys.");
        SetCommandHandler(listCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.ConfigJsonListAsync(ct)));

        var getCommand = new Command("get", "Get config value by key.");
        var getKeyArg = new Argument<string>("key", "Config key path.");
        getCommand.AddArgument(getKeyArg);
        SetCommandHandler(getCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.ConfigJsonGetAsync(context.ParseResult.GetValueForArgument(getKeyArg), ct)));

        var setCommand = new Command("set", "Set config value by key.");
        var setKeyArg = new Argument<string>("key", "Config key path.");
        var setValueArg = new Argument<string?>("value", () => null, "Config value (omit when using --stdin).");
        var setStdinOption = new Option<bool>("--stdin", "Read value from stdin.");
        setCommand.AddArgument(setKeyArg);
        setCommand.AddArgument(setValueArg);
        setCommand.AddOption(setStdinOption);
        SetCommandHandler(setCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.ConfigJsonSetAsync(
                context.ParseResult.GetValueForArgument(setKeyArg),
                context.ParseResult.GetValueForArgument(setValueArg),
                context.ParseResult.GetValueForOption(setStdinOption),
                ct)));

        var removeCommand = new Command("remove", "Remove config key.");
        var removeKeyArg = new Argument<string>("key", "Config key path.");
        removeCommand.AddArgument(removeKeyArg);
        SetCommandHandler(removeCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.ConfigJsonRemoveAsync(
                context.ParseResult.GetValueForArgument(removeKeyArg),
                context.ParseResult.GetValueForOption(yesOption),
                ct)));

        var importCommand = new Command("import", "Import config.json from nested JSON.");
        var importFileOption = new Option<string?>("--file", "Input JSON file path.");
        var importStdinOption = new Option<bool>("--stdin", "Read JSON from stdin.");
        importCommand.AddOption(importFileOption);
        importCommand.AddOption(importStdinOption);
        SetCommandHandler(importCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.ConfigJsonImportAsync(
                context.ParseResult.GetValueForOption(importFileOption),
                context.ParseResult.GetValueForOption(importStdinOption),
                context.ParseResult.GetValueForOption(yesOption),
                ct)));

        var exportCommand = new Command("export", "Export config.json as nested JSON.");
        var exportFileOption = new Option<string?>("--file", "Output file path.");
        exportCommand.AddOption(exportFileOption);
        SetCommandHandler(exportCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.ConfigJsonExportAsync(
                context.ParseResult.GetValueForOption(exportFileOption), ct)));

        command.AddCommand(listCommand);
        command.AddCommand(getCommand);
        command.AddCommand(setCommand);
        command.AddCommand(removeCommand);
        command.AddCommand(importCommand);
        command.AddCommand(exportCommand);
        return command;
    }

    private static Command CreateLlmCommand(Option<bool> jsonOption, Option<bool> quietOption, Option<bool> yesOption)
    {
        var command = new Command("llm", "Manage LLM providers, instances, defaults, keys, and probes.");

        var providerTypesCommand = new Command("provider-types", "Manage provider type catalog.");
        var providerTypesListCommand = new Command("list", "List provider types.");
        SetCommandHandler(providerTypesListCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.LlmProviderTypesListAsync(ct)));
        providerTypesCommand.AddCommand(providerTypesListCommand);

        var instancesCommand = new Command("instances", "Manage configured LLM instances.");
        var instancesListCommand = new Command("list", "List configured LLM instances.");
        SetCommandHandler(instancesListCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.LlmInstancesListAsync(ct)));

        var instancesGetCommand = new Command("get", "Get one LLM instance by name.");
        var instancesGetNameArg = new Argument<string>("provider-name", "LLM provider instance name.");
        instancesGetCommand.AddArgument(instancesGetNameArg);
        SetCommandHandler(instancesGetCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.LlmInstancesGetAsync(
                context.ParseResult.GetValueForArgument(instancesGetNameArg), ct)));

        var instancesUpsertCommand = new Command("upsert", "Create or update an LLM instance.");
        var instancesUpsertNameArg = new Argument<string>("provider-name", "LLM provider instance name.");
        var providerTypeOption = new Option<string>("--provider-type", "Provider type id (openai/deepseek/anthropic/...).") { IsRequired = true };
        var modelOption = new Option<string>("--model", "Model name.") { IsRequired = true };
        var endpointOption = new Option<string?>("--endpoint", "Optional endpoint override.");
        var apiKeyOption = new Option<string?>("--api-key", "Optional API key.");
        var apiKeyStdinOption = new Option<bool>("--api-key-stdin", "Read API key from stdin.");
        var copyApiKeyFromOption = new Option<string?>("--copy-api-key-from", "Copy API key from another instance.");
        var forceCopyApiKeyFromOption = new Option<bool>("--force-copy-api-key-from", "Force overwrite existing API key when copying.");
        instancesUpsertCommand.AddArgument(instancesUpsertNameArg);
        instancesUpsertCommand.AddOption(providerTypeOption);
        instancesUpsertCommand.AddOption(modelOption);
        instancesUpsertCommand.AddOption(endpointOption);
        instancesUpsertCommand.AddOption(apiKeyOption);
        instancesUpsertCommand.AddOption(apiKeyStdinOption);
        instancesUpsertCommand.AddOption(copyApiKeyFromOption);
        instancesUpsertCommand.AddOption(forceCopyApiKeyFromOption);
        SetCommandHandler(instancesUpsertCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.LlmInstancesUpsertAsync(
                context.ParseResult.GetValueForArgument(instancesUpsertNameArg),
                GetRequiredOptionValue(context, providerTypeOption),
                GetRequiredOptionValue(context, modelOption),
                context.ParseResult.GetValueForOption(endpointOption),
                context.ParseResult.GetValueForOption(apiKeyOption),
                context.ParseResult.GetValueForOption(apiKeyStdinOption),
                context.ParseResult.GetValueForOption(copyApiKeyFromOption),
                context.ParseResult.GetValueForOption(forceCopyApiKeyFromOption),
                ct)));

        var instancesDeleteCommand = new Command("delete", "Delete an LLM instance.");
        var instancesDeleteNameArg = new Argument<string>("provider-name", "LLM provider instance name.");
        instancesDeleteCommand.AddArgument(instancesDeleteNameArg);
        SetCommandHandler(instancesDeleteCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.LlmInstancesDeleteAsync(
                context.ParseResult.GetValueForArgument(instancesDeleteNameArg),
                context.ParseResult.GetValueForOption(yesOption),
                ct)));

        instancesCommand.AddCommand(instancesListCommand);
        instancesCommand.AddCommand(instancesGetCommand);
        instancesCommand.AddCommand(instancesUpsertCommand);
        instancesCommand.AddCommand(instancesDeleteCommand);

        var defaultCommand = new Command("default", "Manage default LLM provider.");
        var defaultGetCommand = new Command("get", "Get default LLM provider.");
        SetCommandHandler(defaultGetCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.LlmDefaultGetAsync(ct)));

        var defaultSetCommand = new Command("set", "Set default LLM provider.");
        var defaultSetNameArg = new Argument<string>("provider-name", "LLM provider instance name.");
        defaultSetCommand.AddArgument(defaultSetNameArg);
        SetCommandHandler(defaultSetCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.LlmDefaultSetAsync(
                context.ParseResult.GetValueForArgument(defaultSetNameArg), ct)));
        defaultCommand.AddCommand(defaultGetCommand);
        defaultCommand.AddCommand(defaultSetCommand);

        var apiKeyCommand = new Command("api-key", "Manage LLM API keys.");
        var apiKeyGetCommand = new Command("get", "Get API key status for provider.");
        var apiKeyGetNameArg = new Argument<string>("provider-name", "LLM provider instance name.");
        var revealOption = new Option<bool>("--reveal", "Reveal full API key.");
        apiKeyGetCommand.AddArgument(apiKeyGetNameArg);
        apiKeyGetCommand.AddOption(revealOption);
        SetCommandHandler(apiKeyGetCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.LlmApiKeyGetAsync(
                context.ParseResult.GetValueForArgument(apiKeyGetNameArg),
                context.ParseResult.GetValueForOption(revealOption),
                ct)));

        var apiKeySetCommand = new Command("set", "Set API key for provider.");
        var apiKeySetNameArg = new Argument<string>("provider-name", "LLM provider instance name.");
        var apiKeySetValueArg = new Argument<string?>("value", () => null, "API key value (omit when using --stdin).");
        var apiKeySetStdinOption = new Option<bool>("--stdin", "Read API key from stdin.");
        apiKeySetCommand.AddArgument(apiKeySetNameArg);
        apiKeySetCommand.AddArgument(apiKeySetValueArg);
        apiKeySetCommand.AddOption(apiKeySetStdinOption);
        SetCommandHandler(apiKeySetCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.LlmApiKeySetAsync(
                context.ParseResult.GetValueForArgument(apiKeySetNameArg),
                context.ParseResult.GetValueForArgument(apiKeySetValueArg),
                context.ParseResult.GetValueForOption(apiKeySetStdinOption),
                ct)));

        var apiKeyRemoveCommand = new Command("remove", "Remove API key for provider.");
        var apiKeyRemoveNameArg = new Argument<string>("provider-name", "LLM provider instance name.");
        apiKeyRemoveCommand.AddArgument(apiKeyRemoveNameArg);
        SetCommandHandler(apiKeyRemoveCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.LlmApiKeyRemoveAsync(
                context.ParseResult.GetValueForArgument(apiKeyRemoveNameArg),
                context.ParseResult.GetValueForOption(yesOption),
                ct)));

        apiKeyCommand.AddCommand(apiKeyGetCommand);
        apiKeyCommand.AddCommand(apiKeySetCommand);
        apiKeyCommand.AddCommand(apiKeyRemoveCommand);

        var probeCommand = new Command("probe", "Probe LLM provider connectivity and models.");
        var probeTestCommand = new Command("test", "Test provider connectivity.");
        var probeTestNameArg = new Argument<string>("provider-name", "LLM provider instance name.");
        probeTestCommand.AddArgument(probeTestNameArg);
        SetCommandHandler(probeTestCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.LlmProbeTestAsync(
                context.ParseResult.GetValueForArgument(probeTestNameArg), ct)));

        var probeModelsCommand = new Command("models", "Fetch models for provider.");
        var probeModelsNameArg = new Argument<string>("provider-name", "LLM provider instance name.");
        var limitOption = new Option<int>("--limit", () => 200, "Maximum number of models.");
        probeModelsCommand.AddArgument(probeModelsNameArg);
        probeModelsCommand.AddOption(limitOption);
        SetCommandHandler(probeModelsCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.LlmProbeModelsAsync(
                context.ParseResult.GetValueForArgument(probeModelsNameArg),
                context.ParseResult.GetValueForOption(limitOption),
                ct)));
        probeCommand.AddCommand(probeTestCommand);
        probeCommand.AddCommand(probeModelsCommand);

        command.AddCommand(providerTypesCommand);
        command.AddCommand(instancesCommand);
        command.AddCommand(defaultCommand);
        command.AddCommand(apiKeyCommand);
        command.AddCommand(probeCommand);
        return command;
    }

    private static Command CreateWorkflowsCommand(Option<bool> jsonOption, Option<bool> quietOption, Option<bool> yesOption)
    {
        var command = new Command("workflows", "Manage workflow YAML files.");
        var sourceOption = new Option<string>("--source", () => "all", "Workflow source: home | repo | all.");

        var listCommand = new Command("list", "List workflow files.");
        listCommand.AddOption(sourceOption);
        SetCommandHandler(listCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.WorkflowsListAsync(
                GetOptionValueOrDefault(context, sourceOption, "all"), ct)));

        var getCommand = new Command("get", "Get workflow file content.");
        var getFilenameArg = new Argument<string>("filename", "Workflow filename (.yaml/.yml).");
        getCommand.AddArgument(getFilenameArg);
        getCommand.AddOption(sourceOption);
        SetCommandHandler(getCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.WorkflowsGetAsync(
                context.ParseResult.GetValueForArgument(getFilenameArg),
                GetOptionValueOrDefault(context, sourceOption, "all"),
                ct)));

        var putCommand = new Command("put", "Create or update workflow file.");
        var putFilenameArg = new Argument<string>("filename", "Workflow filename (.yaml/.yml).");
        var putFileOption = new Option<string?>("--file", "Input YAML file path.");
        var putStdinOption = new Option<bool>("--stdin", "Read YAML from stdin.");
        var putSourceOption = new Option<string>("--source", () => "home", "Write source: home | repo.");
        putCommand.AddArgument(putFilenameArg);
        putCommand.AddOption(putFileOption);
        putCommand.AddOption(putStdinOption);
        putCommand.AddOption(putSourceOption);
        SetCommandHandler(putCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.WorkflowsPutAsync(
                context.ParseResult.GetValueForArgument(putFilenameArg),
                GetOptionValueOrDefault(context, putSourceOption, "home"),
                context.ParseResult.GetValueForOption(putFileOption),
                context.ParseResult.GetValueForOption(putStdinOption),
                ct)));

        var deleteCommand = new Command("delete", "Delete workflow file.");
        var deleteFilenameArg = new Argument<string>("filename", "Workflow filename (.yaml/.yml).");
        var deleteSourceOption = new Option<string>("--source", () => "home", "Delete source: home | repo.");
        deleteCommand.AddArgument(deleteFilenameArg);
        deleteCommand.AddOption(deleteSourceOption);
        SetCommandHandler(deleteCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.WorkflowsDeleteAsync(
                context.ParseResult.GetValueForArgument(deleteFilenameArg),
                GetOptionValueOrDefault(context, deleteSourceOption, "home"),
                context.ParseResult.GetValueForOption(yesOption),
                ct)));

        command.AddCommand(listCommand);
        command.AddCommand(getCommand);
        command.AddCommand(putCommand);
        command.AddCommand(deleteCommand);
        return command;
    }

    private static Command CreateConnectorsCommand(Option<bool> jsonOption, Option<bool> quietOption, Option<bool> yesOption)
    {
        var command = new Command("connectors", "Manage connectors.json.");

        var listCommand = new Command("list", "List connectors.");
        SetCommandHandler(listCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.ConnectorsListAsync(ct)));

        var getCommand = new Command("get", "Get connector by name.");
        var getNameArg = new Argument<string>("name", "Connector name.");
        getCommand.AddArgument(getNameArg);
        SetCommandHandler(getCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.ConnectorsGetAsync(
                context.ParseResult.GetValueForArgument(getNameArg), ct)));

        var putCommand = new Command("put", "Create or update connector entry.");
        var putNameArg = new Argument<string>("name", "Connector name.");
        var entryJsonOption = new Option<string?>("--entry-json", "Connector entry JSON object.");
        var fileOption = new Option<string?>("--file", "Input JSON file path.");
        var stdinOption = new Option<bool>("--stdin", "Read JSON from stdin.");
        putCommand.AddArgument(putNameArg);
        putCommand.AddOption(entryJsonOption);
        putCommand.AddOption(fileOption);
        putCommand.AddOption(stdinOption);
        SetCommandHandler(putCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.ConnectorsPutAsync(
                context.ParseResult.GetValueForArgument(putNameArg),
                context.ParseResult.GetValueForOption(entryJsonOption),
                context.ParseResult.GetValueForOption(fileOption),
                context.ParseResult.GetValueForOption(stdinOption),
                ct)));

        var deleteCommand = new Command("delete", "Delete connector entry.");
        var deleteNameArg = new Argument<string>("name", "Connector name.");
        deleteCommand.AddArgument(deleteNameArg);
        SetCommandHandler(deleteCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.ConnectorsDeleteAsync(
                context.ParseResult.GetValueForArgument(deleteNameArg),
                context.ParseResult.GetValueForOption(yesOption),
                ct)));

        var validateCommand = new Command("validate", "Validate connectors JSON payload or current file.");
        var validateFileOption = new Option<string?>("--file", "Input JSON file path.");
        var validateStdinOption = new Option<bool>("--stdin", "Read JSON from stdin.");
        validateCommand.AddOption(validateFileOption);
        validateCommand.AddOption(validateStdinOption);
        SetCommandHandler(validateCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.ConnectorsValidateAsync(
                context.ParseResult.GetValueForOption(validateFileOption),
                context.ParseResult.GetValueForOption(validateStdinOption),
                ct)));

        var importCommand = new Command("import", "Import connectors JSON.");
        var importFileOption = new Option<string?>("--file", "Input JSON file path.");
        var importStdinOption = new Option<bool>("--stdin", "Read JSON from stdin.");
        importCommand.AddOption(importFileOption);
        importCommand.AddOption(importStdinOption);
        SetCommandHandler(importCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.ConnectorsImportAsync(
                context.ParseResult.GetValueForOption(importFileOption),
                context.ParseResult.GetValueForOption(importStdinOption),
                context.ParseResult.GetValueForOption(yesOption),
                ct)));

        var exportCommand = new Command("export", "Export connectors JSON.");
        var exportFileOption = new Option<string?>("--file", "Output file path.");
        exportCommand.AddOption(exportFileOption);
        SetCommandHandler(exportCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.ConnectorsExportAsync(
                context.ParseResult.GetValueForOption(exportFileOption), ct)));

        command.AddCommand(listCommand);
        command.AddCommand(getCommand);
        command.AddCommand(putCommand);
        command.AddCommand(deleteCommand);
        command.AddCommand(validateCommand);
        command.AddCommand(importCommand);
        command.AddCommand(exportCommand);
        return command;
    }

    private static Command CreateMcpCommand(Option<bool> jsonOption, Option<bool> quietOption, Option<bool> yesOption)
    {
        var command = new Command("mcp", "Manage mcp.json.");

        var listCommand = new Command("list", "List MCP servers.");
        SetCommandHandler(listCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.McpListAsync(ct)));

        var getCommand = new Command("get", "Get MCP server by name.");
        var getNameArg = new Argument<string>("name", "MCP server name.");
        getCommand.AddArgument(getNameArg);
        SetCommandHandler(getCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.McpGetAsync(
                context.ParseResult.GetValueForArgument(getNameArg), ct)));

        var putCommand = new Command("put", "Create or update MCP server entry.");
        var putNameArg = new Argument<string>("name", "MCP server name.");
        var entryJsonOption = new Option<string?>("--entry-json", "MCP server JSON object.");
        var fileOption = new Option<string?>("--file", "Input JSON file path.");
        var stdinOption = new Option<bool>("--stdin", "Read JSON from stdin.");
        putCommand.AddArgument(putNameArg);
        putCommand.AddOption(entryJsonOption);
        putCommand.AddOption(fileOption);
        putCommand.AddOption(stdinOption);
        SetCommandHandler(putCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.McpPutAsync(
                context.ParseResult.GetValueForArgument(putNameArg),
                context.ParseResult.GetValueForOption(entryJsonOption),
                context.ParseResult.GetValueForOption(fileOption),
                context.ParseResult.GetValueForOption(stdinOption),
                ct)));

        var deleteCommand = new Command("delete", "Delete MCP server entry.");
        var deleteNameArg = new Argument<string>("name", "MCP server name.");
        deleteCommand.AddArgument(deleteNameArg);
        SetCommandHandler(deleteCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.McpDeleteAsync(
                context.ParseResult.GetValueForArgument(deleteNameArg),
                context.ParseResult.GetValueForOption(yesOption),
                ct)));

        var validateCommand = new Command("validate", "Validate MCP JSON payload or current file.");
        var validateFileOption = new Option<string?>("--file", "Input JSON file path.");
        var validateStdinOption = new Option<bool>("--stdin", "Read JSON from stdin.");
        validateCommand.AddOption(validateFileOption);
        validateCommand.AddOption(validateStdinOption);
        SetCommandHandler(validateCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.McpValidateAsync(
                context.ParseResult.GetValueForOption(validateFileOption),
                context.ParseResult.GetValueForOption(validateStdinOption),
                ct)));

        var importCommand = new Command("import", "Import MCP JSON.");
        var importFileOption = new Option<string?>("--file", "Input JSON file path.");
        var importStdinOption = new Option<bool>("--stdin", "Read JSON from stdin.");
        importCommand.AddOption(importFileOption);
        importCommand.AddOption(importStdinOption);
        SetCommandHandler(importCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.McpImportAsync(
                context.ParseResult.GetValueForOption(importFileOption),
                context.ParseResult.GetValueForOption(importStdinOption),
                context.ParseResult.GetValueForOption(yesOption),
                ct)));

        var exportCommand = new Command("export", "Export MCP JSON.");
        var exportFileOption = new Option<string?>("--file", "Output file path.");
        exportCommand.AddOption(exportFileOption);
        SetCommandHandler(exportCommand, context =>
            Run(context, jsonOption, quietOption, ct => ConfigCommandHandlers.McpExportAsync(
                context.ParseResult.GetValueForOption(exportFileOption), ct)));

        command.AddCommand(listCommand);
        command.AddCommand(getCommand);
        command.AddCommand(putCommand);
        command.AddCommand(deleteCommand);
        command.AddCommand(validateCommand);
        command.AddCommand(importCommand);
        command.AddCommand(exportCommand);
        return command;
    }

    private static void SetCommandHandler(Command command, Func<InvocationContext, Task<int>> runner)
    {
        command.SetHandler(async context => { context.ExitCode = await runner(context); });
    }

    private static Task<int> Run(
        InvocationContext context,
        Option<bool> jsonOption,
        Option<bool> quietOption,
        Func<CancellationToken, Task<ConfigCliResult>> action)
    {
        var asJson = context.ParseResult.GetValueForOption(jsonOption);
        var quiet = context.ParseResult.GetValueForOption(quietOption);
        return ConfigCliExecution.ExecuteAsync(asJson, quiet, action, CancellationToken.None);
    }

    private static string GetRequiredOptionValue(InvocationContext context, Option<string> option)
    {
        var value = context.ParseResult.GetValueForOption(option);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        throw new InvalidOperationException($"Missing required option: {option.Name}");
    }

    private static string GetOptionValueOrDefault(
        InvocationContext context,
        Option<string> option,
        string fallback)
    {
        var value = context.ParseResult.GetValueForOption(option);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
