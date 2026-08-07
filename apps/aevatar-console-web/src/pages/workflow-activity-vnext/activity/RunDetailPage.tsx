import { ArrowLeftOutlined, ReloadOutlined } from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { getLocale } from '@umijs/max';
import { Alert, Button, Descriptions, Modal, Space, Tabs } from 'antd';
import React from 'react';
import {
  WorkflowActivityApiError,
  workflowActivityApi,
} from '@/shared/api/workflowActivityApi';
import { t } from '@/shared/i18n/messages';
import type {
  WorkflowActivityRunDetail,
  WorkflowRunForkAcceptedReceipt,
} from '@/shared/models/workflowActivity';
import { history } from '@/shared/navigation/history';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import {
  buildWorkflowActivityRunHref,
  buildWorkflowActivitySectionHref,
} from '../navigation';
import TableScrollRegion from '../TableScrollRegion';
import TechnicalDetails from '../TechnicalDetails';
import WorkflowActivityVNextShell from '../WorkflowActivityVNextShell';
import {
  buildFailurePresentation,
  buildStepMetrics,
  buildUsagePresentation,
  getStepDisplayName,
  getTimelineEventLabel,
} from './runDetailPresentation';
import {
  classifyRunFailure,
  type RunFailureAction,
  type RunFailureEvidence,
  RunFailureToastContent,
} from './runFailurePresentation';
import { getRunOriginLabel, getRunStatusPresentation } from './runPresentation';
import { resolveRunRecovery } from './runRecovery';

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

function RunFailureSummary({
  detail,
  kind,
}: {
  readonly detail: string;
  readonly kind: 'run' | 'step';
}) {
  return (
    <>
      <span>
        {kind === 'run'
          ? t(
              'workflowActivityVNext.run.failedSummary',
              'The run did not complete.',
            )
          : t(
              'workflowActivityVNext.run.stepFailedSummary',
              'This step did not complete.',
            )}
      </span>
      <TechnicalDetails>{detail}</TechnicalDetails>
    </>
  );
}

function formatDuration(durationMs: number | null): string {
  if (durationMs === null || !Number.isFinite(durationMs) || durationMs < 0) {
    return t('workflowActivityVNext.common.notReported', 'Not reported');
  }
  const totalSeconds = Math.round(durationMs / 1000);
  if (totalSeconds < 60) {
    return t('workflowActivityVNext.run.durationSeconds', '{seconds}s', {
      seconds: totalSeconds,
    });
  }
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return seconds
    ? t(
        'workflowActivityVNext.run.durationMinutesSeconds',
        '{minutes}m {seconds}s',
        { minutes, seconds },
      )
    : t('workflowActivityVNext.run.durationMinutes', '{minutes}m', {
        minutes,
      });
}

function runDurationMilliseconds(
  startedAtUtc: string | null,
  timeline: readonly { readonly kind: string; readonly timestampUtc: string }[],
): number | null {
  if (!startedAtUtc) return null;
  const startedAt = Date.parse(startedAtUtc);
  const terminalTimestamp = [...timeline]
    .reverse()
    .find((event) =>
      ['runcompleted', 'runerror', 'runstopped'].includes(
        event.kind.trim().toLowerCase(),
      ),
    )?.timestampUtc;
  if (!terminalTimestamp) return null;
  const terminalAt = Date.parse(terminalTimestamp);
  return Number.isFinite(startedAt) && Number.isFinite(terminalAt)
    ? Math.max(0, terminalAt - startedAt)
    : null;
}

function formatLocalTimestamp(timestampUtc: string): string {
  const timestamp = new Date(timestampUtc);
  if (Number.isNaN(timestamp.getTime())) return timestampUtc;
  return new Intl.DateTimeFormat(getLocale(), {
    dateStyle: 'medium',
    timeStyle: 'medium',
  }).format(timestamp);
}

function formatTimelineOffset(
  startedAtUtc: string | null,
  timestampUtc: string,
): string {
  if (!startedAtUtc) return '';
  const startedAt = Date.parse(startedAtUtc);
  const timestamp = Date.parse(timestampUtc);
  if (!Number.isFinite(startedAt) || !Number.isFinite(timestamp)) return '';
  return `+${formatDuration(Math.max(0, timestamp - startedAt))}`;
}

