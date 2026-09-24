import {
  CaretRightFilled,
  CheckOutlined,
  CloseOutlined,
  CopyOutlined,
  ExpandOutlined,
  FileTextOutlined,
  UserOutlined,
} from '@ant-design/icons';
import {
  Button,
  Empty,
  Space,
  Tag,
  Typography,
} from 'antd';
import React from 'react';
import type { Edge, Node } from '@xyflow/react';
import GraphCanvas from '@/shared/graphs/GraphCanvas';
import type {
  StudioGraphEdgeData,
  StudioGraphNodeData,
  StudioGraphRole,
  StudioGraphStep,
} from '@/shared/studio/graph';
import type {
  StudioExecutionDetail,
  StudioExecutionSummary,
  StudioScopeBindingImplementationKind,
  StudioWorkflowSummary,
} from '@/shared/studio/models';
import {
  buildExecutionTrace,
  decorateEdgesForExecution,
  decorateNodesForExecution,
  findExecutionLogIndexForStep,
  formatDurationBetween,
  formatExecutionLogClipboard,
  formatExecutionLogsClipboard,
  type ExecutionInteractionState,
} from '@/shared/studio/execution';
import { formatDateTime } from '@/shared/datetime/dateTime';
import {
  cardStackStyle,
  fillCardStyle,
} from '@/shared/ui/proComponents';
import { AevatarPanel, AevatarStatusTag } from '@/shared/ui/aevatarPageShells';
import ConsoleOperationNotice from '@/shared/ui/ConsoleOperationNotice';
import AevatarTooltip from '@/shared/ui/AevatarTooltip';
import { describeError } from '@/shared/ui/errorText';
import {
  AEVATAR_INTERACTIVE_BUTTON_CLASS,
  AEVATAR_PRESSABLE_CARD_CLASS,
} from '@/shared/ui/interactionStandards';
import { sanitizeUserFacingText } from '@/shared/ui/userFacingIdentifiers';
import { t } from "@/shared/i18n/messages";

type QueryState<T> = {
  readonly isLoading: boolean;
  readonly isError: boolean;
  readonly error: unknown;
  readonly data: T | undefined;
};

type StudioNoticeLike = {
  readonly type: 'success' | 'info' | 'warning' | 'error';
  readonly message: string;
};

type StudioCompactNoticeLike = {
  readonly description?: React.ReactNode;
  readonly title: React.ReactNode;
  readonly type: StudioNoticeLike['type'] | 'default';
};

function readWorkflowSortTimestamp(value: string): number {
  const timestamp = Date.parse(value);
  return Number.isFinite(timestamp) ? timestamp : 0;
}

function sanitizeVisibleText(value: string | null | undefined): string {
  return sanitizeUserFacingText(value) || '';
}

function compareWorkflowSummaryPriority(
  left: StudioWorkflowSummary,
  right: StudioWorkflowSummary,
): number {
  const updatedDelta =
    readWorkflowSortTimestamp(right.updatedAtUtc) -
    readWorkflowSortTimestamp(left.updatedAtUtc);
  if (updatedDelta !== 0) {
    return updatedDelta;
  }

  if (left.stepCount !== right.stepCount) {
    return right.stepCount - left.stepCount;
  }

  const leftDescriptionLength = left.description.trim().length;
  const rightDescriptionLength = right.description.trim().length;
  if (leftDescriptionLength !== rightDescriptionLength) {
    return rightDescriptionLength - leftDescriptionLength;
  }

  return left.workflowId.localeCompare(right.workflowId);
}

export function dedupeStudioWorkflowSummaries(
  workflows: readonly StudioWorkflowSummary[],
): StudioWorkflowSummary[] {
  const dedupedWorkflows = new Map<string, StudioWorkflowSummary>();

  for (const workflow of workflows) {
    const key =
      workflow.name.trim().toLowerCase() ||
      workflow.workflowId.trim().toLowerCase();
    const current = dedupedWorkflows.get(key);
    if (!current) {
      dedupedWorkflows.set(key, workflow);
      continue;
    }

    if (compareWorkflowSummaryPriority(workflow, current) < 0) {
      dedupedWorkflows.set(key, workflow);
    }
  }

  return Array.from(dedupedWorkflows.values()).sort((left, right) =>
    compareWorkflowSummaryPriority(left, right),
  );
}

function getStudioNoticeAccent(
  type: StudioNoticeLike['type'] | 'default',
): { border: string; background: string; label: string } {
  switch (type) {
    case 'success':
      return {
        border: 'rgba(82, 196, 26, 0.28)',
        background: 'rgba(246, 255, 237, 0.96)',
        label: t("pages.studio.studioworkbenchsections.success", "success"),
      };
    case 'warning':
      return {
        border: 'rgba(250, 173, 20, 0.28)',
        background: 'rgba(255, 251, 230, 0.96)',
        label: t("pages.studio.studioworkbenchsections.notice", "Notice"),
      };
    case 'error':
      return {
        border: 'rgba(255, 77, 79, 0.28)',
        background: 'rgba(255, 241, 240, 0.96)',
        label: t("pages.studio.studioworkbenchsections.mistake", "mistake"),
      };
    case 'info':
      return {
        border: 'rgba(22, 119, 255, 0.24)',
        background: 'rgba(240, 245, 255, 0.96)',
        label: t("pages.studio.studioworkbenchsections.hint", "hint"),
      };
    default:
      return {
        border: 'var(--ant-color-border-secondary)',
        background: 'var(--ant-color-fill-quaternary)',
        label: t("pages.studio.studioworkbenchsections.state", "state"),
      };
  }
}

const studioNoticeCardStyle: React.CSSProperties = {
  border: '1px solid',
  borderRadius: 18,
  display: 'grid',
  gap: 10,
  padding: 14,
};

const studioCompactNoticeStackStyle: React.CSSProperties = {
  display: 'grid',
  gap: 8,
};

const studioCompactNoticeStyle: React.CSSProperties = {
  alignItems: 'center',
  border: '1px solid',
  borderRadius: 8,
  display: 'flex',
  gap: 10,
  minHeight: 40,
  padding: '8px 12px',
};

const studioCompactNoticeBodyStyle: React.CSSProperties = {
  alignItems: 'baseline',
  display: 'flex',
  flex: 1,
  flexWrap: 'wrap',
  gap: 8,
  minWidth: 0,
};

const studioEmptyPanelStyle: React.CSSProperties = {
  alignItems: 'center',
  background: '#ffffff',
  border: '1px dashed #d9d9d9',
  borderRadius: 18,
  color: '#6b7280',
  display: 'grid',
  gap: 8,
  justifyItems: 'center',
  padding: 28,
  textAlign: 'center',
};

const panelIconButtonStyle: React.CSSProperties = {
  alignItems: 'center',
  background: '#ffffff',
  border: '1px solid #e5e7eb',
  borderRadius: 10,
  color: '#4b5563',
  cursor: 'pointer',
  display: 'inline-flex',
  height: 30,
  justifyContent: 'center',
  width: 30,
};

const logCardBaseStyle: React.CSSProperties = {
  alignItems: 'flex-start',
  background: '#ffffff',
  border: '1px solid #eef2f7',
  borderRadius: 14,
  cursor: 'pointer',
  display: 'grid',
  gap: 6,
  padding: 12,
  textAlign: 'left',
  width: '100%',
};

const executionActionButtonStyle: React.CSSProperties = {
  borderRadius: 10,
  minWidth: 96,
};

const executionTextareaStyle: React.CSSProperties = {
  border: '1px solid #d9d9d9',
  borderRadius: 12,
  fontSize: 13,
  lineHeight: '20px',
  minHeight: 108,
  padding: 12,
  resize: 'vertical',
  width: '100%',
};

const observeDetailsGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 16,
  gridTemplateColumns: 'repeat(auto-fit, minmax(min(100%, 360px), 1fr))',
};

const observeMetricGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 12,
  gridTemplateColumns: 'repeat(auto-fit, minmax(150px, 1fr))',
};

const observeMetricCardStyle: React.CSSProperties = {
  border: '1px solid #eef2f7',
  borderRadius: 8,
  display: 'grid',
  gap: 4,
  minWidth: 0,
  padding: 12,
};

const observeRunHeaderStyle: React.CSSProperties = {
  alignItems: 'flex-start',
  background: '#ffffff',
  border: '1px solid #e5e7eb',
  borderRadius: 8,
  display: 'grid',
  gap: 14,
  gridTemplateColumns: 'minmax(0, 1fr) auto',
  padding: 16,
};

