import type { RuntimeEvent } from '@/shared/agui/runtimeEventSemantics';

export type StudioObserveSessionSeed = {
  readonly actorId: string;
  readonly assistantText: string;
  readonly commandId: string;
  readonly completedAtUtc: string | null;
  readonly endpointId: string;
  readonly error: string;
  readonly events: RuntimeEvent[];
  readonly finalOutput: string;
  readonly mode: 'stream' | 'invoke';
  readonly payloadBase64: string;
  readonly payloadTypeUrl: string;
  readonly prompt: string;
  readonly runId: string;
  readonly serviceId: string;
  readonly serviceLabel: string;
  readonly startedAtUtc: string;
  readonly status: 'running' | 'success' | 'error';
};

const STORAGE_PREFIX = 'aevatar-console:studio:observe-session:';

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? '';
}

function buildStorageKey(scopeId: string, serviceId: string): string {
  return `${STORAGE_PREFIX}${scopeId}::${serviceId}`;
}

export function isStudioObserveSessionSeedFresh(
  seed: StudioObserveSessionSeed | null | undefined,
  maxAgeMs = 5 * 60 * 1000,
): boolean {
  if (!seed) {
    return false;
  }

  const timestamp =
    Date.parse(trimOptional(seed.completedAtUtc) || trimOptional(seed.startedAtUtc));
  return Number.isFinite(timestamp) && Date.now() - timestamp <= maxAgeMs;
}

export function saveStudioObserveSessionSeed(input: {
  scopeId: string;
  session: StudioObserveSessionSeed;
}): void {
  if (typeof window === 'undefined') {
    return;
  }

  const scopeId = trimOptional(input.scopeId);
  const serviceId = trimOptional(input.session.serviceId);
  if (!scopeId || !serviceId) {
    return;
  }

  window.sessionStorage.setItem(
    buildStorageKey(scopeId, serviceId),
    JSON.stringify(input.session),
  );
}

export function loadStudioObserveSessionSeed(input: {
  scopeId: string;
  serviceId: string;
}): StudioObserveSessionSeed | null {
  if (typeof window === 'undefined') {
    return null;
  }

  const scopeId = trimOptional(input.scopeId);
  const serviceId = trimOptional(input.serviceId);
  if (!scopeId || !serviceId) {
    return null;
  }

  const raw = window.sessionStorage.getItem(buildStorageKey(scopeId, serviceId));
  if (!raw) {
    return null;
  }

  try {
    const parsed = JSON.parse(raw) as Partial<StudioObserveSessionSeed>;
    const normalizedSession: StudioObserveSessionSeed = {
      actorId: trimOptional(parsed.actorId),
      assistantText: trimOptional(parsed.assistantText),
      commandId: trimOptional(parsed.commandId),
      completedAtUtc: trimOptional(parsed.completedAtUtc) || null,
      endpointId: trimOptional(parsed.endpointId),
      error: trimOptional(parsed.error),
      events: Array.isArray(parsed.events) ? (parsed.events as RuntimeEvent[]) : [],
      finalOutput: trimOptional(parsed.finalOutput),
      mode: parsed.mode === 'invoke' ? 'invoke' : 'stream',
      payloadBase64: trimOptional(parsed.payloadBase64),
      payloadTypeUrl: trimOptional(parsed.payloadTypeUrl),
      prompt: trimOptional(parsed.prompt),
      runId: trimOptional(parsed.runId),
      serviceId: trimOptional(parsed.serviceId),
      serviceLabel: trimOptional(parsed.serviceLabel),
      startedAtUtc: trimOptional(parsed.startedAtUtc),
      status:
        parsed.status === 'error'
          ? 'error'
          : parsed.status === 'success'
            ? 'success'
            : 'running',
    };

    return normalizedSession.endpointId &&
      normalizedSession.serviceId &&
      normalizedSession.startedAtUtc
      ? normalizedSession
      : null;
  } catch {
    return null;
  }
}

export function clearStudioObserveSessionSeed(input: {
  scopeId: string;
  serviceId: string;
}): void {
  if (typeof window === 'undefined') {
    return;
  }

  const scopeId = trimOptional(input.scopeId);
  const serviceId = trimOptional(input.serviceId);
  if (!scopeId || !serviceId) {
    return;
  }

  window.sessionStorage.removeItem(buildStorageKey(scopeId, serviceId));
}
