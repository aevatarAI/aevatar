import type { StudioTeamSummary } from "@/shared/studio/models";

const pendingTeamRosterStorageKey = "aevatar:teams:pending-roster:v1";
const pendingTeamRosterTtlMs = 10 * 60 * 1000;

type PendingTeamRosterEntry = {
  readonly storedAt: number;
  readonly team: StudioTeamSummary;
};

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function canUseSessionStorage(): boolean {
  return typeof window !== "undefined" && Boolean(window.sessionStorage);
}

function isValidTeamSummary(value: unknown): value is StudioTeamSummary {
  if (!value || typeof value !== "object") {
    return false;
  }

  const record = value as Partial<StudioTeamSummary>;
  return Boolean(
    trimOptional(record.scopeId) &&
      trimOptional(record.teamId) &&
      trimOptional(record.displayName),
  );
}

function readPendingEntries(now = Date.now()): PendingTeamRosterEntry[] {
  if (!canUseSessionStorage()) {
    return [];
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(
      window.sessionStorage.getItem(pendingTeamRosterStorageKey) || "[]",
    );
  } catch {
    window.sessionStorage.removeItem(pendingTeamRosterStorageKey);
    return [];
  }

  if (!Array.isArray(parsed)) {
    window.sessionStorage.removeItem(pendingTeamRosterStorageKey);
    return [];
  }

  const entries = parsed.filter((entry): entry is PendingTeamRosterEntry => {
    if (!entry || typeof entry !== "object") {
      return false;
    }

    const candidate = entry as Partial<PendingTeamRosterEntry>;
    return (
      typeof candidate.storedAt === "number" &&
      now - candidate.storedAt <= pendingTeamRosterTtlMs &&
      isValidTeamSummary(candidate.team)
    );
  });

  if (entries.length !== parsed.length) {
    writePendingEntries(entries);
  }

  return entries;
}

function writePendingEntries(entries: readonly PendingTeamRosterEntry[]): void {
  if (!canUseSessionStorage()) {
    return;
  }

  if (entries.length === 0) {
    window.sessionStorage.removeItem(pendingTeamRosterStorageKey);
    return;
  }

  window.sessionStorage.setItem(
    pendingTeamRosterStorageKey,
    JSON.stringify(entries),
  );
}

export function rememberPendingTeamRosterSummary(team: StudioTeamSummary): void {
  const scopeId = trimOptional(team.scopeId);
  const teamId = trimOptional(team.teamId);
  if (!scopeId || !teamId) {
    return;
  }

  const existing = readPendingEntries();
  const nextEntry: PendingTeamRosterEntry = {
    storedAt: Date.now(),
    team,
  };
  writePendingEntries([
    nextEntry,
    ...existing.filter(
      (entry) =>
        trimOptional(entry.team.scopeId) !== scopeId ||
        trimOptional(entry.team.teamId) !== teamId,
    ),
  ]);
}

export function mergePendingTeamRosterSummaries(
  scopeId: string,
  teams: readonly StudioTeamSummary[],
): readonly StudioTeamSummary[] {
  const normalizedScopeId = trimOptional(scopeId);
  if (!normalizedScopeId) {
    return teams;
  }

  const seen = new Set(
    teams.map((team) => trimOptional(team.teamId)).filter(Boolean),
  );
  const pendingTeams = readPendingEntries()
    .map((entry) => entry.team)
    .filter(
      (team) =>
        trimOptional(team.scopeId) === normalizedScopeId &&
        !seen.has(trimOptional(team.teamId)),
    );

  if (pendingTeams.length === 0) {
    return teams;
  }

  return [...teams, ...pendingTeams];
}

export function clearSyncedPendingTeamRosterSummaries(
  scopeId: string,
  teams: readonly StudioTeamSummary[],
): void {
  const normalizedScopeId = trimOptional(scopeId);
  if (!normalizedScopeId) {
    return;
  }

  const syncedTeamIds = new Set(
    teams.map((team) => trimOptional(team.teamId)).filter(Boolean),
  );
  if (syncedTeamIds.size === 0) {
    return;
  }

  const remaining = readPendingEntries().filter(
    (entry) =>
      trimOptional(entry.team.scopeId) !== normalizedScopeId ||
      !syncedTeamIds.has(trimOptional(entry.team.teamId)),
  );
  writePendingEntries(remaining);
}
