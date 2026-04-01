import { type Collection, type Db } from "mongodb";
import type { WorkflowType, SessionStatus } from "../types.js";

export interface Session {
  id: string;
  runId: string;
  workflowType: WorkflowType;
  status: SessionStatus;
  triggeredBy: string;
  startedAt: string;
  stoppedAt?: string;
  duration?: number;
  error?: string;
  mode?: string;
  direction?: string;
}

export interface SessionDTO {
  id: string;
  runId: string;
  workflowType: WorkflowType;
  status: SessionStatus;
  triggeredBy: string;
  startedAt: string;
  stoppedAt?: string;
  duration?: number;
  error?: string;
  mode?: string;
  direction?: string;
}

const COLLECTION = "sessions";

export function getSessionCollection(db: Db): Collection<Session> {
  return db.collection<Session>(COLLECTION);
}

export async function ensureSessionIndexes(db: Db): Promise<void> {
  const col = getSessionCollection(db);
  await col.createIndex({ id: 1 }, { unique: true });
  await col.createIndex({ runId: 1 });
  await col.createIndex({ workflowType: 1, status: 1 });
  await col.createIndex({ startedAt: -1 });
}

export function toDTO(doc: Session & { _id?: unknown }): SessionDTO {
  const { _id, ...rest } = doc;
  return rest;
}
