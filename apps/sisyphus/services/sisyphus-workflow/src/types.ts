export interface ErrorResponse {
  error: string;
}

export interface PaginatedQuery {
  page?: number;
  pageSize?: number;
}

// --- Deployment Pipeline Types ---

export type DeploymentStatus = "draft" | "compiled" | "deployed" | "out_of_sync";

export interface DeploymentState {
  status: DeploymentStatus;
  contentHash?: string;
  lastCompiledAt?: string;
  lastDeployedAt?: string;
  lastDeployedArtifactId?: string;
  mainnetRevisionId?: string;
  deployError?: string;
}

// --- Mainnet DTO Types (matching C# contracts in Aevatar.Studio.Application) ---

export interface MainnetConnectorDto {
  Name: string;
  Type: string;
  Enabled: boolean;
  TimeoutMs: number;
  Retry: number;
  Http: {
    BaseUrl: string;
    AllowedMethods: string[];
    AllowedPaths: string[];
    AllowedInputKeys: string[];
    DefaultHeaders: Record<string, string>;
  };
  Cli: {
    Command: string;
    FixedArguments: string[];
    AllowedOperations: string[];
    AllowedInputKeys: string[];
    WorkingDirectory: string;
    Environment: Record<string, string>;
  };
  Mcp: {
    ServerName: string;
    Command: string;
    Arguments: string[];
    Environment: Record<string, string>;
    DefaultTool: string;
    AllowedTools: string[];
    AllowedInputKeys: string[];
  };
}

export interface MainnetRoleDto {
  Id: string;
  Name: string;
  SystemPrompt: string;
  Provider: string;
  Model: string;
  Connectors: string[];
}
