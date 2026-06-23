import {
  CheckCircleFilled,
  ClockCircleOutlined,
  CloseCircleFilled,
  CodeOutlined,
  ExclamationCircleFilled,
  LoadingOutlined,
  NodeIndexOutlined,
  ReloadOutlined,
} from "@ant-design/icons";
import { MarkerType, Position, type Edge, type Node } from "@xyflow/react";
import { useQuery } from "@tanstack/react-query";
import {
  Alert,
  Button,
  Empty,
  Space,
  Spin,
  Tabs,
  Tag,
  Tooltip,
  Typography,
} from "antd";
import React from "react";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { studioApi } from "@/shared/studio/api";
import { formatDateTime } from "@/shared/datetime/dateTime";
import type {
  ScopeMemberRunSummary,
  ScopeServiceRunAuditReport,
  ScopeServiceRunAuditStep,
  ScopeServiceRunAuditTimelineEvent,
} from "@/shared/models/runtime/scopeServices";
import { history } from "@/shared/navigation/history";
import { buildRuntimeRunsHref } from "@/shared/navigation/runtimeRoutes";
import { buildTeamMemberWorkflowStudioHref } from "@/shared/navigation/teamRoutes";
import GraphCanvas from "@/shared/graphs/GraphCanvas";
import type {
  StudioGraphEdgeData,
  StudioGraphNodeData,
} from "@/shared/studio/graph";
import { t } from "@/shared/i18n/messages";

type MemberPublishedRunsReplayProps = {
  readonly initialActorId?: string;
  readonly initialRunId?: string;
  readonly memberId: string;
  readonly scopeId: string;
  readonly teamId?: string;
};

type RunStatusTone = "default" | "processing" | "success" | "warning" | "error";

