import { t } from "@/shared/i18n/messages";
import type {
  MissionWallFocusReason,
  MissionWallLiveStatus,
  MissionWallPriorityLevel,
  MissionWallRun,
  MissionWallRunStatus,
  MissionWallStepStatus,
} from "./models";

export function formatDuration(ms?: number): string {
  if (ms === undefined || !Number.isFinite(ms)) {
    return "--";
  }

  const totalSeconds = Math.max(0, Math.round(ms / 1000));
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`;
}

export function formatLatency(ms?: number): string | undefined {
  if (ms === undefined || !Number.isFinite(ms) || ms <= 0) {
    return undefined;
  }

  if (ms < 1000) {
    return t("pages.missionwall.latencyMs", "{latency}ms", {
      latency: String(Math.round(ms)),
    });
  }

  return t("pages.missionwall.latencySeconds", "{latency}s", {
    latency: (ms / 1000).toFixed(1),
  });
}

export function formatLiveStatus(status: MissionWallLiveStatus): string {
  switch (status) {
    case "live":
      return t("pages.missionwall.liveStatus.live", "On");
    case "degraded":
      return t("pages.missionwall.liveStatus.degraded", "Degraded");
    case "disconnected":
      return t("pages.missionwall.liveStatus.disconnected", "Disconnected");
    case "idle":
      return t("pages.missionwall.liveStatus.idle", "Idle");
    default:
      return t("pages.missionwall.liveStatus.unknown", "Unknown");
  }
}

export function formatRunStatus(status: MissionWallRunStatus): string {
  switch (status) {
    case "running":
      return t("pages.missionwall.status.running", "LIVE");
    case "completed":
      return t("pages.missionwall.status.completed", "DONE");
    case "waiting":
      return t("pages.missionwall.status.waiting", "WAIT");
    case "failed":
      return t("pages.missionwall.status.failed", "FAILED");
    case "timed_out":
      return t("pages.missionwall.status.timedOut", "TIMEOUT");
    case "retrying":
      return t("pages.missionwall.status.retrying", "RETRY");
    case "stale":
      return t("pages.missionwall.status.stale", "STALE");
    case "stopped":
      return t("pages.missionwall.status.stopped", "STOP");
    case "unknown":
      return t("pages.missionwall.status.published", "PUBLISHED");
    default:
      return t("pages.missionwall.status.unknown", "UNKNOWN");
  }
}

export function formatStepStatus(status: MissionWallStepStatus): string {
  switch (status) {
    case "active":
      return t("pages.missionwall.stepStatus.active", "ACTIVE");
    case "completed":
      return t("pages.missionwall.stepStatus.completed", "COMPLETED");
    case "failed":
      return t("pages.missionwall.stepStatus.failed", "FAILED");
    case "retrying":
      return t("pages.missionwall.stepStatus.retrying", "RETRYING");
    case "waiting":
      return t("pages.missionwall.stepStatus.waiting", "WAITING");
    case "idle":
      return t("pages.missionwall.stepStatus.idle", "NEXT");
    default:
      return t("pages.missionwall.stepStatus.unknown", "UNKNOWN");
  }
}

export function formatFocusReason(reason?: MissionWallFocusReason): string {
  if (!reason) {
    return t("pages.missionwall.focusReason.none", "No focus");
  }

  switch (reason) {
    case "failed":
      return t("pages.missionwall.focusReason.failed", "failed");
    case "timed_out":
      return t("pages.missionwall.focusReason.timedOut", "timed out");
    case "waiting_human":
      return t(
        "pages.missionwall.focusReason.waitingHuman",
        "waiting approval",
      );
    case "stale_projection":
      return t("pages.missionwall.focusReason.staleProjection", "stale");
    case "stale_live":
      return t("pages.missionwall.focusReason.staleLive", "stale");
    case "retrying":
      return t("pages.missionwall.focusReason.retrying", "retrying");
    case "latest_running":
      return t("pages.missionwall.focusReason.latestRunning", "latest running");
    case "recently_completed":
      return t(
        "pages.missionwall.focusReason.recentlyCompleted",
        "recently completed",
      );
    default:
      return t("pages.missionwall.focusReason.unknown", "unknown");
  }
}

export function priorityTone(
  priorityLevel: MissionWallPriorityLevel,
  status?: MissionWallRunStatus,
): "blue" | "green" | "grey" | "red" | "teal" | "yellow" {
  if (priorityLevel === "error") {
    return "red";
  }

  if (priorityLevel === "warning") {
    return status === "retrying" ? "red" : "yellow";
  }

  if (status === "completed") {
    return "green";
  }

  if (status === "running") {
    return "blue";
  }

  if (status === "unknown") {
    return "teal";
  }

  return "grey";
}

export function stepTone(
  status: MissionWallStepStatus,
): "blue" | "green" | "grey" | "red" | "teal" | "yellow" {
  switch (status) {
    case "active":
      return "teal";
    case "completed":
      return "green";
    case "failed":
      return "red";
    case "retrying":
    case "waiting":
      return "yellow";
    default:
      return "grey";
  }
}

export function formatRunStage(run: MissionWallRun): string {
  const step = run.currentStepLabel || run.currentStepId;

  if (run.hasRuntimeRun === false || run.status === "unknown") {
    return t("pages.missionwall.runtimeData.noRuntimeRun", "No visible run");
  }

  if (run.status === "failed") {
    return step
      ? t("pages.missionwall.runStage.failedAtStep", "{step} failed", {
          step,
        })
      : formatRunStatus(run.status);
  }

  if (run.status === "timed_out") {
    return step
      ? t("pages.missionwall.runStage.timedOutAtStep", "{step} timed out", {
          step,
        })
      : formatRunStatus(run.status);
  }

  if (run.status === "waiting") {
    return step
      ? t("pages.missionwall.runStage.waitingAtStep", "Waiting at {step}", {
          step,
        })
      : formatRunStatus(run.status);
  }

  if (run.status === "retrying") {
    return step
      ? t("pages.missionwall.runStage.retryingAtStep", "Retrying {step}", {
          step,
        })
      : formatRunStatus(run.status);
  }

  if (run.status === "running") {
    return step
      ? t("pages.missionwall.runStage.runningAtStep", "Running {step}", {
          step,
        })
      : formatRunStatus(run.status);
  }

  if (run.status === "stale") {
    return step
      ? t("pages.missionwall.runStage.staleAtStep", "Stale at {step}", {
          step,
        })
      : formatRunStatus(run.status);
  }

  return formatRunStatus(run.status);
}
