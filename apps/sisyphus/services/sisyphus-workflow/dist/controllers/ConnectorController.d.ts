import { Controller } from "tsoa";
import { type ConnectorDefinitionDTO, type HttpEndpoint, type AuthConfig } from "../models/connector-definition.js";
import type { MainnetConnectorDto } from "../types.js";
interface CreateConnectorRequest {
    name: string;
    description: string;
    type: "http" | "mcp";
    baseUrl?: string;
    authConfig?: AuthConfig;
    endpoints?: HttpEndpoint[];
    mcpConfig?: Record<string, unknown>;
}
interface UpdateConnectorRequest {
    name?: string;
    description?: string;
    type?: "http" | "mcp";
    baseUrl?: string;
    authConfig?: AuthConfig;
    endpoints?: HttpEndpoint[];
    mcpConfig?: Record<string, unknown>;
}
interface ConnectorListResponse {
    connectors: ConnectorDefinitionDTO[];
    total: number;
    page: number;
    pageSize: number;
}
export declare class ConnectorController extends Controller {
    /**
     * Create a new connector definition.
     * @summary Create connector
     */
    createConnector(body: CreateConnectorRequest): Promise<ConnectorDefinitionDTO>;
    /**
     * List all connector definitions with pagination.
     * @summary List connectors
     */
    listConnectors(page?: number, pageSize?: number): Promise<ConnectorListResponse>;
    /**
     * Get a single connector by ID.
     * @summary Get connector
     */
    getConnector(connectorId: string): Promise<ConnectorDefinitionDTO>;
    /**
     * Update a connector definition.
     * @summary Update connector
     */
    updateConnector(connectorId: string, body: UpdateConnectorRequest): Promise<ConnectorDefinitionDTO>;
    /**
     * Delete a connector definition.
     * @summary Delete connector
     */
    deleteConnector(connectorId: string): Promise<void>;
    /**
     * Compile a single connector to mainnet ConnectorDefinitionDto format.
     * @summary Compile connector
     */
    compileConnector(connectorId: string): Promise<MainnetConnectorDto>;
    /**
     * Sync ALL connectors to Aevatar mainnet. Loads all connectors from DB,
     * maps to mainnet format, and PUTs the full catalog.
     * @summary Sync connectors to Aevatar
     */
    syncConnectors(authorization?: string): Promise<{
        success: boolean;
        count: number;
    }>;
}
export {};
//# sourceMappingURL=ConnectorController.d.ts.map