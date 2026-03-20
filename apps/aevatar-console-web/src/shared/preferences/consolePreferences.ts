export type ActorGraphDirection = 'Both' | 'Outbound' | 'Inbound';
export type StudioAppearanceTheme = 'blue' | 'coral' | 'forest';
export type StudioColorMode = 'light' | 'dark';

export interface ConsolePreferences {
  preferredWorkflow: string;
  actorTimelineTake: number;
  actorGraphDepth: number;
  actorGraphTake: number;
  actorGraphDirection: ActorGraphDirection;
  studioAppearanceTheme: StudioAppearanceTheme;
  studioColorMode: StudioColorMode;
  grafanaBaseUrl: string;
  jaegerBaseUrl: string;
  lokiBaseUrl: string;
}

export const CONSOLE_PREFERENCES_UPDATED_EVENT =
  'aevatar-console-preferences-updated';

const STORAGE_KEY = 'aevatar-console-preferences';

export const defaultConsolePreferences: ConsolePreferences = {
  preferredWorkflow: 'direct',
  actorTimelineTake: 50,
  actorGraphDepth: 3,
  actorGraphTake: 100,
  actorGraphDirection: 'Both',
  studioAppearanceTheme: 'blue',
  studioColorMode: 'light',
  grafanaBaseUrl: '',
  jaegerBaseUrl: '',
  lokiBaseUrl: '',
};

function parsePositiveInt(value: unknown, fallback: number): number {
  if (typeof value !== 'number' || Number.isNaN(value) || value <= 0) {
    return fallback;
  }

  return Math.floor(value);
}

function parseDirection(value: unknown): ActorGraphDirection {
  if (value === 'Inbound' || value === 'Outbound' || value === 'Both') {
    return value;
  }

  return defaultConsolePreferences.actorGraphDirection;
}

function parseStudioAppearanceTheme(value: unknown): StudioAppearanceTheme {
  if (value === 'coral' || value === 'forest' || value === 'blue') {
    return value;
  }

  return defaultConsolePreferences.studioAppearanceTheme;
}

function parseStudioColorMode(value: unknown): StudioColorMode {
  return value === 'dark' ? 'dark' : defaultConsolePreferences.studioColorMode;
}

function sanitizePreferences(
  value: Partial<ConsolePreferences> | null | undefined,
): ConsolePreferences {
  return {
    preferredWorkflow:
      typeof value?.preferredWorkflow === 'string' &&
      value.preferredWorkflow.trim().length > 0
        ? value.preferredWorkflow.trim()
        : defaultConsolePreferences.preferredWorkflow,
    actorTimelineTake: parsePositiveInt(
      value?.actorTimelineTake,
      defaultConsolePreferences.actorTimelineTake,
    ),
    actorGraphDepth: parsePositiveInt(
      value?.actorGraphDepth,
      defaultConsolePreferences.actorGraphDepth,
    ),
    actorGraphTake: parsePositiveInt(
      value?.actorGraphTake,
      defaultConsolePreferences.actorGraphTake,
    ),
    actorGraphDirection: parseDirection(value?.actorGraphDirection),
    studioAppearanceTheme: parseStudioAppearanceTheme(
      value?.studioAppearanceTheme,
    ),
    studioColorMode: parseStudioColorMode(value?.studioColorMode),
    grafanaBaseUrl:
      typeof value?.grafanaBaseUrl === 'string' ? value.grafanaBaseUrl.trim() : '',
    jaegerBaseUrl:
      typeof value?.jaegerBaseUrl === 'string' ? value.jaegerBaseUrl.trim() : '',
    lokiBaseUrl:
      typeof value?.lokiBaseUrl === 'string' ? value.lokiBaseUrl.trim() : '',
  };
}

export function loadConsolePreferences(): ConsolePreferences {
  if (typeof window === 'undefined') {
    return defaultConsolePreferences;
  }

  const raw = window.localStorage.getItem(STORAGE_KEY);
  if (!raw) {
    return defaultConsolePreferences;
  }

  try {
    return sanitizePreferences(JSON.parse(raw) as Partial<ConsolePreferences>);
  } catch {
    return defaultConsolePreferences;
  }
}

export function saveConsolePreferences(value: ConsolePreferences): ConsolePreferences {
  const sanitized = sanitizePreferences(value);
  if (typeof window !== 'undefined') {
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(sanitized));
    window.dispatchEvent(new Event(CONSOLE_PREFERENCES_UPDATED_EVENT));
  }

  return sanitized;
}

export function resetConsolePreferences(): ConsolePreferences {
  if (typeof window !== 'undefined') {
    window.localStorage.removeItem(STORAGE_KEY);
    window.dispatchEvent(new Event(CONSOLE_PREFERENCES_UPDATED_EVENT));
  }

  return defaultConsolePreferences;
}
