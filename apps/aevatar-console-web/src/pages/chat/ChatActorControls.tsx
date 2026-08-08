import {
  CheckOutlined,
  CloseOutlined,
  PauseCircleOutlined,
  RedoOutlined,
  StopOutlined,
} from '@ant-design/icons';
import { Button, Tag, Tooltip } from 'antd';
import React, { useState } from 'react';
import { t } from '@/shared/i18n/messages';
import type {
  ChatActionSummary,
  ChatActorProjection,
  ChatActorStep,
  ChatPendingApproval,
  ChatPendingInput,
  ChatServiceConnectActionRequest,
} from './chatActorState';
import { chatActionIdentityKey } from './chatActorState';
import type { ChatInputAnswer } from './chatApi';
import type { ChatExternalEffect, ChatPlanGate } from './chatTaskPlan';

type ActionReport = {
  actionRequestId: string;
  originTurnId: string;
  disposition: 'completed' | 'declined' | 'failed' | 'cancelled' | 'expired';
  resource?: { userService: { userServiceId: string } };
};

export type ChatActionJourney = {
  report?: ActionReport;
  busy?: boolean;
  error?: string;
  baseline?: ReadonlySet<string>;
};

type Props = {
  projection: ChatActorProjection | null;
  actionJourneys?: ReadonlyMap<string, ChatActionJourney>;
  disabled?: boolean;
  onInputResolve: (answer: ChatInputAnswer, input: ChatPendingInput) => void;
  onPlanResolve: (confirmed: boolean, gate: ChatPlanGate) => void;
  onStop: () => void;
  onSteer: (instruction: string) => void;
  onRetry: (step: ChatActorStep) => void;
  onSkip: (step: ChatActorStep) => void;
  onActionOpen: (request: ChatServiceConnectActionRequest) => void;
  onActionRefresh: (request: ChatServiceConnectActionRequest) => void;
  onActionConnectCredential: (
    request: ChatServiceConnectActionRequest,
    credential: string,
  ) => Promise<void>;
  onActionReport: (
    request: ChatServiceConnectActionRequest,
    disposition: ActionReport['disposition'],
  ) => void;
};

const buttonStyle: React.CSSProperties = {
  background: '#fff',
  border: '1px solid #d8dee8',
  borderRadius: 7,
  cursor: 'pointer',
  fontSize: 12,
  minHeight: 30,
  padding: '5px 10px',
};