const observeRunTitleStyle: React.CSSProperties = {
  color: '#111827',
  fontSize: 18,
  fontWeight: 700,
  lineHeight: '24px',
  margin: 0,
};

const observeRunSubtitleStyle: React.CSSProperties = {
  color: '#4b5563',
  fontSize: 13,
  lineHeight: '20px',
  margin: 0,
  overflowWrap: 'anywhere',
};

const observeRunMetricGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 10,
  gridTemplateColumns: 'repeat(auto-fit, minmax(150px, 1fr))',
};

const observeRunMetricStyle: React.CSSProperties = {
  background: '#f9fafb',
  border: '1px solid #eef2f7',
  borderRadius: 8,
  display: 'grid',
  gap: 3,
  minWidth: 0,
  padding: '10px 12px',
};

const observeStageShellStyle: React.CSSProperties = {
  background: '#ffffff',
  border: '1px solid #e5e7eb',
  borderRadius: 8,
  display: 'flex',
  flexDirection: 'column',
  minHeight: 'calc(100vh - 244px)',
  overflow: 'hidden',
};

const observeStageBarStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 12,
  minHeight: 42,
  padding: '8px 16px',
};

type ObserveCompareRow = {
  readonly baseline: string;
  readonly current: string;
  readonly delta: 'same' | 'changed' | 'regression' | 'current-only';
  readonly label: string;
};

type ObserveHealthItem = {
  readonly label: string;
  readonly note: string;
  readonly status: 'active' | 'blocked' | 'warning' | 'pending';
  readonly value: string;
};

type ObservePlaybackEntry = {
  readonly detail: string;
  readonly label: string;
  readonly status: 'active' | 'done' | 'waiting';
  readonly timestamp: string;
};

const StudioNoticeCard: React.FC<{
  readonly type?: StudioNoticeLike['type'] | 'default';
  readonly title: React.ReactNode;
  readonly description?: React.ReactNode;
}> = ({ type = 'default', title, description }) => {
  const accent = getStudioNoticeAccent(type);

  return (
    <div
      style={{
        ...studioNoticeCardStyle,
        background: accent.background,
        borderColor: accent.border,
      }}
    >
      <Space wrap size={[8, 8]}>
        <Tag color={type === 'default' ? 'default' : type}>{accent.label}</Tag>
        <Typography.Text strong>{title}</Typography.Text>
      </Space>
      {description ? (
        typeof description === 'string' ? (
          <Typography.Paragraph style={{ margin: 0 }} type="secondary">
            {description}
          </Typography.Paragraph>
        ) : (
          description
        )
      ) : null}
    </div>
  );
};

const StudioCompactNotice: React.FC<StudioCompactNoticeLike> = ({
  description,
  title,
  type,
}) => {
  const accent = getStudioNoticeAccent(type);

  return (
    <div
      style={{
        ...studioCompactNoticeStyle,
        background: accent.background,
        borderColor: accent.border,
      }}
    >
      <Tag color={type === 'default' ? 'default' : type}>{accent.label}</Tag>
      <div style={studioCompactNoticeBodyStyle}>
        <Typography.Text strong>{title}</Typography.Text>
        {description ? (
          typeof description === 'string' ? (
            <Typography.Text type="secondary">{description}</Typography.Text>
          ) : (
            description
          )
        ) : null}
      </div>
    </div>
  );
};

function StudioCatalogEmptyPanel(props: {
  readonly icon: React.ReactNode;
  readonly title: string;
  readonly copy: string;
}) {
  return (
    <div style={studioEmptyPanelStyle}>
      <div style={{ fontSize: 22 }}>{props.icon}</div>
      <Typography.Text strong>{props.title}</Typography.Text>
      <Typography.Text type="secondary">{props.copy}</Typography.Text>
    </div>
  );
}

function formatObserveNotAvailable(): string {
  return t("pages.studio.studioworkbenchsections.not.available", "n/a");
}

function formatObserveCoverageLogs(coverage: string, count: number): string {
  return t(
    "pages.studio.studioworkbenchsections.coverage.logs",
    "{coverage} · {count} logs",
    {
      count,
      coverage,
    },
  );
}

function formatObserveCoverageReplies(coverage: string, count: number): string {
  return t(
    "pages.studio.studioworkbenchsections.coverage.replies",
    "{coverage} · {count} replies",
    {
      count,
      coverage,
    },
  );
}

function trimObserveText(value: string | null | undefined, limit = 84): string {
  const trimmed = String(value || '').trim();
  if (!trimmed) {
    return formatObserveNotAvailable();
  }

  return trimmed.length > limit ? `${trimmed.slice(0, limit - 3)}...` : trimmed;
}

function readExecutionDurationMs(
  execution: Pick<StudioExecutionSummary, 'startedAtUtc' | 'completedAtUtc'> | null | undefined,
): number {
  if (!execution?.startedAtUtc) {
    return 0;
  }

  const start = Date.parse(execution.startedAtUtc);
  const end = execution.completedAtUtc
    ? Date.parse(execution.completedAtUtc)
    : Date.now();
  if (!Number.isFinite(start) || !Number.isFinite(end) || end <= start) {
    return 0;
  }

  return end - start;
}

function readObserveStepCoverage(
  execution:
    | Pick<StudioExecutionSummary, 'completedSteps' | 'totalSteps'>
    | null
    | undefined,
  fallbackCompleted?: number,
  fallbackTotal?: number,
): string {
  const completedSteps =
    typeof execution?.completedSteps === 'number'
      ? execution.completedSteps
      : fallbackCompleted ?? null;
  const totalSteps =
    typeof execution?.totalSteps === 'number'
      ? execution.totalSteps
      : fallbackTotal ?? null;

  if (completedSteps === null && totalSteps === null) {
    return formatObserveNotAvailable();
  }

  return `${completedSteps ?? 0}/${totalSteps ?? 0}`;
}

function resolveObserveDelta(input: {
  current: string;
  baseline: string;
  regressionWhen?: boolean;
}): ObserveCompareRow['delta'] {
  if (!input.baseline || input.baseline === formatObserveNotAvailable()) {
    return 'current-only';
  }

  if (input.regressionWhen) {
    return 'regression';
  }

  return input.current === input.baseline ? 'same' : 'changed';
}

