import pino from "pino";

const logger = pino({ name: "paper-compiler:storage" });

const STORAGE_URL = process.env["CHRONO_STORAGE_URL"] ?? "http://chrono-storage.chronoai-platform:3805";
const BUCKET = process.env["STORAGE_BUCKET"] ?? "sisyphus-papers";

export async function uploadFile(
  key: string,
  data: Buffer | Uint8Array,
  contentType: string,
): Promise<string> {
  const params = new URLSearchParams({ key, contentType });
  const url = `${STORAGE_URL}/api/buckets/${encodeURIComponent(BUCKET)}/objects?${params.toString()}`;

  logger.info({ url, key, contentType, size: data.length }, "Uploading to chrono-storage");

  const resp = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": contentType },
    body: data as unknown as BodyInit,
    signal: AbortSignal.timeout(120000),
  });

  if (!resp.ok) {
    const body = await resp.text();
    throw new Error(`Storage upload failed: HTTP ${resp.status} — ${body}`);
  }

  const result = await resp.json() as { data?: { url?: string } };
  const s3Url = result.data?.url ?? `s3://${BUCKET}/${key}`;
  logger.info({ key, s3Url }, "Upload successful");
  return s3Url;
}

export async function getPresignedUrl(key: string): Promise<string> {
  const params = new URLSearchParams({ key });
  const url = `${STORAGE_URL}/api/buckets/${encodeURIComponent(BUCKET)}/presigned-url?${params.toString()}`;

  const resp = await fetch(url, { signal: AbortSignal.timeout(10000) });
  if (!resp.ok) {
    const body = await resp.text();
    throw new Error(`Presigned URL failed: HTTP ${resp.status} — ${body}`);
  }

  const result = await resp.json() as { data?: { url?: string } };
  return result.data?.url ?? "";
}

export async function ensureBucket(): Promise<void> {
  const url = `${STORAGE_URL}/api/buckets`;
  try {
    // Check if bucket exists
    const headResp = await fetch(`${url}/${encodeURIComponent(BUCKET)}`, {
      method: "HEAD",
      signal: AbortSignal.timeout(5000),
    });
    if (headResp.ok) return;

    // Create bucket
    const resp = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name: BUCKET }),
      signal: AbortSignal.timeout(10000),
    });
    if (resp.ok || resp.status === 409) {
      logger.info({ bucket: BUCKET }, "Storage bucket ready");
    } else {
      logger.warn({ bucket: BUCKET, status: resp.status }, "Failed to create bucket");
    }
  } catch (err) {
    logger.warn({ err: (err as Error).message }, "Storage bucket check failed — will retry on upload");
  }
}
