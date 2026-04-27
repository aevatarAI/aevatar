const STORAGE_PREFIX = 'aevatar-studio-invoke-history:';
const MAX_HISTORY_ENTRIES = 8;

export type StudioInvokeHistoryStorageKey = string;

function trimText(value: string | null | undefined): string {
  return value?.trim() ?? '';
}

export function buildStudioInvokeHistoryStorageKey(options: {
  scopeId?: string | null;
  memberKey?: string | null;
}): StudioInvokeHistoryStorageKey {
  const scopeId = trimText(options.scopeId);
  const memberKey = trimText(options.memberKey);
  if (!scopeId || !memberKey) {
    return '';
  }

  return `${STORAGE_PREFIX}${scopeId}::${memberKey}`;
}

export function loadStudioInvokeHistory<T>(
  storageKey: StudioInvokeHistoryStorageKey,
): T[] {
  if (!storageKey || typeof window === 'undefined') {
    return [];
  }

  try {
    const raw = window.sessionStorage.getItem(storageKey);
    if (!raw) {
      return [];
    }

    const parsed = JSON.parse(raw);
    if (!Array.isArray(parsed)) {
      return [];
    }

    return parsed.slice(0, MAX_HISTORY_ENTRIES) as T[];
  } catch {
    return [];
  }
}

export function saveStudioInvokeHistory<T>(
  storageKey: StudioInvokeHistoryStorageKey,
  entries: readonly T[],
): void {
  if (!storageKey || typeof window === 'undefined') {
    return;
  }

  try {
    if (entries.length === 0) {
      window.sessionStorage.removeItem(storageKey);
      return;
    }

    window.sessionStorage.setItem(
      storageKey,
      JSON.stringify(entries.slice(0, MAX_HISTORY_ENTRIES)),
    );
  } catch {
    // sessionStorage can throw on quota or privacy modes; history is
    // best-effort transcript closeout, not a source of truth.
  }
}

export const STUDIO_INVOKE_HISTORY_LIMIT = MAX_HISTORY_ENTRIES;