function buildObserveCompareRows(input: {
  baselineExecution: StudioExecutionSummary | null;
  selectedExecution: StudioExecutionDetail | null | undefined;
  traceLogCount: number;
  executedStepCount: number;
}): ObserveCompareRow[] {
  const { baselineExecution, selectedExecution, traceLogCount, executedStepCount } = input;
  const baselineDurationMs = readExecutionDurationMs(baselineExecution);
  const currentDurationMs = readExecutionDurationMs(selectedExecution);
  const baselineDurationLabel = baselineExecution
    ? formatDurationBetween(
        baselineExecution.startedAtUtc,
        baselineExecution.completedAtUtc,
      ) || formatObserveNotAvailable()
    : formatObserveNotAvailable();
  const currentDurationLabel = selectedExecution
    ? formatDurationBetween(
        selectedExecution.startedAtUtc,
        selectedExecution.completedAtUtc,
      ) || formatObserveNotAvailable()
    : formatObserveNotAvailable();

  const compare = (
    label: string,
    current: string,
    baseline: string,
    delta: ObserveCompareRow['delta'],
  ): ObserveCompareRow => ({
    baseline,
    current,
    delta,
    label,
  });

  if (!selectedExecution) {
    return [
      compare(
        t("pages.studio.studioworkbenchsections.status", "status"),
        t("pages.studio.studioworkbenchsections.no.run.selected.lower", "no run selected"),
        formatObserveNotAvailable(),
        'current-only',
      ),
      compare(
        t("pages.studio.studioworkbenchsections.duration", "duration"),
        formatObserveNotAvailable(),
        formatObserveNotAvailable(),
        'current-only',
      ),
      compare(
        t("pages.studio.studioworkbenchsections.actor.lower", "actor"),
        formatObserveNotAvailable(),
        formatObserveNotAvailable(),
        'current-only',
      ),
    ];
  }

  const rows: ObserveCompareRow[] = [
    compare(
      t("pages.studio.studioworkbenchsections.status", "status"),
      trimObserveText(selectedExecution.status),
      trimObserveText(baselineExecution?.status),
      resolveObserveDelta({
        current: trimObserveText(selectedExecution.status),
        baseline: trimObserveText(baselineExecution?.status),
        regressionWhen:
          selectedExecution.status.toLowerCase().includes('fail') ||
          selectedExecution.status.toLowerCase().includes('stopped'),
      }),
    ),
    compare(
      t("pages.studio.studioworkbenchsections.revision.lower", "revision"),
      trimObserveText(selectedExecution.revisionId),
      trimObserveText(baselineExecution?.revisionId),
      resolveObserveDelta({
        current: trimObserveText(selectedExecution.revisionId),
        baseline: trimObserveText(baselineExecution?.revisionId),
      }),
    ),
    compare(
      t("pages.studio.studioworkbenchsections.state.version.lower", "state version"),
      trimObserveText(
        selectedExecution.stateVersion !== null &&
          selectedExecution.stateVersion !== undefined
          ? `v${selectedExecution.stateVersion}`
          : formatObserveNotAvailable(),
      ),
      trimObserveText(
        baselineExecution?.stateVersion !== null &&
          baselineExecution?.stateVersion !== undefined
          ? `v${baselineExecution.stateVersion}`
          : formatObserveNotAvailable(),
      ),
      resolveObserveDelta({
        current: trimObserveText(
          selectedExecution.stateVersion !== null &&
            selectedExecution.stateVersion !== undefined
            ? `v${selectedExecution.stateVersion}`
            : formatObserveNotAvailable(),
        ),
        baseline: trimObserveText(
          baselineExecution?.stateVersion !== null &&
            baselineExecution?.stateVersion !== undefined
            ? `v${baselineExecution.stateVersion}`
            : formatObserveNotAvailable(),
        ),
      }),
    ),
    compare(
      t("pages.studio.studioworkbenchsections.duration", "duration"),
      currentDurationLabel,
      baselineDurationLabel,
      resolveObserveDelta({
        current: currentDurationLabel,
        baseline: baselineDurationLabel,
        regressionWhen:
          baselineDurationMs > 0 && currentDurationMs > baselineDurationMs,
      }),
    ),
    compare(
      t("pages.studio.studioworkbenchsections.steps.lower", "steps"),
      formatObserveCoverageLogs(
        readObserveStepCoverage(selectedExecution, executedStepCount),
        traceLogCount,
      ),
      baselineExecution
        ? formatObserveCoverageReplies(
            readObserveStepCoverage(baselineExecution),
            baselineExecution.roleReplyCount ?? 0,
          )
        : formatObserveNotAvailable(),
      resolveObserveDelta({
        current: formatObserveCoverageLogs(
          readObserveStepCoverage(selectedExecution, executedStepCount),
          traceLogCount,
        ),
        baseline: baselineExecution
          ? formatObserveCoverageReplies(
              readObserveStepCoverage(baselineExecution),
              baselineExecution.roleReplyCount ?? 0,
            )
          : formatObserveNotAvailable(),
      }),
    ),
    compare(
      t("pages.studio.studioworkbenchsections.actor.lower", "actor"),
      selectedExecution.actorId
        ? t("pages.studio.studioworkbenchsections.runtime.available", "Runtime available")
        : formatObserveNotAvailable(),
      baselineExecution?.actorId
        ? t("pages.studio.studioworkbenchsections.runtime.available.2", "Runtime available")
        : formatObserveNotAvailable(),
      resolveObserveDelta({
        current: selectedExecution.actorId
          ? t("pages.studio.studioworkbenchsections.runtime.available.3", "Runtime available")
          : formatObserveNotAvailable(),
        baseline: baselineExecution?.actorId
          ? t("pages.studio.studioworkbenchsections.runtime.available.4", "Runtime available")
          : formatObserveNotAvailable(),
      }),
    ),
    compare(
      t("pages.studio.studioworkbenchsections.output", "output"),
      trimObserveText(selectedExecution.output),
      trimObserveText(baselineExecution?.output),
      resolveObserveDelta({
        current: trimObserveText(selectedExecution.output),
        baseline: trimObserveText(baselineExecution?.output),
      }),
    ),
  ];

  if (selectedExecution.error || baselineExecution?.error) {
    rows.push(
      compare(
        t("pages.studio.studioworkbenchsections.error.lower", "error"),
        trimObserveText(
          selectedExecution.error ||
            t("pages.studio.studioworkbenchsections.none", "none"),
        ),
        trimObserveText(
          baselineExecution?.error ||
            t("pages.studio.studioworkbenchsections.none", "none"),
        ),
        baselineExecution
          ? selectedExecution.error === baselineExecution.error
            ? 'same'
            : selectedExecution.error
              ? 'regression'
              : 'changed'
          : 'current-only',
      ),
    );
  }

  return rows;
}

function buildObserveHealthItems(input: {
  activeExecutionInteraction: ExecutionInteractionState | null;
  executions: readonly StudioExecutionSummary[];
  baselineExecution: StudioExecutionSummary | null;
  selectedExecution: StudioExecutionDetail | null | undefined;
  traceLogCount: number;
}): ObserveHealthItem[] {
  const {
    activeExecutionInteraction,
    baselineExecution,
    executions,
    selectedExecution,
    traceLogCount,
  } = input;
  const recentExecutions = executions.slice(0, 5);
  const failedCount = recentExecutions.filter((item) =>
    String(item.status || '').trim().toLowerCase().includes('fail'),
  ).length;
  const stoppedCount = recentExecutions.filter((item) =>
    String(item.status || '').trim().toLowerCase().includes('stop'),
  ).length;
  const runtimeStatus = String(selectedExecution?.status || '').trim().toLowerCase();
  const selectedCoverage = readObserveStepCoverage(selectedExecution);
  const auditReady = selectedExecution?.auditSource === 'run-audit';
  const humanGateValue = activeExecutionInteraction
    ? activeExecutionInteraction.kind === 'human_approval'
      ? t("pages.studio.studioworkbenchsections.awaiting.approval", "awaiting approval")
      : activeExecutionInteraction.kind === 'wait_signal'
        ? t("pages.studio.studioworkbenchsections.awaiting.signal", "awaiting signal")
        : t("pages.studio.studioworkbenchsections.awaiting.input", "awaiting input")
    : t("pages.studio.studioworkbenchsections.clear", "clear");

  return [
    {
      label: t("pages.studio.studioworkbenchsections.runtime", "runtime"),
      note: selectedExecution
        ? t(
            "pages.studio.studioworkbenchsections.selected.run.updated",
            "Selected run updated {updatedAt}.",
            {
              updatedAt: formatDateTime(
                selectedExecution.updatedAtUtc || selectedExecution.startedAtUtc,
              ),
            },
          )
        : t("pages.studio.studioworkbenchsections.no.workflow.run.selected.yet", "No workflow run selected yet."),
      status: selectedExecution
        ? runtimeStatus.includes('fail')
          ? 'blocked'
          : runtimeStatus.includes('stop')
            ? 'warning'
            : runtimeStatus.includes('run')
            ? 'active'
            : 'pending'
        : 'pending',
      value: selectedExecution
        ? trimObserveText(selectedExecution.status)
        : t("pages.studio.studioworkbenchsections.idle", "idle"),
    },
    {
      label: t("pages.studio.studioworkbenchsections.recent.runs", "recent runs"),
      note: t(
        "pages.studio.studioworkbenchsections.recent.run.failures",
        "{failedCount} failed, {stoppedCount} stopped in the latest {runCount} runs.",
        {
          failedCount,
          runCount: recentExecutions.length || 0,
          stoppedCount,
        },
      ),
      status: failedCount > 0 || stoppedCount > 0 ? 'warning' : 'active',
      value: recentExecutions.length
        ? t("pages.studio.studioworkbenchsections.tracked.count", "{count} tracked", {
            count: recentExecutions.length,
          })
        : t("pages.studio.studioworkbenchsections.warming.up", "warming up"),
    },
    {
      label: t("pages.studio.studioworkbenchsections.human.gate", "human gate"),
      note: activeExecutionInteraction
        ? activeExecutionInteraction.prompt
        : t("pages.studio.studioworkbenchsections.no.human.approval.or.input", "No human approval or input is currently blocking this run."),
      status: activeExecutionInteraction ? 'warning' : 'active',
      value: humanGateValue,
    },
    {
      label: t("pages.studio.studioworkbenchsections.audit.fidelity", "audit fidelity"),
      note:
        selectedExecution
          ? auditReady
            ? t("pages.studio.studioworkbenchsections.run.audit.updated", "Run audit updated {updatedAt}.", {
                updatedAt: formatDateTime(
                  selectedExecution.auditUpdatedAtUtc ||
                    selectedExecution.updatedAtUtc,
                ),
              })
            : t("pages.studio.studioworkbenchsections.only.run.summary.available", "Only the run summary is available so far.")
          : t("pages.studio.studioworkbenchsections.no.run.selected.yet", "No run selected yet."),
      status: selectedExecution ? (auditReady ? 'active' : 'pending') : 'pending',
      value: auditReady
        ? t("pages.studio.studioworkbenchsections.run.audit.ready", "run audit ready")
        : t("pages.studio.studioworkbenchsections.summary.only", "summary only"),
    },
    {
      label: t("pages.studio.studioworkbenchsections.coverage", "coverage"),
      note:
        selectedExecution
          ? t(
              "pages.studio.studioworkbenchsections.coverage.detail",
              "{coverage} steps completed · {replyCount} role replies · {logCount} trace logs.",
              {
                coverage: selectedCoverage,
                logCount: traceLogCount,
                replyCount: selectedExecution.roleReplyCount ?? 0,
              },
            )
          : t("pages.studio.studioworkbenchsections.no.run.selected.yet", "No run selected yet."),
      status:
        selectedExecution && traceLogCount > 0
          ? 'active'
          : selectedExecution
            ? 'warning'
            : 'pending',
      value: selectedExecution ? selectedCoverage : formatObserveNotAvailable(),
    },
    {
      label: t("pages.studio.studioworkbenchsections.baseline", "baseline"),
      note: baselineExecution
        ? t(
            "pages.studio.studioworkbenchsections.comparing.against.baseline",
            "Comparing against the nearest previous run from the same member service.",
          )
        : t("pages.studio.studioworkbenchsections.observe.trustworthy.after.baseline", "Observe becomes more trustworthy after another member run lands and a baseline exists."),
      status: baselineExecution ? 'active' : 'pending',
      value: baselineExecution
        ? t("pages.studio.studioworkbenchsections.available", "available")
        : t("pages.studio.studioworkbenchsections.warming.up", "warming up"),
    },
  ];
}

