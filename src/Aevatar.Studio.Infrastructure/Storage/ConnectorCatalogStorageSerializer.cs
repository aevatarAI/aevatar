using Aevatar.GAgents.ConnectorCatalog;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Infrastructure.Storage;

internal static class ConnectorCatalogStorageSerializer
{
    public static async Task<IReadOnlyList<StoredConnectorDefinition>> ReadCatalogAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var payload = await ReadPayloadAsync(stream, cancellationToken);
        var state = ConnectorCatalogState.Parser.ParseFrom(payload);
        return state.Connectors
            .Select(ToStoredConnectorDefinition)
            .ToList()
            .AsReadOnly();
    }

    public static async Task WriteCatalogAsync(
        Stream stream,
        IReadOnlyList<StoredConnectorDefinition> connectors,
        CancellationToken cancellationToken)
    {
        var payload = new ConnectorCatalogState();
        payload.Connectors.AddRange(connectors.Select(ToProtoConnectorDefinition));
        await stream.WriteAsync(payload.ToByteArray(), cancellationToken);
    }

    public static async Task<ParsedConnectorDraft> ReadDraftAsync(
        Stream stream,
        DateTimeOffset fallbackUpdatedAtUtc,
        CancellationToken cancellationToken)
    {
        var payload = await ReadPayloadAsync(stream, cancellationToken);
        var draftEntry = ConnectorDraftEntry.Parser.ParseFrom(payload);
        var protobufUpdatedAtUtc = draftEntry.UpdatedAtUtc?.ToDateTimeOffset() ?? fallbackUpdatedAtUtc;
        var protobufDraft = draftEntry.Draft is not null ? ToStoredConnectorDefinition(draftEntry.Draft) : null;
        return new ParsedConnectorDraft(protobufUpdatedAtUtc, protobufDraft);
    }

    public static async Task WriteDraftAsync(
        Stream stream,
        StoredConnectorDefinition? draft,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        var payload = new ConnectorDraftEntry
        {
            Draft = draft is not null ? ToProtoConnectorDefinition(draft) : null,
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(updatedAtUtc),
        };

        await stream.WriteAsync(payload.ToByteArray(), cancellationToken);
    }

    internal sealed record ParsedConnectorDraft(
        DateTimeOffset UpdatedAtUtc,
        StoredConnectorDefinition? Draft);

    private static async Task<byte[]> ReadPayloadAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static StoredConnectorDefinition ToStoredConnectorDefinition(ConnectorDefinitionEntry entry) =>
        new(
            Name: entry.Name,
            Type: entry.Type,
            Enabled: entry.Enabled,
            TimeoutMs: entry.TimeoutMs,
            Retry: entry.Retry,
            Http: entry.Http is not null ? ToStoredHttpConfig(entry.Http) : EmptyHttpConfig(),
            Cli: entry.Cli is not null ? ToStoredCliConfig(entry.Cli) : EmptyCliConfig(),
            Mcp: entry.Mcp is not null ? ToStoredMcpConfig(entry.Mcp) : EmptyMcpConfig());

    private static StoredHttpConnectorConfig ToStoredHttpConfig(HttpConnectorConfigEntry entry) =>
        new(
            BaseUrl: entry.BaseUrl,
            AllowedMethods: entry.AllowedMethods.ToList().AsReadOnly(),
            AllowedPaths: entry.AllowedPaths.ToList().AsReadOnly(),
            AllowedInputKeys: entry.AllowedInputKeys.ToList().AsReadOnly(),
            DefaultHeaders: new Dictionary<string, string>(entry.DefaultHeaders, StringComparer.OrdinalIgnoreCase),
            Auth: entry.Auth is not null ? ToStoredAuthConfig(entry.Auth) : EmptyAuthConfig());

    private static StoredCliConnectorConfig ToStoredCliConfig(CliConnectorConfigEntry entry) =>
        new(
            Command: entry.Command,
            FixedArguments: entry.FixedArguments.ToList().AsReadOnly(),
            AllowedOperations: entry.AllowedOperations.ToList().AsReadOnly(),
            AllowedInputKeys: entry.AllowedInputKeys.ToList().AsReadOnly(),
            WorkingDirectory: entry.WorkingDirectory,
            Environment: new Dictionary<string, string>(entry.Environment, StringComparer.OrdinalIgnoreCase));

    private static StoredMcpConnectorConfig ToStoredMcpConfig(McpConnectorConfigEntry entry) =>
        new(
            ServerName: entry.ServerName,
            Command: entry.Command,
            Url: entry.Url,
            Arguments: entry.Arguments.ToList().AsReadOnly(),
            Environment: new Dictionary<string, string>(entry.Environment, StringComparer.OrdinalIgnoreCase),
            AdditionalHeaders: new Dictionary<string, string>(entry.AdditionalHeaders, StringComparer.OrdinalIgnoreCase),
            Auth: entry.Auth is not null ? ToStoredAuthConfig(entry.Auth) : EmptyAuthConfig(),
            DefaultTool: entry.DefaultTool,
            AllowedTools: entry.AllowedTools.ToList().AsReadOnly(),
            AllowedInputKeys: entry.AllowedInputKeys.ToList().AsReadOnly());

    private static StoredConnectorAuthConfig ToStoredAuthConfig(ConnectorAuthEntry entry) =>
        new(
            Type: entry.Type,
            TokenUrl: entry.TokenUrl,
            ClientId: entry.ClientId,
            ClientSecret: entry.ClientSecret,
            Scope: entry.Scope);

    private static ConnectorDefinitionEntry ToProtoConnectorDefinition(StoredConnectorDefinition def)
    {
        var entry = new ConnectorDefinitionEntry
        {
            Name = def.Name,
            Type = def.Type,
            Enabled = def.Enabled,
            TimeoutMs = def.TimeoutMs,
            Retry = def.Retry,
            Http = new HttpConnectorConfigEntry
            {
                BaseUrl = def.Http.BaseUrl,
                Auth = ToProtoAuthConfig(def.Http.Auth),
            },
            Cli = new CliConnectorConfigEntry
            {
                Command = def.Cli.Command,
                WorkingDirectory = def.Cli.WorkingDirectory,
            },
            Mcp = new McpConnectorConfigEntry
            {
                ServerName = def.Mcp.ServerName,
                Command = def.Mcp.Command,
                Url = def.Mcp.Url,
                Auth = ToProtoAuthConfig(def.Mcp.Auth),
                DefaultTool = def.Mcp.DefaultTool,
            },
        };

        entry.Http.AllowedMethods.AddRange(def.Http.AllowedMethods);
        entry.Http.AllowedPaths.AddRange(def.Http.AllowedPaths);
        entry.Http.AllowedInputKeys.AddRange(def.Http.AllowedInputKeys);
        AddMapEntries(entry.Http.DefaultHeaders, def.Http.DefaultHeaders);
        entry.Cli.FixedArguments.AddRange(def.Cli.FixedArguments);
        entry.Cli.AllowedOperations.AddRange(def.Cli.AllowedOperations);
        entry.Cli.AllowedInputKeys.AddRange(def.Cli.AllowedInputKeys);
        AddMapEntries(entry.Cli.Environment, def.Cli.Environment);
        entry.Mcp.Arguments.AddRange(def.Mcp.Arguments);
        AddMapEntries(entry.Mcp.Environment, def.Mcp.Environment);
        AddMapEntries(entry.Mcp.AdditionalHeaders, def.Mcp.AdditionalHeaders);
        entry.Mcp.AllowedTools.AddRange(def.Mcp.AllowedTools);
        entry.Mcp.AllowedInputKeys.AddRange(def.Mcp.AllowedInputKeys);
        return entry;
    }

    private static ConnectorAuthEntry ToProtoAuthConfig(StoredConnectorAuthConfig auth) =>
        new()
        {
            Type = auth.Type,
            TokenUrl = auth.TokenUrl,
            ClientId = auth.ClientId,
            ClientSecret = auth.ClientSecret,
            Scope = auth.Scope,
        };

    private static void AddMapEntries(
        Google.Protobuf.Collections.MapField<string, string> target,
        IReadOnlyDictionary<string, string> source)
    {
        foreach (var item in source)
        {
            target[item.Key] = item.Value;
        }
    }

    private static StoredHttpConnectorConfig EmptyHttpConfig() =>
        new(string.Empty, [], [], [], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), EmptyAuthConfig());

    private static StoredCliConnectorConfig EmptyCliConfig() =>
        new(string.Empty, [], [], [], string.Empty, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static StoredMcpConnectorConfig EmptyMcpConfig() =>
        new(
            string.Empty,
            string.Empty,
            string.Empty,
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            EmptyAuthConfig(),
            string.Empty,
            [],
            []);

    private static StoredConnectorAuthConfig EmptyAuthConfig() =>
        new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
}