const memberPublishedRunsReplayCss = `
.member-published-runs-replay {
  background: #f7f8fa;
  color: #111827;
  display: grid;
  grid-template-columns: minmax(260px, 320px) minmax(0, 1fr);
  height: 100vh;
  min-height: 0;
  width: 100%;
}

.member-published-runs-replay__rail {
  background: #ffffff;
  border-right: 1px solid #e5e7eb;
  display: flex;
  flex-direction: column;
  min-height: 0;
  min-width: 0;
}

.member-published-runs-replay__rail-header {
  border-bottom: 1px solid #e5e7eb;
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 18px 18px 14px;
}

.member-published-runs-replay__rail-title {
  align-items: center;
  display: flex;
  gap: 8px;
  justify-content: space-between;
  min-width: 0;
}

.member-published-runs-replay__rail-title-main {
  align-items: center;
  display: flex;
  gap: 8px;
  min-width: 0;
}

.member-published-runs-replay__rail-tools {
  align-items: center;
  display: flex;
  gap: 8px;
  justify-content: space-between;
  min-width: 0;
}

.member-published-runs-replay__list {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 6px;
  min-height: 0;
  overflow: auto;
  padding: 12px;
}

.member-published-runs-replay__run {
  background: transparent;
  border: 1px solid transparent;
  border-radius: 8px;
  color: inherit;
  cursor: pointer;
  display: grid;
  gap: 4px;
  min-width: 0;
  padding: 10px 12px;
  text-align: left;
  transition: background 120ms ease, border-color 120ms ease, box-shadow 120ms ease;
  width: 100%;
}

.member-published-runs-replay__run:hover {
  background: #f3f4f6;
  border-color: #e5e7eb;
}

.member-published-runs-replay__run--selected {
  background: #eef6ff;
  border-color: #83b7ff;
  box-shadow: inset 3px 0 0 #1677ff;
}

.member-published-runs-replay__run-title {
  align-items: center;
  display: flex;
  gap: 8px;
  min-width: 0;
}

.member-published-runs-replay__stage {
  display: grid;
  grid-template-rows: auto minmax(280px, 1fr) minmax(220px, 34vh);
  min-height: 0;
  min-width: 0;
}

.member-published-runs-replay__stage-header {
  align-items: center;
  background: #ffffff;
  border-bottom: 1px solid #e5e7eb;
  display: flex;
  gap: 12px;
  justify-content: space-between;
  min-width: 0;
  padding: 14px 22px;
}

.member-published-runs-replay__stage-title {
  display: grid;
  gap: 4px;
  min-width: 0;
}

.member-published-runs-replay__stage-actions {
  align-items: center;
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  justify-content: flex-end;
}

.member-published-runs-replay__graph {
  background:
    linear-gradient(135deg, rgba(17, 24, 39, 0.035) 25%, transparent 25%) -8px 0 / 16px 16px,
    linear-gradient(225deg, rgba(17, 24, 39, 0.03) 25%, transparent 25%) -8px 0 / 16px 16px,
    #fbfbfc;
  min-height: 0;
  min-width: 0;
  padding: 18px;
}

.member-published-runs-replay__graph-inner {
  height: 100%;
  min-height: 0;
}

.member-published-runs-replay__details {
  background: #ffffff;
  border-top: 1px solid #e5e7eb;
  display: grid;
  grid-template-columns: minmax(220px, 300px) minmax(0, 1fr);
  min-height: 0;
  min-width: 0;
}

.member-published-runs-replay__logs {
  border-right: 1px solid #e5e7eb;
  display: flex;
  flex-direction: column;
  min-height: 0;
  min-width: 0;
}

.member-published-runs-replay__logs-header,
.member-published-runs-replay__inspector-header {
  align-items: center;
  border-bottom: 1px solid #edf0f3;
  display: flex;
  gap: 8px;
  justify-content: space-between;
  min-width: 0;
  padding: 10px 14px;
}

.member-published-runs-replay__step-list {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 4px;
  min-height: 0;
  overflow: auto;
  padding: 8px;
}

.member-published-runs-replay__step {
  align-items: center;
  background: transparent;
  border: 1px solid transparent;
  border-radius: 8px;
  color: inherit;
  cursor: pointer;
  display: grid;
  gap: 8px;
  grid-template-columns: 18px minmax(0, 1fr) auto;
  min-width: 0;
  padding: 8px 10px;
  text-align: left;
  width: 100%;
}

.member-published-runs-replay__step:hover {
  background: #f8fafc;
}

.member-published-runs-replay__step--selected {
  background: #eef6ff;
  border-color: #bfdbfe;
}

.member-published-runs-replay__inspector {
  display: flex;
  flex-direction: column;
  min-height: 0;
  min-width: 0;
}

.member-published-runs-replay__inspector-body {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 12px 14px 16px;
}

.member-published-runs-replay__kv {
  border: 1px solid #e5e7eb;
  border-radius: 8px;
  overflow: hidden;
}

.member-published-runs-replay__kv-row {
  display: grid;
  grid-template-columns: minmax(120px, 30%) minmax(0, 1fr);
  min-width: 0;
}

.member-published-runs-replay__kv-row + .member-published-runs-replay__kv-row {
  border-top: 1px solid #edf0f3;
}

.member-published-runs-replay__kv-key,
.member-published-runs-replay__kv-value {
  min-width: 0;
  overflow-wrap: anywhere;
  padding: 8px 10px;
}

.member-published-runs-replay__kv-key {
  background: #f8fafc;
  color: #475467;
  font-weight: 600;
}

.member-published-runs-replay__pre {
  background: #0f172a;
  border-radius: 8px;
  color: #dbeafe;
  font-size: 12px;
  line-height: 1.55;
  margin: 0;
  overflow: auto;
  padding: 12px;
  white-space: pre-wrap;
  word-break: break-word;
}

.member-published-runs-replay__empty {
  align-items: center;
  display: flex;
  height: 100%;
  justify-content: center;
  min-height: 180px;
}

@media (max-width: 900px) {
  .member-published-runs-replay {
    grid-template-columns: 1fr;
    grid-template-rows: minmax(180px, 34vh) minmax(0, 1fr);
  }

  .member-published-runs-replay__rail {
    border-bottom: 1px solid #e5e7eb;
    border-right: none;
  }

  .member-published-runs-replay__stage {
    grid-template-rows: auto minmax(260px, 1fr) minmax(220px, 42vh);
  }

  .member-published-runs-replay__details {
    grid-template-columns: 1fr;
    grid-template-rows: minmax(130px, 36%) minmax(0, 1fr);
  }

  .member-published-runs-replay__logs {
    border-bottom: 1px solid #e5e7eb;
    border-right: none;
  }
}
`;

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function normalizeRunStatus(status: string | null | undefined): string {
  const normalized = trimOptional(status).toLowerCase().replace(/[\s-]+/g, "_");
  return normalized || "unknown";
}

function getRunStatusTone(status: string | null | undefined): RunStatusTone {
  switch (normalizeRunStatus(status)) {
    case "completed":
      return "success";
    case "failed":
    case "timed_out":
    case "not_found":
      return "error";
    case "running":
      return "processing";
    case "stopped":
    case "disabled":
      return "warning";
    default:
      return "default";
  }
}

