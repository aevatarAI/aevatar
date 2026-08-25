import {
  ArrowLeftOutlined,
  CheckCircleFilled,
  ClockCircleOutlined,
  CloseCircleFilled,
  ExclamationCircleFilled,
  LoadingOutlined,
  NodeIndexOutlined,
  ReloadOutlined,
} from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { type Edge, MarkerType, type Node, Position } from '@xyflow/react';
import {
  Alert,
  Button,
  Empty,
  Modal,
  Space,
  Tabs,
  Tag,
  Typography,
} from 'antd';
import React from 'react';
import {
  WorkflowActivityApiError,
  workflowActivityApi,
} from '@/shared/api/workflowActivityApi';
import { formatDateTime } from '@/shared/datetime/dateTime';
import GraphCanvas from '@/shared/graphs/GraphCanvas';
import { t } from '@/shared/i18n/messages';
import type {
  WorkflowActivityRunDetail,
  WorkflowActivityRunFeedRow,
  WorkflowActivityRunGraph,
  WorkflowActivityRunSummary,
  WorkflowActivityStep,
  WorkflowActivityTimelineEvent,
  WorkflowRunForkAcceptedReceipt,
} from '@/shared/models/workflowActivity';
import { history } from '@/shared/navigation/history';
import type {
  StudioGraphEdgeData,
  StudioGraphNodeData,
} from '@/shared/studio/graph';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import { useConsoleLocation } from '../hooks/useConsoleLocation';
import {
  buildWorkflowActivityRunHref,
  buildWorkflowActivitySectionHref,
} from '../navigation';
import TechnicalDetails from '../TechnicalDetails';
import WorkflowActivityVNextShell from '../WorkflowActivityVNextShell';
import {
  classifyRunFailure,
  type RunFailureAction,
  type RunFailureEvidence,
  RunFailureToastContent,
} from './runFailurePresentation';
import { getRunOriginLabel, getRunStatusPresentation } from './runPresentation';

type RunStatusTone = 'default' | 'processing' | 'success' | 'warning' | 'error';

type RunHistoryEntry = WorkflowActivityRunSummary & {
  readonly context?: string;
  readonly inputSummary?: string;
  readonly workflowId?: string;
};

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? '';
}

function getRunBadgeClass(status: string): string {
  return `wa-vnext__status wa-vnext__status--${getRunStatusPresentation(status).className}`;
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function failureTitle(error: unknown): string {
  if (error instanceof WorkflowActivityApiError) {
    if (error.status === 401) {
      return t(
        'workflowActivityVNext.state.unauthorized',
        'Sign in to continue',
      );
    }
    if (error.status === 403) {
      return t(
        'workflowActivityVNext.state.forbidden',
        "You don't have access to this workspace",
      );
    }
  }
  return t('workflowActivityVNext.run.unavailable', 'Run unavailable');
}

function readApiFailure(error: unknown): RunFailureEvidence | null {
  if (error instanceof WorkflowActivityApiError) {
    return {
      code: error.code,
      correlationId: error.correlationId,
      message: error.message,
      retryAfterSeconds: error.retryAfterSeconds,
      status: error.status,
    };
  }
  if (error instanceof Error) return { message: error.message };
  return null;
}

function readCommittedRunFailure(
  run: WorkflowActivityRunDetail | undefined,
): RunFailureEvidence | null {
  if (!run) return null;
  const status = run.summary.status.trim().toLowerCase();
  if (status === 'cancelled' || status === 'canceled') {
    return {
      code: 'RUN_CANCELLED',
      message: t('workflowActivityVNext.failure.cancelled', 'Run cancelled.'),
    };
  }
  if (run.summary.success !== false && status !== 'failed') return null;

  const primaryDiagnostic = run.diagnostics.find(
    (diagnostic) => diagnostic.severity.trim().toLowerCase() === 'error',
  );
  return {
    code: primaryDiagnostic?.code,
    message:
      primaryDiagnostic?.message ||
      run.finalError ||
      run.steps.find((step) => step.error)?.error,
  };
}

function formatRunStatus(status: string | null | undefined): string {
  const normalized = trimOptional(status)
    .toLowerCase()
    .replace(/[\s-]+/g, '_');
  if (!normalized) {
    return t('workflowActivityVNext.common.unknown', 'Unknown');
  }

  return normalized
    .split('_')
    .filter(Boolean)
    .map((segment) => `${segment.charAt(0).toUpperCase()}${segment.slice(1)}`)
    .join(' ');
}

function getStepExecutionStatus(
  step: WorkflowActivityStep,
): 'idle' | 'active' | 'waiting' | 'completed' | 'failed' {
  if (step.success === true) return 'completed';
  if (step.success === false || trimOptional(step.error)) return 'failed';
  if (trimOptional(step.suspensionType)) return 'waiting';
  if (trimOptional(step.requestedAtUtc) && !trimOptional(step.completedAtUtc)) {
    return 'active';
  }
  return 'idle';
}

function getStepStatusTone(step: WorkflowActivityStep): RunStatusTone {
  const status = getStepExecutionStatus(step);
  if (status === 'completed') return 'success';
  if (status === 'failed') return 'error';
  if (status === 'active') return 'processing';
  if (status === 'waiting') return 'warning';
  return 'default';
}

function getStepStatusLabel(step: WorkflowActivityStep): string {
  const status = getStepExecutionStatus(step);
  if (status === 'active') return 'Running';
  if (status === 'idle') return 'Pending';
  return formatRunStatus(status);
}

function renderStepStatusIcon(step: WorkflowActivityStep): React.ReactNode {
  const status = getStepExecutionStatus(step);
  if (status === 'completed')
    return <CheckCircleFilled style={{ color: '#16a34a' }} />;
  if (status === 'failed')
    return <CloseCircleFilled style={{ color: '#dc2626' }} />;
  if (status === 'active')
    return <LoadingOutlined style={{ color: '#2563eb' }} />;
  if (status === 'waiting')
    return <ExclamationCircleFilled style={{ color: '#d97706' }} />;
  return <ClockCircleOutlined style={{ color: '#94a3b8' }} />;
}

function formatDurationMs(value: number | null | undefined): string {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) {
    return 'n/a';
  }
  if (value < 1000) return `${Math.round(value)}ms`;
  if (value < 60_000)
    return `${(value / 1000).toFixed(value < 10_000 ? 2 : 1)}s`;
  const minutes = Math.floor(value / 60_000);
  const seconds = Math.round((value % 60_000) / 1000);
  return `${minutes}m ${seconds}s`;
}

