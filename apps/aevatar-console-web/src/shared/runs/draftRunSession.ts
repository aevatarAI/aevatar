export type DraftRunPayload = {
  workflowName: string;
  workflowYamls: string[];
  createdAt: string;
};

const STORAGE_PREFIX = "aevatar-console-draft-run:";

function buildStorageKey(key: string): string {
  return `${STORAGE_PREFIX}${key}`;
}

function createDraftRunKey(): string {
  const random = globalThis.crypto?.randomUUID?.();
  if (random) {
    return random;
  }

  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

export function saveDraftRunPayload(payload: {
  workflowName: string;
  workflowYamls: string[];
}): string {
  if (typeof window === "undefined") {
    return "";
  }

  const key = createDraftRunKey();
  const normalizedPayload: DraftRunPayload = {
    workflowName: payload.workflowName.trim(),
    workflowYamls: payload.workflowYamls
      .map((item) => item.trim())
      .filter((item) => item.length > 0),
    createdAt: new Date().toISOString(),
  };
  window.sessionStorage.setItem(
    buildStorageKey(key),
    JSON.stringify(normalizedPayload)
  );
  return key;
}

export function loadDraftRunPayload(key: string | null | undefined): DraftRunPayload | null {
  const normalizedKey = key?.trim();
  if (!normalizedKey || typeof window === "undefined") {
    return null;
  }

  const raw = window.sessionStorage.getItem(buildStorageKey(normalizedKey));
  if (!raw) {
    return null;
  }

  try {
    const parsed = JSON.parse(raw) as Partial<DraftRunPayload>;
    const workflowName = parsed.workflowName?.trim();
    const workflowYamls = Array.isArray(parsed.workflowYamls)
      ? parsed.workflowYamls
          .map((item) => String(item ?? "").trim())
          .filter((item) => item.length > 0)
      : [];
    if (!workflowName || workflowYamls.length === 0) {
      return null;
    }

    return {
      workflowName,
      workflowYamls,
      createdAt: parsed.createdAt?.trim() || "",
    };
  } catch {
    return null;
  }
}

export function deleteDraftRunPayload(key: string | null | undefined): void {
  const normalizedKey = key?.trim();
  if (!normalizedKey || typeof window === "undefined") {
    return;
  }

  window.sessionStorage.removeItem(buildStorageKey(normalizedKey));
}