function formatRunStatus(status: string | null | undefined): string {
  const normalized = normalizeRunStatus(status);
  if (normalized === "timed_out") {
    return "Timed out";
  }

  if (normalized === "not_found") {
    return "Not found";
  }

  return normalized
    .split("_")
    .filter(Boolean)
    .map((segment) => `${segment.charAt(0).toUpperCase()}${segment.slice(1)}`)
    .join(" ");
}

function formatDurationMs(value: number | null | undefined): string {
  if (typeof value !== "number" || !Number.isFinite(value) || value < 0) {
    return "n/a";
  }

  if (value < 1000) {
    return `${Math.round(value)}ms`;
  }

  if (value < 60_000) {
    return `${(value / 1000).toFixed(value < 10_000 ? 2 : 1)}s`;
  }

  const minutes = Math.floor(value / 60_000);
  const seconds = Math.round((value % 60_000) / 1000);
  return `${minutes}m ${seconds}s`;
}

function getElapsedMs(
  startedAt: string | null | undefined,
  endedAt: string | null | undefined,
): number | null {
  const started = Date.parse(trimOptional(startedAt));
  const ended = Date.parse(trimOptional(endedAt));
  if (!Number.isFinite(started) || !Number.isFinite(ended) || ended <= started) {
    return null;
  }

  return ended - started;
}

function getStepSpanDurationMs(
  steps: readonly ScopeServiceRunAuditStep[],
): number | null {
  const timestamps = steps.flatMap((step) => {
    const requested = Date.parse(trimOptional(step.requestedAt));
    const completed = Date.parse(trimOptional(step.completedAt));
    return [requested, completed].filter(Number.isFinite);
  });
  if (timestamps.length < 2) {
    return null;
  }

  const started = Math.min(...timestamps);
  const ended = Math.max(...timestamps);
  return ended > started ? ended - started : null;
}

function getStepTotalDurationMs(
  steps: readonly ScopeServiceRunAuditStep[],
): number | null {
  const durations = steps
    .map((step) => step.durationMs)
    .filter(
      (value): value is number =>
        typeof value === "number" && Number.isFinite(value) && value >= 0,
    );
  if (!durations.length) {
    return null;
  }

  const total = durations.reduce((sum, duration) => sum + duration, 0);
  return total > 0 ? total : null;
}

function getAuditDurationMs(
  audit: ScopeServiceRunAuditReport | null | undefined,
): number | null {
  if (!audit) {
    return null;
  }

  if (
    typeof audit.durationMs === "number" &&
    Number.isFinite(audit.durationMs) &&
    audit.durationMs > 0
  ) {
    return audit.durationMs;
  }

  return (
    getElapsedMs(audit.startedAt, audit.endedAt) ??
    getStepSpanDurationMs(audit.steps) ??
    getStepTotalDurationMs(audit.steps)
  );
}

function formatRunTime(run: ScopeMemberRunSummary): string {
  return formatDateTime(run.lastUpdatedAt || run.boundAt || run.bindingUpdatedAt);
}

function getRunSortTimestamp(run: ScopeMemberRunSummary): number {
  return (
    Date.parse(
      trimOptional(run.lastUpdatedAt) ||
        trimOptional(run.boundAt) ||
        trimOptional(run.bindingUpdatedAt),
    ) || 0
  );
}

function createFallbackRunSummary(input: {
  readonly actorId: string;
  readonly memberId: string;
  readonly runId: string;
  readonly scopeId: string;
}): ScopeMemberRunSummary {
  return {
    actorId: input.actorId,
    bindingUpdatedAt: null,
    boundAt: null,
    completedSteps: 0,
    completionStatus: "unknown",
    definitionActorId: "",
    deploymentId: "",
    lastError: "",
    lastEventId: "",
    lastOutput: "",
    lastSuccess: null,
    lastUpdatedAt: null,
    memberId: input.memberId,
    publishedServiceId: "",
    revisionId: "",
    roleReplyCount: 0,
    runId: input.runId,
    scopeId: input.scopeId,
    stateVersion: 0,
    totalSteps: 0,
    workflowName: "Current published run",
  };
}

function stepSortTimestamp(step: ScopeServiceRunAuditStep): number {
  return (
    Date.parse(trimOptional(step.requestedAt) || trimOptional(step.completedAt)) || 0
  );
}

function getStepExecutionStatus(
  step: ScopeServiceRunAuditStep,
): StudioGraphNodeData["executionStatus"] {
  if (step.success === true) {
    return "completed";
  }

  if (step.success === false || trimOptional(step.error)) {
    return "failed";
  }

  if (trimOptional(step.suspensionType)) {
    return "waiting";
  }

  if (trimOptional(step.requestedAt) && !trimOptional(step.completedAt)) {
    return "active";
  }

  return "idle";
}

