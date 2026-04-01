import type { ConnectorDefinition } from "./models/connector-definition.js";
import type { MainnetConnectorDto, MainnetRoleDto } from "./types.js";

const EMPTY_HTTP = {
  BaseUrl: "",
  AllowedMethods: [] as string[],
  AllowedPaths: [] as string[],
  AllowedInputKeys: [] as string[],
  DefaultHeaders: {} as Record<string, string>,
};

const EMPTY_CLI = {
  Command: "",
  FixedArguments: [] as string[],
  AllowedOperations: [] as string[],
  AllowedInputKeys: [] as string[],
  WorkingDirectory: "",
  Environment: {} as Record<string, string>,
};

const EMPTY_MCP = {
  ServerName: "",
  Command: "",
  Arguments: [] as string[],
  Environment: {} as Record<string, string>,
  DefaultTool: "",
  AllowedTools: [] as string[],
  AllowedInputKeys: [] as string[],
};

/**
 * Map a Sisyphus ConnectorDefinition to the mainnet ConnectorDefinitionDto format.
 * See: Aevatar.Studio.Application/Studio/Contracts/ConnectorContracts.cs
 */
export function mapConnectorToMainnet(connector: ConnectorDefinition): MainnetConnectorDto {
  const base: MainnetConnectorDto = {
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
    const methods = new Set<string>();
    const paths: string[] = [];

    for (const ep of connector.endpoints ?? []) {
      if (ep.method) methods.add(ep.method.toUpperCase());
      if (ep.path) paths.push(ep.path);
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
      base.TimeoutMs = connector.authConfig.timeoutMs as number;
    }
  }

  if (connector.type === "mcp" && connector.mcpConfig) {
    const cfg = connector.mcpConfig;
    base.Mcp = {
      ServerName: (cfg.serverName as string) ?? connector.name,
      Command: (cfg.command as string) ?? "",
      Arguments: (cfg.arguments as string[]) ?? [],
      Environment: (cfg.environment as Record<string, string>) ?? {},
      DefaultTool: (cfg.defaultTool as string) ?? "",
      AllowedTools: (cfg.allowedTools as string[]) ?? [],
      AllowedInputKeys: (cfg.allowedInputKeys as string[]) ?? [],
    };
  }

  return base;
}

/**
 * Map a workflow role (with resolved skill content) to the mainnet RoleDefinitionDto format.
 * See: Aevatar.Studio.Application/Studio/Contracts/RoleContracts.cs
 */
export function mapRoleToMainnet(
  role: { name: string; description?: string },
  systemPrompt: string,
  connectorNames: string[],
): MainnetRoleDto {
  return {
    Id: role.name.toLowerCase().replace(/\s+/g, "-"),
    Name: role.name,
    SystemPrompt: systemPrompt,
    Provider: "",
    Model: "",
    Connectors: connectorNames,
  };
}
