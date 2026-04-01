import { type Collection, type Db } from "mongodb";

export interface AgUiEvent {
  sessionId: string;
  timestamp: string;
  eventType: string;
  payload: Record<string, unknown>;
  createdAt: Date;
}

const COLLECTION = "agui_events";

const TTL_SECONDS = parseInt(process.env["EVENT_TTL_DAYS"] ?? "30", 10) * 24 * 60 * 60;

export function getEventLogCollection(db: Db): Collection<AgUiEvent> {
  return db.collection<AgUiEvent>(COLLECTION);
}

export async function ensureEventLogIndexes(db: Db): Promise<void> {
  const col = getEventLogCollection(db);
  await col.createIndex({ sessionId: 1, timestamp: 1 });
  await col.createIndex({ createdAt: 1 }, { expireAfterSeconds: TTL_SECONDS });
}
