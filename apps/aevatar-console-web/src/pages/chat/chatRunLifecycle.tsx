import { Button, Space, Tag, Typography } from 'antd';
import React from 'react';
import { t } from '@/shared/i18n/messages';
import type {
  ChatMessage,
  ChatSessionState,
  LocalChatStatus,
} from './chatTypes';

const PERMISSION_REMEDIATION_CODES = new Set([
  'REQUIRED_SERVICE_ACCESS_MISSING',
  'SERVICE_ACCESS_REVIEW_REQUIRED',
  'NYXID_SERVICE_ACCESS_REQUIRED',
  'NYXID_OPERATION_AUTHORIZATION_REQUIRED',
  'TEAM_AUTOMATION_REAUTHORIZATION_REQUIRED',
  'WORKFLOW_AUTHORIZATION_REQUIRED',
]);

export type ChatRunLifecycleTone =
  | 'default'
  | 'processing'
  | 'success'
  | 'warning'
  | 'error';

export type ChatRunLifecycleView = {
  details: string[];
  permissionCode?: string;
  state:
    | 'pending'
    | 'running'
    | 'needs_you'
    | 'failed'
    | 'stopped'
    | 'completed'
    | 'recovery';
  title: string;
  tone: ChatRunLifecycleTone;
};

function latestAssistantMessage(
  messages: readonly ChatMessage[],
): ChatMessage | undefined {
  return [...messages]
    .reverse()
    .find((message) => message.role === 'assistant');
}

function durableState(
  completionStatus?: string,
): ChatRunLifecycleView['state'] | undefined {
  switch (completionStatus?.trim().toLowerCase()) {
    case 'pending':
    case 'queued':
    case 'accepted':
      return 'pending';
    case 'running':
    case 'in_progress':
    case 'executing':
      return 'running';
    case 'waiting_approval':
    case 'waiting_input':
    case 'waiting_signal':
    case 'suspended':
      return 'needs_you';
    case 'failed':
    case 'error':
      return 'failed';
    case 'stopped':
    case 'cancelled':
    case 'canceled':
      return 'stopped';
    case 'completed':
    case 'succeeded':
    case 'success':
      return 'completed';
    default:
      return undefined;
  }
}

export function isPermissionRemediationCode(code?: string): boolean {
  return Boolean(code && PERMISSION_REMEDIATION_CODES.has(code));
}

export function resolveChatRunLifecycle(input: {
  completionStatus?: string;
  failureCode?: string;
  messages: readonly ChatMessage[];
  reconciliationFailed?: boolean;
  runId?: string;
  session: ChatSessionState;
  status: LocalChatStatus;
}): ChatRunLifecycleView | null {
  const latest = latestAssistantMessage(input.messages);
  const runId = input.runId || input.session.runId;
  const runningStep = [...(latest?.steps ?? [])]
    .reverse()
    .find((step) => step.status === 'running');
  const runningTool = [...(latest?.toolCalls ?? [])]
    .reverse()
    .find((tool) => tool.status === 'running');
  const details = [
    runId
      ? t('pages.chat.runLifecycle.run', 'Run {runId}', {
          runId,
        })
      : '',
    runningStep
      ? t('pages.chat.runLifecycle.currentStep', 'Current step: {step}', {
          step: runningStep.name,
        })
      : '',
    runningTool
      ? t('pages.chat.runLifecycle.runningTool', 'Running tool: {tool}', {
          tool: runningTool.name,
        })
      : '',
  ].filter(Boolean);

  if (isPermissionRemediationCode(input.failureCode)) {
    return {
      details,
      permissionCode: input.failureCode,
      state: 'needs_you',
      title: t(
        'pages.chat.runLifecycle.permissionRequired',
        'Permission required',
      ),
      tone: 'warning',
    };
  }

  if (latest?.pendingApproval || latest?.pendingRunIntervention) {
    return {
      details,
      state: 'needs_you',
      title: t('pages.chat.runLifecycle.needsYou', 'Run needs you'),
      tone: 'warning',
    };
  }

  if (
    !runId &&
    (input.status === 'completed_text' ||
      input.status === 'completed_with_studio_target')
  ) {
    return {
      details: [
        t(
          'pages.chat.runLifecycle.contextUnavailable',
          'This history entry has no durable Run identity. Start a new turn or open Runs to recover context.',
        ),
      ],
      state: 'recovery',
      title: t(
        'pages.chat.runLifecycle.contextMissing',
        'Run context unavailable',
      ),
      tone: 'warning',
    };
  }

  if (input.reconciliationFailed && runId) {
    return {
      details: [
        ...details,
        t(
          'pages.chat.runLifecycle.refreshFailed',
          'Durable Run state could not be refreshed. Open Run Detail or retry the conversation to recover.',
        ),
      ],
      state: 'recovery',
      title: t(
        'pages.chat.runLifecycle.refreshFailedTitle',
        'Run state unavailable',
      ),
      tone: 'warning',
    };
  }

  const authoritativeState = durableState(input.completionStatus);
  const state =
    authoritativeState ??
    (input.status === 'stopped'
      ? 'stopped'
      : input.status === 'error'
        ? 'failed'
        : input.status === 'streaming' || input.status === 'creating'
          ? runId
            ? 'running'
            : 'pending'
          : input.status === 'completed_text' ||
              input.status === 'completed_with_studio_target'
            ? 'completed'
            : undefined);

  switch (state) {
    case 'pending':
      return {
        details,
        state,
        title: t('pages.chat.runLifecycle.pending', 'Run pending'),
        tone: 'default',
      };
    case 'running':
      return {
        details,
        state,
        title: t('pages.chat.runLifecycle.running', 'Run in progress'),
        tone: 'processing',
      };
    case 'needs_you':
      return {
        details,
        state,
        title: t('pages.chat.runLifecycle.needsYou', 'Run needs you'),
        tone: 'warning',
      };
    case 'failed':
      return {
        details,
        state,
        title: t('pages.chat.runLifecycle.failed', 'Run failed'),
        tone: 'error',
      };
    case 'stopped':
      return {
        details,
        state,
        title: t('pages.chat.runLifecycle.stopped', 'Run stopped'),
        tone: 'default',
      };
    case 'completed':
      return {
        details,
        state,
        title: t('pages.chat.runLifecycle.completed', 'Run completed'),
        tone: 'success',
      };
    default:
      return input.messages.length > 0
        ? {
            details: [
              t(
                'pages.chat.runLifecycle.contextUnavailable',
                'This history entry has no durable Run identity. Start a new turn or open Runs to recover context.',
              ),
            ],
            state: 'recovery',
            title: t(
              'pages.chat.runLifecycle.contextMissing',
              'Run context unavailable',
            ),
            tone: 'warning',
          }
        : null;
  }
}