export function ChatActorControls({
  projection,
  actionJourneys = new Map(),
  disabled = false,
  onInputResolve,
  onPlanResolve,
  onStop,
  onRetry,
  onSkip,
  onActionOpen,
  onActionRefresh,
  onActionConnectCredential,
  onActionReport,
}: Props): React.ReactElement | null {
  const [selectedOptionIds, setSelectedOptionIds] = useState<string[]>([]);
  const steps = [...(projection?.steps.values() ?? [])];
  const canStop = steps.some((step) => step.availableActions?.stop === true);
  const active = projection?.activeTurn?.status === 'active';
  const actions = [...(projection?.actions.values() ?? [])].filter(
    (action) => action.action === 'service.connect',
  );
  const terminal = projection ? latestTerminalFact(projection) : null;
  const pendingApproval = projection?.pendingApproval;
  const visibleApproval =
    pendingApproval && shouldRenderApproval(pendingApproval)
      ? pendingApproval
      : null;
  const hasControls = Boolean(
    projection?.task ||
      projection?.pendingInput ||
      visibleApproval ||
      canStop ||
      active ||
      actions.length ||
      terminal ||
      steps.some(
        (step) => step.availableActions?.retry || step.availableActions?.skip,
      ),
  );
  if (!projection || !hasControls) return null;

  const pendingInput = projection.pendingInput;
  return (
    <section
      aria-label={t('pages.chat.actorControls.actorControls', 'Actor controls')}
      style={{ display: 'flex', flexDirection: 'column', gap: 10 }}
    >
      {projection.task ? (
        <TaskPlanLedger
          disabled={disabled}
          onPlanResolve={onPlanResolve}
          projection={projection}
        />
      ) : null}

      {pendingInput ? (
        <ControlCard
          title={t('pages.chat.actorControls.inputRequired', 'Input required')}
        >
          <div>{pendingInput.prompt}</div>
          {pendingInput.options.map((option) => (
            <label key={option.optionId} style={{ display: 'block' }}>
              <input
                checked={selectedOptionIds.includes(option.optionId)}
                disabled={disabled}
                name={`actor-input-${pendingInput.requestId}`}
                onChange={(event) => {
                  if (pendingInput.multiSelect) {
                    setSelectedOptionIds((current) =>
                      event.target.checked
                        ? [...new Set([...current, option.optionId])]
                        : current.filter((id) => id !== option.optionId),
                    );
                  } else {
                    setSelectedOptionIds(
                      event.target.checked ? [option.optionId] : [],
                    );
                  }
                }}
                type={pendingInput.multiSelect ? 'checkbox' : 'radio'}
              />{' '}
              {option.label}
              {option.description ? ` — ${option.description}` : ''}
            </label>
          ))}
          {pendingInput.allowFreeText ? (
            <div style={{ color: '#64748b', fontSize: 12 }}>
              {t(
                'pages.chat.actorControls.answerInComposer',
                'Type the answer in the composer below.',
              )}
            </div>
          ) : null}
          {pendingInput.options.length ? (
            <Button
              disabled={disabled || !selectedOptionIds.length}
              onClick={() =>
                onInputResolve({ selectedOptionIds }, pendingInput)
              }
              size="small"
              type="primary"
            >
              {t('pages.chat.actorControls.submitAnswer', 'Submit answer')}
            </Button>
          ) : null}
        </ControlCard>
      ) : null}

      {visibleApproval ? (
        <ControlCard
          title={t('pages.chat.actorControls.nyxIdDecision', 'NyxID decision')}
        >
          <div>
            {visibleApproval.action || visibleApproval.toolName}
            {visibleApproval.target ? ` · ${visibleApproval.target}` : ''}
          </div>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
            <Tag>{String(visibleApproval.reversibility || 'unknown')}</Tag>
            <Tag>
              {t(
                'pages.chat.actorControls.nyxIdRequestObserved',
                'NyxID request observed',
              )}
            </Tag>
          </div>
          <div style={{ color: '#64748b', fontSize: 12 }}>
            {t(
              'pages.chat.actorControls.nyxIdDecisionOnly',
              'This decision is owned by NyxID. Studio only shows committed facts.',
            )}
          </div>
          <div style={{ color: '#475569', fontSize: 11 }}>
            {visibleApproval.nyxidRequestId}
          </div>
          {visibleApproval.expiresAt ? (
            <div style={{ color: '#64748b', fontSize: 11 }}>
              {visibleApproval.expiresAt}
            </div>
          ) : null}
        </ControlCard>
      ) : null}

      {steps.map((step) =>
        step.availableActions?.retry || step.availableActions?.skip ? (
          <ControlCard
            key={step.stepId}
            title={String(step.description || step.stepId)}
          >
            <div style={{ display: 'flex', gap: 8 }}>
              {step.availableActions.retry ? (
                <Button
                  aria-label={t(
                    'pages.chat.actorControls.retryStep',
                    'Retry {step}',
                    { step: String(step.description || step.stepId) },
                  )}
                  disabled={disabled}
                  onClick={() => onRetry(step)}
                  icon={<RedoOutlined />}
                  size="small"
                >
                  {t('pages.chat.actorControls.retry', 'Retry')}
                </Button>
              ) : null}
              {step.availableActions.skip ? (
                <Button
                  aria-label={t(
                    'pages.chat.actorControls.skipStep',
                    'Skip {step}',
                    { step: String(step.description || step.stepId) },
                  )}
                  disabled={disabled}
                  onClick={() => onSkip(step)}
                  size="small"
                >
                  {t('pages.chat.actorControls.skip', 'Skip')}
                </Button>
              ) : null}
            </div>
          </ControlCard>
        ) : null,
      )}

      {actions.map((action) => (
        <ActionCard
          action={action}
          actorConfirmed={steps.some(
            (step) =>
              step.actionRequestId === action.actionRequestId &&
              step.kind === 'postcondition' &&
              step.status === 'done' &&
              step.externalEffect === 'confirmed',
          )}
          disabled={disabled}
          journey={actionJourneys.get(
            chatActionIdentityKey(action.actorId, action.actionRequestId),
          )}
          key={chatActionIdentityKey(action.actorId, action.actionRequestId)}
          presentationTitle={projection.steps.get(action.stepId)?.description}
          onOpen={onActionOpen}
          onRefresh={onActionRefresh}
          onConnectCredential={onActionConnectCredential}
          onReport={onActionReport}
        />
      ))}

      {terminal ? <TerminalFact terminal={terminal} /> : null}

      {active ? (
        <ControlCard
          title={t('pages.chat.actorControls.activeTask', 'Active task')}
        >
          <div style={{ color: '#64748b', fontSize: 12 }}>
            {t(
              'pages.chat.actorControls.steerInComposer',
              'Type a steering instruction in the composer.',
            )}
          </div>
          <div style={{ display: 'flex', gap: 8 }}>
            {canStop ? (
              <Button
                danger
                disabled={disabled}
                icon={<StopOutlined />}
                onClick={onStop}
                size="small"
              >
                {t('pages.chat.actorControls.stopTask', 'Stop task')}
              </Button>
            ) : null}
          </div>
        </ControlCard>
      ) : null}
    </section>
  );
}