function getStepStatusTone(step: ScopeServiceRunAuditStep): RunStatusTone {
  const status = getStepExecutionStatus(step);
  if (status === "completed") {
    return "success";
  }
  if (status === "failed") {
    return "error";
  }
  if (status === "active") {
    return "processing";
  }
  if (status === "waiting") {
    return "warning";
  }
  return "default";
}

function getStepStatusLabel(step: ScopeServiceRunAuditStep): string {
  const status = getStepExecutionStatus(step);
  if (status === "active") {
    return "Running";
  }
  if (status === "idle") {
    return "Pending";
  }
  return formatRunStatus(status);
}

function renderStepStatusIcon(step: ScopeServiceRunAuditStep): React.ReactNode {
  const status = getStepExecutionStatus(step);
  if (status === "completed") {
    return <CheckCircleFilled style={{ color: "#16a34a" }} />;
  }
  if (status === "failed") {
    return <CloseCircleFilled style={{ color: "#dc2626" }} />;
  }
  if (status === "active") {
    return <LoadingOutlined style={{ color: "#2563eb" }} />;
  }
  if (status === "waiting") {
    return <ExclamationCircleFilled style={{ color: "#d97706" }} />;
  }
  return <ClockCircleOutlined style={{ color: "#94a3b8" }} />;
}

function summarizeStepParameters(step: ScopeServiceRunAuditStep): string {
  const entries = Object.entries(step.requestParameters).filter(
    ([key, value]) => trimOptional(key) || trimOptional(value),
  );
  if (!entries.length) {
    return step.targetRole ? `role: ${step.targetRole}` : step.stepType || "step";
  }

  return entries
    .slice(0, 2)
    .map(([key, value]) => `${key}: ${value}`)
    .join(" | ");
}

function getSelectedStepDefaultId(
  audit: ScopeServiceRunAuditReport | null | undefined,
): string {
  const steps = audit?.steps ?? [];
  return (
    steps.find((step) => step.success === false || trimOptional(step.error))
      ?.stepId ||
    steps[0]?.stepId ||
    ""
  );
}

function buildExecutionGraph(
  audit: ScopeServiceRunAuditReport | null | undefined,
  selectedStepId: string,
): {
  edges: Edge<StudioGraphEdgeData>[];
  nodes: Node<StudioGraphNodeData>[];
  orderedSteps: ScopeServiceRunAuditStep[];
} {
  const orderedSteps = [...(audit?.steps ?? [])].sort((left, right) => {
    const leftTimestamp = stepSortTimestamp(left);
    const rightTimestamp = stepSortTimestamp(right);
    if (leftTimestamp !== rightTimestamp) {
      return leftTimestamp - rightTimestamp;
    }
    return left.stepId.localeCompare(right.stepId);
  });
  const stepIdSet = new Set(orderedSteps.map((step) => step.stepId));
  const nodes: Node<StudioGraphNodeData>[] = orderedSteps.map((step, index) => ({
    data: {
      branchCount: trimOptional(step.branchKey) ? 1 : 0,
      executionFocused: step.stepId === selectedStepId,
      executionStatus: getStepExecutionStatus(step),
      kind: "step",
      label: step.stepId,
      parametersSummary: summarizeStepParameters(step),
      stepId: step.stepId,
      stepType: step.stepType || "step",
      subtitle: step.stepType || "Step",
      targetRole: step.targetRole,
      title: step.stepId,
    },
    id: `step:${step.stepId}`,
    position: {
      x: 120 + index * 310,
      y: 150 + (index % 2 === 0 ? 0 : 44),
    },
    sourcePosition: Position.Right,
    targetPosition: Position.Left,
    type: "studioWorkflowNode",
  }));
  const edges: Edge<StudioGraphEdgeData>[] = [];
  const edgeKeys = new Set<string>();

  const addEdge = (
    sourceStepId: string,
    targetStepId: string,
    implicit: boolean,
    branchLabel?: string,
  ) => {
    if (!stepIdSet.has(sourceStepId) || !stepIdSet.has(targetStepId)) {
      return;
    }

    const key = `${sourceStepId}->${targetStepId}:${branchLabel ?? ""}`;
    if (edgeKeys.has(key)) {
      return;
    }

    edgeKeys.add(key);
    const kind = branchLabel ? "branch" : "next";
    edges.push({
      animated: false,
      data: {
        branchLabel,
        implicit,
        kind,
      },
      id: `edge:${sourceStepId}:${targetStepId}:${kind}:${edges.length}`,
      label: branchLabel || undefined,
      markerEnd: {
        color: implicit ? "#94a3b8" : "#1677ff",
        height: 10,
        type: MarkerType.ArrowClosed,
        width: 10,
      },
      source: `step:${sourceStepId}`,
      style: {
        stroke: implicit ? "#94a3b8" : "#1677ff",
        strokeDasharray: implicit ? "5 5" : undefined,
        strokeWidth: implicit ? 1.6 : 2.4,
      },
      target: `step:${targetStepId}`,
      type: "smoothstep",
    });
  };

  for (const step of orderedSteps) {
    const nextStepId = trimOptional(step.nextStepId);
    if (nextStepId) {
      addEdge(step.stepId, nextStepId, false, trimOptional(step.branchKey) || undefined);
    }
  }

  for (const relation of audit?.topology ?? []) {
    const parent = trimOptional(relation.parent);
    const child = trimOptional(relation.child);
    if (parent && child) {
      addEdge(parent, child, false);
    }
  }

  if (edges.length === 0) {
    orderedSteps.forEach((step, index) => {
      const next = orderedSteps[index + 1];
      if (next) {
        addEdge(step.stepId, next.stepId, true);
      }
    });
  }

  return { edges, nodes, orderedSteps };
}

