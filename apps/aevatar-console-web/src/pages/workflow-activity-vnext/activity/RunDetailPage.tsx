import { ArrowLeftOutlined, ReloadOutlined } from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Descriptions, Modal, Space, Tabs } from 'antd';
import React from 'react';
import {
  WorkflowActivityApiError,
  workflowActivityApi,
} from '@/shared/api/workflowActivityApi';
import { t } from '@/shared/i18n/messages';
import type {
  WorkflowActivityRunDetail,
  WorkflowRecoveryRecommendedAction,
  WorkflowRunForkAcceptedReceipt,
  WorkflowRunLineage,
  WorkflowRunLineageRunRef,
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
  classifyRunFailure,
  type RunFailureAction,
  type RunFailureEvidence,
  RunFailureToastContent,
} from './runFailurePresentation';
import { getRunOriginLabel, getRunStatusPresentation } from './runPresentation';
import {
  type RecoveryActionPresentation,
  resolveRunRecovery,
} from './runRecovery';

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

function RunLink({
  runId,
  scopeId,
}: {
  readonly runId: string;
  readonly scopeId: string;
}) {
  if (!runId.trim()) {
    return <>{t('workflowActivityVNext.common.unavailable', 'Unavailable')}</>;
  }
  return (
    <a
      className="wa-vnext__mono"
      href={buildWorkflowActivityRunHref(scopeId, runId)}
    >
      {runId}
    </a>
  );
}

function ChildRunList({
  runs,
  scopeId,
}: {
  readonly runs: readonly WorkflowRunLineageRunRef[];
  readonly scopeId: string;
}) {
  if (!runs.length) {
    return (
      <p>{t('workflowActivityVNext.run.noRelatedRuns', 'No related runs.')}</p>
    );
  }
  return (
    <ul className="wa-vnext__related-list">
      {runs.map((run) => (
        <li key={`${run.relationshipId}:${run.runId}`}>
          <RunLink runId={run.runId} scopeId={scopeId} />
          {run.stepId ? (
            <span className="wa-vnext__sub">
              {t('workflowActivityVNext.run.fromStep', 'From step {step}', {
                step: run.stepId,
              })}
            </span>
          ) : null}
        </li>
      ))}
    </ul>
  );
}

function RelatedRuns({
  lineage,
  scopeId,
}: {
  readonly lineage: WorkflowRunLineage;
  readonly scopeId: string;
}) {
  return (
    <section className="wa-vnext__related-runs">
      <h2>{t('workflowActivityVNext.run.relatedRuns', 'Related runs')}</h2>
      {lineage.availability !== 1 ? (
        <Alert
          message={
            lineage.unavailableReason ||
            t(
              'workflowActivityVNext.run.lineageUnavailable',
              'Related run history is unavailable.',
            )
          }
          showIcon
          type="info"
        />
      ) : (
        <div className="wa-vnext__related-groups">
          <section>
            <h3>
              {t('workflowActivityVNext.run.retryHistory', 'Retry history')}
            </h3>
            {lineage.retryFork.availability === 1 ? (
              <Descriptions
                column={1}
                size="small"
                items={[
                  {
                    key: 'source',
                    label: t(
                      'workflowActivityVNext.run.sourceRun',
                      'Source run',
                    ),
                    children: (
                      <RunLink
                        runId={lineage.retryFork.sourceRunId}
                        scopeId={scopeId}
                      />
                    ),
                  },
                  {
                    key: 'original',
                    label: t(
                      'workflowActivityVNext.run.originalRun',
                      'Original run',
                    ),
                    children: (
                      <RunLink
                        runId={lineage.retryFork.originalRunId}
                        scopeId={scopeId}
                      />
                    ),
                  },
                  {
                    key: 'attempt',
                    label: t('workflowActivityVNext.run.attempt', 'Attempt'),
                    children: lineage.retryFork.attempt,
                  },
                  {
                    key: 'startingStep',
                    label: t(
                      'workflowActivityVNext.run.startingStep',
                      'Starting step',
                    ),
                    children: lineage.retryFork.startAtStepId,
                  },
                  {
                    key: 'children',
                    label: t(
                      'workflowActivityVNext.run.childRuns',
                      'Child runs',
                    ),
                    children: (
                      <ChildRunList
                        runs={lineage.retryFork.childRuns}
                        scopeId={scopeId}
                      />
                    ),
                  },
                ]}
              />
            ) : (
              <p>
                {t('workflowActivityVNext.common.unavailable', 'Unavailable')}
              </p>
            )}
          </section>
          <section>
            <h3>
              {t('workflowActivityVNext.run.subWorkflows', 'Sub-workflows')}
            </h3>
            {lineage.subWorkflow.availability === 1 ? (
              <Descriptions
                column={1}
                size="small"
                items={[
                  {
                    key: 'parent',
                    label: t(
                      'workflowActivityVNext.run.parentRun',
                      'Parent run',
                    ),
                    children: (
                      <RunLink
                        runId={lineage.subWorkflow.parentRunId}
                        scopeId={scopeId}
                      />
                    ),
                  },
                  {
                    key: 'root',
                    label: t('workflowActivityVNext.run.rootRun', 'Root run'),
                    children: (
                      <RunLink
                        runId={lineage.subWorkflow.rootRunId}
                        scopeId={scopeId}
                      />
                    ),
                  },
                  {
                    key: 'depth',
                    label: t('workflowActivityVNext.run.depth', 'Depth'),
                    children: lineage.subWorkflow.depth,
                  },
                  {
                    key: 'parentStep',
                    label: t(
                      'workflowActivityVNext.run.parentStep',
                      'Parent step',
                    ),
                    children: lineage.subWorkflow.parentStepId,
                  },
                  {
                    key: 'children',
                    label: t(
                      'workflowActivityVNext.run.childRuns',
                      'Child runs',
                    ),
                    children: (
                      <ChildRunList
                        runs={lineage.subWorkflow.childRuns}
                        scopeId={scopeId}
                      />
                    ),
                  },
                ]}
              />
            ) : (
              <p>
                {t('workflowActivityVNext.common.unavailable', 'Unavailable')}
              </p>
            )}
          </section>
        </div>
      )}
    </section>
  );
}