function stepOutcome(success: boolean | null): string {
  if (success === null) {
    return t('workflowActivityVNext.run.waiting', 'Waiting');
  }
  return success
    ? t('workflowActivityVNext.common.succeeded', 'Succeeded')
    : t('workflowActivityVNext.common.failed', 'Failed');
}

function localizedTimelineEventLabel(event: {
  readonly kind: string;
  readonly stage: string;
}): string {
  const label = getTimelineEventLabel(event);
  const translations: Record<string, [string, string]> = {
    'Run completed': [
      'workflowActivityVNext.run.timelineEvent.completed',
      'Run completed',
    ],
    'Run failed': [
      'workflowActivityVNext.run.timelineEvent.failed',
      'Run failed',
    ],
    'Run started': [
      'workflowActivityVNext.run.timelineEvent.started',
      'Run started',
    ],
    'Run stopped': [
      'workflowActivityVNext.run.timelineEvent.stopped',
      'Run stopped',
    ],
    'Run updated': [
      'workflowActivityVNext.run.timelineEvent.updated',
      'Run updated',
    ],
    'Step produced a response': [
      'workflowActivityVNext.run.timelineEvent.response',
      'Step produced a response',
    ],
    'Tool finished': [
      'workflowActivityVNext.run.timelineEvent.toolFinished',
      'Tool finished',
    ],
    'Tool started': [
      'workflowActivityVNext.run.timelineEvent.toolStarted',
      'Tool started',
    ],
  };
  const translation = translations[label];
  return translation ? t(translation[0], translation[1]) : label;
}

