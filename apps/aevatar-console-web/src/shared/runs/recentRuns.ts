import type { AGUIEvent } from "@aevatar-react-sdk/types";
import {
  type RunEndpointKind,
  normalizeRunEndpointKind,
} from "./endpointKinds";

export interface RecentRunEntry {
  id: string;
  recordedAt: string;
  scopeId: string;
  serviceOverrideId: string;
  serviceId?: string;
  endpointId: string;
  endpointKind: RunEndpointKind;
  payloadTypeUrl: string;
  payloadBase64: string;
  routeName: string;
  workflowName?: string;
  prompt: string;
  actorId: string;
  commandId: string;
  runId: string;
  status: string;
  lastMessagePreview: string;
  observedEvents: AGUIEvent[];
}

const STORAGE_KEY = 'aevatar-console-recent-runs';
const MAX_RECENT_RUNS = 6;

function readLegacyCompatibleString(
  value: Record<string, unknown>,
  primaryKey: string,
  legacyKey?: string
): string {
  const primaryValue = value[primaryKey];
  if (typeof primaryValue === 'string' && primaryValue.trim()) {
    return primaryValue.trim();
  }

  if (!legacyKey) {
    return '';
  }

  const legacyValue = value[legacyKey];
  return typeof legacyValue === 'string' ? legacyValue.trim() : '';
}

function sanitizeEntry(value: Partial<RecentRunEntry> & {
  serviceId?: string;
  workflowName?: string;
}): RecentRunEntry | null {
  const id = value.id?.trim();
  if (!id) {
    return null;
  }

  const record = value as Record<string, unknown>;

  const sanitized: RecentRunEntry = {
    id,
    recordedAt: value.recordedAt?.trim() || new Date().toISOString(),
    scopeId: value.scopeId?.trim() || "",
    serviceOverrideId: readLegacyCompatibleString(
      record,
      'serviceOverrideId',
      'serviceId'
    ),
    endpointId: value.endpointId?.trim() || "",
    endpointKind: normalizeRunEndpointKind(
      typeof record.endpointKind === "string" ? record.endpointKind : undefined,
      value.endpointId
    ),
    payloadTypeUrl: value.payloadTypeUrl?.trim() || "",
    payloadBase64: value.payloadBase64?.trim() || "",
    routeName: readLegacyCompatibleString(record, 'routeName', 'workflowName'),
    prompt: value.prompt?.trim() || '',
    actorId: value.actorId?.trim() || '',
    commandId: value.commandId?.trim() || '',
    runId: value.runId?.trim() || '',
    status: value.status?.trim() || 'unknown',
    lastMessagePreview: value.lastMessagePreview?.trim() || '',
    observedEvents: Array.isArray(value.observedEvents)
      ? value.observedEvents
          .filter(
            (event): event is AGUIEvent =>
              typeof event === "object" && event !== null
          )
          .map((event) => ({ ...event }))
      : [],
  };

  Object.defineProperties(sanitized, {
    serviceId: {
      enumerable: false,
      get: () => sanitized.serviceOverrideId,
    },
    workflowName: {
      enumerable: false,
      get: () => sanitized.routeName,
    },
  });

  return sanitized;
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
