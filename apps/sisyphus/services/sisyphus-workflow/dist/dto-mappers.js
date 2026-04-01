const EMPTY_HTTP = {
    BaseUrl: "",
    AllowedMethods: [],
    AllowedPaths: [],
    AllowedInputKeys: [],
    DefaultHeaders: {},
};
const EMPTY_CLI = {
    Command: "",
    FixedArguments: [],
    AllowedOperations: [],
    AllowedInputKeys: [],
    WorkingDirectory: "",
    Environment: {},
};
const EMPTY_MCP = {
    ServerName: "",
    Command: "",
    Arguments: [],
    Environment: {},
    DefaultTool: "",
    AllowedTools: [],
    AllowedInputKeys: [],
};
/**
 * Map a Sisyphus ConnectorDefinition to the mainnet ConnectorDefinitionDto format.
 * See: Aevatar.Studio.Application/Studio/Contracts/ConnectorContracts.cs
 */
export function mapConnectorToMainnet(connector) {
    const base = {
        Name: connector.name,
        Type: connector.type,
        Enabled: true,
        TimeoutMs: 60000,
        Retry: 0,
        Http: { ...EMPTY_HTTP },
        Cli: { ...EMPTY_CLI },
        Mcp: { ...EMPTY_MCP },
    };
    if (connector.type === "http") {
        const methods = new Set();
        const paths = [];
        for (const ep of connector.endpoints ?? []) {
            if (ep.method)
                methods.add(ep.method.toUpperCase());
            if (ep.path)
                paths.push(ep.path);
        }
        base.Http = {
            BaseUrl: connector.baseUrl ?? "",
            AllowedMethods: [...methods],
            AllowedPaths: paths.length > 0 ? paths : ["/*"],
            AllowedInputKeys: [],
            DefaultHeaders: {},
        };
        // Extract timeout from authConfig if present
        if (connector.authConfig?.timeoutMs) {
            base.TimeoutMs = connector.authConfig.timeoutMs;
        }
    }
    if (connector.type === "mcp" && connector.mcpConfig) {
        const cfg = connector.mcpConfig;
        base.Mcp = {
            ServerName: cfg.serverName ?? connector.name,
            Command: cfg.command ?? "",
            Arguments: cfg.arguments ?? [],
            Environment: cfg.environment ?? {},
            DefaultTool: cfg.defaultTool ?? "",
            AllowedTools: cfg.allowedTools ?? [],
            AllowedInputKeys: cfg.allowedInputKeys ?? [],
        };
    }
    return base;
}
/**
 * Map a workflow role (with resolved skill content) to the mainnet RoleDefinitionDto format.
 * See: Aevatar.Studio.Application/Studio/Contracts/RoleContracts.cs
 */
export function mapRoleToMainnet(role, systemPrompt, connectorNames) {
    return {
        Id: role.name.toLowerCase().replace(/\s+/g, "-"),
        Name: role.name,
        SystemPrompt: systemPrompt,
        Provider: "",
        Model: "",
        Connectors: connectorNames,
    };
}
//# sourceMappingURL=dto-mappers.js.map