function recoveryRecommendationLabel(
  action: WorkflowRecoveryRecommendedAction,
): string | null {
  switch (action) {
    case 1:
      return t('workflowActivityVNext.run.retry', 'Retry failed step');
    case 2:
      return t('workflowActivityVNext.run.runAgain', 'Run again');
    case 3:
    case 4:
      return t('workflowActivityVNext.run.reviewSettings', 'Review settings');
    case 5:
      return t('workflowActivityVNext.run.editWorkflow', 'Edit workflow');
    case 6:
      return t('workflowActivityVNext.run.reviewInput', 'Review input');
    case 7:
      return t(
        'workflowActivityVNext.common.technicalDetails',
        'Technical details',
      );
    default:
      return null;
  }
}

function RecoveryUnavailableNotice({
  action,
  actionName,
  onOpenSettings,
}: {
  readonly action: RecoveryActionPresentation;
  readonly actionName: string;
  readonly onOpenSettings: () => void;
}) {
  if (action.enabled || !action.reason) return null;
  const recommendations = [
    ...new Set(
      action.recommendedActions
        .map((recommendation) => ({
          action: recommendation,
          label: recoveryRecommendationLabel(recommendation),
        }))
        .filter(
          (
            recommendation,
          ): recommendation is {
            action: WorkflowRecoveryRecommendedAction;
            label: string;
          } => Boolean(recommendation.label),
        ),
    ),
  ];
  return (
    <Alert
      action={
        recommendations.some(
          ({ action: recommendation }) =>
            recommendation === 3 || recommendation === 4,
        ) ? (
          <Button onClick={onOpenSettings} size="small">
            {t('workflowActivityVNext.run.reviewSettings', 'Review settings')}
          </Button>
        ) : undefined
      }
      description={
        recommendations.length ? (
          <span>
            {t(
              'workflowActivityVNext.run.recommendedNextSteps',
              'Recommended: {actions}',
              { actions: recommendations.map(({ label }) => label).join(', ') },
            )}
          </span>
        ) : undefined
      }
      message={
        <span>
          <strong>{actionName}</strong>
          <span className="wa-vnext__sub">{action.reason}</span>
        </span>
      }
      showIcon
      type="info"
    />
  );
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
  const recovery = resolveRunRecovery(run.recoveryCapability);
  const pendingRecoveryPresentation = pendingRecovery
    ? pendingRecovery.kind === 'retry'
      ? recovery.retry
      : recovery.runAgain
    : null;
  const statusPresentation = getRunStatusPresentation(run.summary.status);
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
            aria-disabled={!recovery.retry.enabled}
            className={
              !recovery.retry.enabled ? 'wa-vnext__aria-disabled' : undefined
            }
            loading={forking}
            onClick={() => {
              if (!recovery.retry.enabled) return;
              setPendingRecovery({
                kind: 'retry',
                stepId: recovery.retry.startingStepId,
              });
            }}
            danger
          >
            {t('workflowActivityVNext.run.retry', 'Retry failed step')}
          </Button>
          <Button
            aria-disabled={!recovery.runAgain.enabled}
            className={
              !recovery.runAgain.enabled ? 'wa-vnext__aria-disabled' : undefined
            }
            loading={forking}
            onClick={() => {
              if (!recovery.runAgain.enabled) return;
              setPendingRecovery({
                kind: 'run_again',
                stepId: recovery.runAgain.startingStepId,
              });
            }}
          >
            {t('workflowActivityVNext.run.runAgain', 'Run again')}
          </Button>
        </Space>
      </div>
      <div className="wa-vnext__recovery-notices">
        <RecoveryUnavailableNotice
          action={recovery.retry}
          actionName={t('workflowActivityVNext.run.retry', 'Retry failed step')}
          onOpenSettings={() =>
            history.push(buildWorkflowActivitySectionHref(scopeId, 'settings'))
          }
        />
        <RecoveryUnavailableNotice
          action={recovery.runAgain}
          actionName={t('workflowActivityVNext.run.runAgain', 'Run again')}
          onOpenSettings={() =>
            history.push(buildWorkflowActivitySectionHref(scopeId, 'settings'))
          }
        />
      </div>
      {receipt ? (
        <Alert
          action={
            <Button
              onClick={() =>
                history.push(
                  buildWorkflowActivityRunHref(scopeId, receipt.newRunId),
                )
              }
            >
              {t('workflowActivityVNext.run.openNewRun', 'Open new run')}
            </Button>
          }
          description={
            <>
              <p>
                {t(
                  'workflowActivityVNext.run.forkAcceptedDescription',
                  'The request was accepted. Open the new run to follow its progress.',
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
            'New run accepted',
          )}
          showIcon
          type="success"
        />
      ) : null}
      <div className="wa-vnext__run-summary">
        <Descriptions
          bordered
          column={{ xs: 1, sm: 2 }}
          items={[
            {
              key: 'origin',
              label: t('workflowActivityVNext.activity.columnOrigin', 'Source'),
              children: getRunOriginLabel(run.summary.runOrigin),
            },
            {
              key: 'input',
              label: t('workflowActivityVNext.run.input', 'Input'),
              children:
                run.input || t('workflowActivityVNext.common.empty', 'Empty'),
            },
            {
              key: 'output',
              label: t('workflowActivityVNext.run.output', 'Final output'),
              children:
                run.finalOutput ||
                t('workflowActivityVNext.common.unavailable', 'Unavailable'),
            },
            {
              key: 'error',
              label: t('workflowActivityVNext.run.error', 'Final error'),
              children: run.finalError ? (
                <RunFailureSummary detail={run.finalError} kind="run" />
              ) : (
                t('workflowActivityVNext.common.unavailable', 'Unavailable')
              ),
            },
          ]}
        />
      </div>
      <RelatedRuns lineage={run.lineage} scopeId={scopeId} />
      <Tabs
        className="wa-vnext__run-tabs"
        items={[
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
                      <th>{t('workflowActivityVNext.run.type', 'Type')}</th>
                      <th>
                        {t(
                          'workflowActivityVNext.activity.columnStatus',
                          'Status',
                        )}
                      </th>
                      <th>{t('workflowActivityVNext.run.output', 'Output')}</th>
                      <th>
                        {t(
                          'workflowActivityVNext.run.requestParameters',
                          'Request parameters',
                        )}
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {run.steps.map((step) => (
                      <tr key={step.stepId}>
                        <td
                          className="wa-vnext__mono"
                          data-label={t(
                            'workflowActivityVNext.run.step',
                            'Step',
                          )}
                        >
                          {step.stepId}
                        </td>
                        <td
                          data-label={t(
                            'workflowActivityVNext.run.type',
                            'Type',
                          )}
                        >
                          {step.stepType}
                        </td>
                        <td
                          data-label={t(
                            'workflowActivityVNext.activity.columnStatus',
                            'Status',
                          )}
                        >
                          {step.success === null
                            ? t(
                                'workflowActivityVNext.common.pending',
                                'Pending',
                              )
                            : step.success
                              ? t(
                                  'workflowActivityVNext.common.succeeded',
                                  'Succeeded',
                                )
                              : t(
                                  'workflowActivityVNext.common.failed',
                                  'Failed',
                                )}
                        </td>
                        <td
                          data-label={t(
                            'workflowActivityVNext.run.output',
                            'Output',
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
                        </td>
                        <td
                          data-label={t(
                            'workflowActivityVNext.run.requestParameters',
                            'Request parameters',
                          )}
                        >
                          <pre className="wa-vnext__mono">
                            {Object.keys(step.requestParameters).length
                              ? JSON.stringify(step.requestParameters, null, 2)
                              : t(
                                  'workflowActivityVNext.common.empty',
                                  'Empty',
                                )}
                          </pre>
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
            key: 'diagnostics',
            label: t('workflowActivityVNext.run.diagnostics', 'Diagnostics'),
            children: run.diagnostics.length ? (
              <TableScrollRegion
                ariaLabel={t(
                  'workflowActivityVNext.run.diagnostics',
                  'Diagnostics',
                )}
              >
                <table className="wa-vnext__table">
                  <thead>
                    <tr>
                      <th>
                        {t('workflowActivityVNext.run.severity', 'Severity')}
                      </th>
                      <th>{t('workflowActivityVNext.run.code', 'Code')}</th>
                      <th>{t('workflowActivityVNext.run.step', 'Step')}</th>
                      <th>
                        {t('workflowActivityVNext.run.message', 'Message')}
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {run.diagnostics.map((diagnostic) => (
                      <tr
                        key={[
                          diagnostic.timestampUtc,
                          diagnostic.code,
                          diagnostic.stepId,
                          diagnostic.message,
                        ].join('|')}
                      >
                        <td
                          data-label={t(
                            'workflowActivityVNext.run.severity',
                            'Severity',
                          )}
                        >
                          {diagnostic.severity}
                        </td>
                        <td
                          className="wa-vnext__mono"
                          data-label={t(
                            'workflowActivityVNext.run.code',
                            'Code',
                          )}
                        >
                          {diagnostic.code}
                        </td>
                        <td
                          className="wa-vnext__mono"
                          data-label={t(
                            'workflowActivityVNext.run.step',
                            'Step',
                          )}
                        >
                          {diagnostic.stepId ||
                            t(
                              'workflowActivityVNext.common.unavailable',
                              'Unavailable',
                            )}
                        </td>
                        <td
                          data-label={t(
                            'workflowActivityVNext.run.message',
                            'Message',
                          )}
                        >
                          {diagnostic.message}
                          {diagnostic.hint ? (
                            <span className="wa-vnext__sub">
                              {diagnostic.hint}
                            </span>
                          ) : null}
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
                    'workflowActivityVNext.run.noDiagnostics',
                    'No diagnostics were returned.',
                  )}
                </p>
              </div>
            ),
          },
          {
            key: 'timeline',
            label: t('workflowActivityVNext.run.timeline', 'Timeline'),
            children: run.timeline.length ? (
              <ol>
                {run.timeline.map((event) => (
                  <li
                    key={[
                      event.timestampUtc,
                      event.kind,
                      event.stepId,
                      event.message,
                    ].join('|')}
                  >
                    <span className="wa-vnext__mono">{event.timestampUtc}</span>{' '}
                    · {event.kind} · {event.message}
                    {event.content ? (
                      <pre className="wa-vnext__mono">{event.content}</pre>
                    ) : null}
                    {event.toolCall ? (
                      <pre className="wa-vnext__mono">
                        {JSON.stringify(event.toolCall, null, 2)}
                      </pre>
                    ) : null}
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
            key: 'statistics',
            label: t(
              'workflowActivityVNext.run.statisticsUsage',
              'Statistics and usage',
            ),
            children: (
              <Descriptions
                bordered
                column={{ xs: 1, sm: 2 }}
                items={[
                  {
                    key: 'totalSteps',
                    label: t(
                      'workflowActivityVNext.run.totalSteps',
                      'Total steps',
                    ),
                    children: run.statistics.totalSteps,
                  },
                  {
                    key: 'requestedSteps',
                    label: t(
                      'workflowActivityVNext.run.requestedSteps',
                      'Requested steps',
                    ),
                    children: run.statistics.requestedSteps,
                  },
                  {
                    key: 'completedSteps',
                    label: t(
                      'workflowActivityVNext.run.completedSteps',
                      'Completed steps',
                    ),
                    children: run.statistics.completedSteps,
                  },
                  {
                    key: 'roleReplies',
                    label: t(
                      'workflowActivityVNext.run.roleReplies',
                      'Role replies',
                    ),
                    children: run.statistics.roleReplyCount,
                  },
                  {
                    key: 'promptTokens',
                    label: t(
                      'workflowActivityVNext.run.promptTokens',
                      'Prompt tokens',
                    ),
                    children: run.usageTotals.promptTokens,
                  },
                  {
                    key: 'completionTokens',
                    label: t(
                      'workflowActivityVNext.run.completionTokens',
                      'Completion tokens',
                    ),
                    children: run.usageTotals.completionTokens,
                  },
                  {
                    key: 'totalTokens',
                    label: t(
                      'workflowActivityVNext.run.totalTokens',
                      'Total tokens',
                    ),
                    children: run.usageTotals.totalTokens,
                  },
                  {
                    key: 'cost',
                    label: t('workflowActivityVNext.run.cost', 'Returned cost'),
                    children: run.usageTotals.cost,
                  },
                ]}
              />
            ),
          },
          {
            key: 'graph',
            label: t('workflowActivityVNext.run.graph', 'Graph'),
            children: graph.isPending ? (
              <p>
                {t(
                  'workflowActivityVNext.run.graphLoading',
                  'Loading run graph…',
                )}
              </p>
            ) : graph.isError ? (
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
            ) : (
              <div>
                <p>
                  {t(
                    'workflowActivityVNext.run.graphSummary',
                    '{nodes} nodes · {edges} edges',
                    {
                      nodes: graph.data?.nodes.length ?? 0,
                      edges: graph.data?.edges.length ?? 0,
                    },
                  )}
                </p>
                <ul>
                  {graph.data?.nodes.map((node) => (
                    <li className="wa-vnext__mono" key={node.nodeId}>
                      {node.nodeId}
                      {node.stepId ? ` · ${node.stepId}` : ''}
                    </li>
                  ))}
                </ul>
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
        <Descriptions
          column={1}
          items={[
            {
              key: 'revision',
              label: t(
                'workflowActivityVNext.run.definitionRevision',
                'Definition revision',
              ),
              children:
                recovery.workflowDefinitionRevisionId ||
                t('workflowActivityVNext.common.unavailable', 'Unavailable'),
            },
            {
              key: 'version',
              label: t(
                'workflowActivityVNext.run.definitionVersion',
                'Definition version',
              ),
              children: recovery.workflowDefinitionVersion,
            },
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
            {
              key: 'reuse',
              label: t(
                'workflowActivityVNext.run.priorOutputs',
                'Prior step outputs',
              ),
              children: pendingRecoveryPresentation?.reusesPriorStepOutputs
                ? t(
                    'workflowActivityVNext.run.priorOutputsReused',
                    'Prior step outputs will be reused.',
                  )
                : t(
                    'workflowActivityVNext.run.priorOutputsNotReused',
                    'Prior step outputs will not be reused.',
                  ),
            },
          ]}
        />
        <Alert
          message={t(
            'workflowActivityVNext.run.sourceImmutable',
            "This creates a separate run. The source run won't change.",
          )}
          showIcon
          type="info"
        />
        {pendingRecoveryPresentation?.mayIncurModelOrToolCost ? (
          <Alert
            message={t(
              'workflowActivityVNext.run.costWarning',
              'This action may incur model or tool costs again.',
            )}
            showIcon
            type="warning"
          />
        ) : null}
      </Modal>
    </WorkflowActivityVNextShell>
  );
};

export default RunDetailPage;