function lifecycleStateLabel(state: ChatRunLifecycleView['state']): string {
  switch (state) {
    case 'pending':
      return t('pages.chat.runLifecycle.state.pending', 'Pending');
    case 'running':
      return t('pages.chat.runLifecycle.state.running', 'Running');
    case 'needs_you':
      return t('pages.chat.runLifecycle.state.needsYou', 'Needs you');
    case 'failed':
      return t('pages.chat.runLifecycle.state.failed', 'Failed');
    case 'stopped':
      return t('pages.chat.runLifecycle.state.stopped', 'Stopped');
    case 'completed':
      return t('pages.chat.runLifecycle.state.completed', 'Completed');
    case 'recovery':
      return t('pages.chat.runLifecycle.state.recovery', 'Recovery');
  }
}

export function ChatRunLifecycleCard({
  onOpenRun,
  onReviewAccess,
  view,
}: {
  onOpenRun?: () => void;
  onReviewAccess?: () => void;
  view: ChatRunLifecycleView;
}): React.ReactElement {
  return (
    <section
      aria-label={t('pages.chat.runLifecycle.label', 'Run lifecycle')}
      style={{
        background: '#ffffff',
        border: '1px solid #d9dee8',
        borderRadius: 6,
        margin: '0 auto 14px',
        maxWidth: 1440,
        padding: '12px 14px',
        width: '100%',
      }}
    >
      <Space direction="vertical" size={8} style={{ width: '100%' }}>
        <Space align="center" size={8} wrap>
          <Typography.Text strong>{view.title}</Typography.Text>
          <Tag color={view.tone}>{lifecycleStateLabel(view.state)}</Tag>
        </Space>
        {view.details.map((detail) => (
          <Typography.Text
            key={detail}
            style={{ color: '#4b5563', fontSize: 12 }}
          >
            {detail}
          </Typography.Text>
        ))}
        {view.permissionCode ? (
          <Typography.Text code>{view.permissionCode}</Typography.Text>
        ) : null}
        {onOpenRun || onReviewAccess ? (
          <Space size={8} wrap>
            {onOpenRun ? (
              <Button onClick={onOpenRun} size="small">
                {t('pages.chat.runLifecycle.openRun', 'Open Run Detail')}
              </Button>
            ) : null}
            {onReviewAccess ? (
              <Button onClick={onReviewAccess} size="small" type="primary">
                {t('pages.chat.runLifecycle.reviewAccess', 'Review access')}
              </Button>
            ) : null}
          </Space>
        ) : null}
      </Space>
    </section>
  );
}
