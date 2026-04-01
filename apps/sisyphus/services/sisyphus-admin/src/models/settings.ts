import { type Collection, type Db } from "mongodb";

/**
 * Singleton settings document. There is exactly one document in this collection
 * with id = "global". All Sisyphus services can read these values via the admin API.
 */
export interface Settings {
  id: "global";
  graphId: string;
  verifyCronIntervalHours: number;
  eventRetentionDays: number;
  defaultResearchMode: "graph_based" | "exploration";
  graphViewNodeLimit: number;
  updatedAt: string;
  updatedBy?: string;
}

export type SettingsDTO = Omit<Settings, "_id">;

export const DEFAULT_SETTINGS: Settings = {
  id: "global",
  graphId: "",
  verifyCronIntervalHours: 6,
  eventRetentionDays: 30,
  defaultResearchMode: "graph_based",
  graphViewNodeLimit: 200,
  updatedAt: new Date().toISOString(),
};

const COLLECTION = "admin_settings";

export function getSettingsCollection(db: Db): Collection<Settings> {
  return db.collection<Settings>(COLLECTION);
}

export async function ensureSettingsIndexes(db: Db): Promise<void> {
  const col = getSettingsCollection(db);
  await col.createIndex({ id: 1 }, { unique: true });
}

export function toDTO(doc: Settings & { _id?: unknown }): SettingsDTO {
  const { _id, ...rest } = doc;
  return rest;
}
