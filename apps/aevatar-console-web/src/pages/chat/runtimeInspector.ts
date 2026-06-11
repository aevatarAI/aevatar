import type {
  WorkflowActorSnapshot,
  WorkflowActorTimelineItem,
} from "@/shared/models/runtime/actors";
import { t } from "@/shared/i18n/messages";

export type RuntimeTimelineBlockingSummary = {
  kind: "human_input" | "human_approval" | "wait_signal";
  prompt: string;
  signalName?: string;
  stage: string;
  stepId: string;
  summary: string;
  timeoutLabel?: string;
  timestamp: string;
  title: string;
};

const BLOCKING_TIMELINE_STAGES = new Set(["signal.waiting", "workflow.suspended"]);
const CLEARING_TIMELINE_STAGES = new Set([
  "signal.buffered",
  "workflow.resumed",
  "workflow.completed",
  "workflow.failed",
  "workflow.stopped",
]);

function trimOptional(value?: string): string | undefined {
  const normalized = value?.trim();
  return normalized ? normalized : undefined;
}

function parseTimelineTimestampMs(item: WorkflowActorTimelineItem): number {
  const timestamp = Date.parse(item.timestamp || "");
  return Number.isFinite(timestamp) ? timestamp : 0;
}

function normalizeTimelineStage(item: WorkflowActorTimelineItem): string {
  return (item.stage || item.eventType || "").trim().toLowerCase();
}

function findLatestBlockingTimelineItem(
  timeline: readonly WorkflowActorTimelineItem[]
): WorkflowActorTimelineItem | undefined {
  let latestBlocking: WorkflowActorTimelineItem | undefined;
  let latestBlockingMs = -1;
  let latestClearingMs = -1;

  for (const item of timeline) {
    const stage = normalizeTimelineStage(item);
    const stamp = parseTimelineTimestampMs(item);

    if (BLOCKING_TIMELINE_STAGES.has(stage) && stamp >= latestBlockingMs) {
      latestBlocking = item;
      latestBlockingMs = stamp;
    }

    if (CLEARING_TIMELINE_STAGES.has(stage) && stamp >= latestClearingMs) {
      latestClearingMs = stamp;
    }
  }

  if (!latestBlocking) {
    return undefined;
  }

  return latestClearingMs > latestBlockingMs ? undefined : latestBlocking;
}

function parseSuspensionType(item: WorkflowActorTimelineItem): string | undefined {
  return trimOptional(item.data.suspension_type) ||
    trimOptional(item.data.suspensionType) ||
    trimOptional(item.data.kind) ||
    trimOptional(item.message)
    ? (
        trimOptional(item.data.suspension_type) ||
        trimOptional(item.data.suspensionType) ||
        trimOptional(item.data.kind) ||
        trimOptional(item.message)
      )
    : undefined;
}

export function buildTimelineBlockingSummary(
  timeline: readonly WorkflowActorTimelineItem[]
): RuntimeTimelineBlockingSummary | undefined {
  const latestBlocking = findLatestBlockingTimelineItem(timeline);
  if (!latestBlocking) {
    return undefined;
  }

  const stage = normalizeTimelineStage(latestBlocking);
  const stepId = trimOptional(latestBlocking.stepId) || "runtime-gate";

  if (stage === "signal.waiting") {
    const signalName =
      trimOptional(latestBlocking.data.signal_name) ||
      trimOptional(latestBlocking.data.signalName) ||
      trimOptional(latestBlocking.message) ||
      "continue";
    const timeoutMs = Number(
      latestBlocking.data.timeout_ms || latestBlocking.data.timeoutMs || ""
    );

    return {
      kind: "wait_signal",
      prompt:
        trimOptional(latestBlocking.data.prompt) ||
        `Runtime is waiting for signal ${signalName} before ${stepId} can continue.`,
      signalName,
      stage,
      stepId,
      summary:
        t(
          "pages.chat.runtimeinspector.runtime.paused.external.signal.gate",
          "Runtime is paused at an external signal gate and cannot continue until the signal arrives.",
        ),
      timeoutLabel:
        Number.isFinite(timeoutMs) && timeoutMs > 0
          ? `Times out in ${Math.max(1, Math.round(timeoutMs / 1000))}s`
          : undefined,
      timestamp: latestBlocking.timestamp,
      title: t("pages.chat.runtimeinspector.waiting.for.signal.name", "Waiting for {signalName}", { signalName }),
    };
  }

  const suspensionType = (parseSuspensionType(latestBlocking) || "").toLowerCase();
  const isApproval =
    suspensionType.includes("approval") || suspensionType.includes("approve");
  const timeoutSeconds = Number(
    latestBlocking.data.timeout_seconds ||
      latestBlocking.data.timeoutSeconds ||
      ""
  );

  return {
    kind: isApproval ? "human_approval" : "human_input",
    prompt:
      trimOptional(latestBlocking.data.prompt) ||
      trimOptional(latestBlocking.data.reason) ||
      trimOptional(latestBlocking.data.variable_name) ||
      trimOptional(latestBlocking.data.variableName) ||
      `${stepId} needs operator input before runtime can continue.`,
    stage,
    stepId,
    summary: isApproval
      ? t(
          "pages.chat.runtimeinspector.runtime.paused.waiting.approval",
          "Runtime is paused and waiting for approval before it can enter the execution path.",
        )
      : t(
          "pages.chat.runtimeinspector.runtime.paused.waiting.operator.context",
          "Runtime is paused and waiting for additional operator context before it can continue.",
        ),
    timeoutLabel:
      Number.isFinite(timeoutSeconds) && timeoutSeconds > 0
        ? `Times out in ${Math.max(1, Math.round(timeoutSeconds))}s`
        : undefined,
    timestamp: latestBlocking.timestamp,
    title: isApproval
      ? t("pages.chat.runtimeinspector.waiting.for.approval", "Waiting for approval")
      : t("pages.chat.runtimeinspector.waiting.for.input", "Waiting for input"),
  };
}

export function describeActorCompletionStatus(
  snapshot: WorkflowActorSnapshot | null | undefined
): string {
  if (!snapshot) {
    return t("pages.chat.runtimeinspector.unavailable", "Unavailable");
  }

  switch (snapshot.completionStatusValue) {
    case 1:
      return t("pages.chat.runtimeinspector.completed", "Completed");
    case 3:
      return t("pages.chat.runtimeinspector.failed", "Failed");
    case 4:
      return t("pages.chat.runtimeinspector.stopped", "Stopped");
    default:
      return snapshot.lastSuccess === false
        ? t("pages.chat.runtimeinspector.error", "Error")
        : t("pages.chat.runtimeinspector.running", "Running");
  }
}