function TaskPlanLedger({
  disabled,
  onPlanResolve,
  projection,
}: {
  disabled: boolean;
  onPlanResolve: Props['onPlanResolve'];
  projection: ChatActorProjection;
}): React.ReactElement | null {
  const plan = projection.task;
  if (!plan) return null;
  const gate = plan.gate;
  const pendingGate = gate?.mode === 'confirm' && gate.status === 'pending';
  const statusCounts = plan.steps.reduce<Record<string, number>>(
    (counts, step) => {
      counts[step.status] = (counts[step.status] ?? 0) + 1;
      return counts;
    },
    {},
  );
  return (
    <section
      aria-label={t('pages.chat.actorControls.taskPlan', 'Task plan')}
      style={{
        background: '#fff',
        border: '1px solid #d8dee8',
        borderRadius: 8,
        overflow: 'hidden',
      }}
    >
      <div
        style={{
          alignItems: 'flex-start',
          background: '#f8fafc',
          borderBottom: '1px solid #e2e8f0',
          display: 'flex',
          flexWrap: 'wrap',
          gap: 10,
          justifyContent: 'space-between',
          padding: '12px 14px',
        }}
      >
        <div style={{ minWidth: 0 }}>
          <div
            style={{
              color: '#0f172a',
              fontSize: 14,
              fontWeight: 700,
              overflowWrap: 'anywhere',
            }}
          >
            {plan.title}
          </div>
          <div style={{ color: '#64748b', fontSize: 11, marginTop: 3 }}>
            {t(
              'pages.chat.actorControls.planRevision',
              'Plan revision {revision}',
              {
                revision: plan.planRevision,
              },
            )}
          </div>
        </div>
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
          <StatusTag status={plan.status} />
          {gate ? (
            <Tag>{`${gate.mode} · ${gate.status || 'ready'}`}</Tag>
          ) : null}
          {Object.entries(statusCounts).map(([status, count]) => (
            <Tag key={status}>{`${status} ${count}`}</Tag>
          ))}
        </div>
      </div>
      {gate?.reason ? (
        <div
          style={{
            borderBottom: '1px solid #eef2f7',
            color: '#475569',
            fontSize: 12,
            padding: '9px 14px',
          }}
        >
          {gate.reason}
        </div>
      ) : null}
      {pendingGate ? (
        <div
          style={{
            alignItems: 'center',
            borderBottom: '1px solid #eef2f7',
            display: 'flex',
            flexWrap: 'wrap',
            gap: 8,
            padding: '10px 14px',
          }}
        >
          <strong style={{ color: '#0f172a', fontSize: 12 }}>
            {t(
              'pages.chat.actorControls.planDecision',
              'Confirm this disclosed plan',
            )}
          </strong>
          <Button
            disabled={disabled}
            icon={<CheckOutlined />}
            onClick={() => onPlanResolve(true, gate)}
            size="small"
            type="primary"
          >
            {t('pages.chat.actorControls.confirmPlan', 'Confirm plan')}
          </Button>
          <Button
            danger
            disabled={disabled}
            icon={<CloseOutlined />}
            onClick={() => onPlanResolve(false, gate)}
            size="small"
          >
            {t('pages.chat.actorControls.rejectPlan', 'Reject plan')}
          </Button>
        </div>
      ) : null}
      <ol style={{ listStyle: 'none', margin: 0, padding: 0 }}>
        {plan.steps.map((step) => {
          const stalled = isActorReportedStalled(step);
          const verified =
            step.kind === 'postcondition' &&
            step.status === 'done' &&
            step.externalEffect === 'confirmed';
          return (
            <li
              key={step.stepId}
              style={{ borderTop: '1px solid #eef2f7', padding: '11px 14px' }}
            >
              <div
                style={{
                  alignItems: 'flex-start',
                  display: 'grid',
                  gap: 10,
                  gridTemplateColumns: '24px minmax(0, 1fr)',
                }}
              >
                <span
                  style={{
                    color: '#64748b',
                    fontSize: 12,
                    fontVariantNumeric: 'tabular-nums',
                    paddingTop: 2,
                  }}
                >
                  {step.order}
                </span>
                <div style={{ minWidth: 0 }}>
                  <div
                    style={{
                      alignItems: 'center',
                      display: 'flex',
                      flexWrap: 'wrap',
                      gap: 6,
                    }}
                  >
                    <strong
                      style={{
                        color: '#172033',
                        fontSize: 13,
                        overflowWrap: 'anywhere',
                      }}
                    >
                      {step.description}
                    </strong>
                    <StatusTag status={step.status} />
                    {stalled ? (
                      <Tag color="warning" icon={<PauseCircleOutlined />}>
                        {t('pages.chat.actorControls.stalled', 'Stalled')}
                      </Tag>
                    ) : null}
                    <EffectTag effect={step.externalEffect} />
                  </div>
                  <div
                    style={{
                      color: '#64748b',
                      display: 'flex',
                      flexWrap: 'wrap',
                      fontSize: 11,
                      gap: '4px 10px',
                      marginTop: 5,
                    }}
                  >
                    <span>{step.source.label}</span>
                    {step.source.kind === 'tool' && step.source.serviceSlug ? (
                      <span>{step.source.serviceSlug}</span>
                    ) : null}
                    {step.addedBy ? (
                      <span>{`addedBy: ${step.addedBy}`}</span>
                    ) : null}
                    {step.addedInPlanRevision ? (
                      <span>{`r${step.addedInPlanRevision}`}</span>
                    ) : null}
                    {step.estimate ? (
                      <span>{`~${step.estimate.seconds}s`}</span>
                    ) : null}
                  </div>
                  {step.operation ? (
                    <>
                      <div
                        style={{
                          background: '#f8fafc',
                          borderRadius: 6,
                          color: '#475569',
                          fontFamily: 'SFMono-Regular, Menlo, monospace',
                          fontSize: 11,
                          marginTop: 7,
                          overflowWrap: 'anywhere',
                          padding: '6px 8px',
                        }}
                      >
                        {[
                          step.operation.kind,
                          step.operation.phase,
                          step.operation.operationId,
                          step.operation.operationGeneration !== undefined
                            ? `generation ${step.operation.operationGeneration}`
                            : '',
                        ]
                          .filter(Boolean)
                          .join(' · ')}
                      </div>
                      {step.operation.lastProgressAt ? (
                        <div
                          style={{
                            color: '#64748b',
                            fontSize: 11,
                            marginTop: 5,
                          }}
                        >
                          {t(
                            'pages.chat.actorControls.lastProgressAt',
                            'Last progress {time}',
                            { time: step.operation.lastProgressAt },
                          )}
                        </div>
                      ) : null}
                      {stalled && step.operation.stalledAt ? (
                        <div
                          style={{
                            color: '#92400e',
                            fontSize: 11,
                            marginTop: 3,
                          }}
                        >
                          {t(
                            'pages.chat.actorControls.stalledAt',
                            'Stalled since {time}',
                            { time: step.operation.stalledAt },
                          )}
                        </div>
                      ) : null}
                    </>
                  ) : null}
                  {step.substeps.length ? (
                    <ul
                      style={{
                        display: 'grid',
                        gap: 4,
                        listStyle: 'none',
                        margin: '8px 0 0',
                        padding: 0,
                      }}
                    >
                      {step.substeps.map((substep) => (
                        <li
                          key={substep.substepId}
                          style={{
                            color: '#475569',
                            display: 'flex',
                            fontSize: 11,
                            gap: 7,
                          }}
                        >
                          <span aria-hidden>
                            {substep.status === 'done'
                              ? '✓'
                              : substep.status === 'failed'
                                ? '×'
                                : '•'}
                          </span>
                          <span
                            style={{ overflowWrap: 'anywhere' }}
                          >{`${substep.title} · ${substep.status}`}</span>
                        </li>
                      ))}
                    </ul>
                  ) : null}
                  {verified ? (
                    <div
                      style={{
                        color: '#047857',
                        fontSize: 11,
                        fontWeight: 600,
                        marginTop: 7,
                      }}
                    >
                      {t(
                        'pages.chat.actorControls.verifiedAgainst',
                        'Verified against {check}',
                        { check: step.source.label },
                      )}
                    </div>
                  ) : null}
                  {step.safeMessage ? (
                    <div
                      style={{ color: '#9f1239', fontSize: 11, marginTop: 6 }}
                    >
                      {step.safeMessage}
                    </div>
                  ) : null}
                </div>
              </div>
            </li>
          );
        })}
      </ol>
      <CommittedResults projection={projection} />
    </section>
  );
}

