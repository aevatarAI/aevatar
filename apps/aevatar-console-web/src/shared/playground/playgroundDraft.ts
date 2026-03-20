export interface PlaygroundDraft {
  yaml: string;
  prompt: string;
  sourceWorkflow: string;
  updatedAt: string;
}

export const PLAYGROUND_DRAFT_UPDATED_EVENT = 'aevatar-playground-draft-updated';

const STORAGE_KEY = 'aevatar-console-playground-draft';

export const defaultPlaygroundDraft: PlaygroundDraft = {
  yaml: '',
  prompt: '',
  sourceWorkflow: '',
  updatedAt: '',
};

function sanitizeDraft(
  value: Partial<PlaygroundDraft> | null | undefined,
): PlaygroundDraft {
  return {
    yaml: typeof value?.yaml === 'string' ? value.yaml : '',
    prompt: typeof value?.prompt === 'string' ? value.prompt.trim() : '',
    sourceWorkflow:
      typeof value?.sourceWorkflow === 'string' ? value.sourceWorkflow.trim() : '',
    updatedAt: typeof value?.updatedAt === 'string' ? value.updatedAt : '',
  };
}

function notifyDraftUpdated(): void {
  if (typeof window !== 'undefined') {
    window.dispatchEvent(new Event(PLAYGROUND_DRAFT_UPDATED_EVENT));
  }
}

export function loadPlaygroundDraft(): PlaygroundDraft {
  if (typeof window === 'undefined') {
    return defaultPlaygroundDraft;
  }

  const raw = window.localStorage.getItem(STORAGE_KEY);
  if (!raw) {
    return defaultPlaygroundDraft;
  }

  try {
    return sanitizeDraft(JSON.parse(raw) as Partial<PlaygroundDraft>);
  } catch {
    return defaultPlaygroundDraft;
  }
}

export function savePlaygroundDraft(
  value: Partial<PlaygroundDraft>,
): PlaygroundDraft {
  const previous = loadPlaygroundDraft();
  const sanitized = sanitizeDraft({
    ...previous,
    ...value,
    updatedAt: new Date().toISOString(),
  });

  if (typeof window !== 'undefined') {
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(sanitized));
    notifyDraftUpdated();
  }

  return sanitized;
}

export function resetPlaygroundDraft(): PlaygroundDraft {
  if (typeof window !== 'undefined') {
    window.localStorage.removeItem(STORAGE_KEY);
    notifyDraftUpdated();
  }

  return defaultPlaygroundDraft;
}
