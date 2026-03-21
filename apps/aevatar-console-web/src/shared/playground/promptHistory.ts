export interface PlaygroundPromptHistoryEntry {
  id: string;
  prompt: string;
  workflowName: string;
  updatedAt: string;
}

const STORAGE_KEY = 'aevatar-console-playground-prompt-history';
const MAX_ENTRIES = 8;

function sanitizeEntry(
  value: Partial<PlaygroundPromptHistoryEntry> | null | undefined,
): PlaygroundPromptHistoryEntry | undefined {
  const prompt = typeof value?.prompt === 'string' ? value.prompt.trim() : '';
  if (!prompt) {
    return undefined;
  }

  const workflowName =
    typeof value?.workflowName === 'string' ? value.workflowName.trim() : '';
  const updatedAt =
    typeof value?.updatedAt === 'string' && value.updatedAt
      ? value.updatedAt
      : new Date(0).toISOString();

  return {
    id: typeof value?.id === 'string' && value.id ? value.id : `${workflowName}:${prompt}`,
    prompt,
    workflowName,
    updatedAt,
  };
}

export function loadPlaygroundPromptHistory(): PlaygroundPromptHistoryEntry[] {
  if (typeof window === 'undefined') {
    return [];
  }

  const raw = window.localStorage.getItem(STORAGE_KEY);
  if (!raw) {
    return [];
  }

  try {
    const parsed = JSON.parse(raw) as Array<Partial<PlaygroundPromptHistoryEntry>>;
    return parsed
      .map((item) => sanitizeEntry(item))
      .filter((item): item is PlaygroundPromptHistoryEntry => Boolean(item))
      .slice(0, MAX_ENTRIES);
  } catch {
    return [];
  }
}

export function savePlaygroundPromptHistoryEntry(input: {
  prompt: string;
  workflowName?: string;
}): PlaygroundPromptHistoryEntry[] {
  const prompt = input.prompt.trim();
  if (!prompt || typeof window === 'undefined') {
    return loadPlaygroundPromptHistory();
  }

  const workflowName = input.workflowName?.trim() ?? '';
  const nextEntry: PlaygroundPromptHistoryEntry = {
    id: `${workflowName}:${prompt}`,
    prompt,
    workflowName,
    updatedAt: new Date().toISOString(),
  };

  const history = loadPlaygroundPromptHistory().filter(
    (entry) => !(entry.prompt === prompt && entry.workflowName === workflowName),
  );
  const next = [nextEntry, ...history].slice(0, MAX_ENTRIES);
  window.localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
  return next;
}

export function clearPlaygroundPromptHistory(): PlaygroundPromptHistoryEntry[] {
  if (typeof window !== 'undefined') {
    window.localStorage.removeItem(STORAGE_KEY);
  }

  return [];
}
