import { type Collection, type Db } from "mongodb";
import type { MainnetConnectorDto, MainnetRoleDto } from "../types.js";

export interface CompiledArtifact {
  id: string;
  workflowId: string;
  workflowYaml: string;
  connectorJson: Record<string, unknown>[];
  mainnetConnectors: MainnetConnectorDto[];
  mainnetRoles: MainnetRoleDto[];
  contentHash: string;
  compiledAt: string;
}

const COLLECTION = "compiled_artifacts";

export function getCompiledArtifactCollection(db: Db): Collection<CompiledArtifact> {
  return db.collection<CompiledArtifact>(COLLECTION);
}

export async function ensureCompiledArtifactIndexes(db: Db): Promise<void> {
  const col = getCompiledArtifactCollection(db);
  await col.createIndex({ id: 1 }, { unique: true });
  await col.createIndex({ workflowId: 1 });
}

export function toDTO(doc: CompiledArtifact & { _id?: unknown }): Omit<CompiledArtifact, "_id"> {
  const { _id, ...rest } = doc;
  return rest;
}