function CommittedResults({
  projection,
}: {
  projection: ChatActorProjection;
}): React.ReactElement | null {
  const results = [
    ['control', projection.latestControlResult],
    ['step-control', projection.latestStepControlResult],
    ['input', projection.latestInputResolution],
    ['approval', projection.latestApprovalResolution],
  ].filter((entry): entry is [string, Record<string, unknown>] =>
    Boolean(entry[1]),
  );
  if (!results.length) return null;
  return (
    <section
      aria-label={t(
        'pages.chat.actorControls.committedResults',
        'Committed results',
      )}
      style={{
        borderTop: '1px solid #e2e8f0',
        display: 'grid',
        gap: 6,
        padding: '10px 14px',
      }}
    >
      <strong
        style={{ color: '#334155', fontSize: 11, textTransform: 'uppercase' }}
      >
        {t('pages.chat.actorControls.committedResults', 'Committed results')}
      </strong>
      {results.map(([kind, result]) => (
        <div
          key={kind}
          style={{
            color: '#475569',
            display: 'flex',
            flexWrap: 'wrap',
            fontSize: 11,
            gap: 7,
          }}
        >
          <Tag>{String(result.outcome || result.status || 'committed')}</Tag>
          {typeof result.approved === 'boolean' ? (
            <Tag color={result.approved ? 'success' : 'error'}>
              {result.approved ? 'approved' : 'denied'}
            </Tag>
          ) : null}
          {result.reasonCode ? <span>{String(result.reasonCode)}</span> : null}
          {result.safeMessage ? (
            <span>{String(result.safeMessage)}</span>
          ) : null}
          {result.committedAt ? (
            <span>{String(result.committedAt)}</span>
          ) : null}
        </div>
      ))}
    </section>
  );
}