function getRunDurationMs(
  run: WorkflowActivityRunDetail | WorkflowActivityRunSummary,
  steps?: readonly WorkflowActivityStep[],
): number | null {
  const summary = 'summary' in run ? run.summary : run;
  const startedAt = Date.parse(trimOptional(summary.startedAtUtc));
  const updatedAt = Date.parse(trimOptional(summary.updatedAtUtc));
  if (
    Number.isFinite(startedAt) &&
    Number.isFinite(updatedAt) &&
    updatedAt > startedAt
  ) {
    return updatedAt - startedAt;
  }

  const durations = (steps ?? [])
    .map((step) => step.durationMs)
    .filter(
      (value): value is number =>
        typeof value === 'number' && Number.isFinite(value) && value >= 0,
    );
  if (!durations.length) return null;
  const total = durations.reduce((sum, duration) => sum + duration, 0);
  return total > 0 ? total : null;
}

function getStepDisplayName(
  step: WorkflowActivityStep | null | undefined,
): string {
  const stepId = trimOptional(step?.stepId);
  const stepType = trimOptional(step?.stepType);
  return stepId || stepType || t('workflowActivityVNext.run.step', 'Step');
}

function summarizeStepParameters(step: WorkflowActivityStep): string {
  const entries = Object.entries(step.requestParameters).filter(
    ([key, value]) => trimOptional(key) || trimOptional(value),
  );
  if (!entries.length) {
    return step.stepType || t('workflowActivityVNext.run.step', 'step');
  }
  return entries
    .slice(0, 2)
    .map(([key, value]) => `${key}: ${value}`)
    .join(' | ');
}

function getStepSortTimestamp(step: WorkflowActivityStep): number {
  return (
    Date.parse(
      trimOptional(step.requestedAtUtc) || trimOptional(step.completedAtUtc),
    ) || 0
  );
}

function getSelectedStepDefaultId(
  steps: readonly WorkflowActivityStep[],
  graph?: WorkflowActivityRunGraph,
): string {
  const failed = steps.find(
    (step) => step.success === false || trimOptional(step.error),
  );
  if (failed) return failed.stepId;

  const rootStepId = trimOptional(
    graph?.nodes.find((node) => node.nodeId === graph?.rootNodeId)?.stepId,
  );
  if (rootStepId && steps.some((step) => step.stepId === rootStepId)) {
    return rootStepId;
  }

  return steps[0]?.stepId ?? '';
}

function buildExecutionGraph(
  detail: WorkflowActivityRunDetail | undefined,
  graph: WorkflowActivityRunGraph | undefined,
  selectedStepId: string,
): {
  readonly edges: Edge<StudioGraphEdgeData>[];
  readonly nodes: Node<StudioGraphNodeData>[];
  readonly orderedSteps: WorkflowActivityStep[];
} {
  const orderedSteps = [...(detail?.steps ?? [])].sort((left, right) => {
    const leftTime = getStepSortTimestamp(left);
    const rightTime = getStepSortTimestamp(right);
    if (leftTime !== rightTime) return leftTime - rightTime;
    return left.stepId.localeCompare(right.stepId);
  });
  const stepById = new Map(
    orderedSteps.map((step) => [step.stepId, step] as const),
  );
  const nodeById = new Map(
    graph?.nodes.map((node) => [node.nodeId, node] as const) ?? [],
  );
  const stepIdByNodeId = new Map(
    graph?.nodes.map(
      (node) => [node.nodeId, trimOptional(node.stepId)] as const,
    ) ?? [],
  );
  const nodes: Node<StudioGraphNodeData>[] = orderedSteps.map(
    (step, index) => ({
      data: {
        branchCount: trimOptional(step.branchKey) ? 1 : 0,
        executionFocused: step.stepId === selectedStepId,
        executionStatus: getStepExecutionStatus(step),
        kind: 'step',
        label: getStepDisplayName(step),
        parametersSummary: summarizeStepParameters(step),
        stepId: step.stepId,
        stepType: step.stepType || 'step',
        subtitle: step.stepType || t('workflowActivityVNext.run.step', 'Step'),
        targetRole: step.targetRole,
        title: getStepDisplayName(step),
      },
      id: `step:${step.stepId}`,
      position: {
        x: 120 + index * 310,
        y: 150 + (index % 2 === 0 ? 0 : 44),
      },
      sourcePosition: Position.Right,
      targetPosition: Position.Left,
      type: 'studioWorkflowNode',
    }),
  );
  const edges: Edge<StudioGraphEdgeData>[] = [];
  const seen = new Set<string>();
  const pushEdge = (
    sourceStepId: string,
    targetStepId: string,
    implicit: boolean,
    branchLabel?: string,
  ) => {
    if (!stepById.has(sourceStepId) || !stepById.has(targetStepId)) return;
    const key = `${sourceStepId}->${targetStepId}:${branchLabel ?? ''}`;
    if (seen.has(key)) return;
    seen.add(key);
    edges.push({
      animated: false,
      data: {
        branchLabel,
        implicit,
        kind: branchLabel ? 'branch' : 'next',
      },
      id: `edge:${sourceStepId}:${targetStepId}:${edges.length}`,
      label: branchLabel || undefined,
      markerEnd: {
        color: implicit ? '#94a3b8' : '#1677ff',
        height: 10,
        type: MarkerType.ArrowClosed,
        width: 10,
      },
      source: `step:${sourceStepId}`,
      style: {
        stroke: implicit ? '#94a3b8' : '#1677ff',
        strokeDasharray: implicit ? '5 5' : undefined,
        strokeWidth: implicit ? 1.6 : 2.4,
      },
      target: `step:${targetStepId}`,
      type: 'smoothstep',
    });
  };

  if (graph?.edges.length) {
    for (const edge of graph.edges) {
      const sourceStepId =
        trimOptional(stepIdByNodeId.get(edge.fromNodeId)) ||
        nodeById.get(edge.fromNodeId)?.stepId ||
        '';
      const targetStepId =
        trimOptional(stepIdByNodeId.get(edge.toNodeId)) ||
        nodeById.get(edge.toNodeId)?.stepId ||
        '';
      if (sourceStepId && targetStepId) {
        pushEdge(
          sourceStepId,
          targetStepId,
          false,
          trimOptional(edge.branchKey) || undefined,
        );
      }
    }
  }

  for (const step of orderedSteps) {
    const nextStepId = trimOptional(step.nextStepId);
    if (nextStepId) {
      pushEdge(
        step.stepId,
        nextStepId,
        false,
        trimOptional(step.branchKey) || undefined,
      );
    }
  }

  if (!edges.length) {
    orderedSteps.forEach((step, index) => {
      const next = orderedSteps[index + 1];
      if (next) {
        pushEdge(step.stepId, next.stepId, true);
      }
    });
  }

  return { edges, nodes, orderedSteps };
}

