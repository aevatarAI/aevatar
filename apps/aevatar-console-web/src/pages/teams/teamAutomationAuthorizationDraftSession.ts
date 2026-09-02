import type { TeamAutomationRoute } from "@/shared/api/teamAutomationApi";

const namespace = "aevatar.teamAutomationAuthorizationDraft.v1";
const ttlMs = 15 * 60 * 1_000;

export type TeamAutomationAuthorizationDraftInput = TeamAutomationRoute & {
  readonly mode: "create" | "reauthorize";
  readonly scheduleId?: string;
  readonly displayName: string;
  readonly prompt: string;
  readonly scheduleCron: string;
  readonly scheduleTimezone: string;
  readonly enabled: boolean;
};

type PersistedDraft = TeamAutomationAuthorizationDraftInput & {
  readonly schemaVersion: 1;
  readonly savedAt: number;
  readonly expiresAt: number;
};

const persistedKeys = [
  "schemaVersion",
  "scopeId",
  "teamId",
  "memberId",
  "mode",
  "scheduleId",
  "displayName",
  "prompt",
  "scheduleCron",
  "scheduleTimezone",
  "enabled",
  "savedAt",
  "expiresAt",
] as const;

function normalizeRequired(value: string): string {
  const normalized = value.trim();
  if (!normalized || normalized.length > 4_000 || /[\u0000-\u001f\u007f]/.test(normalized)) {
    throw new Error("Team automation recovery draft contains an invalid field.");
  }
  return normalized;
}

function key(route: TeamAutomationRoute): string {
  return [
    namespace,
    encodeURIComponent(normalizeRequired(route.scopeId)),
    encodeURIComponent(normalizeRequired(route.teamId)),
    encodeURIComponent(normalizeRequired(route.memberId)),
  ].join(":");
}

function normalizeDraft(input: TeamAutomationAuthorizationDraftInput): TeamAutomationAuthorizationDraftInput {
  const scheduleId = input.scheduleId?.trim();
  if (input.mode === "reauthorize" && !scheduleId) {
    throw new Error("Reauthorization recovery requires a schedule ID.");
  }
  return {
    scopeId: normalizeRequired(input.scopeId),
    teamId: normalizeRequired(input.teamId),
    memberId: normalizeRequired(input.memberId),
    mode: input.mode,
    scheduleId: scheduleId || undefined,
    displayName: input.displayName.trim(),
    prompt: input.prompt.trim(),
    scheduleCron: normalizeRequired(input.scheduleCron),
    scheduleTimezone: normalizeRequired(input.scheduleTimezone),
    enabled: input.enabled,
  };
}

function parse(raw: string): PersistedDraft | null {
  try {
    const value: unknown = JSON.parse(raw);
    if (!value || typeof value !== "object" || Array.isArray(value)) {
      return null;
    }
    const record = value as Record<string, unknown>;
    const actualKeys = Object.keys(record).sort();
    const expectedKeys = persistedKeys
      .filter((candidate) => candidate !== "scheduleId" || "scheduleId" in record)
      .sort();
    if (
      actualKeys.length !== expectedKeys.length ||
      actualKeys.some((candidate, index) => candidate !== expectedKeys[index]) ||
      record.schemaVersion !== 1 ||
      (record.mode !== "create" && record.mode !== "reauthorize") ||
      typeof record.enabled !== "boolean" ||
      typeof record.savedAt !== "number" ||
      typeof record.expiresAt !== "number" ||
      record.expiresAt - record.savedAt !== ttlMs
    ) {
      return null;
    }
    return {
      ...normalizeDraft(record as unknown as TeamAutomationAuthorizationDraftInput),
      schemaVersion: 1,
      savedAt: record.savedAt,
      expiresAt: record.expiresAt,
    };
  } catch {
    return null;
  }
}

export function saveTeamAutomationAuthorizationDraft(
  storage: Storage,
  input: TeamAutomationAuthorizationDraftInput,
  now = Date.now(),
): void {
  const draft = normalizeDraft(input);
  const persisted: PersistedDraft = {
    schemaVersion: 1,
    ...draft,
    savedAt: now,
    expiresAt: now + ttlMs,
  };
  storage.setItem(key(draft), JSON.stringify(persisted));
}

export function consumeTeamAutomationAuthorizationDraft(
  storage: Storage,
  route: TeamAutomationRoute,
  now = Date.now(),
): TeamAutomationAuthorizationDraftInput | null {
  const expectedKey = key(route);
  let matched: TeamAutomationAuthorizationDraftInput | null = null;
  const draftKeys = Array.from({ length: storage.length }, (_, index) => storage.key(index))
    .filter((candidate): candidate is string => Boolean(candidate?.startsWith(`${namespace}:`)));

  for (const candidate of draftKeys) {
    const raw = storage.getItem(candidate);
    storage.removeItem(candidate);
    const parsed = raw ? parse(raw) : null;
    if (
      candidate !== expectedKey ||
      !parsed ||
      parsed.savedAt > now ||
      parsed.expiresAt < now ||
      key(parsed) !== expectedKey
    ) {
      continue;
    }
    const { schemaVersion: _schemaVersion, savedAt: _savedAt, expiresAt: _expiresAt, ...draft } = parsed;
    matched = draft;
  }

  return matched;
}
