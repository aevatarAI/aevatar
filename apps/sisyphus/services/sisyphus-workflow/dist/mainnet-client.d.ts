import type { MainnetConnectorDto, MainnetRoleDto } from "./types.js";
/**
 * Upload the full connector catalog to the mainnet.
 * PUT /api/connectors — replaces entire catalog.
 */
export declare function uploadConnectors(connectors: MainnetConnectorDto[], authorization?: string): Promise<void>;
/**
 * Upload the full role catalog to the mainnet.
 * PUT /api/roles — replaces entire catalog.
 */
export declare function uploadRoles(roles: MainnetRoleDto[], authorization?: string): Promise<void>;
/**
 * Bind a compiled workflow to the scope's default service.
 * PUT /api/scopes/{scopeId}/binding
 * This makes the workflow available via POST /api/scopes/{scopeId}/invoke/chat:stream
 */
export declare function uploadWorkflow(workflowId: string, yaml: string, name: string, inlineYamls?: Record<string, string>, revisionId?: string, authorization?: string): Promise<Record<string, unknown>>;
//# sourceMappingURL=mainnet-client.d.ts.map