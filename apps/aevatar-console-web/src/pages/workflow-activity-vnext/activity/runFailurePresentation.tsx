import { Button, Space, Typography } from 'antd';
import React from 'react';
import { t } from '@/shared/i18n/messages';

export type RunFailureCategory =
  | 'access_denied'
  | 'cancelled'
  | 'internal_failure'
  | 'invalid_input'
  | 'rate_limited'
  | 'resource_missing'
  | 'session_expired'
  | 'state_conflict'
  | 'timeout_or_offline'
  | 'upstream_unavailable';

export type RunFailureAction =
  | 'back_to_activity'
  | 'open_activity'
  | 'open_settings'
  | 'reload'
  | 'retry'
  | 'review_input'
  | 'sign_in';

export type RunFailureEvidence = {
  readonly code?: string;
  readonly correlationId?: string;
  readonly message?: string;
  readonly retryAfterSeconds?: number;
  readonly status?: number;
};

export type RunFailurePresentation = {
  readonly action: RunFailureAction;
  readonly actionLabel: string;
  readonly category: RunFailureCategory;
  readonly correlationId?: string;
  readonly duration: number;
  readonly guidance: string;
  readonly intent: 'error' | 'info' | 'warning';
  readonly message: string;
  readonly retryAfterSeconds?: number;
};

type CategoryDefinition = Omit<
  RunFailurePresentation,
  'correlationId' | 'message' | 'retryAfterSeconds'
> & {
  readonly fallbackMessage: string;
};

const RUN_FAILURE_TOAST_DURATION_SECONDS = 8;

function normalizeCode(value: string | undefined): string {
  return value?.trim().toUpperCase() ?? '';
}

