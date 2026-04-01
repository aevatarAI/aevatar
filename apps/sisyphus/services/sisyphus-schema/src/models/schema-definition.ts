import { type Collection, type Db, ObjectId } from "mongodb";

export interface SchemaDefinition {
  _id?: ObjectId;
  id: string;
  name: string;
  description: string;
  entityType: "node" | "edge";
  nodeType: string;
  applicableTypes: string[];
  jsonSchema: Record<string, unknown>;
  createdAt: string;
  updatedAt: string;
}

export interface SchemaDefinitionDTO {
  id: string;
  name: string;
  description: string;
  entityType: "node" | "edge";
  nodeType: string;
  applicableTypes: string[];
  jsonSchema: Record<string, unknown>;
  createdAt: string;
  updatedAt: string;
}

const COLLECTION = "schema_definitions";

export function getSchemaCollection(db: Db): Collection<SchemaDefinition> {
  return db.collection<SchemaDefinition>(COLLECTION);
}

export async function ensureIndexes(db: Db): Promise<void> {
  const col = getSchemaCollection(db);
  await col.createIndex({ id: 1 }, { unique: true });
  await col.createIndex({ name: 1 }, { unique: true });
}

export function toDTO(doc: SchemaDefinition): SchemaDefinitionDTO {
  const { _id, ...rest } = doc;
  return rest;
}
