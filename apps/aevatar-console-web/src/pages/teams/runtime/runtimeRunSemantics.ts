import type { ScopeServiceRunSummary } from "@/shared/models/runtime/scopeServices";

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function parseRunTimestamp(value: string | null | undefined): number {
  const parsed = Date.parse(value || "");
  return Number.isFinite(parsed) ? parsed : 0;
}

function normalizeTeamRunStatus(value: string | null | undefined): string {
  return trimOptional(value).toLowerCase();
}

export function compareTeamRuns(
  left: ScopeServiceRunSummary,
  right: ScopeServiceRunSummary,
): number {
  const rightTime = parseRunTimestamp(right.lastUpdatedAt);
  const leftTime = parseRunTimestamp(left.lastUpdatedAt);
  if (rightTime !== leftTime) {
    return rightTime - leftTime;
  }

  if (right.stateVersion !== left.stateVersion) {
    return right.stateVersion - left.stateVersion;
  }

  return right.runId.localeCompare(left.runId);
}

export function selectLatestTeamRun(
  runs: readonly ScopeServiceRunSummary[],
  options?: {
    readonly preferredRunId?: string | null;
  },
): ScopeServiceRunSummary | null {
  const sortedRuns = [...runs].sort(compareTeamRuns);
  const preferredRunId = trimOptional(options?.preferredRunId);
  return (
    (preferredRunId
      ? sortedRuns.find((run) => trimOptional(run.runId) === preferredRunId) ?? null
      : null) ||
    sortedRuns[0] ||
    null
  );
}

export function isSuccessfulTeamRun(
  run: ScopeServiceRunSummary | null | undefined,
): boolean {
  if (!run) {
    return false;
  }

  if (run.lastSuccess === true) {
    return true;
  }

  return ["completed", "finished", "success", "succeeded"].includes(
    normalizeTeamRunStatus(run.completionStatus),
  );
}

export function isWaitingTeamRun(
  run: ScopeServiceRunSummary | null | undefined,
): boolean {
  if (!run) {
    return false;
  }

  return [
    "waiting",
    "waiting_approval",
    "waiting_signal",
    "blocked",
    "human_approval",
    "human_input",
    "suspended",
  ].includes(normalizeTeamRunStatus(run.completionStatus));
}

export function isFailedTeamRun(
  run: ScopeServiceRunSummary | null | undefined,
): boolean {
  if (!run) {
    return false;
  }

  if (isWaitingTeamRun(run)) {
    return false;
  }

  if (run.lastSuccess === false) {
    return true;
  }

  return ["failed", "error", "stopped", "timed_out", "timedout"].includes(
    normalizeTeamRunStatus(run.completionStatus),
  );
}

export function formatTeamRunStatusLabel(
  status: string | null | undefined,
): string {
  switch (normalizeTeamRunStatus(status)) {
    case "waiting":
    case "waiting_approval":
    case "waiting_signal":
      return "待关注";
    case "failed":
    case "error":
      return "异常";
    case "completed":
      return "已完成";
    default:
      return trimOptional(status) || "未知";
  }
}
