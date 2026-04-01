import { type Collection, type Db } from "mongodb";

export interface UploadHistory {
  id: string;
  user: string;
  uploadTime: string;
  nodeCount: number;
  edgeCount: number;
  nodeIds: string[];
  edgeIds: string[];
  status: "success" | "partial" | "failed";
  error?: string;
}

export interface UploadHistoryDTO {
  id: string;
  user: string;
  uploadTime: string;
  nodeCount: number;
  edgeCount: number;
  nodeIds: string[];
  edgeIds: string[];
  status: string;
  error?: string;
}

const COLLECTION = "upload_history";

export function getUploadHistoryCollection(db: Db): Collection<UploadHistory> {
  return db.collection<UploadHistory>(COLLECTION);
}

export async function ensureIndexes(db: Db): Promise<void> {
  const col = getUploadHistoryCollection(db);
  await col.createIndex({ id: 1 }, { unique: true });
  await col.createIndex({ uploadTime: -1 });
}