function buildObservePlaybackEntries(
  logs: NonNullable<ReturnType<typeof buildExecutionTrace>>['logs'] | undefined,
): ObservePlaybackEntry[] {
  if (!logs?.length) {
    return [];
  }

  return logs
    .filter((log) =>
      Boolean(
        log.interaction ||
          log.title.toLowerCase().includes('approved') ||
          log.title.toLowerCase().includes('rejected') ||
          log.title.toLowerCase().includes('input submitted') ||
          log.title.toLowerCase().includes('signal sent') ||
          log.title.toLowerCase().includes('waiting for signal') ||
          log.title.toLowerCase().includes('stop requested'),
      ),
    )
    .slice(-6)
    .map((log) => ({
      detail: trimObserveText(
        sanitizeVisibleText(log.previewText || log.meta || ''),
        140,
      ),
      label: log.title,
      status: log.interaction
        ? 'waiting'
        : log.tone === 'pending'
          ? 'active'
          : 'done' as ObservePlaybackEntry['status'],
      timestamp: formatDateTime(log.timestamp),
    }))
    .reverse();
}

export type StudioExecutionPageProps = {
  readonly executions: QueryState<StudioExecutionSummary[]>;
  readonly selectedExecution: QueryState<StudioExecutionDetail>;
  readonly workflowGraph: {
    readonly roles: StudioGraphRole[];
    readonly steps: StudioGraphStep[];
    readonly nodes: Node<StudioGraphNodeData>[];
    readonly edges: Edge<StudioGraphEdgeData>[];
  };
  readonly draftWorkflowName: string;
  readonly activeWorkflowName: string;
  readonly activeWorkflowDescription: string;
  readonly activeDirectoryLabel: string;
  readonly selectedMemberLabel?: string;
  readonly currentImplementationLabel?: string;
  readonly currentImplementationKind?: StudioScopeBindingImplementationKind;
  readonly emptyState?: {
    readonly title: string;
    readonly description: string;
  } | null;
  readonly executionCanStop: boolean;
  readonly executionStopPending: boolean;
  readonly runPrompt: string;
  readonly executionNotice: StudioNoticeLike | null;
  readonly logsPopoutMode?: boolean;
  readonly logsDetached?: boolean;
  readonly onOpenExecution: (executionId: string) => void;
  readonly onResumeExecution: (
    interaction: ExecutionInteractionState,
    action: 'submit' | 'approve' | 'reject' | 'signal',
    userInput: string,
  ) => Promise<void>;
  readonly onStopExecution: () => void;
  readonly onPopOutLogs?: () => void;
};