function TerminalFact({
  terminal,
}: {
  terminal: Record<string, unknown>;
}): React.ReactElement {
  const status = String(terminal.status || 'terminal');
  return (
    <ControlCard
      title={t('pages.chat.actorControls.taskResult', 'Task result')}
    >
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: 7 }}>
        <StatusTag status={status} />
        {terminal.safeMessage ? (
          <span style={{ color: '#475569', fontSize: 12 }}>
            {String(terminal.safeMessage)}
          </span>
        ) : null}
        {terminal.terminalAt ? (
          <span style={{ color: '#64748b', fontSize: 11 }}>
            {String(terminal.terminalAt)}
          </span>
        ) : null}
      </div>
    </ControlCard>
  );
}

function latestTerminalFact(
  projection: ChatActorProjection,
): Record<string, unknown> | null {
  const candidates = [projection.latestTurn, ...projection.recentTerminalTurns];
  return (
    candidates.find(
      (candidate) =>
        candidate &&
        candidate.status !== 'active' &&
        candidate.status !== 'running',
    ) ?? null
  );
}

function StatusTag({ status }: { status: string }): React.ReactElement {
  const color =
    status === 'done' || status === 'succeeded'
      ? 'success'
      : status === 'failed' || status === 'uncertain'
        ? 'error'
        : status === 'running' || status === 'active'
          ? 'processing'
          : status === 'waiting' || status === 'blocked'
            ? 'warning'
            : 'default';
  return <Tag color={color}>{status}</Tag>;
}

