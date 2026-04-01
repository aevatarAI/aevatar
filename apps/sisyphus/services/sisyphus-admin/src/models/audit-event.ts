import { type Collection, type Db } from "mongodb";

export type AuditAction =
  | "create" | "update" | "delete"
  | "compile" | "deploy"
  | "trigger" | "stop"
  | "login" | "logout";

export type AuditResource =
  | "workflow" | "connector" | "schema"
  | "settings" | "session" | "user";

/**
 * A single audit trail entry recording who did what to which resource.
 * Other Sisyphus services POST events here after performing operations.
 */
export interface AuditEvent {
  id: string;
  timestamp: string;
  userId: string;
  userName?: string;
  action: AuditAction;
  resource: AuditResource;
  resourceId?: string;
  resourceName?: string;
  service: string;
  details?: Record<string, unknown>;
  createdAt: Date;
}

export type AuditEventDTO = Omit<AuditEvent, "_id" | "createdAt"> & { createdAt: string };

const COLLECTION = "admin_audit_events";

export function getAuditEventCollection(db: Db): Collection<AuditEvent> {
  return db.collection<AuditEvent>(COLLECTION);
}

export async function ensureAuditEventIndexes(db: Db): Promise<void> {
  const col = getAuditEventCollection(db);
  await col.createIndex({ id: 1 }, { unique: true });
  await col.createIndex({ timestamp: -1 });
  await col.createIndex({ userId: 1, timestamp: -1 });
  await col.createIndex({ resource: 1, action: 1, timestamp: -1 });
  await col.createIndex({ service: 1, timestamp: -1 });
  // TTL: auto-delete after 365 days
  await col.createIndex({ createdAt: 1 }, { expireAfterSeconds: 365 * 24 * 60 * 60 });
}

export function toDTO(doc: AuditEvent & { _id?: unknown }): AuditEventDTO {
  const { _id, createdAt, ...rest } = doc;
  return { ...rest, createdAt: createdAt.toISOString() };
}