function filterTimelineForStep(
  timeline: readonly WorkflowActivityTimelineEvent[],
  selectedStepId: string,
): readonly WorkflowActivityTimelineEvent[] {
  if (!selectedStepId) return timeline;
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
    <div className="wa-vnext-run-detail__kv">
      {entries.map(([key, value]) => (
        <div className="wa-vnext-run-detail__kv-row" key={key}>
          <div className="wa-vnext-run-detail__kv-key">{key}</div>
          <div className="wa-vnext-run-detail__kv-value">{value || 'n/a'}</div>
        </div>
      ))}
    </div>
  );
}

function renderTextBlock(value: string) {
  const normalized = value.trim();
  if (!normalized) {
    return <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} />;
  }
  return <pre className="wa-vnext-run-detail__pre">{normalized}</pre>;
}

function formatRunTime(run: WorkflowActivityRunSummary): string {
  return formatDateTime(run.updatedAtUtc);
}

function compareRunsByUpdatedTime(
  left: WorkflowActivityRunSummary,
  right: WorkflowActivityRunSummary,
): number {
  const leftTime =
    Date.parse(
      trimOptional(left.updatedAtUtc) || trimOptional(left.startedAtUtc),
    ) || 0;
  const rightTime =
    Date.parse(
      trimOptional(right.updatedAtUtc) || trimOptional(right.startedAtUtc),
    ) || 0;
  if (leftTime !== rightTime) return rightTime - leftTime;
  return left.runId.localeCompare(right.runId);
}

function toHistoryEntryFromFeedRow(
  row: WorkflowActivityRunFeedRow,
): RunHistoryEntry {
  const context =
    trimOptional(row.firstFailure.message) ||
    trimOptional(row.waiting.prompt) ||
    trimOptional(row.waiting.waitingKind) ||
    trimOptional(row.currentStep.inputSummary) ||
    trimOptional(row.inputSummary);

  return {
    runId: row.runId,
    workflowName: row.workflowName,
    status: row.status,
    success: row.success,
    startedAtUtc: row.startedAtUtc,
    updatedAtUtc: row.updatedAtUtc,
    stateVersion: row.stateVersion,
    scopeId: row.scopeId,
    runOrigin: row.runOrigin,
    context,
    inputSummary: row.inputSummary,
    workflowId: row.workflowId,
  };
}

function RunDetailRefreshOverlay() {
  const label = t(
    'workflowActivityVNext.run.refreshingDetail',
    'Refreshing run details…',
  );

  return (
    <div
      aria-label={label}
      aria-live="polite"
      className="wa-vnext-run-detail__refresh-overlay"
      role="status"
    >
      <span className="wa-vnext-run-detail__refresh-indicator">
        <LoadingOutlined aria-hidden="true" spin />
        <span>{label}</span>
      </span>
    </div>
  );
}