function EffectTag({
  effect,
}: {
  effect: ChatExternalEffect;
}): React.ReactElement {
  const color =
    effect === 'confirmed'
      ? 'success'
      : effect === 'may_have_changed'
        ? 'error'
        : effect === 'not_applied'
          ? 'blue'
          : 'default';
  return (
    <Tooltip
      title={t(
        'pages.chat.actorControls.externalEffect',
        'External effect evidence',
      )}
    >
      <Tag color={color}>{effect}</Tag>
    </Tooltip>
  );
}

function isActorReportedStalled(step: ChatActorStep): boolean {
  return Boolean(
    (step.status === 'running' || step.status === 'waiting') &&
      step.operation?.lastProgressAt &&
      step.operation.stalledAt &&
      (step.availableActions.retry ||
        step.availableActions.skip ||
        step.availableActions.stop),
  );
}

function shouldRenderApproval(approval: ChatPendingApproval): boolean {
  return Boolean(approval.nyxidRequestId);
}

function ControlCard({
  children,
  title,
}: {
  children: React.ReactNode;
  title: string;
}): React.ReactElement {
  return (
    <div
      style={{
        background: '#f8fafc',
        border: '1px solid #d8dee8',
        borderRadius: 8,
        display: 'flex',
        flexDirection: 'column',
        gap: 8,
        padding: 12,
      }}
    >
      <strong>{title}</strong>
      {children}
    </div>
  );
}