export const StudioExecutionPage: React.FC<StudioExecutionPageProps> = ({
  executions,
  selectedExecution,
  workflowGraph,
  draftWorkflowName,
  activeWorkflowName,
  activeWorkflowDescription,
  activeDirectoryLabel,
  selectedMemberLabel,
  currentImplementationLabel,
  currentImplementationKind = 'unknown',
  emptyState = null,
  executionCanStop,
  executionStopPending,
  runPrompt,
  executionNotice,
  logsPopoutMode = false,
  logsDetached = false,
  onOpenExecution,
  onResumeExecution,
  onStopExecution,
  onPopOutLogs,
}) => {
  const [activeExecutionLogIndex, setActiveExecutionLogIndex] =
    React.useState<number | null>(null);
  const [copiedExecutionLogIndex, setCopiedExecutionLogIndex] =
    React.useState<number | null>(null);
  const [copiedAllExecutionLogs, setCopiedAllExecutionLogs] = React.useState(false);
  const [executionActionInput, setExecutionActionInput] = React.useState('');
  const [executionActionPendingKey, setExecutionActionPendingKey] =
    React.useState('');

  const selectedExecutionDetail = selectedExecution.data;
  const executionTrace = React.useMemo(
    () => buildExecutionTrace(selectedExecutionDetail),
    [selectedExecutionDetail],
  );
  const workflowGraphAvailable =
    currentImplementationKind === 'workflow' && workflowGraph.nodes.length > 0;

  React.useEffect(() => {
    setActiveExecutionLogIndex(executionTrace?.defaultLogIndex ?? null);
    setExecutionActionInput('');
    setExecutionActionPendingKey('');
  }, [executionTrace, selectedExecutionDetail?.executionId]);

  const currentMemberExecutions = React.useMemo(
    () => executions.data ?? [],
    [executions.data],
  );

  const activeExecutionLog =
    executionTrace && Number.isInteger(activeExecutionLogIndex)
      ? executionTrace.logs[activeExecutionLogIndex as number] || null
      : null;
  const activeExecutionInteraction =
    activeExecutionLog?.interaction &&
    activeExecutionLog.stepId &&
    executionTrace?.stepStates.get(activeExecutionLog.stepId)?.status === 'waiting'
      ? activeExecutionLog.interaction
      : null;
  const executionActionKeyBase =
    selectedExecutionDetail?.executionId && activeExecutionInteraction
      ? `${selectedExecutionDetail.executionId}:${activeExecutionInteraction.stepId}`
      : '';
  const decoratedExecutionNodes = React.useMemo(
    () =>
      decorateNodesForExecution(
        workflowGraph.nodes,
        executionTrace,
        activeExecutionLogIndex,
      ),
    [activeExecutionLogIndex, executionTrace, workflowGraph.nodes],
  );
  const decoratedExecutionEdges = React.useMemo(
    () =>
      decorateEdgesForExecution(
        workflowGraph.edges,
        workflowGraph.nodes,
        executionTrace,
        activeExecutionLogIndex,
      ),
    [activeExecutionLogIndex, executionTrace, workflowGraph.edges, workflowGraph.nodes],
  );
  const executionLogCount = executionTrace?.logs.length ?? 0;
  const executionExecutedSteps = React.useMemo(() => {
    const tracedStepCount = new Set(
      (executionTrace?.logs ?? [])
        .map((log) => log.stepId || '')
        .filter(Boolean),
    ).size;

    if (tracedStepCount > 0) {
      return tracedStepCount;
    }

    return selectedExecutionDetail?.completedSteps ?? 0;
  }, [executionTrace, selectedExecutionDetail?.completedSteps]);
  const executionTotalSteps =
    (workflowGraphAvailable
      ? workflowGraph.steps.length || workflowGraph.nodes.length
      : 0) ||
    selectedExecutionDetail?.totalSteps ||
    workflowGraph.steps.length ||
    workflowGraph.nodes.length;
  const executionStatusKey = String(selectedExecutionDetail?.status || '')
    .trim()
    .toLowerCase();
  const executionStatusLabel =
    executionStatusKey === 'running'
      ? t("pages.studio.studioworkbenchsections.running", "Running")
      : executionStatusKey === 'completed'
        ? t("pages.studio.studioworkbenchsections.completed", "Completed")
        : executionStatusKey === 'failed'
          ? t("pages.studio.studioworkbenchsections.execution.failed", "Execution failed")
          : selectedExecutionDetail
            ? t("pages.studio.studioworkbenchsections.waiting.for.execution", "Waiting for execution")
            : t("pages.studio.studioworkbenchsections.not.started", "Not started");
  const executionAccentColor =
    executionStatusKey === 'running'
      ? '#1890ff'
      : executionStatusKey === 'completed'
        ? '#52c41a'
        : executionStatusKey === 'failed'
          ? '#ff4d4f'
          : '#8c8c8c';
  const executionBarStyle: React.CSSProperties =
    executionStatusKey === 'running'
      ? {
          background: '#e6f7ff',
          borderBottom: '1px solid #91d5ff',
        }
      : executionStatusKey === 'completed'
        ? {
            background: '#f6ffed',
            borderBottom: '1px solid #b7eb8f',
          }
        : executionStatusKey === 'failed'
          ? {
              background: '#fff2f0',
              borderBottom: '1px solid #ffccc7',
            }
          : {
              background: '#fafafa',
              borderBottom: '1px solid #f0f0f0',
            };
  const executionPromptPreview = (selectedExecutionDetail?.prompt || runPrompt).trim();
  const executionDurationLabel = selectedExecutionDetail
    ? formatDurationBetween(
        selectedExecutionDetail.startedAtUtc,
        selectedExecutionDetail.completedAtUtc,
      )
    : '';
  const baselineExecution =
    currentMemberExecutions.find(
      (item) => item.executionId !== selectedExecutionDetail?.executionId,
    ) || null;
  const observeCompareRows = React.useMemo(
    () =>
      buildObserveCompareRows({
        baselineExecution,
        selectedExecution: selectedExecutionDetail,
        traceLogCount: executionLogCount,
        executedStepCount: executionExecutedSteps,
      }),
    [
      baselineExecution,
      executionExecutedSteps,
      executionLogCount,
      selectedExecutionDetail,
    ],
  );
  const observeHealthItems = React.useMemo(
    () =>
      buildObserveHealthItems({
        activeExecutionInteraction,
        baselineExecution,
        executions: currentMemberExecutions,
        selectedExecution: selectedExecutionDetail,
        traceLogCount: executionLogCount,
      }),
    [
      activeExecutionInteraction,
      baselineExecution,
      currentMemberExecutions,
      executionLogCount,
      selectedExecutionDetail,
    ],
  );
  const observePlaybackEntries = React.useMemo(
    () => buildObservePlaybackEntries(executionTrace?.logs),
    [executionTrace?.logs],
  );
  const workflowGraphFallback = React.useMemo(() => {
    switch (currentImplementationKind) {
      case 'script':
        return {
          title: t("pages.studio.studioworkbenchsections.script.members.do.not.expose", "Script members do not expose a workflow graph."),
          copy:
            t("pages.studio.studioworkbenchsections.observe.still.shows.runtime.logs", "Observe still shows runtime logs, audit facts, and run controls below. Workflow graph playback is available for workflow-backed members only."),
        };
      case 'gagent':
        return {
          title: t("pages.studio.studioworkbenchsections.gagent.members.do.not.expose", "GAgent members do not expose a workflow graph."),
          copy:
            t("pages.studio.studioworkbenchsections.observe.still.shows.runtime.logs.2", "Observe still shows runtime logs, audit facts, and run controls below. Workflow graph playback is available for workflow-backed members only."),
        };
      case 'workflow':
        return {
          title: t("pages.studio.studioworkbenchsections.workflow.graph.unavailable.for.this", "Workflow graph unavailable for this member."),
          copy:
            t("pages.studio.studioworkbenchsections.studio.could.not.resolve.matching", "Studio could not resolve a matching workflow document for the current member context right now. Logs, audit facts, and run controls are still available below."),
        };
      default:
        return {
          title: t("pages.studio.studioworkbenchsections.workflow.graph.unavailable", "Workflow graph unavailable."),
          copy:
            t("pages.studio.studioworkbenchsections.observe.still.shows.runtime.logs.3", "Observe still shows runtime logs, audit facts, and run controls below."),
        };
    }
  }, [currentImplementationKind]);

  const copyText = async (value: string): Promise<boolean> => {
    if (!value || typeof navigator === 'undefined' || !navigator.clipboard) {
      return false;
    }

    await navigator.clipboard.writeText(value);
    return true;
  };

  const showExecutionLogCopyFeedback = (mode: 'single' | 'all', index?: number) => {
    setCopiedExecutionLogIndex(mode === 'single' ? index ?? null : null);
    setCopiedAllExecutionLogs(mode === 'all');
    window.setTimeout(() => {
      setCopiedExecutionLogIndex(null);
      setCopiedAllExecutionLogs(false);
    }, 1600);
  };

  const handleExecutionLogClick = async (
    log: NonNullable<typeof executionTrace>['logs'][number],
    index: number,
  ) => {
    setActiveExecutionLogIndex(index);
    const copied = await copyText(formatExecutionLogClipboard(log));
    if (copied) {
      showExecutionLogCopyFeedback('single', index);
    }
  };

  const handleCopyAllExecutionLogs = async () => {
    const copied = await copyText(formatExecutionLogsClipboard(executionTrace));
    if (copied) {
      showExecutionLogCopyFeedback('all');
    }
  };

  const handleExecutionInteraction = async (
    interaction: ExecutionInteractionState,
    action: 'submit' | 'approve' | 'reject' | 'signal',
  ) => {
    const trimmedInput = executionActionInput.trim();
    if (interaction.kind === 'human_input' && !trimmedInput) {
      return;
    }

    const pendingKey = `${executionActionKeyBase}:${action}`;
    setExecutionActionPendingKey(pendingKey);

    try {
      await onResumeExecution(interaction, action, trimmedInput);
      setExecutionActionInput('');
    } finally {
      setExecutionActionPendingKey('');
    }
  };

  const renderExecutionLogs = (fullscreen: boolean) => {
    const hasRuns = currentMemberExecutions.length > 0;

    return (
      <section
        style={{
          background: '#ffffff',
          borderTop: fullscreen ? 'none' : '1px solid #f0f0f0',
          display: 'grid',
          gap: 12,
          minHeight: fullscreen ? '100%' : activeExecutionInteraction ? 300 : 220,
          padding: 12,
        }}
      >
        <div
          style={{
            alignItems: 'center',
            display: 'flex',
            gap: 12,
            justifyContent: 'space-between',
          }}
        >
          <div style={{ display: 'grid', gap: 2 }}>
            <Typography.Text strong style={{ fontSize: 12, margin: 0 }}>
              {t("pages.studio.studioworkbenchsections.execution.log", "execution log")}</Typography.Text>
            <Typography.Text type="secondary" style={{ fontSize: 11, margin: 0 }}>
              {executionLogCount} {t("pages.studio.studioworkbenchsections.events", "events")}</Typography.Text>
          </div>

          <div
            style={{
              alignItems: 'center',
              display: 'flex',
              flexWrap: 'wrap',
              gap: 8,
              justifyContent: 'flex-end',
            }}
          >
            {hasRuns ? (
              <select
                aria-label={t("pages.studio.studioworkbenchsections.select.run.record", "Select run record")}
                value={selectedExecutionDetail?.executionId || ''}
                onChange={(event) => {
                  if (event.target.value) {
                    onOpenExecution(event.target.value);
                  }
                }}
                style={{
                  border: '1px solid #d9d9d9',
                  borderRadius: 8,
                  fontSize: 12,
                  height: 30,
                  minWidth: 220,
                  padding: '0 8px',
                }}
              >
                <option value="">
                  {selectedExecutionDetail
                    ? t("pages.studio.studioworkbenchsections.copy", "{value1} · {value2}", { value1: formatDateTime(selectedExecutionDetail.startedAtUtc), value2: selectedExecutionDetail.status })
                    : t("pages.studio.studioworkbenchsections.runs", "{value1} runs", { value1: currentMemberExecutions.length })}
                </option>
                {currentMemberExecutions.map((execution) => (
                  <option key={execution.executionId} value={execution.executionId}>
                    {formatDateTime(execution.startedAtUtc)} · {execution.status}
                  </option>
                ))}
              </select>
            ) : null}

            {executionTrace?.logs?.length ? (
              <button
                className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                type="button"
                style={panelIconButtonStyle}
                title={t("pages.studio.studioworkbenchsections.copy.all.execution.logs", "Copy all execution logs")}
                aria-label={t("pages.studio.studioworkbenchsections.copy.all.execution.logs.2", "Copy all execution logs.")}
                onClick={() => void handleCopyAllExecutionLogs()}
              >
                {copiedAllExecutionLogs ? <CheckOutlined /> : <CopyOutlined />}
              </button>
            ) : null}

            {selectedExecutionDetail?.executionId && !fullscreen ? (
              <button
                aria-pressed={logsDetached}
                className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                type="button"
                style={{
                  ...panelIconButtonStyle,
                  borderColor: logsDetached ? '#1677ff' : '#e5e7eb',
                  color: logsDetached ? '#1677ff' : '#4b5563',
                }}
                title={logsDetached ? t("pages.studio.studioworkbenchsections.focus.log.window", "Focus log window") : t("pages.studio.studioworkbenchsections.pop.up.log.window", "Pop up log window")}
                aria-label={t("pages.studio.studioworkbenchsections.pop.out.execution.logs", "Pop out execution logs.")}
                onClick={onPopOutLogs}
              >
                <ExpandOutlined />
              </button>
            ) : null}

            {fullscreen ? (
              <button
                className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                type="button"
                style={panelIconButtonStyle}
                title={t("pages.studio.studioworkbenchsections.close.window", "close window")}
                aria-label={t("pages.studio.studioworkbenchsections.close.logs.window", "Close logs window.")}
                onClick={() => {
                  if (typeof window !== 'undefined') {
                    window.close();
                  }
                }}
              >
                <CloseOutlined />
              </button>
            ) : null}
          </div>
        </div>

        {activeExecutionInteraction ? (
          <div
            style={{
              border: '1px solid #e5e7eb',
              borderRadius: 16,
              display: 'grid',
              gap: 12,
              padding: 14,
            }}
          >
            <div
              style={{
                alignItems: 'center',
                display: 'flex',
                gap: 12,
                justifyContent: 'space-between',
              }}
            >
              <div>
                <Typography.Text strong>
                  {activeExecutionInteraction.kind === 'human_approval'
                    ? t("pages.studio.studioworkbenchsections.waiting.for.manual.approval", "Waiting for manual approval")
                    : activeExecutionInteraction.kind === 'wait_signal'
                      ? t("pages.studio.studioworkbenchsections.wait.for.external.signal", "wait for external signal")
                    : null}
                </Typography.Text>
                <div style={{ color: '#6b7280', fontSize: 12, marginTop: 4 }}>
                  {activeExecutionInteraction.kind === 'human_approval'
                    ? t("pages.studio.studioworkbenchsections.view.the.current.level", "View the current level and decide whether to pass or reject.")
                    : activeExecutionInteraction.kind === 'wait_signal'
                      ? t("pages.studio.studioworkbenchsections.after.sending.the.signal", "After sending the signal that the current step is waiting for, execution continues.")
                    : t("pages.studio.studioworkbenchsections.after.filling.in.the", "After filling in the missing information, the current step continues.")}
                </div>
              </div>
              <span
                style={{
                  background: '#f3f4f6',
                  borderRadius: 999,
                  color: '#4b5563',
                  fontSize: 11,
                  padding: '4px 10px',
                }}
              >
                {activeExecutionInteraction.stepId}
              </span>
            </div>

            {activeExecutionInteraction.prompt ? (
              <div
                style={{
                  background: '#fafafa',
                  border: '1px solid #f0f0f0',
                  borderRadius: 12,
                  color: '#374151',
                  fontSize: 12,
                  lineHeight: '20px',
                  padding: 12,
                }}
              >
                {activeExecutionInteraction.prompt}
              </div>
            ) : null}

            <textarea
              aria-label={t("pages.studio.studioworkbenchsections.perform.interactive.input", "Perform interactive input")}
              value={executionActionInput}
              onChange={(event) => setExecutionActionInput(event.target.value)}
              style={executionTextareaStyle}
              placeholder={
                activeExecutionInteraction.kind === 'human_approval'
                  ? t("pages.studio.studioworkbenchsections.optional.additional.information", "Optional additional information")
                  : activeExecutionInteraction.kind === 'wait_signal'
                    ? t("pages.studio.studioworkbenchsections.optional.signal.payload", "Optional signal payload")
                  : t("pages.studio.studioworkbenchsections.enter.what.you.need", "Enter what you need to continue")
              }
            />

            <div
              style={{
                display: 'flex',
                flexWrap: 'wrap',
                gap: 8,
                justifyContent: 'flex-end',
              }}
            >
              {activeExecutionInteraction.kind === 'human_approval' ? (
                <>
                  <Button
                    danger
                    style={executionActionButtonStyle}
                    disabled={
                      executionActionPendingKey ===
                      `${executionActionKeyBase}:reject`
                    }
                    onClick={() =>
                      void handleExecutionInteraction(
                        activeExecutionInteraction,
                        'reject',
                      )
                    }
                  >
                    {executionActionPendingKey ===
                    `${executionActionKeyBase}:reject`
                      ? t("pages.studio.studioworkbenchsections.rejecting", "Rejecting...")
                      : t("pages.studio.studioworkbenchsections.turn.down", "turn down")}
                  </Button>
                  <Button
                    type="primary"
                    style={executionActionButtonStyle}
                    disabled={
                      executionActionPendingKey ===
                      `${executionActionKeyBase}:approve`
                    }
                    onClick={() =>
                      void handleExecutionInteraction(
                        activeExecutionInteraction,
                        'approve',
                      )
                    }
                  >
                    {executionActionPendingKey ===
                    `${executionActionKeyBase}:approve`
                      ? t("pages.studio.studioworkbenchsections.passing", "Passing...")
                      : t("pages.studio.studioworkbenchsections.pass", "pass")}
                  </Button>
                </>
              ) : activeExecutionInteraction.kind === 'wait_signal' ? (
                <Button
                  type="primary"
                  style={executionActionButtonStyle}
                  disabled={
                    executionActionPendingKey ===
                    `${executionActionKeyBase}:signal`
                  }
                  onClick={() =>
                    void handleExecutionInteraction(
                      activeExecutionInteraction,
                      'signal',
                    )
                  }
                >
                  {executionActionPendingKey ===
                  `${executionActionKeyBase}:signal`
                    ? t("pages.studio.studioworkbenchsections.sending", "Sending...")
                    : t("pages.studio.studioworkbenchsections.send.signal", "send signal")}
                </Button>
              ) : (
                <Button
                  type="primary"
                  style={executionActionButtonStyle}
                  disabled={
                    executionActionPendingKey ===
                    `${executionActionKeyBase}:submit`
                  }
                  onClick={() =>
                    void handleExecutionInteraction(
                      activeExecutionInteraction,
                      'submit',
                    )
                  }
                >
                  {executionActionPendingKey ===
                  `${executionActionKeyBase}:submit`
                    ? t("pages.studio.studioworkbenchsections.submitting", "Submitting...")
                    : t("pages.studio.studioworkbenchsections.submit", "submit")}
                </Button>
              )}
            </div>
          </div>
        ) : null}

        <div
          style={{
            display: 'grid',
            gap: 10,
            maxHeight: fullscreen ? 'none' : 320,
            overflowY: 'auto',
          }}
        >
          {!hasRuns ? (
            <StudioCatalogEmptyPanel
              icon={<CaretRightFilled style={{ color: '#CBD5E1' }} />}
              title={t("pages.studio.studioworkbenchsections.no.running.records.yet", "No running records yet")}
              copy={t("pages.studio.studioworkbenchsections.after.member.is.triggered", "After a member is triggered to run, the execution log will be displayed here.")}
            />
          ) : executionTrace?.logs?.length ? (
            executionTrace.logs.map((log, index) => (
              <button
                className={AEVATAR_PRESSABLE_CARD_CLASS}
                key={`${log.timestamp}-${log.stepId || 'run'}-${log.title}`}
                type="button"
                onClick={() => void handleExecutionLogClick(log, index)}
                style={{
                  ...logCardBaseStyle,
                  background:
                    activeExecutionLogIndex === index ? '#F5F7FF' : '#FFFFFF',
                  borderColor:
                    activeExecutionLogIndex === index ? '#91caff' : '#eef2f7',
                }}
                title={t("pages.studio.studioworkbenchsections.click.to.copy.this", "Click to copy this post")}
              >
                <div
                  style={{
                    alignItems: 'center',
                    display: 'flex',
                    gap: 8,
                    justifyContent: 'space-between',
                  }}
                >
                  <Typography.Text strong style={{ fontSize: 12 }}>
                    {log.title}
                  </Typography.Text>
                  <div
                    style={{
                      alignItems: 'center',
                      color: '#9ca3af',
                      display: 'flex',
                      gap: 8,
                      fontSize: 11,
                    }}
                  >
                    {copiedExecutionLogIndex === index ? (
                      <span style={{ color: '#1677ff' }}>
                        <CheckOutlined /> {t("pages.studio.studioworkbenchsections.copied", "Copied")}</span>
                    ) : null}
                    {formatDateTime(log.timestamp)}
                  </div>
                </div>
                {log.meta ? (
                  <Typography.Text type="secondary" style={{ fontSize: 11 }}>
                    {sanitizeVisibleText(log.meta)}
                  </Typography.Text>
                ) : null}
                <div style={{ color: '#374151', fontSize: 12 }}>
                  {sanitizeVisibleText(log.previewText || log.meta) || log.title}
                </div>
              </button>
            ))
          ) : (
            <StudioCatalogEmptyPanel
              icon={<FileTextOutlined style={{ color: '#CBD5E1' }} />}
              title={t("pages.studio.studioworkbenchsections.no.logs.yet", "No logs yet")}
              copy={t("pages.studio.studioworkbenchsections.after.selecting.running.record", "After selecting a running record, step execution and status changes will be displayed here.")}
            />
          )}
        </div>
      </section>
    );
  };

  if (logsPopoutMode) {
    return (
      <div style={{ ...fillCardStyle, height: '100%' }}>
        {selectedExecution.isError ? (
          <StudioCompactNotice
            type="error"
            title={t("pages.studio.studioworkbenchsections.failed.to.read.execution", "Failed to read execution details")}
            description={describeError(selectedExecution.error)}
          />
        ) : selectedExecution.data ? (
          renderExecutionLogs(true)
        ) : (
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description={t("pages.studio.studioworkbenchsections.after.selecting.running.record.2", "After selecting a running record, the execution log will be displayed here.")}
          />
        )}
      </div>
    );
  }

  const compactNotices: StudioCompactNoticeLike[] = [];
  if (executions.isError) {
    compactNotices.push({
      description: describeError(executions.error),
      title: t("pages.studio.studioworkbenchsections.failed.to.read.run", "Failed to read run list"),
      type: 'error',
    });
  }
  if (selectedExecution.isError) {
    compactNotices.push({
      description: describeError(selectedExecution.error),
      title: t("pages.studio.studioworkbenchsections.failed.to.read.execution.2", "Failed to read execution details"),
      type: 'error',
    });
  }
  if (executionNotice && executionNotice.type !== 'error') {
    compactNotices.push({
      description: executionNotice.message,
      title:
        executionNotice.type === 'info'
          ? t("pages.studio.studioworkbenchsections.requested.to.stop.running", "Requested to stop running")
          : t("pages.studio.studioworkbenchsections.execution.status.updated", "Execution status updated"),
      type: executionNotice.type,
    });
  }
  if (selectedExecutionDetail?.error) {
    compactNotices.push({
      description: selectedExecutionDetail.error,
      title: t("pages.studio.studioworkbenchsections.execution.exception", "Execution exception"),
      type: 'error',
    });
  }
  if (emptyState) {
    compactNotices.push({
      description: emptyState.description,
      title: emptyState.title,
      type: 'info',
    });
  }

  return (
    <div style={cardStackStyle}>
      <ConsoleOperationNotice
        errorMessage={t(
          'pages.studio.studioworkbenchsections.executionActionFailed',
          'Execution action could not be completed. Try again.',
        )}
        notice={
          executionNotice?.type === 'error' ? executionNotice : null
        }
      />
      {compactNotices.length > 0 ? (
        <div style={studioCompactNoticeStackStyle}>
          {compactNotices.map((notice) => (
            <StudioCompactNotice
              key={String(notice.title)}
              {...notice}
            />
          ))}
        </div>
      ) : null}

      <section style={observeRunHeaderStyle}>
        <div style={{ display: 'grid', gap: 10, minWidth: 0 }}>
          <Space wrap size={[8, 8]}>
            <AevatarStatusTag
              domain="run"
              label="run"
              status={executionStatusKey || 'idle'}
            />
            <Tag>
              {currentImplementationKind === 'workflow'
                ? 'workflow'
                : currentImplementationKind === 'script'
                  ? 'script'
                  : currentImplementationKind === 'gagent'
                    ? 'gagent'
                    : 'member'}
            </Tag>
            <Tag color={selectedExecutionDetail?.auditSource === 'run-audit' ? 'green' : 'default'}>
              {selectedExecutionDetail?.auditSource === 'run-audit'
                ? t("pages.studio.studioworkbenchsections.audit.ready", "audit ready")
                : t("pages.studio.studioworkbenchsections.summary.only", "summary only")}
            </Tag>
            <Tag color={baselineExecution ? 'blue' : 'default'}>
              {baselineExecution
                ? t("pages.studio.studioworkbenchsections.baseline.ready", "baseline ready")
                : t("pages.studio.studioworkbenchsections.baseline.warming", "baseline warming")}
            </Tag>
          </Space>
          <h2 style={observeRunTitleStyle}>
            {selectedMemberLabel ||
              activeWorkflowName ||
              draftWorkflowName ||
              t("pages.studio.studioworkbenchsections.current.member", "Current member")}
          </h2>
          <p style={observeRunSubtitleStyle}>
            {currentImplementationLabel ||
              activeWorkflowName ||
              draftWorkflowName ||
              t("pages.studio.studioworkbenchsections.current.implementation", "Current implementation")}{' '}
            · {selectedExecutionDetail?.executionId ||
              t("pages.studio.studioworkbenchsections.no.run.selected", "No run selected")}
            {executionDurationLabel ? t("pages.studio.studioworkbenchsections.copy.2", "· {value1}", { value1: executionDurationLabel }) : ''}
          </p>
          {executionPromptPreview ? (
            <Typography.Paragraph
              ellipsis={{ rows: 2, expandable: true, symbol: 'more' }}
              style={{ margin: 0 }}
              type="secondary"
            >
              {t("pages.studio.studioworkbenchsections.input.preview", "Input: {preview}", {
                preview: executionPromptPreview,
              })}
            </Typography.Paragraph>
          ) : null}
        </div>

        {executionCanStop ? (
          <Button
            danger
            loading={executionStopPending}
            disabled={executionStopPending}
            onClick={onStopExecution}
          >
            {t("pages.studio.studioworkbenchsections.stop.run", "Stop run")}</Button>
        ) : null}

        <div style={{ gridColumn: '1 / -1' }}>
          <div style={observeRunMetricGridStyle}>
            <div style={observeRunMetricStyle}>
              <Typography.Text type="secondary">{t("pages.studio.studioworkbenchsections.progress", "Progress")}</Typography.Text>
              <Typography.Text strong>
                {executionExecutedSteps}/{executionTotalSteps || 0} {t("pages.studio.studioworkbenchsections.steps", "steps")}</Typography.Text>
            </div>
            <div style={observeRunMetricStyle}>
              <Typography.Text type="secondary">{t("pages.studio.studioworkbenchsections.events.2", "Events")}</Typography.Text>
              <Typography.Text strong>{executionLogCount} {t("pages.studio.studioworkbenchsections.logs", "logs")}</Typography.Text>
            </div>
	            <div style={observeRunMetricStyle}>
	              <Typography.Text type="secondary">{t("pages.studio.studioworkbenchsections.runtime", "Runtime")}</Typography.Text>
	              <Typography.Text strong>
	                {selectedExecutionDetail?.actorId
                    ? t("pages.studio.studioworkbenchsections.runtime.available", "Runtime available")
                    : formatObserveNotAvailable()}
	              </Typography.Text>
	            </div>
            <div style={observeRunMetricStyle}>
              <Typography.Text type="secondary">{t("pages.studio.studioworkbenchsections.state.version", "State Version")}</Typography.Text>
              <Typography.Text strong>
                {selectedExecutionDetail?.stateVersion !== null &&
                selectedExecutionDetail?.stateVersion !== undefined
                  ? `v${selectedExecutionDetail.stateVersion}`
                  : formatObserveNotAvailable()}
              </Typography.Text>
            </div>
            <div style={observeRunMetricStyle}>
              <Typography.Text type="secondary">{t("pages.studio.studioworkbenchsections.updated", "Updated")}</Typography.Text>
              <Typography.Text strong>
                {selectedExecutionDetail?.updatedAtUtc
                  ? formatDateTime(selectedExecutionDetail.updatedAtUtc)
                  : formatObserveNotAvailable()}
              </Typography.Text>
            </div>
          </div>
        </div>
      </section>

      <div
        style={observeStageShellStyle}
      >
        <div
          style={{
            ...executionBarStyle,
            ...observeStageBarStyle,
          }}
        >
          <span
            style={{
              width: 8,
              height: 8,
              borderRadius: '50%',
              background: executionAccentColor,
            }}
          />
          <Typography.Text
            strong
            style={{ color: executionAccentColor, margin: 0 }}
          >
            {executionStatusLabel}
          </Typography.Text>
          <Typography.Text type="secondary" style={{ margin: 0 }}>
            {(activeWorkflowName || draftWorkflowName || t("pages.studio.studioworkbenchsections.current.process", "current process")).trim() || t("pages.studio.studioworkbenchsections.current.process.2", "current process")} {t("pages.studio.studioworkbenchsections.executed", "· Executed")}{executionExecutedSteps}/{executionTotalSteps || 0} {t("pages.studio.studioworkbenchsections.step", "step")}{executionDurationLabel ? t("pages.studio.studioworkbenchsections.time.spent", "· Time spent {value1}", { value1: executionDurationLabel }) : ''}
          </Typography.Text>
        </div>

        <div
          style={{
            background: '#FAFAFA',
            display: 'flex',
            flex: 1,
            flexDirection: 'column',
            minHeight: 0,
          }}
        >
          <div
            style={{
              flex: 1,
              minHeight: 320,
              overflow: 'hidden',
              position: 'relative',
            }}
          >
            {workflowGraphAvailable ? (
              <GraphCanvas
                height="100%"
                bottomInset={0}
                variant="studio"
                nodes={decoratedExecutionNodes}
                edges={decoratedExecutionEdges}
                selectedNodeId={
                  activeExecutionLog?.stepId
                    ? decoratedExecutionNodes.find(
                        (node) => node.data.stepId === activeExecutionLog.stepId,
                      )?.id
                    : undefined
                }
                onNodeSelect={(nodeId) => {
                  const stepId =
                    decoratedExecutionNodes.find((node) => node.id === nodeId)?.data.stepId ||
                    '';
                  const logIndex = findExecutionLogIndexForStep(executionTrace, stepId);
                  if (logIndex !== null) {
                    setActiveExecutionLogIndex(logIndex);
                  }
                }}
              />
            ) : (
              <div
                style={{
                  alignItems: 'center',
                  display: 'grid',
                  height: '100%',
                  padding: 24,
                }}
              >
                <StudioCatalogEmptyPanel
                  icon={<FileTextOutlined style={{ color: '#CBD5E1' }} />}
                  title={workflowGraphFallback.title}
                  copy={workflowGraphFallback.copy}
                />
              </div>
            )}
          </div>

          {renderExecutionLogs(false)}
        </div>
      </div>

      <div style={observeDetailsGridStyle}>
        <AevatarPanel
          title={t("pages.studio.studioworkbenchsections.run.compare", "Run Compare")}
          titleHelp={t("pages.studio.studioworkbenchsections.compare.the.selected.run.with", "Compare the selected run with the nearest previous run from this member.")}
          extra={
            baselineExecution ? (
              <Tag>
                {t("pages.studio.studioworkbenchsections.baseline.execution.label", "Baseline")}
              </Tag>
            ) : (
              <Tag>{t("pages.studio.studioworkbenchsections.no.baseline.yet", "no baseline yet")}</Tag>
            )
          }
        >
          <div style={{ display: 'grid', gap: 10 }}>
            {observeCompareRows.map((row) => (
              <div
                key={row.label}
                style={{
                  alignItems: 'center',
                  borderBottom: '1px solid #f3f4f6',
                  display: 'grid',
                  gap: 10,
                  gridTemplateColumns: '104px minmax(0, 1fr) minmax(0, 1fr) auto',
                  paddingBottom: 10,
                }}
              >
                <Typography.Text type="secondary">{row.label}</Typography.Text>
                <AevatarTooltip title={row.current}>
                  <Typography.Text ellipsis>{row.current}</Typography.Text>
                </AevatarTooltip>
                <AevatarTooltip title={row.baseline}>
                  <Typography.Text ellipsis type="secondary">
                    {row.baseline}
                  </Typography.Text>
                </AevatarTooltip>
                <AevatarStatusTag
                  domain="observation"
                  label="delta"
                  status={row.delta}
                />
              </div>
            ))}
          </div>
        </AevatarPanel>

        <AevatarPanel
          title={t("pages.studio.studioworkbenchsections.human.playback", "Human Playback")}
          titleHelp={t("pages.studio.studioworkbenchsections.show.approvals.inputs.signals.and", "Show approvals, inputs, signals, and recent human hand-offs from the selected run.")}
        >
          {observePlaybackEntries.length > 0 ? (
            <div style={{ display: 'grid', gap: 10 }}>
              {observePlaybackEntries.map((entry) => (
                <div
                  key={`${entry.timestamp}-${entry.label}`}
                  style={{
                    borderBottom: '1px solid #f3f4f6',
                    display: 'grid',
                    gap: 4,
                    paddingBottom: 10,
                  }}
                >
                  <div
                    style={{
                      alignItems: 'center',
                      display: 'flex',
                      gap: 8,
                      justifyContent: 'space-between',
                    }}
                  >
                    <Typography.Text strong>{entry.label}</Typography.Text>
                    <AevatarStatusTag
                      domain="observation"
                      label="playback"
                      status={entry.status}
                    />
                  </div>
                  <Typography.Text type="secondary">{entry.detail}</Typography.Text>
                  <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                    {entry.timestamp}
                  </Typography.Text>
                </div>
              ))}
            </div>
          ) : (
            <StudioCatalogEmptyPanel
              icon={<UserOutlined style={{ color: '#CBD5E1' }} />}
              title={t("pages.studio.studioworkbenchsections.no.manual.intervention.yet", "No manual intervention yet")}
              copy={t("pages.studio.studioworkbenchsections.the.currently.selected.run", "The currently selected run does not yet have an approval, input, or replay fragment.")}
            />
          )}
        </AevatarPanel>

        <AevatarPanel
          title={t("pages.studio.studioworkbenchsections.observation.facts", "Observation Facts")}
          titleHelp={t("pages.studio.studioworkbenchsections.keep.identity.provenance.and.trace", "Keep identity, provenance, and trace trust visible without sending operators back to Bind.")}
        >
          <div style={{ display: 'grid', gap: 14 }}>
            <div style={observeMetricGridStyle}>
              {observeHealthItems.map((item) => (
                <div key={item.label} style={observeMetricCardStyle}>
                  <div
                    style={{
                      alignItems: 'center',
                      display: 'flex',
                      gap: 8,
                      justifyContent: 'space-between',
                    }}
                  >
                    <Typography.Text type="secondary">{item.label}</Typography.Text>
                    <AevatarStatusTag
                      domain="observation"
                      label="health"
                      status={item.status}
                    />
                  </div>
                  <Typography.Text strong>{item.value}</Typography.Text>
                  <Typography.Text type="secondary">{item.note}</Typography.Text>
                </div>
              ))}
            </div>
            <Space wrap size={[8, 8]}>
              <Tag>
                {selectedExecutionDetail
                  ? t("pages.studio.studioworkbenchsections.runtime.facts.available", "Runtime facts available")
                  : formatObserveNotAvailable()}
              </Tag>
              <Tag>
                {activeDirectoryLabel ||
                  t("pages.studio.studioworkbenchsections.workspace", "Workspace")}
              </Tag>
            </Space>
            <Typography.Text type="secondary">
              {selectedExecutionDetail
                ? t(
                    "pages.studio.studioworkbenchsections.selected.facts.come.from",
                    "Selected facts come from the current member runtime.",
                  )
                : activeWorkflowDescription ||
                  t("pages.studio.studioworkbenchsections.the.current.observe.page", "The current Observe page only displays the running facts of the current member; contract and release information remain in Bind.")}
            </Typography.Text>
            <Typography.Text type="secondary">
              {baselineExecution
                ? t(
                    "pages.studio.studioworkbenchsections.baseline.detail",
                    "Baseline started {startedAt}.",
                    {
                      startedAt: formatDateTime(baselineExecution.startedAtUtc),
                    },
                  )
                : t("pages.studio.studioworkbenchsections.observe.can.compare.more", "Observe can compare more meaningfully after another run lands.")}
            </Typography.Text>
          </div>
        </AevatarPanel>
      </div>
    </div>
  );
};
