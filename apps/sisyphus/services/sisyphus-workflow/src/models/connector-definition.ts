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

const COLLECTION = "connector_definitions";

export function getConnectorCollection(db: Db): Collection<ConnectorDefinition> {
  return db.collection<ConnectorDefinition>(COLLECTION);
}

export async function ensureConnectorIndexes(db: Db): Promise<void> {
  const col = getConnectorCollection(db);
  await col.createIndex({ id: 1 }, { unique: true });
  await col.createIndex({ name: 1 });
}

export function toDTO(doc: ConnectorDefinition & { _id?: unknown }): ConnectorDefinitionDTO {
  const { _id, ...rest } = doc;
  return rest;
}
