import type React from 'react';
import type { InvokeResultState } from './StudioMemberInvokePanel.currentRun';

export const studioInvokeColors = {
  accent: '#1677ff',
  activeBorder: '#91caff',
  assistantSoft: '#eff6ff',
  border: '#e5e7eb',
  borderStrong: '#bfdbfe',
  danger: '#b91c1c',
  dangerBorder: '#fecaca',
  dangerSoft: '#fef2f2',
  idle: '#94a3b8',
  meta: '#6b7280',
  muted: '#64748b',
  panel: '#ffffff',
  rawSurface: '#0f172a',
  rawText: '#e2e8f0',
  readyMuted: '#475569',
  success: '#15803d',
  successBorder: '#86efac',
  successDot: '#22c55e',
  successSoft: '#f0fdf4',
  surface: '#f8fafc',
  surfaceActive: '#f5f7ff',
  text: '#111827',
  textSoft: '#334155',
} as const;

export const monoFontFamily =
  "ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, 'Liberation Mono', monospace";

export const contractStatusPillBaseStyle: React.CSSProperties = {
  borderRadius: 999,
  display: 'inline-flex',
  fontSize: 12,
  fontWeight: 700,
  lineHeight: '18px',
  padding: '4px 10px',
  width: 'fit-content',
};

export const helperTextStyle: React.CSSProperties = {
  color: studioInvokeColors.muted,
  fontSize: 13,
  lineHeight: 1.6,
  minWidth: 0,
};

export const contractValueStyle: React.CSSProperties = {
  color: studioInvokeColors.text,
  display: 'block',
  fontSize: 13,
  fontWeight: 600,
  lineHeight: '20px',
  minWidth: 0,
  overflowWrap: 'anywhere',
  wordBreak: 'break-word',
};

export function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? '';
}

export function trimPreview(value: string, limit = 180): string {
  const trimmed = value.trim();
  if (!trimmed) {
    return '';
  }

  return trimmed.length > limit ? `${trimmed.slice(0, limit - 3)}...` : trimmed;
}

export function truncateMiddle(value: string, head = 18, tail = 12): string {
  if (value.length <= head + tail + 3) {
    return value;
  }

  return `${value.slice(0, head)}...${value.slice(-tail)}`;
}

export function formatHistoryTimestamp(value: number): string {
  if (!Number.isFinite(value) || value <= 0) {
    return '刚刚';
  }

  return new Intl.DateTimeFormat(globalThis.navigator?.language, {
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    month: 'short',
  }).format(value);
}

export function getInvokeRunStatusLabel(
  status: InvokeResultState['status'],
): string {
  switch (status) {
    case 'running':
      return '运行中';
    case 'success':
      return '成功';
    case 'error':
      return '失败';
    default:
      return '空闲';
  }
}

export function getInvokeStatusTone(status: InvokeResultState['status']): {
  readonly background: string;
  readonly border: string;
  readonly color: string;
  readonly dot: string;
} {
  if (status === 'running') {
    return {
      background: studioInvokeColors.assistantSoft,
      border: studioInvokeColors.borderStrong,
      color: '#1d4ed8',
      dot: studioInvokeColors.accent,
    };
  }

  if (status === 'success') {
    return {
      background: studioInvokeColors.successSoft,
      border: studioInvokeColors.successBorder,
      color: studioInvokeColors.success,
      dot: studioInvokeColors.successDot,
    };
  }

  if (status === 'error') {
    return {
      background: studioInvokeColors.dangerSoft,
      border: studioInvokeColors.dangerBorder,
      color: studioInvokeColors.danger,
      dot: '#ef4444',
    };
  }

  return {
    background: studioInvokeColors.surface,
    border: studioInvokeColors.border,
    color: studioInvokeColors.muted,
    dot: studioInvokeColors.idle,
  };
}