function filterTimelineForStep(
  timeline: readonly ScopeServiceRunAuditTimelineEvent[],
  selectedStepId: string,
): readonly ScopeServiceRunAuditTimelineEvent[] {
  if (!selectedStepId) {
    return timeline;
  }

  const scoped = timeline.filter(
    (event) => trimOptional(event.stepId) === selectedStepId,
  );
  return scoped.length ? scoped : timeline;
}

function renderKeyValueRows(values: Readonly<Record<string, string>>) {
  const entries = Object.entries(values).filter(
    ([key, value]) => trimOptional(key) || trimOptional(value),
  );
  if (!entries.length) {
    return <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} />;
  }

  return (
    <div className="member-published-runs-replay__kv">
      {entries.map(([key, value]) => (
        <div className="member-published-runs-replay__kv-row" key={key}>
          <div className="member-published-runs-replay__kv-key">{key}</div>
          <div className="member-published-runs-replay__kv-value">{value || "n/a"}</div>
        </div>
      ))}
    </div>
  );
}

function renderTextBlock(value: string) {
  const normalized = trimOptional(value);
  if (!normalized) {
    return <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} />;
  }

  return <pre className="member-published-runs-replay__pre">{normalized}</pre>;
}

const MemberPublishedRunsReplay: React.FC<MemberPublishedRunsReplayProps> = ({
  initialActorId,
  initialRunId,
  memberId,
  scopeId,
  teamId,
}) => {
  const [selectedStepId, setSelectedStepId] = React.useState("");
  const normalizedInitialActorId = trimOptional(initialActorId);
  const normalizedInitialRunId = trimOptional(initialRunId);
  const normalizedTeamId = trimOptional(teamId);

  const memberQuery = useQuery({
    enabled: Boolean(scopeId && memberId),
    queryFn: () => studioApi.getMember(scopeId, memberId),
    queryKey: ["runtime-member-published-runs", "member", scopeId, memberId],
    retry: false,
  });

  const runsQuery = useQuery({
    enabled: Boolean(scopeId && memberId),
    queryFn: () => scopeRuntimeApi.listMemberRuns(scopeId, memberId, { take: 200 }),
    queryKey: ["runtime-member-published-runs", scopeId, memberId],
    retry: false,
  });

  const runs = React.useMemo(() => {
    return [...(runsQuery.data?.runs ?? [])].sort(
      (left, right) => getRunSortTimestamp(right) - getRunSortTimestamp(left),
    );
  }, [runsQuery.data?.runs]);

  const selectedCatalogRun =
    runs.find((run) => run.runId === normalizedInitialRunId) ??
    (normalizedInitialRunId ? null : runs[0] ?? null);
  const selectedRunId = selectedCatalogRun?.runId ?? normalizedInitialRunId;
  const selectedRunActorId =
    selectedCatalogRun?.actorId || normalizedInitialActorId || "";

  React.useEffect(() => {
    if (!selectedCatalogRun?.runId || normalizedInitialRunId) {
      return;
    }

    history.replace(
      buildRuntimeRunsHref({
        actorId: selectedCatalogRun.actorId || undefined,
        memberId,
        runId: selectedCatalogRun.runId,
        scopeId,
        teamId: normalizedTeamId || undefined,
      }),
    );
  }, [memberId, normalizedInitialRunId, normalizedTeamId, scopeId, selectedCatalogRun]);

  const auditQuery = useQuery({
    enabled: Boolean(scopeId && memberId && selectedRunId),
    queryFn: () =>
      scopeRuntimeApi.getMemberRunAudit(scopeId, memberId, selectedRunId, {
        actorId: selectedRunActorId || undefined,
      }),
    queryKey: [
      "runtime-member-published-run-audit",
      scopeId,
      memberId,
      selectedRunId,
      selectedRunActorId,
    ],
    retry: false,
  });

  const selectedRun =
    selectedCatalogRun ??
    auditQuery.data?.summary ??
    (normalizedInitialRunId
      ? createFallbackRunSummary({
          actorId: selectedRunActorId,
          memberId,
          runId: normalizedInitialRunId,
          scopeId,
        })
      : null);
  const audit = auditQuery.data?.audit ?? null;
  const displayRuns =
    runs.length || !selectedRun ? runs : [selectedRun];
  const graph = React.useMemo(
    () => buildExecutionGraph(audit, selectedStepId),
    [audit, selectedStepId],
  );
  const selectedStep =
    graph.orderedSteps.find((step) => step.stepId === selectedStepId) ??
    graph.orderedSteps[0] ??
    null;
  const scopedTimeline = React.useMemo(
    () => filterTimelineForStep(audit?.timeline ?? [], selectedStep?.stepId ?? ""),
    [audit?.timeline, selectedStep?.stepId],
  );
  const draftWorkflowId = trimOptional(
    memberQuery.data?.implementationRef?.workflowId ??
      memberQuery.data?.summary.implementationRef?.workflowId,
  );
  const editorHref =
    normalizedTeamId && memberId
      ? buildTeamMemberWorkflowStudioHref({
          memberId,
          mode: "edit-member",
          scopeId,
          teamId: normalizedTeamId,
          workflowId: draftWorkflowId || undefined,
        })
      : "";

  React.useEffect(() => {
    const defaultStepId = getSelectedStepDefaultId(audit);
    setSelectedStepId((current) => {
      if (
        current &&
        (audit?.steps ?? []).some((step) => step.stepId === current)
      ) {
        return current;
      }
      return defaultStepId;
    });
  }, [audit]);

  const handleSelectRun = React.useCallback(
    (run: ScopeMemberRunSummary) => {
      history.push(
        buildRuntimeRunsHref({
          memberId,
          runId: run.runId,
          scopeId,
          teamId: normalizedTeamId || undefined,
        }),
      );
      setSelectedStepId("");
    },
    [memberId, normalizedTeamId, scopeId],
  );

  const handleNodeSelect = React.useCallback((nodeId: string) => {
    const stepId = nodeId.startsWith("step:") ? nodeId.slice("step:".length) : nodeId;
    setSelectedStepId(stepId);
  }, []);

  const runStatusTone = getRunStatusTone(selectedRun?.completionStatus);
  const selectedRunTime = selectedRun ? formatRunTime(selectedRun) : "n/a";
  const auditDuration = getAuditDurationMs(audit);
  const selectedRunDuration = formatDurationMs(auditDuration);

  return (
    <div className="member-published-runs-replay" data-testid="member-published-runs-replay">
      <style>{memberPublishedRunsReplayCss}</style>
      <aside className="member-published-runs-replay__rail">
        <div className="member-published-runs-replay__rail-header">
          <div className="member-published-runs-replay__rail-title">
            <div className="member-published-runs-replay__rail-title-main">
              <div style={{ minWidth: 0 }}>
                <Typography.Title level={5} style={{ margin: 0 }}>
                  {t("pages.runs.memberPublishedRuns.publishedRuns", "Published runs")}
                </Typography.Title>
                <Typography.Text
                  ellipsis
                  style={{ display: "block", maxWidth: 260 }}
                  type="secondary"
                >
                  {runsQuery.data?.displayName || memberId}
                </Typography.Text>
              </div>
            </div>
            <Tooltip title={t("pages.runs.memberPublishedRuns.refresh", "Refresh")}>
              <Button
                aria-label={t("pages.runs.memberPublishedRuns.refresh", "Refresh")}
                icon={<ReloadOutlined />}
                loading={runsQuery.isFetching}
                onClick={() => runsQuery.refetch()}
                shape="circle"
                size="small"
              />
            </Tooltip>
          </div>
        </div>
        <div className="member-published-runs-replay__list">
          {runsQuery.isLoading ? (
            <div className="member-published-runs-replay__empty">
              <Spin />
            </div>
          ) : runsQuery.error ? (
            <Alert
              showIcon
              type="error"
              message={t(
                "pages.runs.memberPublishedRuns.listUnavailable",
                "Published runs are unavailable.",
              )}
              description={
                runsQuery.error instanceof Error
                  ? runsQuery.error.message
                  : String(runsQuery.error)
              }
            />
          ) : displayRuns.length ? (
            displayRuns.map((run) => {
              const selected = run.runId === selectedRunId;
              return (
                <button
                  className={`member-published-runs-replay__run${
                    selected ? " member-published-runs-replay__run--selected" : ""
                  }`}
                  key={run.runId}
                  onClick={() => handleSelectRun(run)}
                  type="button"
                >
                  <div className="member-published-runs-replay__run-title">
                    <Tag
                      color={getRunStatusTone(run.completionStatus)}
                      style={{ marginInlineEnd: 0 }}
                    >
                      {formatRunStatus(run.completionStatus)}
                    </Tag>
                    <Typography.Text ellipsis style={{ minWidth: 0 }}>
                      {formatRunTime(run)}
                    </Typography.Text>
                  </div>
                  {run.workflowName ? (
                    <Typography.Text ellipsis type="secondary">
                      {run.workflowName}
                    </Typography.Text>
                  ) : null}
                </button>
              );
            })
          ) : (
            <div className="member-published-runs-replay__empty">
              <Empty
                image={Empty.PRESENTED_IMAGE_SIMPLE}
                description={
                  t(
                    "pages.runs.memberPublishedRuns.noRuns",
                    "No published runs yet.",
                  )
                }
              />
            </div>
          )}
        </div>
      </aside>
      <section className="member-published-runs-replay__stage">
        <header className="member-published-runs-replay__stage-header">
          <div className="member-published-runs-replay__stage-title">
            <Space wrap size={8}>
              <Typography.Title level={4} style={{ margin: 0 }}>
                {selectedRunTime}
              </Typography.Title>
              {selectedRun ? (
                <Tag color={runStatusTone} style={{ marginInlineEnd: 0 }}>
                  {formatRunStatus(selectedRun.completionStatus)}
                </Tag>
              ) : null}
              {audit ? <Tag>{selectedRunDuration}</Tag> : null}
            </Space>
            {selectedRun?.workflowName ? (
              <Typography.Text ellipsis type="secondary">
                {selectedRun.workflowName}
              </Typography.Text>
            ) : !selectedRun ? (
              <Typography.Text ellipsis type="secondary">
                {t("pages.runs.memberPublishedRuns.selectPublishedRun", "Select a published run")}
              </Typography.Text>
            ) : null}
          </div>
          <div className="member-published-runs-replay__stage-actions">
            <Button
              icon={<ReloadOutlined />}
              loading={auditQuery.isFetching}
              onClick={() => {
                runsQuery.refetch();
                auditQuery.refetch();
              }}
            >
              {t("pages.runs.memberPublishedRuns.refresh", "Refresh")}
            </Button>
            {editorHref ? (
              <Button
                disabled={memberQuery.isLoading}
                icon={<CodeOutlined />}
                onClick={() => history.push(editorHref)}
              >
                {t("pages.runs.memberPublishedRuns.openEditor", "Open editor")}
              </Button>
            ) : null}
          </div>
        </header>
        <div className="member-published-runs-replay__graph">
          <div className="member-published-runs-replay__graph-inner">
            {auditQuery.error ? (
              <Alert
                showIcon
                type="error"
                message={t(
                  "pages.runs.memberPublishedRuns.auditUnavailable",
                  "Published run audit is unavailable.",
                )}
                description={
                  auditQuery.error instanceof Error
                    ? auditQuery.error.message
                    : String(auditQuery.error)
                }
              />
            ) : auditQuery.isLoading && selectedRun ? (
              <div className="member-published-runs-replay__empty">
                <Spin />
              </div>
            ) : graph.nodes.length ? (
              <GraphCanvas
                autoFitKey={selectedRunId}
                edges={graph.edges}
                height="100%"
                nodes={graph.nodes}
                onCanvasSelect={() => setSelectedStepId("")}
                onNodeSelect={handleNodeSelect}
                selectedNodeId={selectedStep ? `step:${selectedStep.stepId}` : undefined}
                variant="studio"
              />
            ) : (
              <div className="member-published-runs-replay__empty">
                <Empty
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                  description={t(
                    "pages.runs.memberPublishedRuns.noAuditSteps",
                    "No audit steps were recorded for this published run.",
                  )}
                />
              </div>
            )}
          </div>
        </div>
        <div className="member-published-runs-replay__details">
          <section className="member-published-runs-replay__logs">
            <div className="member-published-runs-replay__logs-header">
              <Typography.Text strong>
                {t("pages.runs.memberPublishedRuns.logs", "Logs")}
              </Typography.Text>
              {audit ? (
                <Typography.Text type="secondary">
                  {selectedRunDuration}
                </Typography.Text>
              ) : null}
            </div>
            <div className="member-published-runs-replay__step-list">
              {graph.orderedSteps.length ? (
                graph.orderedSteps.map((step) => (
                  <button
                    className={`member-published-runs-replay__step${
                      selectedStep?.stepId === step.stepId
                        ? " member-published-runs-replay__step--selected"
                        : ""
                    }`}
                    key={step.stepId}
                    onClick={() => setSelectedStepId(step.stepId)}
                    type="button"
                  >
                    {renderStepStatusIcon(step)}
                    <span style={{ minWidth: 0 }}>
                      <Typography.Text ellipsis style={{ display: "block" }}>
                        {step.stepId}
                      </Typography.Text>
                      <Typography.Text ellipsis type="secondary">
                        {step.stepType || "step"}
                      </Typography.Text>
                    </span>
                    <Typography.Text type="secondary">
                      {formatDurationMs(step.durationMs)}
                    </Typography.Text>
                  </button>
                ))
              ) : (
                <div className="member-published-runs-replay__empty">
                  <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} />
                </div>
              )}
            </div>
          </section>
          <section className="member-published-runs-replay__inspector">
            <div className="member-published-runs-replay__inspector-header">
              <Space size={8} style={{ minWidth: 0 }}>
                <NodeIndexOutlined style={{ color: "#1677ff" }} />
                <Typography.Text ellipsis strong style={{ maxWidth: 360 }}>
                  {selectedStep?.stepId ||
                    t("pages.runs.memberPublishedRuns.details", "Details")}
                </Typography.Text>
                {selectedStep ? (
                  <Tag
                    color={getStepStatusTone(selectedStep)}
                    style={{ marginInlineEnd: 0 }}
                  >
                    {getStepStatusLabel(selectedStep)}
                  </Tag>
                ) : null}
              </Space>
              {selectedStep ? (
                <Typography.Text type="secondary">
                  {formatDateTime(selectedStep.completedAt || selectedStep.requestedAt)}
                </Typography.Text>
              ) : null}
            </div>
            <div className="member-published-runs-replay__inspector-body">
              <Tabs
                size="small"
                items={[
                  {
                    key: "output",
                    label: t("pages.runs.memberPublishedRuns.output", "Output"),
                    children: selectedStep
                      ? renderTextBlock(
                          selectedStep.error ||
                            selectedStep.outputPreview ||
                            JSON.stringify(selectedStep.completionAnnotations, null, 2),
                        )
                      : renderTextBlock(audit?.finalError || audit?.finalOutput || ""),
                  },
                  {
                    key: "input",
                    label: t("pages.runs.memberPublishedRuns.input", "Input"),
                    children: selectedStep
                      ? renderKeyValueRows(selectedStep.requestParameters)
                      : renderTextBlock(audit?.input || ""),
                  },
                  {
                    key: "timeline",
                    label: t("pages.runs.memberPublishedRuns.timeline", "Timeline"),
                    children: scopedTimeline.length ? (
                      <div className="member-published-runs-replay__kv">
                        {scopedTimeline.map((event, index) => (
                          <div
                            className="member-published-runs-replay__kv-row"
                            key={`${event.timestamp ?? "event"}-${event.stage}-${index}`}
                          >
                            <div className="member-published-runs-replay__kv-key">
                              {formatDateTime(event.timestamp)}
                            </div>
                            <div className="member-published-runs-replay__kv-value">
                              <Typography.Text strong>
                                {event.stage || event.eventType || "event"}
                              </Typography.Text>
                              <br />
                              <Typography.Text type="secondary">
                                {event.message || event.agentId || "n/a"}
                              </Typography.Text>
                            </div>
                          </div>
                        ))}
                      </div>
                    ) : (
                      <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} />
                    ),
                  },
                  {
                    key: "raw",
                    label: t("pages.runs.memberPublishedRuns.raw", "Raw"),
                    children: (
                      <pre className="member-published-runs-replay__pre">
                        {JSON.stringify(selectedStep ?? audit ?? {}, null, 2)}
                      </pre>
                    ),
                  },
                ]}
              />
            </div>
          </section>
        </div>
      </section>
    </div>
  );
};

export default MemberPublishedRunsReplay;
