import { type Collection, type Db } from "mongodb";
export interface HttpEndpoint {
    name: string;
    method: string;
    path: string;
}
export interface AuthConfig {
    type: string;
    [key: string]: unknown;
}
export interface ConnectorDefinition {
    id: string;
    name: string;
    description: string;
    type: "http" | "mcp";
    baseUrl?: string;
    authConfig?: AuthConfig;
    endpoints?: HttpEndpoint[];
    mcpConfig?: Record<string, unknown>;
    createdAt: string;
    updatedAt: string;
}
export interface ConnectorDefinitionDTO {
    id: string;
    name: string;
    description: string;
    type: "http" | "mcp";
    baseUrl?: string;
    authConfig?: AuthConfig;
    endpoints?: HttpEndpoint[];
    mcpConfig?: Record<string, unknown>;
    createdAt: string;
    updatedAt: string;
}
export declare function getConnectorCollection(db: Db): Collection<ConnectorDefinition>;
export declare function ensureConnectorIndexes(db: Db): Promise<void>;
export declare function toDTO(doc: ConnectorDefinition & {
    _id?: unknown;
}): ConnectorDefinitionDTO;
//# sourceMappingURL=connector-definition.d.ts.map