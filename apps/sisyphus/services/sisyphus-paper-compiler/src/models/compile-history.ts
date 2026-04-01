import { type Collection, type Db } from "mongodb";

export interface CompileRecord {
  id: string;
  filterName: string;
  nodeCount: number;
  edgeCount: number;
  status: "running" | "completed" | "failed" | "aborted";
  latexKey?: string;     // S3 object key
  pdfKey?: string;       // S3 object key
  latexFileName?: string;
  pdfFileName?: string;
  error?: string;
  startedAt: string;
  completedAt?: string;
}

const COLLECTION = "compile_history";

export function getCompileCollection(db: Db): Collection<CompileRecord> {
  return db.collection<CompileRecord>(COLLECTION);
}

export async function ensureCompileIndexes(db: Db): Promise<void> {
  const col = getCompileCollection(db);
  await col.createIndex({ id: 1 }, { unique: true });
  await col.createIndex({ startedAt: -1 });
}
