using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using System.Text.Json;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class ConnectorService
{
    private static readonly HashSet<string> SupportedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http",
        "cli",
        "mcp",
    };

    private readonly IConnectorCatalogStore _store;
    private readonly IConnectorCatalogImportParser _importParser;

    public ConnectorService(
        IConnectorCatalogStore store,
        IConnectorCatalogImportParser importParser)
    {
        _store = store;
        _importParser = importParser;
    }

    public async Task<ConnectorCatalogResponse> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        var catalog = await _store.GetConnectorCatalogAsync(cancellationToken);
        return ToResponse(catalog);
    }

    public async Task<ConnectorDraftResponse> GetDraftAsync(CancellationToken cancellationToken = default)
    {
        var draft = await _store.GetConnectorDraftAsync(cancellationToken);
        return ToDraftResponse(draft);
    }

    public async Task<ConnectorCatalogResponse> SaveCatalogAsync(
        SaveConnectorCatalogRequest request,
        CancellationToken cancellationToken = default)
    {
        var connectors = request.Connectors ?? [];
        EnsureUniqueNames(connectors);

        var saved = await _store.SaveConnectorCatalogAsync(
            new StoredConnectorCatalog(
                HomeDirectory: string.Empty,
                FilePath: string.Empty,
                FileExists: false,
                Connectors: connectors
                    .Where(connector => !string.IsNullOrWhiteSpace(connector.Name))
                    .Select(ToStoredConnector)
                    .ToList()),
            cancellationToken);

        return ToResponse(saved);
    }

    public async Task<ImportConnectorCatalogResponse> ImportLocalCatalogAsync(CancellationToken cancellationToken = default)
    {
        var imported = await _store.ImportLocalCatalogAsync(cancellationToken);
        return ToImportResponse(imported);
    }

    public async Task<ImportConnectorCatalogResponse> ImportCatalogAsync(
        string sourceFilePath,
        Stream sourceStream,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath))
        {
            throw new InvalidOperationException("Connector catalog file name is required.");
        }

        IReadOnlyList<StoredConnectorDefinition> connectors;
        try
        {
            connectors = await _importParser.ParseCatalogAsync(sourceStream, cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Connector catalog file '{sourceFilePath}' is not valid JSON.", exception);
        }

        var saved = await SaveCatalogAsync(
            new SaveConnectorCatalogRequest(connectors.Select(ToDto).ToList()),
            cancellationToken);

        return new ImportConnectorCatalogResponse(
            SourceFilePath: sourceFilePath,
            SourceFileExists: true,
            ImportedCount: saved.Connectors.Count,
            HomeDirectory: saved.HomeDirectory,
            FilePath: saved.FilePath,
            FileExists: saved.FileExists,
            Connectors: saved.Connectors);
    }

    public async Task<ConnectorDraftResponse> SaveDraftAsync(
        SaveConnectorDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Draft is null)
        {
            await _store.DeleteConnectorDraftAsync(cancellationToken);
            return await GetDraftAsync(cancellationToken);
        }

        var saved = await _store.SaveConnectorDraftAsync(
            new StoredConnectorDraft(
                HomeDirectory: string.Empty,
                FilePath: string.Empty,
                FileExists: false,
                UpdatedAtUtc: DateTimeOffset.UtcNow,
                Draft: ToStoredConnectorDraft(request.Draft)),
            cancellationToken);

        return ToDraftResponse(saved);
    }

    public Task DeleteDraftAsync(CancellationToken cancellationToken = default) =>
        _store.DeleteConnectorDraftAsync(cancellationToken);

    private static void EnsureUniqueNames(IEnumerable<ConnectorDefinitionDto> connectors)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var connector in connectors)
        {
            var name = connector.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("Connector name is required.");
            }

            var type = connector.Type?.Trim() ?? string.Empty;
            if (!SupportedTypes.Contains(type))
            {
                throw new InvalidOperationException($"Unsupported connector type '{connector.Type}'.");
            }

            EnsureConnectorConfig(connector, type);

            if (!names.Add(name))
            {
                throw new InvalidOperationException($"Duplicate connector name '{name}'.");
            }
        }
    }

    private static void EnsureConnectorConfig(ConnectorDefinitionDto connector, string normalizedType)
    {
        if (string.Equals(normalizedType, "http", StringComparison.OrdinalIgnoreCase))
        {
            var baseUrl = connector.Http?.BaseUrl?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new InvalidOperationException($"Connector '{connector.Name}' requires http.baseUrl.");
            }

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException($"Connector '{connector.Name}' has an invalid http.baseUrl.");
            }

            return;
        }

        if (string.Equals(normalizedType, "cli", StringComparison.OrdinalIgnoreCase))
        {
            var command = connector.Cli?.Command?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(command))
            {
                throw new InvalidOperationException($"Connector '{connector.Name}' requires cli.command.");
            }

            if (command.Contains("://", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Connector '{connector.Name}' cli.command must be a local preinstalled command.");
            }

            return;
        }

        var mcpCommand = connector.Mcp?.Command?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(mcpCommand))
        {
            throw new InvalidOperationException($"Connector '{connector.Name}' requires mcp.command.");
        }
    }

    private static StoredConnectorDefinition ToStoredConnector(ConnectorDefinitionDto connector) =>
        new(
            Name: connector.Name.Trim(),
            Type: connector.Type.Trim().ToLowerInvariant(),
            Enabled: connector.Enabled,
            TimeoutMs: Math.Clamp(connector.TimeoutMs <= 0 ? 30_000 : connector.TimeoutMs, 100, 300_000),
            Retry: Math.Clamp(connector.Retry, 0, 5),
            Http: new StoredHttpConnectorConfig(
                BaseUrl: connector.Http.BaseUrl?.Trim() ?? string.Empty,
                AllowedMethods: connector.Http.AllowedMethods
                    .Select(method => method.Trim().ToUpperInvariant())
                    .Where(method => !string.IsNullOrWhiteSpace(method))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                AllowedPaths: NormalizeList(connector.Http.AllowedPaths),
                AllowedInputKeys: NormalizeList(connector.Http.AllowedInputKeys),
                DefaultHeaders: NormalizeMap(connector.Http.DefaultHeaders)),
            Cli: new StoredCliConnectorConfig(
                Command: connector.Cli.Command?.Trim() ?? string.Empty,
                FixedArguments: NormalizeList(connector.Cli.FixedArguments),
                AllowedOperations: NormalizeList(connector.Cli.AllowedOperations),
                AllowedInputKeys: NormalizeList(connector.Cli.AllowedInputKeys),
                WorkingDirectory: connector.Cli.WorkingDirectory?.Trim() ?? string.Empty,
                Environment: NormalizeMap(connector.Cli.Environment)),
            Mcp: new StoredMcpConnectorConfig(
                ServerName: connector.Mcp.ServerName?.Trim() ?? string.Empty,
                Command: connector.Mcp.Command?.Trim() ?? string.Empty,
                Arguments: NormalizeList(connector.Mcp.Arguments),
                Environment: NormalizeMap(connector.Mcp.Environment),
                DefaultTool: connector.Mcp.DefaultTool?.Trim() ?? string.Empty,
                AllowedTools: NormalizeList(connector.Mcp.AllowedTools),
                AllowedInputKeys: NormalizeList(connector.Mcp.AllowedInputKeys)));

    private static StoredConnectorDefinition ToStoredConnectorDraft(ConnectorDefinitionDto connector) =>
        new(
            Name: connector.Name?.Trim() ?? string.Empty,
            Type: string.IsNullOrWhiteSpace(connector.Type) ? "http" : connector.Type.Trim().ToLowerInvariant(),
            Enabled: connector.Enabled,
            TimeoutMs: Math.Clamp(connector.TimeoutMs <= 0 ? 30_000 : connector.TimeoutMs, 100, 300_000),
            Retry: Math.Clamp(connector.Retry, 0, 5),
            Http: new StoredHttpConnectorConfig(
                BaseUrl: connector.Http.BaseUrl?.Trim() ?? string.Empty,
                AllowedMethods: NormalizeList(connector.Http.AllowedMethods),
                AllowedPaths: NormalizeList(connector.Http.AllowedPaths),
                AllowedInputKeys: NormalizeList(connector.Http.AllowedInputKeys),
                DefaultHeaders: NormalizeMap(connector.Http.DefaultHeaders)),
            Cli: new StoredCliConnectorConfig(
                Command: connector.Cli.Command?.Trim() ?? string.Empty,
                FixedArguments: NormalizeList(connector.Cli.FixedArguments),
                AllowedOperations: NormalizeList(connector.Cli.AllowedOperations),
                AllowedInputKeys: NormalizeList(connector.Cli.AllowedInputKeys),
                WorkingDirectory: connector.Cli.WorkingDirectory?.Trim() ?? string.Empty,
                Environment: NormalizeMap(connector.Cli.Environment)),
            Mcp: new StoredMcpConnectorConfig(
                ServerName: connector.Mcp.ServerName?.Trim() ?? string.Empty,
                Command: connector.Mcp.Command?.Trim() ?? string.Empty,
                Arguments: NormalizeList(connector.Mcp.Arguments),
                Environment: NormalizeMap(connector.Mcp.Environment),
                DefaultTool: connector.Mcp.DefaultTool?.Trim() ?? string.Empty,
                AllowedTools: NormalizeList(connector.Mcp.AllowedTools),
                AllowedInputKeys: NormalizeList(connector.Mcp.AllowedInputKeys)));

    private static ConnectorCatalogResponse ToResponse(StoredConnectorCatalog catalog) =>
        new(
            catalog.HomeDirectory,
            catalog.FilePath,
            catalog.FileExists,
            catalog.Connectors.Select(ToDto).ToList());

    private static ImportConnectorCatalogResponse ToImportResponse(ImportedConnectorCatalog imported) =>
        new(
            imported.SourceFilePath,
            imported.SourceFileExists,
            imported.Catalog.Connectors.Count,
            imported.Catalog.HomeDirectory,
            imported.Catalog.FilePath,
            imported.Catalog.FileExists,
            imported.Catalog.Connectors.Select(ToDto).ToList());

    private static ConnectorDraftResponse ToDraftResponse(StoredConnectorDraft draft) =>
        new(
            draft.HomeDirectory,
            draft.FilePath,
            draft.FileExists,
            draft.UpdatedAtUtc,
            draft.Draft is null ? null : ToDto(draft.Draft));

    private static ConnectorDefinitionDto ToDto(StoredConnectorDefinition connector) =>
        new(
            connector.Name,
            connector.Type,
            connector.Enabled,
            connector.TimeoutMs,
            connector.Retry,
            new HttpConnectorDefinitionDto(
                connector.Http.BaseUrl,
                connector.Http.AllowedMethods.ToList(),
                connector.Http.AllowedPaths.ToList(),
                connector.Http.AllowedInputKeys.ToList(),
                connector.Http.DefaultHeaders.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase)),
            new CliConnectorDefinitionDto(
                connector.Cli.Command,
                connector.Cli.FixedArguments.ToList(),
                connector.Cli.AllowedOperations.ToList(),
                connector.Cli.AllowedInputKeys.ToList(),
                connector.Cli.WorkingDirectory,
                connector.Cli.Environment.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase)),
            new McpConnectorDefinitionDto(
                connector.Mcp.ServerName,
                connector.Mcp.Command,
                connector.Mcp.Arguments.ToList(),
                connector.Mcp.Environment.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase),
                connector.Mcp.DefaultTool,
                connector.Mcp.AllowedTools.ToList(),
                connector.Mcp.AllowedInputKeys.ToList()));

    private static IReadOnlyList<string> NormalizeList(IEnumerable<string> values) =>
        values
            .Select(value => value?.Trim() ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyDictionary<string, string> NormalizeMap(IReadOnlyDictionary<string, string> values) =>
        values
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .ToDictionary(
                item => item.Key.Trim(),
                item => item.Value?.Trim() ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
}