function isSafeUserMessage(value: string | undefined): value is string {
  const normalized = value?.replace(/\s+/g, ' ').trim() ?? '';
  if (!normalized || normalized.length > 240) return false;
  return !(
    /(?:GET|POST|PUT|PATCH|DELETE)\s+\//i.test(normalized) ||
    /(?:stack\s*trace|\bat\s+[A-Za-z_$][\w$]*\s*\()/i.test(normalized) ||
    /https?:\/\//i.test(normalized) ||
    (/^[[{]/.test(normalized) && /[}\]]$/.test(normalized))
  );
}

function categoryFor(evidence: RunFailureEvidence): RunFailureCategory {
  const code = normalizeCode(evidence.code);
  const status = evidence.status;

  if (status === 401 || /(?:SESSION|TOKEN).*(?:EXPIRED|INVALID)/.test(code))
    return 'session_expired';
  if (
    status === 403 ||
    /(?:ACCESS_DENIED|FORBIDDEN|GROUP_NOT_ALLOWED|MODEL_NOT_ALLOWED)/.test(code)
  )
    return 'access_denied';
  if (status === 429 || /RATE_LIMIT/.test(code)) return 'rate_limited';
  if (status === 400 || status === 422 || /(?:INVALID|VALIDATION)/.test(code))
    return 'invalid_input';
  if (status === 404 || /NOT_FOUND/.test(code)) return 'resource_missing';
  if (
    status === 409 ||
    status === 412 ||
    /(?:CONFLICT|STATE_VERSION)/.test(code)
  )
    return 'state_conflict';
  if (
    status === 0 ||
    status === 408 ||
    status === 504 ||
    /(?:NETWORK|OFFLINE|TIMEOUT|TIMED_OUT)/.test(code)
  )
    return 'timeout_or_offline';
  if (
    status === 502 ||
    status === 503 ||
    /(?:PROVIDER|UPSTREAM).*UNAVAILABLE/.test(code)
  )
    return 'upstream_unavailable';
  if (status === 499 || /(?:CANCELLED|CANCELED|ABORTED)/.test(code))
    return 'cancelled';
  return 'internal_failure';
}

function definitionFor(category: RunFailureCategory): CategoryDefinition {
  switch (category) {
    case 'session_expired':
      return {
        action: 'sign_in',
        actionLabel: t(
          'workflowActivityVNext.failure.signInAgain',
          'Sign in again',
        ),
        category,
        duration: RUN_FAILURE_TOAST_DURATION_SECONDS,
        fallbackMessage: t(
          'workflowActivityVNext.failure.sessionExpired',
          'Your session has expired.',
        ),
        guidance: t(
          'workflowActivityVNext.failure.sessionExpiredGuidance',
          'Sign in again to continue.',
        ),
        intent: 'error',
      };
    case 'access_denied':
      return {
        action: 'open_settings',
        actionLabel: t(
          'workflowActivityVNext.failure.chooseAllowedService',
          'Choose allowed service',
        ),
        category,
        duration: RUN_FAILURE_TOAST_DURATION_SECONDS,
        fallbackMessage: t(
          'workflowActivityVNext.failure.accessDenied',
          'You do not have access to use this service or model.',
        ),
        guidance: t(
          'workflowActivityVNext.failure.accessDeniedGuidance',
          'Request access or choose a service and model available to your account.',
        ),
        intent: 'error',
      };
    case 'rate_limited':
      return {
        action: 'retry',
        actionLabel: t('workflowActivityVNext.common.retry', 'Retry'),
        category,
        duration: RUN_FAILURE_TOAST_DURATION_SECONDS,
        fallbackMessage: t(
          'workflowActivityVNext.failure.rateLimited',
          'This request was rate limited.',
        ),
        guidance: t(
          'workflowActivityVNext.failure.rateLimitedGuidance',
          'Wait for the quota window to reset before trying again.',
        ),
        intent: 'warning',
      };
    case 'invalid_input':
      return {
        action: 'review_input',
        actionLabel: t(
          'workflowActivityVNext.failure.reviewInput',
          'Review input',
        ),
        category,
        duration: RUN_FAILURE_TOAST_DURATION_SECONDS,
        fallbackMessage: t(
          'workflowActivityVNext.failure.invalidInput',
          'The input or workflow definition needs attention.',
        ),
        guidance: t(
          'workflowActivityVNext.failure.invalidInputGuidance',
          'Review the affected input or workflow step before trying again.',
        ),
        intent: 'warning',
      };
    case 'resource_missing':
      return {
        action: 'back_to_activity',
        actionLabel: t(
          'workflowActivityVNext.run.backAria',
          'Back to Activity',
        ),
        category,
        duration: RUN_FAILURE_TOAST_DURATION_SECONDS,
        fallbackMessage: t(
          'workflowActivityVNext.failure.resourceMissing',
          'This run is no longer available.',
        ),
        guidance: t(
          'workflowActivityVNext.failure.resourceMissingGuidance',
          'Refresh Activity or return to the run list.',
        ),
        intent: 'error',
      };
    case 'state_conflict':
      return {
        action: 'reload',
        actionLabel: t(
          'workflowActivityVNext.failure.reloadLatest',
          'Reload latest',
        ),
        category,
        duration: RUN_FAILURE_TOAST_DURATION_SECONDS,
        fallbackMessage: t(
          'workflowActivityVNext.failure.stateConflict',
          'This run changed since it was loaded.',
        ),
        guidance: t(
          'workflowActivityVNext.failure.stateConflictGuidance',
          'Reload the latest state before continuing.',
        ),
        intent: 'warning',
      };
    case 'timeout_or_offline':
      return {
        action: 'retry',
        actionLabel: t('workflowActivityVNext.common.retry', 'Retry'),
        category,
        duration: RUN_FAILURE_TOAST_DURATION_SECONDS,
        fallbackMessage: t(
          'workflowActivityVNext.failure.timeoutOrOffline',
          'The request timed out or the connection was interrupted.',
        ),
        guidance: t(
          'workflowActivityVNext.failure.timeoutOrOfflineGuidance',
          'Check the connection, then try again.',
        ),
        intent: 'warning',
      };
    case 'upstream_unavailable':
      return {
        action: 'retry',
        actionLabel: t('workflowActivityVNext.common.retry', 'Retry'),
        category,
        duration: RUN_FAILURE_TOAST_DURATION_SECONDS,
        fallbackMessage: t(
          'workflowActivityVNext.failure.upstreamUnavailable',
          'The selected service is temporarily unavailable.',
        ),
        guidance: t(
          'workflowActivityVNext.failure.upstreamUnavailableGuidance',
          'Check service status or try again later.',
        ),
        intent: 'error',
      };
    case 'cancelled':
      return {
        action: 'open_activity',
        actionLabel: t(
          'workflowActivityVNext.editor.openActivity',
          'Open Activity',
        ),
        category,
        duration: RUN_FAILURE_TOAST_DURATION_SECONDS,
        fallbackMessage: t(
          'workflowActivityVNext.failure.cancelled',
          'Run cancelled.',
        ),
        guidance: t(
          'workflowActivityVNext.failure.cancelledGuidance',
          'The run stopped without being reported as a system failure.',
        ),
        intent: 'info',
      };
    case 'internal_failure':
      return {
        action: 'reload',
        actionLabel: t(
          'workflowActivityVNext.failure.reloadLatest',
          'Reload latest',
        ),
        category,
        duration: RUN_FAILURE_TOAST_DURATION_SECONDS,
        fallbackMessage: t(
          'workflowActivityVNext.failure.internal',
          'The request could not be completed.',
        ),
        guidance: t(
          'workflowActivityVNext.failure.internalGuidance',
          'Try again or contact support with the tracking ID when one is available.',
        ),
        intent: 'error',
      };
  }
}

export function classifyRunFailure(
  evidence: RunFailureEvidence,
): RunFailurePresentation {
  const category = categoryFor(evidence);
  const definition = definitionFor(category);
  const retryAfterSeconds =
    typeof evidence.retryAfterSeconds === 'number' &&
    Number.isFinite(evidence.retryAfterSeconds) &&
    evidence.retryAfterSeconds >= 0
      ? Math.ceil(evidence.retryAfterSeconds)
      : undefined;
  return {
    ...definition,
    correlationId: evidence.correlationId?.trim() || undefined,
    message: isSafeUserMessage(evidence.message)
      ? evidence.message.replace(/\s+/g, ' ').trim()
      : definition.fallbackMessage,
    retryAfterSeconds,
  };
}

export const RunFailureToastContent: React.FC<{
  readonly onAction?: (action: RunFailureAction) => void;
  readonly presentation: RunFailurePresentation;
}> = ({ onAction, presentation }) => {
  const correlationId = presentation.correlationId;
  return (
    <div
      style={{
        alignItems: 'flex-start',
        display: 'flex',
        flexDirection: 'column',
        gap: 4,
        textAlign: 'left',
      }}
    >
      <Typography.Text strong>{presentation.message}</Typography.Text>
      <Typography.Text type="secondary">
        {presentation.guidance}
      </Typography.Text>
      {presentation.retryAfterSeconds !== undefined ? (
        <Typography.Text type="secondary">
          {t(
            'workflowActivityVNext.failure.retryAfter',
            'Try again in {seconds} seconds.',
            { seconds: presentation.retryAfterSeconds },
          )}
        </Typography.Text>
      ) : null}
      <Space size="small" wrap>
        {onAction ? (
          <Button
            onClick={() => onAction(presentation.action)}
            size="small"
            type="link"
          >
            {presentation.actionLabel}
          </Button>
        ) : null}
        {correlationId ? (
          <Button
            onClick={() => {
              void navigator.clipboard?.writeText(correlationId);
            }}
            size="small"
            type="link"
          >
            {t(
              'workflowActivityVNext.failure.copyTrackingId',
              'Copy tracking ID',
            )}
          </Button>
        ) : null}
      </Space>
    </div>
  );
};
