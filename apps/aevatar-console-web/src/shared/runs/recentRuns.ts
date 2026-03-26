export interface RecentRunEntry {
  id: string;
  recordedAt: string;
  scopeId: string;
  serviceId: string;
  workflowName: string;
  prompt: string;
  actorId: string;
  commandId: string;
  runId: string;
  status: string;
  lastMessagePreview: string;
}

const STORAGE_KEY = 'aevatar-console-recent-runs';
const MAX_RECENT_RUNS = 6;

function sanitizeEntry(value: Partial<RecentRunEntry>): RecentRunEntry | null {
  const id = value.id?.trim();
  if (!id) {
    return null;
  }

  return {
    id,
    recordedAt: value.recordedAt?.trim() || new Date().toISOString(),
    scopeId: value.scopeId?.trim() || "",
    serviceId: value.serviceId?.trim() || "",
    workflowName: value.workflowName?.trim() || 'unknown',
    prompt: value.prompt?.trim() || '',
    actorId: value.actorId?.trim() || '',
    commandId: value.commandId?.trim() || '',
    runId: value.runId?.trim() || '',
    status: value.status?.trim() || 'unknown',
    lastMessagePreview: value.lastMessagePreview?.trim() || '',
  };
}

export function loadRecentRuns(): RecentRunEntry[] {
  if (typeof window === 'undefined') {
    return [];
  }

  const raw = window.localStorage.getItem(STORAGE_KEY);
  if (!raw) {
    return [];
  }

  try {
    const parsed = JSON.parse(raw) as Partial<RecentRunEntry>[];
    return parsed
      .map((item) => sanitizeEntry(item))
      .filter((item): item is RecentRunEntry => item !== null)
      .slice(0, MAX_RECENT_RUNS);
  } catch {
    return [];
  }
}

export function saveRecentRun(value: Partial<RecentRunEntry>): RecentRunEntry[] {
  const entry = sanitizeEntry(value);
  if (!entry || typeof window === 'undefined') {
    return loadRecentRuns();
  }

  const next = [
    entry,
    ...loadRecentRuns().filter((existing) => existing.id !== entry.id),
  ].slice(0, MAX_RECENT_RUNS);

  window.localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
  return next;
}

export function clearRecentRuns(): RecentRunEntry[] {
  if (typeof window !== 'undefined') {
    window.localStorage.removeItem(STORAGE_KEY);
  }

  return [];
}
