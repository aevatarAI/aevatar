import { t } from '@/shared/i18n/messages';

export function getRunStatusPresentation(status: string): {
  readonly className: string;
  readonly label: string;
} {
  switch (status.trim().toLowerCase()) {
    case 'accepted':
    case 'pending':
    case 'queued':
      return {
        className: 'accepted',
        label: t('workflowActivityVNext.activity.statusQueued', 'Queued'),
      };
    case 'running':
      return {
        className: 'running',
        label: t('workflowActivityVNext.activity.statusRunning', 'Running'),
      };
    case 'completed':
    case 'succeeded':
      return {
        className: 'succeeded',
        label: t('workflowActivityVNext.activity.statusCompleted', 'Completed'),
      };
    case 'failed':
      return {
        className: 'failed',
        label: t('workflowActivityVNext.common.failed', 'Failed'),
      };
    case 'timed_out':
    case 'timedout':
      return {
        className: 'failed',
        label: t('workflowActivityVNext.activity.statusTimedOut', 'Timed out'),
      };
    case 'canceled':
    case 'cancelled':
    case 'stopped':
      return {
        className: 'unknown',
        label: t('workflowActivityVNext.activity.statusCancelled', 'Cancelled'),
      };
    default:
      return {
        className: 'unknown',
        label: t('workflowActivityVNext.common.unknown', 'Unknown'),
      };
  }
}

export function isRunStatusInProgress(status: string): boolean {
  const normalized = status.trim().toLowerCase();
  return (
    normalized === 'accepted' ||
    normalized === 'pending' ||
    normalized === 'queued' ||
    normalized === 'running'
  );
}

export function isRunStatusTerminal(status: string): boolean {
  const normalized = status.trim().toLowerCase();
  return (
    normalized === 'completed' ||
    normalized === 'succeeded' ||
    normalized === 'failed' ||
    normalized === 'timed_out' ||
    normalized === 'timedout' ||
    normalized === 'canceled' ||
    normalized === 'cancelled' ||
    normalized === 'stopped'
  );
}

export function getRunOriginLabel(origin: string): string {
  switch (origin.trim().toLowerCase()) {
    case 'ad-hoc-chat':
      return t('workflowActivityVNext.activity.originChat', 'Chat');
    case 'draft':
      return t('workflowActivityVNext.activity.originEditor', 'Editor');
    case 'member-invoke':
      return t('workflowActivityVNext.activity.originMember', 'Team member');
    case 'service-invoke':
      return t('workflowActivityVNext.activity.originService', 'Service');
    case 'schedule':
      return t('workflowActivityVNext.activity.originSchedule', 'Schedule');
    default:
      return t('workflowActivityVNext.common.unknown', 'Unknown');
  }
}