function RunDetailLoadingWorkspace() {
  const loadingLabel = t(
    'workflowActivityVNext.run.loadingDescription',
    'Loading run details…',
  );

  return (
    <div
      aria-busy="true"
      className="wa-vnext-run-detail wa-vnext-run-detail--bounded wa-vnext-run-detail--loading"
      role="status"
    >
      <span className="aevatar-loading-visually-hidden">{loadingLabel}</span>
      <aside aria-hidden="true" className="wa-vnext-run-detail__rail">
        <div className="wa-vnext-run-detail__rail-header">
          <div className="wa-vnext-run-detail__rail-title">
            <span className="wa-vnext-run-detail__skeleton-line wa-vnext-run-detail__skeleton-line--title" />
            <span className="wa-vnext-run-detail__skeleton-line wa-vnext-run-detail__skeleton-line--short" />
          </div>
        </div>
        <div className="wa-vnext-run-detail__rail-list">
          {[0, 1, 2, 3].map((index) => (
            <div
              className="wa-vnext-run-detail__run wa-vnext-run-detail__run--loading"
              key={`loading-run-${index}`}
            >
              <span className="wa-vnext-run-detail__skeleton-line wa-vnext-run-detail__skeleton-line--run" />
              <span className="wa-vnext-run-detail__skeleton-line wa-vnext-run-detail__skeleton-line--meta" />
            </div>
          ))}
        </div>
      </aside>
      <section aria-hidden="true" className="wa-vnext-run-detail__stage">
        <header className="wa-vnext-run-detail__stage-header">
          <div className="wa-vnext-run-detail__stage-title">
            <div className="wa-vnext-run-detail__skeleton-heading">
              <span className="wa-vnext-run-detail__skeleton-line wa-vnext-run-detail__skeleton-line--heading" />
              <span className="wa-vnext-run-detail__skeleton-line wa-vnext-run-detail__skeleton-line--pill" />
            </div>
            <span className="wa-vnext-run-detail__skeleton-line wa-vnext-run-detail__skeleton-line--subtitle" />
          </div>
        </header>
        <div className="wa-vnext-run-detail__graph wa-vnext-run-detail__graph--loading">
          <span className="wa-vnext-run-detail__skeleton-connector wa-vnext-run-detail__skeleton-connector--first" />
          <span className="wa-vnext-run-detail__skeleton-connector wa-vnext-run-detail__skeleton-connector--second" />
          {[0, 1, 2].map((index) => (
            <div
              className={`wa-vnext-run-detail__skeleton-node wa-vnext-run-detail__skeleton-node--${index + 1}`}
              key={`loading-node-${index}`}
            >
              <span className="wa-vnext-run-detail__skeleton-line wa-vnext-run-detail__skeleton-line--node-title" />
              <span className="wa-vnext-run-detail__skeleton-line wa-vnext-run-detail__skeleton-line--node-meta" />
            </div>
          ))}
        </div>
        <div className="wa-vnext-run-detail__details">
          <section className="wa-vnext-run-detail__logs">
            <div className="wa-vnext-run-detail__logs-header">
              <span className="wa-vnext-run-detail__skeleton-line wa-vnext-run-detail__skeleton-line--label" />
              <span className="wa-vnext-run-detail__skeleton-line wa-vnext-run-detail__skeleton-line--duration" />
            </div>
            <div className="wa-vnext-run-detail__step-list">
              {[0, 1, 2].map((index) => (
                <div
                  className="wa-vnext-run-detail__step wa-vnext-run-detail__step--loading"
                  key={`loading-step-${index}`}
                >
                  <span className="wa-vnext-run-detail__skeleton-dot" />
                  <span className="wa-vnext-run-detail__skeleton-step-copy">
                    <span className="wa-vnext-run-detail__skeleton-line wa-vnext-run-detail__skeleton-line--step" />
                    <span className="wa-vnext-run-detail__skeleton-line wa-vnext-run-detail__skeleton-line--step-meta" />
                  </span>
                  <span className="wa-vnext-run-detail__skeleton-line wa-vnext-run-detail__skeleton-line--duration" />
                </div>
              ))}
            </div>
          </section>
          <section className="wa-vnext-run-detail__inspector">
            <div className="wa-vnext-run-detail__inspector-header">
              <span className="wa-vnext-run-detail__skeleton-line wa-vnext-run-detail__skeleton-line--inspector-title" />
            </div>
            <div className="wa-vnext-run-detail__inspector-body wa-vnext-run-detail__inspector-body--loading">
              <span className="wa-vnext-run-detail__skeleton-line wa-vnext-run-detail__skeleton-line--tabs" />
              <span className="wa-vnext-run-detail__skeleton-line wa-vnext-run-detail__skeleton-line--content" />
              <span className="wa-vnext-run-detail__skeleton-line wa-vnext-run-detail__skeleton-line--content-short" />
            </div>
          </section>
        </div>
      </section>
    </div>
  );
}