const RunDetailPage: React.FC<{
  readonly runId: string;
  readonly scopeId: string;
}> = ({ runId, scopeId }) => {
  const toast = useConsoleToast();
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
  const [forking, setForking] = React.useState(false);
  const [receipt, setReceipt] =
    React.useState<WorkflowRunForkAcceptedReceipt | null>(null);
  const [pendingRecovery, setPendingRecovery] = React.useState<{
    readonly kind: 'retry' | 'run_again';
    readonly stepId: string;
  } | null>(null);
  const [activeTab, setActiveTab] = React.useState('overview');
  const shownFailureKeys = React.useRef(new Set<string>());

  const failurePresentation = React.useMemo(() => {
    const evidence = detail.error
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
          const returnTo = buildWorkflowActivityRunHref(scopeId, runId);
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
          void detail.refetch();
          void graph.refetch();
          return;
        case 'review_input':
          return;
      }
    },
    [detail, graph, runId, scopeId],
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

  const recovery = resolveRunRecovery(detail.data?.steps ?? [], graph.data);
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

  if (detail.isPending)
    return (
      <WorkflowActivityVNextShell
        activeSection="activity"
        description={t(
          'workflowActivityVNext.run.loadingDescription',
          'Loading run details…',
        )}
        scopeId={scopeId}
        title={t('workflowActivityVNext.run.loading', 'Loading run…')}
      >
        <div className="wa-vnext__state">
          <p>{t('workflowActivityVNext.run.loading', 'Loading run…')}</p>
        </div>
      </WorkflowActivityVNextShell>
    );
  if (detail.isError || !detail.data)
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

  const run = detail.data;
  const statusPresentation = getRunStatusPresentation(run.summary.status);
  const failure = buildFailurePresentation(run);
  const stepMetrics = buildStepMetrics(run.steps);
  const usage = buildUsagePresentation(
    run.usageTotals,
    run.steps,
    run.timeline,
  );
  const notReported = t(
    'workflowActivityVNext.common.notReported',
    'Not reported',
  );
  return (
    <WorkflowActivityVNextShell
      activeSection="activity"
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
            icon={<ReloadOutlined />}
            onClick={() => {
              void detail.refetch();
              void graph.refetch();
            }}
          >
            {t('workflowActivityVNext.common.refresh', 'Refresh')}
          </Button>
        </>
      }
      scopeId={scopeId}
      title={
        run.summary.workflowName ||
        t('workflowActivityVNext.run.title', 'Run details')
      }
    >
      <div className="wa-vnext__toolbar">
        <Space wrap>
          <span
            className={`wa-vnext__status wa-vnext__status--${statusPresentation.className}`}
          >
            {statusPresentation.label}
          </span>
          <span>{getRunOriginLabel(run.summary.runOrigin)}</span>
        </Space>
        <Space wrap>
          <Button
            disabled={!recovery.retryStepId}
            loading={forking}
            onClick={() =>
              recovery.retryStepId &&
              setPendingRecovery({
                kind: 'retry',
                stepId: recovery.retryStepId,
              })
            }
            title={
              !recovery.retryStepId
                ? t(
                    'workflowActivityVNext.run.retryUnavailable',
                    'Retry is available when one step has failed.',
                  )
                : undefined
            }
            danger
          >
            {t('workflowActivityVNext.run.retry', 'Retry failed step')}
          </Button>
          <Button
            disabled={!recovery.runAgainStepId}
            loading={forking}
            onClick={() =>
              recovery.runAgainStepId &&
              setPendingRecovery({
                kind: 'run_again',
                stepId: recovery.runAgainStepId,
              })
            }
            title={
              !recovery.runAgainStepId
                ? t(
                    'workflowActivityVNext.run.runAgainUnavailable',
                    "Run again isn't available for this run.",
                  )
                : undefined
            }
          >
            {t('workflowActivityVNext.run.runAgain', 'Run again')}
          </Button>
        </Space>
      </div>
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
              {t('workflowActivityVNext.editor.openActivity', 'Open Activity')}
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
                <Descriptions
                  column={1}
                  size="small"
                  items={[
                    {
                      key: 'actor',
                      label: t(
                        'workflowActivityVNext.run.newActorId',
                        'Run address',
                      ),
                      children: (
                        <span className="wa-vnext__mono">
                          {receipt.newRunActorId}
                        </span>
                      ),
                    },
                    {
                      key: 'command',
                      label: t(
                        'workflowActivityVNext.run.commandId',
                        'Request ID',
                      ),
                      children: (
                        <span className="wa-vnext__mono">
                          {receipt.acceptedCommandId}
                        </span>
                      ),
                    },
                    {
                      key: 'correlation',
                      label: t(
                        'workflowActivityVNext.run.correlationId',
                        'Tracking ID',
                      ),
                      children: (
                        <span className="wa-vnext__mono">
                          {receipt.correlationId}
                        </span>
                      ),
                    },
                    {
                      key: 'status',
                      label: t(
                        'workflowActivityVNext.run.statusUrl',
                        'Status URL',
                      ),
                      children: (
                        <span className="wa-vnext__mono">
                          {receipt.statusUrl}
                        </span>
                      ),
                    },
                  ]}
                />
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
      <Tabs
        activeKey={activeTab}
        className="wa-vnext__run-tabs"
        onChange={setActiveTab}
        items={[
          {
            key: 'overview',
            label: t('workflowActivityVNext.run.overview', 'Overview'),
            children: (
              <div className="wa-vnext__run-overview">
                <section className="wa-vnext__outcome">
                  <span className="wa-vnext__eyebrow">
                    {t('workflowActivityVNext.run.outcome', 'Outcome')}
                  </span>
                  <h2>{statusPresentation.label}</h2>
                  {run.finalError ? (
                    <span>
                      {t(
                        'workflowActivityVNext.run.failedSummary',
                        'The run did not complete.',
                      )}
                    </span>
                  ) : null}
                  {failure.primaryCause ? (
                    <p className="wa-vnext__primary-cause">
                      {failure.primaryCause}
                    </p>
                  ) : run.finalOutput ? (
                    <p>{run.finalOutput}</p>
                  ) : (
                    <p>
                      {t(
                        'workflowActivityVNext.run.noOutcomeYet',
                        'No final outcome is available yet.',
                      )}
                    </p>
                  )}
                  {stepMetrics.failed > 0 ? (
                    <Button
                      onClick={() => setActiveTab('steps')}
                      type="primary"
                    >
                      {t(
                        'workflowActivityVNext.run.reviewFailedStep',
                        'Review failed step',
                      )}
                    </Button>
                  ) : run.summary.success === null ? (
                    <Button
                      icon={<ReloadOutlined />}
                      onClick={() => void detail.refetch()}
                    >
                      {t('workflowActivityVNext.common.refresh', 'Refresh')}
                    </Button>
                  ) : null}
                </section>
                <div
                  className="wa-vnext__step-metrics"
                  data-testid="run-step-metrics"
                >
                  {[
                    [
                      t('workflowActivityVNext.run.attempted', 'Attempted'),
                      stepMetrics.attempted,
                    ],
                    [
                      t('workflowActivityVNext.run.succeeded', 'Succeeded'),
                      stepMetrics.succeeded,
                    ],
                    [
                      t('workflowActivityVNext.run.failed', 'Failed'),
                      stepMetrics.failed,
                    ],
                    [
                      t('workflowActivityVNext.run.waiting', 'Waiting'),
                      stepMetrics.waiting,
                    ],
                    [
                      t('workflowActivityVNext.run.skipped', 'Skipped'),
                      stepMetrics.skipped,
                    ],
                  ].map(([label, value]) => (
                    <div className="wa-vnext__step-metric" key={String(label)}>
                      <span>{label}</span>
                      {value === null ? (
                        <span>{notReported}</span>
                      ) : (
                        <strong>{value}</strong>
                      )}
                    </div>
                  ))}
                </div>
                <Descriptions
                  bordered
                  column={{ xs: 1, sm: 2 }}
                  items={[
                    {
                      key: 'source',
                      label: t(
                        'workflowActivityVNext.activity.columnOrigin',
                        'Source',
                      ),
                      children: getRunOriginLabel(run.summary.runOrigin),
                    },
                    {
                      key: 'duration',
                      label: t(
                        'workflowActivityVNext.run.duration',
                        'Duration',
                      ),
                      children: formatDuration(
                        runDurationMilliseconds(
                          run.summary.startedAtUtc,
                          run.timeline,
                        ),
                      ),
                    },
                    {
                      key: 'initiator',
                      label: t(
                        'workflowActivityVNext.run.initiator',
                        'Initiator',
                      ),
                      children: t(
                        'workflowActivityVNext.common.unavailable',
                        'Unavailable',
                      ),
                    },
                    {
                      key: 'inputSummary',
                      label: t(
                        'workflowActivityVNext.run.inputSummary',
                        'Input summary',
                      ),
                      children: t(
                        'workflowActivityVNext.common.unavailable',
                        'Unavailable',
                      ),
                    },
                    {
                      key: 'output',
                      label: t(
                        'workflowActivityVNext.run.output',
                        'Final output',
                      ),
                      span: 2,
                      children:
                        run.finalOutput ||
                        t(
                          'workflowActivityVNext.common.unavailable',
                          'Unavailable',
                        ),
                    },
                  ]}
                />
                <TechnicalDetails>
                  <Descriptions
                    column={1}
                    size="small"
                    items={[
                      {
                        key: 'runId',
                        label: t('workflowActivityVNext.run.runId', 'Run ID'),
                        children: (
                          <span className="wa-vnext__mono">{runId}</span>
                        ),
                      },
                      {
                        key: 'stateVersion',
                        label: t(
                          'workflowActivityVNext.run.stateVersion',
                          'State version',
                        ),
                        children: run.summary.stateVersion,
                      },
                      {
                        key: 'input',
                        label: t('workflowActivityVNext.run.input', 'Input'),
                        children:
                          run.input ||
                          t('workflowActivityVNext.common.empty', 'Empty'),
                      },
                      {
                        key: 'finalError',
                        label: t(
                          'workflowActivityVNext.run.error',
                          'Final error',
                        ),
                        children:
                          run.finalError ||
                          t(
                            'workflowActivityVNext.common.unavailable',
                            'Unavailable',
                          ),
                      },
                    ]}
                  />
                  {failure.evidence.length ? (
                    <pre className="wa-vnext__mono">
                      {JSON.stringify(
                        {
                          evidence: failure.evidence,
                          diagnostics: run.diagnostics,
                        },
                        null,
                        2,
                      )}
                    </pre>
                  ) : null}
                </TechnicalDetails>
              </div>
            ),
          },
          {
            key: 'steps',
            label: t('workflowActivityVNext.run.steps', 'Steps'),
            children: run.steps.length ? (
              <TableScrollRegion
                ariaLabel={t('workflowActivityVNext.run.steps', 'Steps')}
              >
                <table className="wa-vnext__table">
                  <thead>
                    <tr>
                      <th>{t('workflowActivityVNext.run.step', 'Step')}</th>
                      <th>
                        {t('workflowActivityVNext.run.outcome', 'Outcome')}
                      </th>
                      <th>
                        {t('workflowActivityVNext.run.duration', 'Duration')}
                      </th>
                      <th>{t('workflowActivityVNext.run.result', 'Result')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {run.steps.map((step) => (
                      <tr key={step.stepId}>
                        <td
                          data-label={t(
                            'workflowActivityVNext.run.step',
                            'Step',
                          )}
                        >
                          <strong>{getStepDisplayName(step)}</strong>
                        </td>
                        <td
                          data-label={t(
                            'workflowActivityVNext.run.outcome',
                            'Outcome',
                          )}
                        >
                          {stepOutcome(step.success)}
                        </td>
                        <td
                          data-label={t(
                            'workflowActivityVNext.run.duration',
                            'Duration',
                          )}
                        >
                          {formatDuration(step.durationMs)}
                        </td>
                        <td
                          data-label={t(
                            'workflowActivityVNext.run.result',
                            'Result',
                          )}
                        >
                          {step.error ? (
                            <RunFailureSummary
                              detail={step.error}
                              kind="step"
                            />
                          ) : (
                            step.outputPreview ||
                            t(
                              'workflowActivityVNext.common.unavailable',
                              'Unavailable',
                            )
                          )}
                          <TechnicalDetails>
                            <pre className="wa-vnext__mono">
                              {JSON.stringify(
                                {
                                  stepId: step.stepId,
                                  requestParameters: step.requestParameters,
                                  requestedAtUtc: step.requestedAtUtc,
                                  completedAtUtc: step.completedAtUtc,
                                  error: step.error,
                                },
                                null,
                                2,
                              )}
                            </pre>
                          </TechnicalDetails>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </TableScrollRegion>
            ) : (
              <div className="wa-vnext__state">
                <p>
                  {t(
                    'workflowActivityVNext.run.noSteps',
                    'No steps are available yet.',
                  )}
                </p>
              </div>
            ),
          },
          {
            key: 'timeline',
            label: t('workflowActivityVNext.run.timeline', 'Timeline'),
            children: run.timeline.length ? (
              <ol className="wa-vnext__timeline">
                {run.timeline.map((event) => (
                  <li
                    key={[
                      event.timestampUtc,
                      event.kind,
                      event.stepId,
                      event.message,
                    ].join('|')}
                  >
                    <div className="wa-vnext__timeline-heading">
                      <strong>{localizedTimelineEventLabel(event)}</strong>
                      <span>
                        {formatTimelineOffset(
                          run.summary.startedAtUtc,
                          event.timestampUtc,
                        )}
                      </span>
                    </div>
                    <time dateTime={event.timestampUtc}>
                      {formatLocalTimestamp(event.timestampUtc)}
                    </time>
                    <TechnicalDetails>
                      <pre className="wa-vnext__mono">
                        {JSON.stringify(event, null, 2)}
                      </pre>
                    </TechnicalDetails>
                  </li>
                ))}
              </ol>
            ) : (
              <div className="wa-vnext__state">
                <p>
                  {t(
                    'workflowActivityVNext.run.noTimeline',
                    'No timeline events are visible yet.',
                  )}
                </p>
              </div>
            ),
          },
          {
            key: 'usage',
            label: t('workflowActivityVNext.run.usage', 'Usage'),
            children: (
              <Descriptions
                bordered
                column={{ xs: 1, sm: 2 }}
                items={[
                  {
                    key: 'reportingState',
                    label: t(
                      'workflowActivityVNext.run.reportingState',
                      'Reporting state',
                    ),
                    children:
                      usage.state === 'reported'
                        ? t('workflowActivityVNext.run.reported', 'Reported')
                        : notReported,
                  },
                  {
                    key: 'toolCalls',
                    label: t(
                      'workflowActivityVNext.run.toolCalls',
                      'Tool calls',
                    ),
                    children: usage.toolCalls ?? notReported,
                  },
                  {
                    key: 'promptTokens',
                    label: t(
                      'workflowActivityVNext.run.promptTokens',
                      'Prompt tokens',
                    ),
                    children: usage.promptTokens ?? notReported,
                  },
                  {
                    key: 'completionTokens',
                    label: t(
                      'workflowActivityVNext.run.completionTokens',
                      'Completion tokens',
                    ),
                    children: usage.completionTokens ?? notReported,
                  },
                  {
                    key: 'totalTokens',
                    label: t(
                      'workflowActivityVNext.run.totalTokens',
                      'Total tokens',
                    ),
                    children: usage.totalTokens ?? notReported,
                  },
                  {
                    key: 'cost',
                    label: t('workflowActivityVNext.run.cost', 'Cost'),
                    children:
                      usage.cost === null
                        ? notReported
                        : `${usage.cost} · ${t(
                            'workflowActivityVNext.run.currencyNotReported',
                            'Currency not reported',
                          )}`,
                  },
                ]}
              />
            ),
          },
          {
            key: 'execution-path',
            label: t(
              'workflowActivityVNext.run.executionPath',
              'Execution path',
            ),
            children: run.steps.length ? (
              <ol className="wa-vnext__execution-path">
                {run.steps.map((step) => (
                  <li key={step.stepId}>
                    <div>
                      <strong>{getStepDisplayName(step)}</strong>
                      <span>{stepOutcome(step.success)}</span>
                    </div>
                    <span>{formatDuration(step.durationMs)}</span>
                    {step.branchKey ? (
                      <span>
                        {t('workflowActivityVNext.run.branch', 'Branch')}:{' '}
                        {step.branchKey}
                      </span>
                    ) : null}
                    {step.suspensionType ? (
                      <span>
                        {t('workflowActivityVNext.run.wait', 'Wait')}:{' '}
                        {step.suspensionType}
                      </span>
                    ) : null}
                    <TechnicalDetails>
                      <Descriptions
                        column={1}
                        size="small"
                        items={[
                          {
                            key: 'stepId',
                            label: t(
                              'workflowActivityVNext.run.stepId',
                              'Step ID',
                            ),
                            children: (
                              <span className="wa-vnext__mono">
                                {step.stepId}
                              </span>
                            ),
                          },
                          {
                            key: 'nextStepId',
                            label: t(
                              'workflowActivityVNext.run.nextStepId',
                              'Next step ID',
                            ),
                            children:
                              step.nextStepId ||
                              t(
                                'workflowActivityVNext.common.unavailable',
                                'Unavailable',
                              ),
                          },
                        ]}
                      />
                    </TechnicalDetails>
                  </li>
                ))}
              </ol>
            ) : (
              <div className="wa-vnext__state">
                <p>
                  {t(
                    'workflowActivityVNext.run.noExecutionPath',
                    'No execution path is available yet.',
                  )}
                </p>
              </div>
            ),
          },
        ]}
      />
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
        <Alert
          message={t(
            'workflowActivityVNext.run.sourceImmutable',
            "This starts a new run. The original run won't change.",
          )}
          showIcon
          type="info"
        />
        <Descriptions
          column={1}
          items={[
            {
              key: 'step',
              label: t(
                'workflowActivityVNext.run.startingStep',
                'Starting step',
              ),
              children: pendingRecovery?.stepId ? (
                <span className="wa-vnext__mono">{pendingRecovery.stepId}</span>
              ) : (
                t('workflowActivityVNext.common.unavailable', 'Unavailable')
              ),
            },
            {
              key: 'input',
              label: t('workflowActivityVNext.run.input', 'Input'),
              children:
                run.input || t('workflowActivityVNext.common.empty', 'Empty'),
            },
          ]}
        />
      </Modal>
    </WorkflowActivityVNextShell>
  );
};

export default RunDetailPage;