function ActionCard({
  action,
  actorConfirmed,
  journey,
  disabled,
  presentationTitle,
  onOpen,
  onRefresh,
  onConnectCredential,
  onReport,
}: {
  action: ChatActionSummary;
  actorConfirmed: boolean;
  journey?: ChatActionJourney;
  disabled: boolean;
  presentationTitle?: string;
  onOpen: Props['onActionOpen'];
  onRefresh: Props['onActionRefresh'];
  onConnectCredential: Props['onActionConnectCredential'];
  onReport: Props['onActionReport'];
}): React.ReactElement | null {
  const [credential, setCredential] = useState('');
  const request = action.request;
  if (action.conflicted) {
    return (
      <ControlCard
        title={
          presentationTitle ||
          t('pages.chat.actorControls.connectionAction', 'Service connection')
        }
      >
        <div role="alert">
          {t(
            'pages.chat.actorControls.actionIdentityConflict',
            'Action identity conflict; this browser journey is disabled.',
          )}
        </div>
      </ControlCard>
    );
  }
  if (!request) {
    const report = action.reports?.at(-1);
    const verified = action.postconditionResult?.verified === true;
    return (
      <ControlCard
        title={
          presentationTitle ||
          t('pages.chat.actorControls.connectionAction', 'Service connection')
        }
      >
        <div style={{ color: '#475569', fontSize: 12 }}>
          {verified
            ? t('pages.chat.actorControls.actorVerified', 'Actor verified')
            : report
              ? `${String(report.disposition)} · ${t('pages.chat.actorControls.postconditionPending', 'postcondition pending')}`
              : t(
                  'pages.chat.actorControls.waitingForAction',
                  'Waiting for the connection decision',
                )}
        </div>
        <div style={{ color: '#64748b', fontSize: 11 }}>
          {t(
            'pages.chat.actorControls.reloadedActionDetailsUnavailable',
            'This committed action is visible, but the current-state contract does not expose its connection parameters.',
          )}
        </div>
      </ControlCard>
    );
  }
  const actorReport = [...(action.reports ?? [])]
    .reverse()
    .find((candidate) => reportMatchesRequest(candidate, request));
  const localReport = journey?.report;
  const report =
    actorReport ??
    (localReport && reportMatchesRequest(localReport, request)
      ? localReport
      : null);
  const expectedId = readUserServiceId(report?.resource);
  const proof = action.postconditionResult;
  const verified = Boolean(
    report?.disposition === 'completed' &&
      (actorConfirmed ||
        (expectedId &&
          proof?.verified === true &&
          proof.actionRequestId === request.actionRequestId &&
          proof.disposition === report.disposition &&
          readUserServiceId(proof.resource) === expectedId)),
  );
  const serviceName =
    'catalogService' in request.params
      ? request.params.catalogService.serviceSlug
      : request.params.customService.name;
  return (
    <ControlCard
      title={
        presentationTitle ||
        t('pages.chat.actorControls.connectService', 'Connect {service}', {
          service: serviceName,
        })
      }
    >
      {'catalogService' in request.params &&
      request.params.catalogService.requestedScopes?.length ? (
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
          {request.params.catalogService.requestedScopes.map((scope) => (
            <Tag key={scope}>{scope}</Tag>
          ))}
        </div>
      ) : null}
      {verified ? (
        <div>
          {t('pages.chat.actorControls.actorVerified', 'Actor verified')}
        </div>
      ) : report ? (
        <div>
          {`${String(report.disposition)} · ${t(
            'pages.chat.actorControls.reportedWaitingProof',
            'Reported; waiting for actor verification',
          )}`}
        </div>
      ) : (
        <div>
          {t(
            'pages.chat.actorControls.waitingForAction',
            'Waiting for the connection decision',
          )}
        </div>
      )}
      {journey?.error ? <div role="alert">{journey.error}</div> : null}
      {!verified ? (
        <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
          {'catalogService' in request.params && !report ? (
            <>
              <input
                aria-label={t(
                  'pages.chat.actorControls.serviceCredential',
                  '{service} credential',
                  { service: serviceName },
                )}
                autoComplete="off"
                disabled={disabled || journey?.busy}
                onChange={(event) => setCredential(event.target.value)}
                type="password"
                value={credential}
              />
              <button
                disabled={disabled || journey?.busy || !credential.trim()}
                onClick={() => {
                  const value = credential;
                  setCredential('');
                  void onConnectCredential(request, value);
                }}
                style={buttonStyle}
                type="button"
              >
                {t('pages.chat.actorControls.connectNow', 'Connect {service}', {
                  service: serviceName,
                })}
              </button>
            </>
          ) : null}
          {!report ? (
            <button
              disabled={disabled || journey?.busy}
              onClick={() => onOpen(request)}
              style={buttonStyle}
              type="button"
            >
              {t('pages.chat.actorControls.openNyxId', 'Open NyxID connection')}
            </button>
          ) : null}
          <button
            aria-label={t(
              'pages.chat.actorControls.refreshConnection',
              'Refresh connection',
            )}
            disabled={disabled || journey?.busy}
            onClick={() => onRefresh(request)}
            style={buttonStyle}
            type="button"
          >
            {t('pages.chat.actorControls.refresh', 'Refresh')}
          </button>
          {!report ? (
            <>
              <button
                disabled={disabled}
                onClick={() => onReport(request, 'declined')}
                style={buttonStyle}
                type="button"
              >
                {t('pages.chat.actorControls.decline', 'Decline')}
              </button>
              <button
                disabled={disabled}
                onClick={() => onReport(request, 'cancelled')}
                style={buttonStyle}
                type="button"
              >
                {t('pages.chat.actorControls.cancel', 'Cancel')}
              </button>
            </>
          ) : null}
        </div>
      ) : null}
    </ControlCard>
  );
}

function reportMatchesRequest(
  input: unknown,
  request: ChatServiceConnectActionRequest,
): input is Record<string, unknown> {
  if (!input || typeof input !== 'object' || Array.isArray(input)) return false;
  const report = input as Record<string, unknown>;
  return (
    report.actionRequestId === request.actionRequestId &&
    report.originTurnId === request.originTurnId &&
    ['completed', 'declined', 'failed', 'cancelled', 'expired'].includes(
      String(report.disposition),
    )
  );
}

function readUserServiceId(input: unknown): string {
  if (!input || typeof input !== 'object' || Array.isArray(input)) return '';
  const resource = input as Record<string, unknown>;
  const nested = resource.userService;
  const nestedId =
    nested && typeof nested === 'object' && !Array.isArray(nested)
      ? (nested as Record<string, unknown>).userServiceId
      : undefined;
  const value = nestedId ?? resource.userServiceId;
  return typeof value === 'string' ? value.trim() : '';
}