const RunDetailPage: React.FC<{
  readonly runId: string;
  readonly scopeId: string;
}> = ({ runId, scopeId }) => {
  const location = useConsoleLocation();
  const toast = useConsoleToast();
  const params = React.useMemo(
    () => new URLSearchParams(location.search),
    [location.search],
  );
  const definitionId = params.get('definition')?.trim() ?? '';
  const routeWorkflowId = params.get('workflowId')?.trim() ?? '';
  const routeRunQuery = React.useMemo(() => {
    if (routeWorkflowId) return { workflowId: routeWorkflowId };
    if (definitionId) return { definition: definitionId };
    return undefined;
  }, [definitionId, routeWorkflowId]);
  const detail = useQuery({
    queryKey: ['workflow-activity-vnext', 'run-detail', scopeId, runId],
    queryFn: () => workflowActivityApi.getRun(scopeId, runId),
    retry: false,
  });
  const graph = useQuery({
    queryKey: ['workflow-activity-vnext', 'run-graph', scopeId, runId],
    queryFn: () => workflowActivityApi.getRunGraph(scopeId, runId),
    retry: false,
  });
  const historyRuns = useQuery({
    queryKey: [
      'workflow-activity-vnext',
      'run-history',
      scopeId,
      routeWorkflowId || definitionId,
    ],
    queryFn: async (): Promise<RunHistoryEntry[]> => {
      if (routeWorkflowId) {
        const page = await workflowActivityApi.listActivityRuns(scopeId, {
          workflowId: routeWorkflowId,
          take: 100,
        });
        return page.items.map(toHistoryEntryFromFeedRow);
      }

      return workflowActivityApi.listRuns(scopeId, {
        definitionActorIds: definitionId ? [definitionId] : undefined,
        take: 100,
      });
    },
    retry: false,
  });
  const [forking, setForking] = React.useState(false);
  const [receipt, setReceipt] =
    React.useState<WorkflowRunForkAcceptedReceipt | null>(null);
  const [pendingRecovery, setPendingRecovery] = React.useState<{
    readonly kind: 'retry' | 'run_again';
    readonly stepId: string;
  } | null>(null);
  const [selectedStepId, setSelectedStepId] = React.useState('');
  const [refreshing, setRefreshing] = React.useState(false);
  const shownFailureKeys = React.useRef(new Set<string>());

  const refreshRunDetail = React.useCallback(async () => {
    if (refreshing) return;
    setRefreshing(true);
    try {
      const results = await Promise.allSettled([
        detail.refetch(),
        graph.refetch(),
        historyRuns.refetch(),
      ]);
      const failed = results.some(
        (result) => result.status === 'rejected' || result.value.isError,
      );
      if (failed) {
        toast.error(
          t(
            'workflowActivityVNext.run.refreshFailed',
            "Some run details couldn't be refreshed",
          ),
          { key: 'run-detail-refresh' },
        );
        return;
      }
      toast.success(
        t(
          'workflowActivityVNext.run.refreshSucceeded',
          'Run details refreshed',
        ),
        { key: 'run-detail-refresh' },
      );
    } finally {
      setRefreshing(false);
    }
  }, [detail, graph, historyRuns, refreshing, toast]);

  const failurePresentation = React.useMemo(() => {
    const evidence =
      detail.error && !detail.data
        ? readApiFailure(detail.error)
        : readCommittedRunFailure(detail.data);
    return evidence ? classifyRunFailure(evidence) : null;
  }, [detail.data, detail.error]);

  const performFailureAction = React.useCallback(
    (action: RunFailureAction) => {
      const activityHref = buildWorkflowActivitySectionHref(
        scopeId,
        'activity',
      );
      switch (action) {
        case 'sign_in': {
          const returnTo = buildWorkflowActivityRunHref(
            scopeId,
            runId,
            routeRunQuery,
          );
          history.push(`/login?redirect=${encodeURIComponent(returnTo)}`);
          return;
        }
        case 'open_settings':
          history.push(buildWorkflowActivitySectionHref(scopeId, 'settings'));
          return;
        case 'back_to_activity':
        case 'open_activity':
          history.push(activityHref);
          return;
        case 'reload':
        case 'retry':
          void refreshRunDetail();
          return;
        case 'review_input':
          return;
      }
    },
    [refreshRunDetail, routeRunQuery, runId, scopeId],
  );

  React.useEffect(() => {
    if (!failurePresentation) return;
    const key = `run-failure:${runId}:${failurePresentation.category}`;
    if (shownFailureKeys.current.has(key)) return;
    shownFailureKeys.current.add(key);
    toast[failurePresentation.intent](
      <RunFailureToastContent
        onAction={
          failurePresentation.action === 'review_input'
            ? undefined
            : performFailureAction
        }
        presentation={failurePresentation}
      />,
      { duration: failurePresentation.duration, key },
    );
  }, [failurePresentation, performFailureAction, runId, toast]);

  React.useEffect(() => {
    if (selectedStepId) return;
    const defaultStepId = getSelectedStepDefaultId(
      detail.data?.steps ?? [],
      graph.data,
    );
    setSelectedStepId(defaultStepId);
  }, [detail.data?.steps, graph.data, selectedStepId]);

  const fork = async (startAtStepId: string): Promise<boolean> => {
    if (forking) return false;
    setForking(true);
    try {
      setReceipt(
        await workflowActivityApi.forkRun({
          sourceRunId: runId,
          startAtStepId,
          input: detail.data?.input,
        }),
      );
      return true;
    } catch {
      toast.error(
        t(
          'workflowActivityVNext.run.startFailed',
          "The new run couldn't be started",
        ),
      );
      return false;
    } finally {
      setForking(false);
    }
  };

  const confirmFork = async () => {
    if (!pendingRecovery) return;
    if (await fork(pendingRecovery.stepId)) setPendingRecovery(null);
  };

  const historyEntries = React.useMemo(
    () => [...(historyRuns.data ?? [])].sort(compareRunsByUpdatedTime),
    [historyRuns.data],
  );
  const fallbackHistoryRun =
    historyEntries.find((entry) => entry.runId === runId) ?? null;

  const openRun = React.useCallback(
    (targetRunId: string) => {
      history.push(
        buildWorkflowActivityRunHref(scopeId, targetRunId, routeRunQuery),
      );
      setSelectedStepId('');
    },
    [routeRunQuery, scopeId],
  );

  const renderHistoryRail = (
    workflowName: string,
    entries: readonly RunHistoryEntry[],
    selectedRunId: string,
  ) => (
    <aside className="wa-vnext-run-detail__rail">
      <div className="wa-vnext-run-detail__rail-header">
        <div className="wa-vnext-run-detail__rail-title">
          <Typography.Title level={5} style={{ margin: 0 }}>
            {t(
              'pages.runs.memberPublishedRuns.publishedRuns',
              'Published runs',
            )}
          </Typography.Title>
          <Typography.Text ellipsis type="secondary">
            {workflowName ||
              t('workflowActivityVNext.common.unknown', 'Unknown')}
          </Typography.Text>
        </div>
      </div>
      <div className="wa-vnext-run-detail__rail-list">
        {historyRuns.isPending ? (
          <div className="wa-vnext__state wa-vnext__state--compact">
            <p>
              {t(
                'workflowActivityVNext.run.historyLoading',
                'Loading run history…',
              )}
            </p>
          </div>
        ) : historyRuns.isError ? (
          <Alert
            showIcon
            type="error"
            message={t(
              'workflowActivityVNext.run.historyUnavailable',
              'Run history is unavailable.',
            )}
            description={errorMessage(historyRuns.error)}
          />
        ) : entries.length ? (
          entries.map((entry) => {
            const selected = entry.runId === selectedRunId;
            const selectedStatus = getRunStatusPresentation(entry.status);
            return (
              <button
                aria-current={selected ? 'true' : undefined}
                aria-label={t(
                  'workflowActivityVNext.run.openRunAria',
                  'Open {runId}',
                  {
                    runId: entry.runId,
                  },
                )}
                className={`wa-vnext-run-detail__run${selected ? ' wa-vnext-run-detail__run--selected' : ''}`}
                key={entry.runId}
                onClick={() => openRun(entry.runId)}
                type="button"
              >
                <div className="wa-vnext-run-detail__run-title">
                  <Tag
                    className={getRunBadgeClass(entry.status)}
                    style={{ marginInlineEnd: 0 }}
                  >
                    {selectedStatus.label}
                  </Tag>
                  <Typography.Text ellipsis style={{ minWidth: 0 }}>
                    {formatRunTime(entry)}
                  </Typography.Text>
                </div>
                <Typography.Text ellipsis type="secondary">
                  {entry.runOrigin ||
                    t('workflowActivityVNext.common.unknown', 'Unknown')}
                </Typography.Text>
              </button>
            );
          })
        ) : (
          <div className="wa-vnext__state wa-vnext__state--compact">
            <h3>
              {t(
                'workflowActivityVNext.run.noHistory',
                'No published runs yet.',
              )}
            </h3>
            <p>
              {t(
                'workflowActivityVNext.run.noHistoryDescription',
                'This workflow has no other runs in the current history context.',
              )}
            </p>
          </div>
        )}
      </div>
    </aside>
  );

  if (detail.isPending) {
    return (
      <WorkflowActivityVNextShell
        activeSection="activity"
        contentClassName="wa-vnext__content--run-detail"
        description={t(
          'workflowActivityVNext.run.description',
          'Review the result, steps, and history for this run.',
        )}
        headerActions={
          <Button
            aria-label={t(
              'workflowActivityVNext.run.backAria',
              'Back to Activity',
            )}
            icon={<ArrowLeftOutlined />}
            onClick={() =>
              history.push(
                buildWorkflowActivitySectionHref(scopeId, 'activity'),
              )
            }
          />
        }
        mainClassName="wa-vnext__main--run-detail"
        scopeId={scopeId}
        title={t('workflowActivityVNext.run.title', 'Run details')}
      >
        <RunDetailLoadingWorkspace />
      </WorkflowActivityVNextShell>
    );
  }

  if (!detail.data) {
    if (fallbackHistoryRun) {
      return (
        <WorkflowActivityVNextShell
          activeSection="activity"
          contentClassName="wa-vnext__content--run-detail"
          description={t(
            'workflowActivityVNext.run.detailFallbackDescription',
            'The run detail request is unavailable, but the selected Activity row is still visible.',
          )}
          headerActions={
            <Button
              className="wa-vnext__run-detail-refresh"
              disabled={refreshing}
              icon={<ReloadOutlined />}
              loading={refreshing}
              onClick={() => void refreshRunDetail()}
            >
              {refreshing
                ? t('workflowActivityVNext.run.refreshing', 'Refreshing…')
                : t('workflowActivityVNext.common.refresh', 'Refresh')}
            </Button>
          }
          scopeId={scopeId}
          title={
            fallbackHistoryRun.workflowName ||
            t('workflowActivityVNext.run.title', 'Run details')
          }
          mainClassName="wa-vnext__main--run-detail"
        >
          <div
            aria-busy={refreshing}
            className="wa-vnext-run-detail wa-vnext-run-detail--bounded"
          >
            <div
              className="wa-vnext-run-detail__refresh-content"
              inert={refreshing}
            >
              {renderHistoryRail(
                fallbackHistoryRun.workflowName,
                historyEntries,
                fallbackHistoryRun.runId,
              )}
              <section className="wa-vnext-run-detail__stage">
                <header className="wa-vnext-run-detail__stage-header">
                  <div className="wa-vnext-run-detail__stage-title">
                    <Space wrap size={8}>
                      <Typography.Title level={4} style={{ margin: 0 }}>
                        {formatDateTime(fallbackHistoryRun.updatedAtUtc)}
                      </Typography.Title>
                      <Tag
                        className={getRunBadgeClass(fallbackHistoryRun.status)}
                        style={{ marginInlineEnd: 0 }}
                      >
                        {
                          getRunStatusPresentation(fallbackHistoryRun.status)
                            .label
                        }
                      </Tag>
                    </Space>
                    <Typography.Text ellipsis type="secondary">
                      {fallbackHistoryRun.workflowName ||
                        t('workflowActivityVNext.common.unknown', 'Unknown')}
                      {' · '}
                      {getRunOriginLabel(fallbackHistoryRun.runOrigin)}
                    </Typography.Text>
                  </div>
                </header>
                <Alert
                  showIcon
                  type="warning"
                  message={t(
                    'workflowActivityVNext.run.detailUnavailableTitle',
                    'Detailed run data is temporarily unavailable.',
                  )}
                  description={
                    <>
                      <p>
                        {t(
                          'workflowActivityVNext.run.detailUnavailableDescription',
                          'The selected run remains highlighted in its workflow history. Retry to load its immutable detail, graph, timeline, and output.',
                        )}
                      </p>
                      {fallbackHistoryRun.context ? (
                        <Typography.Paragraph>
                          {fallbackHistoryRun.context}
                        </Typography.Paragraph>
                      ) : null}
                      {detail.error ? (
                        <TechnicalDetails>
                          {errorMessage(detail.error)}
                        </TechnicalDetails>
                      ) : null}
                    </>
                  }
                  action={
                    <Button onClick={() => void detail.refetch()}>
                      {t('workflowActivityVNext.common.retry', 'Retry')}
                    </Button>
                  }
                />
              </section>
            </div>
            {refreshing ? <RunDetailRefreshOverlay /> : null}
          </div>
        </WorkflowActivityVNextShell>
      );
    }

    return (
      <WorkflowActivityVNextShell
        activeSection="activity"
        description={t(
          'workflowActivityVNext.run.unavailableDescription',
          "This run couldn't be loaded.",
        )}
        scopeId={scopeId}
        title={failureTitle(detail.error)}
      >
        <div className="wa-vnext__state" role="alert">
          <div>
            <h2>{failureTitle(detail.error)}</h2>
            <p>
              {t(
                'workflowActivityVNext.run.unavailableGuidance',
                'Try again to load this run.',
              )}
            </p>
            <Button onClick={() => void detail.refetch()}>
              {t('workflowActivityVNext.common.retry', 'Retry')}
            </Button>
            {detail.error ? (
              <TechnicalDetails>{errorMessage(detail.error)}</TechnicalDetails>
            ) : null}
          </div>
        </div>
      </WorkflowActivityVNextShell>
    );
  }

  const run = detail.data;
  const statusPresentation = getRunStatusPresentation(run.summary.status);
  const normalizedWorkflowName = trimOptional(
    run.summary.workflowName,
  ).toLowerCase();
  const scopedHistoryEntries = routeWorkflowId
    ? historyEntries
    : normalizedWorkflowName
      ? historyEntries.filter(
          (entry) =>
            trimOptional(entry.workflowName).toLowerCase() ===
            normalizedWorkflowName,
        )
      : historyEntries;
  const currentRunHistoryEntry: RunHistoryEntry = run.summary;
  const effectiveHistory = scopedHistoryEntries.some(
    (entry) => entry.runId === run.summary.runId,
  )
    ? scopedHistoryEntries
    : [currentRunHistoryEntry, ...scopedHistoryEntries];
  const selectedHistoryRun =
    effectiveHistory.find((entry) => entry.runId === run.summary.runId) ??
    currentRunHistoryEntry;
  const graphView = buildExecutionGraph(run, graph.data, selectedStepId);
  const selectedStep =
    graphView.orderedSteps.find((step) => step.stepId === selectedStepId) ??
    graphView.orderedSteps[0] ??
    null;
  const scopedTimeline = filterTimelineForStep(
    run.timeline,
    selectedStep?.stepId ?? '',
  );
  const runDurationMs = getRunDurationMs(run, graphView.orderedSteps);
  const selectedStepTitle = getStepDisplayName(selectedStep);
  const selectedStepDuration = formatDurationMs(selectedStep?.durationMs);

  const renderGraph = () => {
    if (graph.isError && !graph.data) {
      return (
        <Alert
          action={
            <Button onClick={() => void graph.refetch()}>
              {t('workflowActivityVNext.common.retry', 'Retry')}
            </Button>
          }
          message={t(
            'workflowActivityVNext.run.graphUnavailable',
            'Run graph unavailable',
          )}
          showIcon
          type="warning"
        />
      );
    }

    if (!graphView.nodes.length) {
      return (
        <div className="wa-vnext__state wa-vnext__state--compact">
          <h3>
            {t(
              'workflowActivityVNext.run.graphEmpty',
              'No graph is available yet.',
            )}
          </h3>
          <p>
            {t(
              'workflowActivityVNext.run.graphEmptyDescription',
              'This run has not materialized a graph view yet.',
            )}
          </p>
        </div>
      );
    }

    return (
      <GraphCanvas
        autoFitKey={run.summary.runId}
        edges={graphView.edges}
        height="100%"
        nodes={graphView.nodes}
        onCanvasSelect={() => setSelectedStepId('')}
        onNodeSelect={(nodeId) =>
          setSelectedStepId(nodeId.replace(/^step:/, ''))
        }
        selectedNodeId={
          selectedStep ? `step:${selectedStep.stepId}` : undefined
        }
        variant="studio"
      />
    );
  };

  return (
    <WorkflowActivityVNextShell
      activeSection="activity"
      contentClassName="wa-vnext__content--run-detail"
      description={t(
        'workflowActivityVNext.run.description',
        'Review the result, steps, and history for this run.',
      )}
      headerActions={
        <>
          <Button
            aria-label={t(
              'workflowActivityVNext.run.backAria',
              'Back to Activity',
            )}
            icon={<ArrowLeftOutlined />}
            onClick={() =>
              history.push(
                buildWorkflowActivitySectionHref(scopeId, 'activity'),
              )
            }
          />
          <Button
            className="wa-vnext__run-detail-refresh"
            disabled={refreshing}
            icon={<ReloadOutlined />}
            loading={refreshing}
            onClick={() => void refreshRunDetail()}
          >
            {refreshing
              ? t('workflowActivityVNext.run.refreshing', 'Refreshing…')
              : t('workflowActivityVNext.common.refresh', 'Refresh')}
          </Button>
        </>
      }
      scopeId={scopeId}
      title={
        run.summary.workflowName ||
        t('workflowActivityVNext.run.title', 'Run details')
      }
      mainClassName="wa-vnext__main--run-detail"
    >
      <div
        aria-busy={refreshing}
        className="wa-vnext-run-detail wa-vnext-run-detail--bounded"
      >
        <div
          className="wa-vnext-run-detail__refresh-content"
          inert={refreshing}
        >
          {renderHistoryRail(
            run.summary.workflowName,
            effectiveHistory,
            selectedHistoryRun.runId,
          )}
          <section className="wa-vnext-run-detail__stage">
            <header className="wa-vnext-run-detail__stage-header">
              <div className="wa-vnext-run-detail__stage-title">
                <Space wrap size={8}>
                  <Typography.Title level={4} style={{ margin: 0 }}>
                    {formatDateTime(run.summary.updatedAtUtc)}
                  </Typography.Title>
                  <Tag
                    className={getRunBadgeClass(run.summary.status)}
                    style={{ marginInlineEnd: 0 }}
                  >
                    {statusPresentation.label}
                  </Tag>
                  <Tag style={{ marginInlineEnd: 0 }}>
                    {formatDurationMs(runDurationMs)}
                  </Tag>
                </Space>
                <Typography.Text ellipsis type="secondary">
                  {run.summary.workflowName ||
                    t('workflowActivityVNext.common.unknown', 'Unknown')}
                  {' · '}
                  {getRunOriginLabel(run.summary.runOrigin)}
                </Typography.Text>
              </div>
            </header>
            {receipt ? (
              <Alert
                action={
                  <Button
                    onClick={() =>
                      history.push(
                        buildWorkflowActivitySectionHref(scopeId, 'activity'),
                      )
                    }
                  >
                    {t(
                      'workflowActivityVNext.editor.openActivity',
                      'Open Activity',
                    )}
                  </Button>
                }
                description={
                  <>
                    <p>
                      {t(
                        'workflowActivityVNext.run.forkAcceptedDescription',
                        'Open Activity to follow its progress.',
                      )}
                    </p>
                    <TechnicalDetails>
                      <ul>
                        <li className="wa-vnext__mono">
                          {receipt.newRunActorId}
                        </li>
                        <li className="wa-vnext__mono">
                          {receipt.acceptedCommandId}
                        </li>
                        <li className="wa-vnext__mono">{receipt.statusUrl}</li>
                      </ul>
                    </TechnicalDetails>
                  </>
                }
                message={t(
                  'workflowActivityVNext.run.forkAccepted',
                  'New run started',
                )}
                showIcon
                type="success"
              />
            ) : null}
            <div className="wa-vnext-run-detail__graph">{renderGraph()}</div>
            <div className="wa-vnext-run-detail__details">
              <section className="wa-vnext-run-detail__logs">
                <div className="wa-vnext-run-detail__logs-header">
                  <Typography.Text strong>
                    {t('workflowActivityVNext.run.logs', 'Logs')}
                  </Typography.Text>
                  <Typography.Text type="secondary">
                    {selectedStepDuration}
                  </Typography.Text>
                </div>
                <div className="wa-vnext-run-detail__step-list">
                  {graphView.orderedSteps.length ? (
                    graphView.orderedSteps.map((step) => {
                      const selected = selectedStep?.stepId === step.stepId;
                      return (
                        <button
                          aria-current={selected ? 'true' : undefined}
                          className={`wa-vnext-run-detail__step${selected ? ' wa-vnext-run-detail__step--selected' : ''}`}
                          key={step.stepId}
                          onClick={() => setSelectedStepId(step.stepId)}
                          type="button"
                        >
                          {renderStepStatusIcon(step)}
                          <span style={{ minWidth: 0 }}>
                            <Typography.Text
                              ellipsis
                              style={{ display: 'block' }}
                            >
                              {getStepDisplayName(step)}
                            </Typography.Text>
                            <Typography.Text ellipsis type="secondary">
                              {step.stepType ||
                                t('workflowActivityVNext.run.step', 'Step')}
                            </Typography.Text>
                          </span>
                          <Typography.Text type="secondary">
                            {formatDurationMs(step.durationMs)}
                          </Typography.Text>
                        </button>
                      );
                    })
                  ) : (
                    <div className="wa-vnext__state wa-vnext__state--compact">
                      <h3>
                        {t(
                          'workflowActivityVNext.run.noSteps',
                          'No steps are available yet.',
                        )}
                      </h3>
                    </div>
                  )}
                </div>
              </section>
              <section className="wa-vnext-run-detail__inspector">
                <div className="wa-vnext-run-detail__inspector-header">
                  <Space size={8} style={{ minWidth: 0 }}>
                    <NodeIndexOutlined style={{ color: '#1677ff' }} />
                    <Typography.Text ellipsis strong style={{ maxWidth: 360 }}>
                      {selectedStepTitle ||
                        t('workflowActivityVNext.run.details', 'Details')}
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
                      {formatDateTime(
                        selectedStep.completedAtUtc ||
                          selectedStep.requestedAtUtc,
                      )}
                    </Typography.Text>
                  ) : null}
                </div>
                <div className="wa-vnext-run-detail__inspector-body">
                  <Tabs
                    size="small"
                    items={[
                      {
                        key: 'output',
                        label: t('workflowActivityVNext.run.output', 'Output'),
                        children: selectedStep
                          ? renderTextBlock(
                              selectedStep.error || selectedStep.outputPreview,
                            )
                          : renderTextBlock(run.finalError || run.finalOutput),
                      },
                      {
                        key: 'input',
                        label: t('workflowActivityVNext.run.input', 'Input'),
                        children: selectedStep
                          ? renderKeyValueRows(selectedStep.requestParameters)
                          : renderTextBlock(run.input),
                      },
                      {
                        key: 'timeline',
                        label: t(
                          'workflowActivityVNext.run.timeline',
                          'Timeline',
                        ),
                        children: scopedTimeline.length ? (
                          <div className="wa-vnext-run-detail__timeline">
                            {scopedTimeline.map((event) => (
                              <div
                                className="wa-vnext-run-detail__timeline-row"
                                key={`${event.timestampUtc}-${event.stage}-${event.kind}-${event.stepId}-${event.agentId}`}
                              >
                                <div className="wa-vnext-run-detail__timeline-key">
                                  {formatDateTime(event.timestampUtc)}
                                </div>
                                <div className="wa-vnext-run-detail__timeline-value">
                                  <Typography.Text strong>
                                    {event.stage || event.kind || 'event'}
                                  </Typography.Text>
                                  <br />
                                  <Typography.Text type="secondary">
                                    {trimOptional(event.message) ||
                                      trimOptional(event.agentId) ||
                                      'event'}
                                  </Typography.Text>
                                </div>
                              </div>
                            ))}
                          </div>
                        ) : (
                          <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} />
                        ),
                      },
                    ]}
                  />
                </div>
              </section>
            </div>
          </section>
        </div>
        {refreshing ? <RunDetailRefreshOverlay /> : null}
      </div>
      <Modal
        aria-label={t(
          'workflowActivityVNext.run.confirmTitle',
          'Confirm new run',
        )}
        cancelText={t('workflowActivityVNext.common.cancel', 'Cancel')}
        confirmLoading={forking}
        okText={
          pendingRecovery?.kind === 'retry'
            ? t('workflowActivityVNext.run.confirmRetry', 'Confirm retry')
            : t(
                'workflowActivityVNext.run.confirmRunAgain',
                'Confirm run again',
              )
        }
        onCancel={() => !forking && setPendingRecovery(null)}
        onOk={() => void confirmFork()}
        open={Boolean(pendingRecovery)}
        title={t('workflowActivityVNext.run.confirmTitle', 'Confirm new run')}
      >
        <Space direction="vertical" size={12}>
          <Typography.Text>
            {pendingRecovery?.stepId ||
              t('workflowActivityVNext.common.unavailable', 'Unavailable')}
          </Typography.Text>
          <Typography.Text type="secondary">
            {run.input || t('workflowActivityVNext.common.empty', 'Empty')}
          </Typography.Text>
        </Space>
      </Modal>
    </WorkflowActivityVNextShell>
  );
};

export default RunDetailPage;
