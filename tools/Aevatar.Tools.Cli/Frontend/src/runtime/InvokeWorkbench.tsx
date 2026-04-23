import { useEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { Check, ChevronDown, Copy, Play, Share2 } from 'lucide-react';

import type { PendingHumanInputInfo, ServiceEndpoint, ServiceOption } from './chatTypes';
import { parseHumanInputChoices } from './humanInputUtils';
import {
  buildInvokeWorkbenchFrames,
  type InvokeEventRecord,
  type InvokeRunSummary,
  type InvokeSurfaceSupport,
  type InvokeWorkbenchFrame,
  type InvokeWorkbenchFrameKind,
  type InvokeWorkbenchMode,
} from './invokeUtils';

export type InvokeHistoryEntry = {
  id: string;
  serviceId: string;
  createdAt: number;
  updatedAt: number;
  request: {
    prompt: string;
    actorId: string;
    headersText: string;
    endpointId: string;
    endpointLabel: string;
  };
  transportLabel: string;
  requestPreview: string;
  events: InvokeEventRecord[];
  summary: InvokeRunSummary;
};

type InvokeWorkbenchProps = {
  scopeId: string;
  service: ServiceOption;
  invokeSupport: InvokeSurfaceSupport;
  invokeableEndpoints: ServiceEndpoint[];
  hiddenInvokeEndpoints: ServiceEndpoint[];
  activeEndpoint: ServiceEndpoint | null;
  endpointId: string;
  onEndpointChange: (endpointId: string) => void;
  prompt: string;
  onPromptChange: (value: string) => void;
  actorId: string;
  onActorIdChange: (value: string) => void;
  headersText: string;
  onHeadersTextChange: (value: string) => void;
  advancedOpen: boolean;
  onAdvancedOpenChange: (open: boolean) => void;
  formError: string | null;
  headerError: string | null;
  transportLabel: string;
  requestPreview: string;
  events: InvokeEventRecord[];
  summary: InvokeRunSummary;
  loading: boolean;
  pendingHumanInput: PendingHumanInputInfo | null;
  resumeLoading: boolean;
  resumeError: string | null;
  history: InvokeHistoryEntry[];
  activeHistoryId: string | null;
  onSelectHistory: (id: string) => void;
  onInvoke: () => void;
  onStop: () => void;
  onLoadFixture: () => void;
  onSaveRequest: () => void;
  onReplayLast: () => void;
  onResumeHumanInput: (userInput: string) => void;
  onGoToChat: () => void;
  onGoToRaw: () => void;
  renderResponse: (text: string) => ReactNode;
  copyText: (text: string) => Promise<boolean>;
};

const workbenchTokens: CSSProperties = {
  '--wb-paper-0': '#fbfaf6',
  '--wb-paper-1': '#f4f1e8',
  '--wb-paper-2': '#f8f6ef',
  '--wb-hairline': '#e3ddcb',
  '--wb-ink-0': '#11161a',
  '--wb-ink-1': '#21272d',
  '--wb-ink-2': '#5b6672',
  '--wb-ink-3': '#89929d',
  '--wb-accent': '#1f4fd6',
  '--wb-accent-ink': '#14337d',
  '--wb-accent-wash': '#e5ecfb',
  '--wb-copper': '#a35a2a',
  '--wb-copper-wash': '#f3e6d7',
  '--wb-ok': '#1d6b3f',
  '--wb-ok-wash': '#e3efe5',
  '--wb-warn': '#8a5a00',
  '--wb-warn-wash': '#f4e8cf',
  '--wb-err': '#a2251c',
  '--wb-err-wash': '#f5ddd8',
  '--wb-shadow': '0 1px 0 rgba(20, 20, 30, 0.04), 0 8px 24px rgba(24, 22, 10, 0.04)',
} as CSSProperties;

function getStepMeta(kind: InvokeWorkbenchFrameKind) {
  switch (kind) {
    case 'run.start':
      return { label: 'run start', color: 'var(--wb-accent)', chip: 'var(--wb-accent-wash)' };
    case 'run.finish':
      return { label: 'run done', color: 'var(--wb-ok)', chip: 'var(--wb-ok-wash)' };
    case 'step.start':
      return { label: 'step start', color: 'var(--wb-accent)', chip: 'var(--wb-accent-wash)' };
    case 'step.done':
      return { label: 'step done', color: 'var(--wb-ok)', chip: 'var(--wb-ok-wash)' };
    case 'step.error':
      return { label: 'error', color: 'var(--wb-err)', chip: 'var(--wb-err-wash)' };
    case 'tool.call':
      return { label: 'tool call', color: 'var(--wb-copper)', chip: 'var(--wb-copper-wash)' };
    case 'tool.result':
      return { label: 'tool result', color: 'var(--wb-copper)', chip: 'var(--wb-copper-wash)' };
    case 'thinking':
      return { label: 'thinking', color: 'var(--wb-ink-2)', chip: 'rgba(91, 102, 114, 0.08)' };
    case 'assistant.message':
      return { label: 'assistant', color: 'var(--wb-accent)', chip: 'var(--wb-accent-wash)' };
    case 'human.request':
      return { label: 'human input', color: 'var(--wb-warn)', chip: 'var(--wb-warn-wash)' };
    default:
      return { label: 'status', color: 'var(--wb-ink-2)', chip: 'rgba(91, 102, 114, 0.08)' };
  }
}

function formatElapsed(ms: number) {
  if (!Number.isFinite(ms) || ms <= 0) {
    return '0ms';
  }

  if (ms < 1000) {
    return `${Math.round(ms)}ms`;
  }

  return `${(ms / 1000).toFixed(ms >= 10_000 ? 1 : 2)}s`;
}

function formatRelativeTimeFromNow(timestamp: number) {
  const diff = Date.now() - timestamp;
  if (diff < 60_000) {
    return 'just now';
  }

  const minutes = Math.floor(diff / 60_000);
  if (minutes < 60) {
    return `${minutes}m ago`;
  }

  const hours = Math.floor(minutes / 60);
  if (hours < 24) {
    return `${hours}h ago`;
  }

  const days = Math.floor(hours / 24);
  return `${days}d ago`;
}

function truncateText(text: string, limit: number) {
  const trimmed = text.trim();
  if (trimmed.length <= limit) {
    return trimmed;
  }

  return `${trimmed.slice(0, Math.max(0, limit - 1))}…`;
}

function getHistoryNote(summary: InvokeRunSummary) {
  if (summary.status === 'error') {
    return summary.errorMessage || 'Invocation failed';
  }

  if (summary.status === 'needs-input') {
    return summary.humanInputPrompt || 'Waiting for input';
  }

  if (summary.textOutput) {
    return truncateText(summary.textOutput.replace(/\s+/g, ' '), 78);
  }

  if (summary.stepCount > 0 || summary.toolCallCount > 0) {
    return `${summary.stepCount} step${summary.stepCount === 1 ? '' : 's'} · ${summary.toolCallCount} tool${summary.toolCallCount === 1 ? '' : 's'}`;
  }

  return 'No response captured yet';
}

function getStatusTone(status: InvokeRunSummary['status'] | 'running') {
  switch (status) {
    case 'running':
      return {
        label: 'streaming',
        foreground: 'var(--wb-accent-ink)',
        background: 'var(--wb-accent-wash)',
      };
    case 'completed':
      return {
        label: 'ready',
        foreground: 'var(--wb-ok)',
        background: 'var(--wb-ok-wash)',
      };
    case 'needs-input':
      return {
        label: 'waiting',
        foreground: 'var(--wb-warn)',
        background: 'var(--wb-warn-wash)',
      };
    case 'submitted':
      return {
        label: 'resume sent',
        foreground: 'var(--wb-accent-ink)',
        background: 'var(--wb-accent-wash)',
      };
    case 'error':
      return {
        label: 'error',
        foreground: 'var(--wb-err)',
        background: 'var(--wb-err-wash)',
      };
    case 'stopped':
      return {
        label: 'stopped',
        foreground: 'var(--wb-warn)',
        background: 'var(--wb-warn-wash)',
      };
    default:
      return {
        label: 'idle',
        foreground: 'var(--wb-ink-2)',
        background: 'rgba(91, 102, 114, 0.08)',
      };
  }
}

type CompareTone = 'same' | 'changed' | 'regression' | 'hand-off';

type CompareRow = {
  label: string;
  current: string;
  baseline: string;
  tone: CompareTone;
};

function getRunHandle(entry: InvokeHistoryEntry | null) {
  if (!entry) {
    return '—';
  }

  return entry.summary.runId || entry.id.slice(0, 8);
}

function getStatusSummaryLabel(status: InvokeRunSummary['status']) {
  switch (status) {
    case 'completed':
      return 'completed';
    case 'needs-input':
      return 'waiting for input';
    case 'submitted':
      return 'resume submitted';
    case 'error':
      return 'error';
    case 'stopped':
      return 'stopped';
    case 'running':
      return 'running';
    default:
      return 'idle';
  }
}

function getCompareToneStyle(tone: CompareTone): CSSProperties {
  switch (tone) {
    case 'same':
      return headerPillStyle('var(--wb-paper-2)', 'var(--wb-ink-2)');
    case 'regression':
      return headerPillStyle('var(--wb-err-wash)', 'var(--wb-err)');
    case 'hand-off':
      return headerPillStyle('var(--wb-accent-wash)', 'var(--wb-accent-ink)');
    default:
      return headerPillStyle('var(--wb-copper-wash)', 'var(--wb-copper)');
  }
}

function getEntryElapsedMs(frames: InvokeWorkbenchFrame[]) {
  return frames.length > 0 ? frames[frames.length - 1].t : 0;
}

function getEntryOutcome(entry: InvokeHistoryEntry) {
  return getHistoryNote(entry.summary);
}

function buildCompareRows(
  current: InvokeHistoryEntry,
  baseline: InvokeHistoryEntry,
  currentFrames: InvokeWorkbenchFrame[],
  baselineFrames: InvokeWorkbenchFrame[],
): CompareRow[] {
  const currentElapsedMs = getEntryElapsedMs(currentFrames);
  const baselineElapsedMs = getEntryElapsedMs(baselineFrames);
  const promptSame = current.request.prompt.trim() === baseline.request.prompt.trim();
  const outcomeSame = getEntryOutcome(current) === getEntryOutcome(baseline);
  const statusTone: CompareTone =
    current.summary.status === baseline.summary.status
      ? 'same'
      : current.summary.status === 'needs-input'
        ? 'hand-off'
        : current.summary.status === 'error'
          ? 'regression'
          : 'changed';
  const elapsedTone: CompareTone =
    currentElapsedMs === baselineElapsedMs
      ? 'same'
      : baselineElapsedMs > 0 && currentElapsedMs > baselineElapsedMs * 1.2
        ? 'regression'
        : 'changed';

  return [
    {
      label: 'status',
      current: getStatusSummaryLabel(current.summary.status),
      baseline: getStatusSummaryLabel(baseline.summary.status),
      tone: statusTone,
    },
    {
      label: 'endpoint',
      current: current.request.endpointLabel || current.request.endpointId,
      baseline: baseline.request.endpointLabel || baseline.request.endpointId,
      tone: current.request.endpointId === baseline.request.endpointId ? 'same' : 'changed',
    },
    {
      label: 'prompt',
      current: truncateText(current.request.prompt || '(empty prompt)', 72),
      baseline: truncateText(baseline.request.prompt || '(empty prompt)', 72),
      tone: promptSame ? 'same' : 'changed',
    },
    {
      label: 'steps',
      current: String(current.summary.stepCount),
      baseline: String(baseline.summary.stepCount),
      tone: current.summary.stepCount === baseline.summary.stepCount ? 'same' : 'changed',
    },
    {
      label: 'tool calls',
      current: String(current.summary.toolCallCount),
      baseline: String(baseline.summary.toolCallCount),
      tone: current.summary.toolCallCount === baseline.summary.toolCallCount ? 'same' : 'changed',
    },
    {
      label: 'elapsed',
      current: currentElapsedMs > 0 ? formatElapsed(currentElapsedMs) : '—',
      baseline: baselineElapsedMs > 0 ? formatElapsed(baselineElapsedMs) : '—',
      tone: elapsedTone,
    },
    {
      label: 'result',
      current: truncateText(getEntryOutcome(current), 72),
      baseline: truncateText(getEntryOutcome(baseline), 72),
      tone: outcomeSame ? 'same' : statusTone === 'hand-off' ? 'hand-off' : current.summary.status === 'error' ? 'regression' : 'changed',
    },
  ];
}

function pickDefaultBaseline(currentEntry: InvokeHistoryEntry | null, history: InvokeHistoryEntry[]) {
  if (!currentEntry) {
    return '';
  }

  const candidates = history.filter(entry => entry.id !== currentEntry.id);
  if (candidates.length === 0) {
    return '';
  }

  if (currentEntry.summary.status !== 'completed') {
    const latestSuccess = candidates.find(entry => entry.summary.status === 'completed');
    if (latestSuccess) {
      return latestSuccess.id;
    }
  }

  const samePrompt = candidates.find(entry => entry.request.prompt.trim() === currentEntry.request.prompt.trim());
  return samePrompt?.id || candidates[0].id;
}

function getServiceTypeLabel(service: ServiceOption) {
  switch (service.kind) {
    case 'nyxid-chat':
      return 'CHAT';
    case 'streaming-proxy':
      return 'PROXY';
    case 'onboarding':
      return 'SETUP';
    default:
      return 'SERVICE';
  }
}

function getServiceSubtitle(service: ServiceOption, endpoint: ServiceEndpoint | null, invokeSupport: InvokeSurfaceSupport) {
  if (!invokeSupport.supported) {
    return invokeSupport.reason;
  }

  if (endpoint?.description) {
    return endpoint.description;
  }

  switch (service.kind) {
    case 'nyxid-chat':
      return 'Talk to the scoped NyxID chat service and inspect the resulting AGUI frame stream.';
    case 'streaming-proxy':
      return 'Run a room-backed streaming conversation and inspect participant, tool, and response events in one place.';
    case 'onboarding':
      return 'Finish provider setup before this surface can invoke a real scope service.';
    default:
      return 'Run one real request against the selected service, then inspect the response, steps, and tool activity together.';
  }
}

function cardStyle(): CSSProperties {
  return {
    background: 'var(--wb-paper-0)',
    border: '1px solid var(--wb-hairline)',
    borderRadius: 22,
    boxShadow: 'var(--wb-shadow)',
  };
}

function headerPillStyle(background: string, color: string): CSSProperties {
  return {
    display: 'inline-flex',
    alignItems: 'center',
    gap: 6,
    padding: '4px 10px',
    borderRadius: 999,
    background,
    color,
    fontSize: 12,
    fontWeight: 600,
  };
}

function WorkbenchStepper({
  invokeDone,
  observeDone,
}: {
  invokeDone: boolean;
  observeDone: boolean;
}) {
  const steps = [
    { label: 'Build', done: true },
    { label: 'Bind', done: true },
    { label: 'Invoke', done: invokeDone, active: true },
    { label: 'Observe', done: observeDone },
  ];

  return (
    <div className="flex flex-wrap items-center gap-2">
      {steps.map((step, index) => (
        <div key={step.label} className="flex items-center gap-2">
          {index > 0 ? (
            <div style={{ width: 24, height: 1, background: 'var(--wb-hairline)' }} />
          ) : null}
          <div
            style={{
              display: 'inline-flex',
              alignItems: 'center',
              gap: 10,
              padding: step.active ? '5px 16px 5px 6px' : '5px 16px 5px 6px',
              borderRadius: 999,
              border: `1px solid ${step.active ? 'var(--wb-ink-0)' : 'var(--wb-hairline)'}`,
              background: step.active ? 'var(--wb-ink-0)' : 'transparent',
              color: step.active ? 'var(--wb-paper-0)' : 'var(--wb-ink-1)',
              fontSize: 13,
              fontWeight: step.active ? 700 : 600,
            }}
          >
            <span
              style={{
                width: 24,
                height: 24,
                borderRadius: 999,
                display: 'inline-flex',
                alignItems: 'center',
                justifyContent: 'center',
                background: step.active ? 'var(--wb-paper-0)' : step.done ? 'var(--wb-accent-wash)' : 'var(--wb-paper-2)',
                color: step.active ? 'var(--wb-ink-0)' : step.done ? 'var(--wb-accent-ink)' : 'var(--wb-ink-2)',
                border: `1px solid ${step.active ? 'var(--wb-paper-0)' : 'var(--wb-hairline)'}`,
              }}
            >
              {step.done ? <Check size={14} /> : index + 1}
            </span>
            <span>{step.label}</span>
          </div>
        </div>
      ))}
    </div>
  );
}

function MetricsBar({
  summary,
  frames,
  loading,
}: {
  summary: InvokeRunSummary;
  frames: InvokeWorkbenchFrame[];
  loading: boolean;
}) {
  const effectiveStatus = loading && summary.status === 'idle' ? 'running' : summary.status;
  const statusTone = getStatusTone(effectiveStatus);
  const errorCount = frames.filter(frame => frame.kind === 'step.error').length;
  const elapsed = frames.length > 0 ? formatElapsed(frames[frames.length - 1].t) : '—';
  const metrics = [
    { label: 'events', value: frames.length },
    { label: 'steps', value: summary.stepCount },
    { label: 'tool calls', value: summary.toolCallCount },
    { label: 'errors', value: errorCount },
    { label: 'elapsed', value: elapsed },
  ];

  return (
    <div
      style={{
        display: 'grid',
        gridTemplateColumns: 'repeat(6, minmax(0, 1fr))',
        border: '1px solid var(--wb-hairline)',
        borderRadius: 12,
        overflow: 'hidden',
        background: 'var(--wb-paper-0)',
      }}
    >
      {metrics.map(metric => (
        <div key={metric.label} style={{ padding: '10px 14px', borderRight: '1px solid var(--wb-hairline)' }}>
          <div style={{ fontSize: 11, letterSpacing: '0.12em', textTransform: 'uppercase', color: 'var(--wb-ink-3)', fontWeight: 700 }}>
            {metric.label}
          </div>
          <div style={{ marginTop: 6, fontSize: 30, lineHeight: 1.1, fontWeight: 700, color: metric.label === 'errors' && errorCount > 0 ? 'var(--wb-err)' : 'var(--wb-ink-0)' }}>
            {metric.value}
          </div>
        </div>
      ))}
      <div style={{ padding: '10px 14px' }}>
        <div style={{ fontSize: 11, letterSpacing: '0.12em', textTransform: 'uppercase', color: 'var(--wb-ink-3)', fontWeight: 700 }}>
          state
        </div>
        <div className="mt-2 flex flex-wrap items-center gap-2">
          <span style={headerPillStyle(statusTone.background, statusTone.foreground)}>{statusTone.label}</span>
          <span style={{ fontSize: 12, fontWeight: 600, color: loading ? 'var(--wb-ok)' : 'var(--wb-ink-2)' }}>
            SSE · {loading ? 'LIVE' : 'IDLE'}
          </span>
        </div>
      </div>
    </div>
  );
}

function TimelineView({
  frames,
  focusedId,
  onFocus,
}: {
  frames: InvokeWorkbenchFrame[];
  focusedId: string | null;
  onFocus: (id: string) => void;
}) {
  return (
    <div style={{ position: 'relative', minHeight: 240 }}>
      <div style={{ position: 'absolute', left: 92, top: 10, bottom: 10, width: 1, background: 'var(--wb-hairline)' }} />
      {frames.length === 0 ? (
        <div className="flex h-full min-h-[240px] items-center justify-center text-[13px]" style={{ color: 'var(--wb-ink-3)' }}>
          Start an invoke to stream AGUI frames into this panel.
        </div>
      ) : (
        frames.map(frame => {
          const meta = getStepMeta(frame.kind);
          const active = frame.id === focusedId;
          return (
            <button
              key={frame.id}
              type="button"
              onClick={() => onFocus(frame.id)}
              className="grid w-full gap-4 px-3 py-2 text-left"
              style={{
                gridTemplateColumns: '84px minmax(0, 1fr)',
                background: active ? 'var(--wb-accent-wash)' : 'transparent',
                borderRadius: 10,
              }}
            >
              <div style={{ paddingTop: 4, textAlign: 'right', fontSize: 12, color: 'var(--wb-ink-3)', fontFamily: 'JetBrains Mono, SFMono-Regular, Menlo, monospace' }}>
                {formatElapsed(frame.t)}
              </div>
              <div style={{ position: 'relative', paddingLeft: 22 }}>
                <span
                  style={{
                    position: 'absolute',
                    left: -5,
                    top: 11,
                    width: 12,
                    height: 12,
                    borderRadius: 999,
                    background: meta.color,
                    border: '2px solid var(--wb-paper-0)',
                  }}
                />
                <div className="flex flex-wrap items-center gap-2">
                  <span style={headerPillStyle(meta.chip, meta.color)}>{meta.label}</span>
                  <span style={{ fontSize: 18, lineHeight: 1.25, fontWeight: 700, color: 'var(--wb-ink-0)' }}>{frame.label}</span>
                  {frame.step ? (
                    <span style={{ fontSize: 13, color: 'var(--wb-ink-3)' }}>· {frame.step}</span>
                  ) : null}
                </div>
                {frame.detail ? (
                  <div style={{ marginTop: 4, fontSize: 15, lineHeight: 1.6, color: 'var(--wb-ink-2)' }}>{frame.detail}</div>
                ) : null}
                {active && frame.text ? (
                  <pre
                    style={{
                      marginTop: 10,
                      padding: '12px 14px',
                      borderRadius: 10,
                      border: '1px solid var(--wb-hairline)',
                      background: 'var(--wb-paper-2)',
                      color: 'var(--wb-ink-1)',
                      fontSize: 13,
                      lineHeight: 1.6,
                      whiteSpace: 'pre-wrap',
                      fontFamily: 'JetBrains Mono, SFMono-Regular, Menlo, monospace',
                    }}
                  >
                    {frame.text}
                  </pre>
                ) : null}
                {active && frame.args ? (
                  <pre
                    style={{
                      marginTop: 10,
                      padding: '12px 14px',
                      borderRadius: 10,
                      border: '1px solid var(--wb-hairline)',
                      background: 'var(--wb-paper-2)',
                      color: 'var(--wb-ink-1)',
                      fontSize: 13,
                      lineHeight: 1.6,
                      whiteSpace: 'pre-wrap',
                      fontFamily: 'JetBrains Mono, SFMono-Regular, Menlo, monospace',
                    }}
                  >
                    {frame.args}
                  </pre>
                ) : null}
                {active && frame.result ? (
                  <pre
                    style={{
                      marginTop: 10,
                      padding: '12px 14px',
                      borderRadius: 10,
                      border: '1px solid var(--wb-hairline)',
                      background: 'var(--wb-paper-2)',
                      color: 'var(--wb-ink-1)',
                      fontSize: 13,
                      lineHeight: 1.6,
                      whiteSpace: 'pre-wrap',
                      fontFamily: 'JetBrains Mono, SFMono-Regular, Menlo, monospace',
                    }}
                  >
                    {frame.result}
                  </pre>
                ) : null}
                {active && frame.error ? (
                  <div
                    style={{
                      marginTop: 10,
                      padding: '12px 14px',
                      borderRadius: 10,
                      border: '1px solid rgba(162, 37, 28, 0.18)',
                      background: 'var(--wb-err-wash)',
                      color: 'var(--wb-err)',
                      fontSize: 13,
                      lineHeight: 1.6,
                      whiteSpace: 'pre-wrap',
                    }}
                  >
                    {frame.error}
                  </div>
                ) : null}
              </div>
            </button>
          );
        })
      )}
    </div>
  );
}

function TraceView({ frames }: { frames: InvokeWorkbenchFrame[] }) {
  const rows = useMemo(() => {
    const stepMap = new Map<string, { start: number; end: number; errors: number; tools: number }>();
    for (const frame of frames) {
      if (!frame.step) {
        continue;
      }

      if (!stepMap.has(frame.step)) {
        stepMap.set(frame.step, { start: frame.t, end: frame.t, errors: 0, tools: 0 });
      }

      const row = stepMap.get(frame.step)!;
      row.start = Math.min(row.start, frame.t);
      row.end = Math.max(row.end, frame.t);
      if (frame.kind === 'step.error') {
        row.errors += 1;
      }
      if (frame.kind === 'tool.call' || frame.kind === 'tool.result') {
        row.tools += 1;
      }
    }

    return Array.from(stepMap.entries());
  }, [frames]);

  const max = Math.max(1, ...rows.map(([, row]) => row.end || 1));

  if (rows.length === 0) {
    return (
      <div className="flex min-h-[240px] items-center justify-center text-[13px]" style={{ color: 'var(--wb-ink-3)' }}>
        Trace becomes available once the run emits named steps.
      </div>
    );
  }

  return (
    <div className="space-y-3 px-2 py-3">
      {rows.map(([name, row]) => {
        const left = (row.start / max) * 100;
        const width = Math.max(6, ((Math.max(row.end - row.start, 80)) / max) * 100);
        const barBackground = row.errors > 0 ? 'var(--wb-err-wash)' : 'var(--wb-accent-wash)';
        const barForeground = row.errors > 0 ? 'var(--wb-err)' : 'var(--wb-accent)';
        return (
          <div key={name} className="grid items-center gap-3" style={{ gridTemplateColumns: '160px minmax(0, 1fr) 90px' }}>
            <div style={{ fontSize: 14, fontWeight: 600, color: 'var(--wb-ink-1)' }}>{name}</div>
            <div
              style={{
                position: 'relative',
                height: 20,
                borderRadius: 999,
                background: 'var(--wb-paper-2)',
                border: '1px solid var(--wb-hairline)',
              }}
            >
              <div
                style={{
                  position: 'absolute',
                  left: `${left}%`,
                  width: `${Math.min(width, 100 - left)}%`,
                  top: 2,
                  bottom: 2,
                  borderRadius: 999,
                  background: barBackground,
                  border: `1px solid ${barForeground}`,
                }}
              />
            </div>
            <div style={{ textAlign: 'right', fontSize: 12, color: 'var(--wb-ink-3)', fontFamily: 'JetBrains Mono, SFMono-Regular, Menlo, monospace' }}>
              {formatElapsed(Math.max(row.end - row.start, 80))}
            </div>
          </div>
        );
      })}
    </div>
  );
}

function TabsView({ frames }: { frames: InvokeWorkbenchFrame[] }) {
  const [tab, setTab] = useState<'steps' | 'tools' | 'messages'>('steps');
  const items = useMemo(() => ({
    steps: frames.filter(frame => frame.kind === 'step.start' || frame.kind === 'step.done' || frame.kind === 'step.error' || frame.kind === 'run.start' || frame.kind === 'run.finish'),
    tools: frames.filter(frame => frame.kind === 'tool.call' || frame.kind === 'tool.result'),
    messages: frames.filter(frame => frame.kind === 'thinking' || frame.kind === 'assistant.message' || frame.kind === 'human.request'),
  }), [frames]);

  const tabs = [
    { id: 'steps' as const, label: 'Steps', count: items.steps.length },
    { id: 'tools' as const, label: 'Tool calls', count: items.tools.length },
    { id: 'messages' as const, label: 'Messages', count: items.messages.length },
  ];

  const list = items[tab];

  return (
    <div className="flex h-full min-h-[240px] flex-col">
      <div className="flex flex-wrap gap-2 px-2 pb-3">
        {tabs.map(entry => (
          <button
            key={entry.id}
            type="button"
            onClick={() => setTab(entry.id)}
            style={{
              padding: '7px 12px',
              borderRadius: 999,
              border: `1px solid ${tab === entry.id ? 'var(--wb-ink-0)' : 'var(--wb-hairline)'}`,
              background: tab === entry.id ? 'var(--wb-ink-0)' : 'transparent',
              color: tab === entry.id ? 'var(--wb-paper-0)' : 'var(--wb-ink-1)',
              fontSize: 13,
              fontWeight: 600,
            }}
          >
            {entry.label} <span style={{ opacity: 0.75 }}>{entry.count}</span>
          </button>
        ))}
      </div>
      <div className="space-y-3 overflow-auto px-2">
        {list.length === 0 ? (
          <div className="flex min-h-[180px] items-center justify-center text-[13px]" style={{ color: 'var(--wb-ink-3)' }}>
            No {tab} yet.
          </div>
        ) : list.map(frame => {
          const meta = getStepMeta(frame.kind);
          return (
            <div
              key={frame.id}
              style={{
                border: '1px solid var(--wb-hairline)',
                borderRadius: 14,
                background: 'var(--wb-paper-0)',
                padding: '12px 14px',
              }}
            >
              <div className="flex flex-wrap items-center gap-2">
                <span style={headerPillStyle(meta.chip, meta.color)}>{meta.label}</span>
                <span style={{ fontSize: 15, fontWeight: 700, color: 'var(--wb-ink-0)' }}>{frame.label}</span>
                <div className="flex-1" />
                <span style={{ fontSize: 12, color: 'var(--wb-ink-3)', fontFamily: 'JetBrains Mono, SFMono-Regular, Menlo, monospace' }}>{formatElapsed(frame.t)}</span>
              </div>
              {frame.detail ? (
                <div style={{ marginTop: 6, fontSize: 14, lineHeight: 1.6, color: 'var(--wb-ink-2)' }}>{frame.detail}</div>
              ) : null}
              {frame.text ? (
                <div style={{ marginTop: 8, fontSize: 14, lineHeight: 1.7, color: 'var(--wb-ink-1)', whiteSpace: 'pre-wrap' }}>{frame.text}</div>
              ) : null}
            </div>
          );
        })}
      </div>
    </div>
  );
}

function BubblesView({ frames }: { frames: InvokeWorkbenchFrame[] }) {
  const bubbles = frames.filter(frame => frame.kind === 'thinking' || frame.kind === 'assistant.message' || frame.kind === 'tool.call' || frame.kind === 'tool.result' || frame.kind === 'human.request');
  if (bubbles.length === 0) {
    return (
      <div className="flex min-h-[240px] items-center justify-center text-[13px]" style={{ color: 'var(--wb-ink-3)' }}>
        Bubble view appears once message-like frames are available.
      </div>
    );
  }

  return (
    <div className="space-y-3 px-3 py-2">
      {bubbles.map(frame => {
        const meta = getStepMeta(frame.kind);
        const alignRight = frame.kind === 'assistant.message';
        return (
          <div key={frame.id} className={`flex ${alignRight ? 'justify-end' : 'justify-start'}`}>
            <div
              style={{
                maxWidth: 560,
                borderRadius: 16,
                border: '1px solid var(--wb-hairline)',
                background: alignRight ? 'var(--wb-accent-wash)' : frame.kind === 'tool.call' || frame.kind === 'tool.result' ? 'var(--wb-copper-wash)' : 'var(--wb-paper-2)',
                padding: '12px 14px',
              }}
            >
              <div className="flex flex-wrap items-center gap-2">
                <span style={headerPillStyle(meta.chip, meta.color)}>{meta.label}</span>
                <span style={{ fontSize: 12, color: 'var(--wb-ink-3)' }}>{formatElapsed(frame.t)}</span>
              </div>
              <div style={{ marginTop: 8, fontSize: 15, fontWeight: 700, color: 'var(--wb-ink-0)' }}>{frame.label}</div>
              {frame.text ? (
                <div style={{ marginTop: 6, fontSize: 14, lineHeight: 1.7, color: 'var(--wb-ink-1)', whiteSpace: 'pre-wrap' }}>{frame.text}</div>
              ) : frame.detail ? (
                <div style={{ marginTop: 6, fontSize: 14, lineHeight: 1.7, color: 'var(--wb-ink-2)', whiteSpace: 'pre-wrap' }}>{frame.detail}</div>
              ) : null}
            </div>
          </div>
        );
      })}
    </div>
  );
}

function RawView({ events }: { events: InvokeEventRecord[] }) {
  return (
    <pre
      style={{
        margin: 0,
        minHeight: 240,
        padding: '14px 16px',
        overflow: 'auto',
        fontSize: 12,
        lineHeight: 1.65,
        color: 'var(--wb-ink-1)',
        background: 'var(--wb-paper-2)',
        fontFamily: 'JetBrains Mono, SFMono-Regular, Menlo, monospace',
        whiteSpace: 'pre-wrap',
      }}
    >
      {events.length > 0
        ? events.map(event => `event: ${event.type}\ndata: ${JSON.stringify(event.data, null, 2)}\n`).join('\n')
        : 'No frames yet.'}
    </pre>
  );
}

function CompareRunsCard({
  currentEntry,
  baselineEntry,
  baselineOptions,
  baselineId,
  onBaselineChange,
  currentFrames,
  baselineFrames,
}: {
  currentEntry: InvokeHistoryEntry | null;
  baselineEntry: InvokeHistoryEntry | null;
  baselineOptions: InvokeHistoryEntry[];
  baselineId: string;
  onBaselineChange: (value: string) => void;
  currentFrames: InvokeWorkbenchFrame[];
  baselineFrames: InvokeWorkbenchFrame[];
}) {
  if (!currentEntry) {
    return null;
  }

  if (!baselineEntry) {
    return (
      <div style={{ ...cardStyle(), overflow: 'hidden' }}>
        <div className="flex flex-wrap items-center gap-3 border-b px-4 py-4" style={{ borderColor: 'var(--wb-hairline)' }}>
          <div style={{ fontSize: 14, fontWeight: 800, color: 'var(--wb-ink-0)' }}>Run compare</div>
          <span style={headerPillStyle('var(--wb-paper-2)', 'var(--wb-ink-2)')}>{getRunHandle(currentEntry)}</span>
        </div>
        <div className="p-4 text-[13px] leading-6" style={{ color: 'var(--wb-ink-3)' }}>
          One more run is needed before this service can show a real before/after comparison.
        </div>
      </div>
    );
  }

  const rows = buildCompareRows(currentEntry, baselineEntry, currentFrames, baselineFrames);

  return (
    <div style={{ ...cardStyle(), overflow: 'hidden' }}>
      <div className="flex flex-wrap items-center gap-3 border-b px-4 py-4" style={{ borderColor: 'var(--wb-hairline)' }}>
        <div style={{ fontSize: 14, fontWeight: 800, color: 'var(--wb-ink-0)' }}>Run compare</div>
        <span style={headerPillStyle('var(--wb-accent-wash)', 'var(--wb-accent-ink)')}>
          {getRunHandle(currentEntry)} vs {getRunHandle(baselineEntry)}
        </span>
        <div className="flex-1" />
        <label className="flex items-center gap-2">
          <span style={{ fontSize: 11, letterSpacing: '0.12em', textTransform: 'uppercase', color: 'var(--wb-ink-3)', fontWeight: 700 }}>baseline</span>
          <select
            value={baselineId}
            onChange={event => onBaselineChange(event.target.value)}
            className="rounded-[12px] border px-3 py-2 text-[12px] font-mono"
            style={{ borderColor: 'var(--wb-hairline)', background: 'var(--wb-paper-2)', color: 'var(--wb-ink-1)' }}
          >
            {baselineOptions.map(entry => (
              <option key={entry.id} value={entry.id}>
                {getRunHandle(entry)}
              </option>
            ))}
          </select>
        </label>
      </div>
      <div className="overflow-auto">
        <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
          <thead>
            <tr style={{ background: 'var(--wb-paper-2)' }}>
              {['Signal', `Current · ${getRunHandle(currentEntry)}`, `Baseline · ${getRunHandle(baselineEntry)}`, 'Δ'].map(header => (
                <th
                  key={header}
                  style={{
                    textAlign: 'left',
                    padding: '10px 14px',
                    borderBottom: '1px solid var(--wb-hairline)',
                    fontSize: 11,
                    letterSpacing: '0.12em',
                    textTransform: 'uppercase',
                    color: 'var(--wb-ink-3)',
                    fontWeight: 700,
                  }}
                >
                  {header}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.map(row => (
              <tr key={row.label} style={{ borderBottom: '1px solid var(--wb-hairline)' }}>
                <td style={{ padding: '10px 14px', color: 'var(--wb-ink-1)', fontWeight: 600 }}>{row.label}</td>
                <td style={{ padding: '10px 14px', color: 'var(--wb-ink-0)', fontFamily: 'JetBrains Mono, SFMono-Regular, Menlo, monospace' }}>{row.current}</td>
                <td style={{ padding: '10px 14px', color: 'var(--wb-ink-2)', fontFamily: 'JetBrains Mono, SFMono-Regular, Menlo, monospace' }}>{row.baseline}</td>
                <td style={{ padding: '10px 14px' }}>
                  <span style={getCompareToneStyle(row.tone)}>
                    {row.tone === 'same' ? 'same' : row.tone === 'regression' ? 'regression' : row.tone === 'hand-off' ? 'hand-off' : 'changed'}
                  </span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function HumanInputCard({
  pendingHumanInput,
  resumeLoading,
  resumeError,
  onResumeHumanInput,
  onGoToChat,
}: {
  pendingHumanInput: PendingHumanInputInfo | null;
  resumeLoading: boolean;
  resumeError: string | null;
  onResumeHumanInput: (userInput: string) => void;
  onGoToChat: () => void;
}) {
  const [draft, setDraft] = useState('');
  const parsed = useMemo(
    () => parseHumanInputChoices(pendingHumanInput?.prompt || '', pendingHumanInput?.options),
    [pendingHumanInput?.options, pendingHumanInput?.prompt],
  );

  useEffect(() => {
    setDraft('');
  }, [pendingHumanInput?.prompt, pendingHumanInput?.runId, pendingHumanInput?.stepId]);

  if (!pendingHumanInput) {
    return null;
  }

  const prompt = parsed.questionText || pendingHumanInput.prompt;

  const submit = (value: string) => {
    const normalized = value.trim();
    if (!normalized || resumeLoading) {
      return;
    }

    onResumeHumanInput(normalized);
  };

  return (
    <div style={{ ...cardStyle(), padding: 18, borderColor: 'rgba(138, 90, 0, 0.24)', background: 'var(--wb-paper-0)' }}>
      <div className="flex flex-wrap items-center gap-2">
        <span style={headerPillStyle('var(--wb-warn-wash)', 'var(--wb-warn)')}>human input</span>
        <span style={{ fontSize: 12, color: 'var(--wb-ink-3)' }}>run pauses here and resumes on answer</span>
      </div>
      <div className="mt-3 flex flex-wrap gap-2">
        <span style={headerPillStyle('var(--wb-paper-2)', 'var(--wb-ink-2)')}>run · {pendingHumanInput.runId}</span>
        <span style={headerPillStyle('var(--wb-paper-2)', 'var(--wb-ink-2)')}>step · {pendingHumanInput.stepId}</span>
      </div>
      {prompt ? (
        <div style={{ marginTop: 10, fontSize: 14, lineHeight: 1.7, color: 'var(--wb-ink-1)', whiteSpace: 'pre-wrap' }}>{prompt}</div>
      ) : null}
      {parsed.choices.length > 0 ? (
        <div className="mt-4 flex flex-wrap gap-2">
          {parsed.choices.map(choice => (
            <button
              key={choice.key}
              type="button"
              onClick={() => submit(choice.value)}
              disabled={resumeLoading}
              style={{
                borderRadius: 12,
                border: '1px solid var(--wb-hairline)',
                background: 'var(--wb-paper-0)',
                color: 'var(--wb-ink-1)',
                padding: '9px 12px',
                fontSize: 13,
                fontWeight: 700,
                opacity: resumeLoading ? 0.45 : 1,
              }}
            >
              {choice.key}. {choice.label}
            </button>
          ))}
        </div>
      ) : null}
      <div className="mt-4">
        <div className="mb-2 text-[11px] font-semibold uppercase tracking-[0.14em]" style={{ color: 'var(--wb-ink-3)' }}>custom answer</div>
        <textarea
          rows={3}
          value={draft}
          onChange={event => setDraft(event.target.value)}
          placeholder="Type the answer you want to send back into the suspended run..."
          className="w-full rounded-[14px] border px-3 py-3 text-[13px] leading-6"
          style={{ borderColor: 'var(--wb-hairline)', background: 'var(--wb-paper-2)', color: 'var(--wb-ink-1)', resize: 'vertical' }}
        />
      </div>
      {resumeError ? (
        <div style={{ marginTop: 12, borderRadius: 12, border: '1px solid rgba(162, 37, 28, 0.18)', background: 'var(--wb-err-wash)', padding: '10px 12px', color: 'var(--wb-err)', fontSize: 13, lineHeight: 1.6 }}>
          {resumeError}
        </div>
      ) : null}
      <div className="mt-4 flex flex-wrap items-center gap-3">
        <button
          type="button"
          onClick={() => submit(draft)}
          disabled={resumeLoading || !draft.trim()}
          style={{
            display: 'inline-flex',
            alignItems: 'center',
            gap: 8,
            borderRadius: 12,
            border: '1px solid var(--wb-accent)',
            background: 'var(--wb-accent)',
            color: 'white',
            padding: '10px 14px',
            fontSize: 13,
            fontWeight: 700,
            opacity: resumeLoading || !draft.trim() ? 0.45 : 1,
          }}
        >
          {resumeLoading ? 'Sending…' : 'Answer and resume'}
        </button>
        <button
          type="button"
          onClick={onGoToChat}
          style={{
            borderRadius: 12,
            border: '1px solid var(--wb-hairline)',
            background: 'var(--wb-paper-0)',
            color: 'var(--wb-ink-1)',
            padding: '10px 14px',
            fontSize: 13,
            fontWeight: 700,
          }}
        >
          Continue in Chat
        </button>
      </div>
    </div>
  );
}

export function InvokeWorkbench(props: InvokeWorkbenchProps) {
  const [mode, setMode] = useState<InvokeWorkbenchMode>('timeline');
  const [focusedId, setFocusedId] = useState<string | null>(null);
  const [compareOpen, setCompareOpen] = useState(false);
  const [baselineHistoryId, setBaselineHistoryId] = useState('');
  const historyRef = useRef<HTMLDivElement | null>(null);
  const frames = useMemo(() => buildInvokeWorkbenchFrames(props.events), [props.events]);
  const historyFramesById = useMemo(
    () => new Map(props.history.map(entry => [entry.id, buildInvokeWorkbenchFrames(entry.events)])),
    [props.history],
  );
  const latestHistory = props.history[0] || null;
  const selectedHistory = useMemo(
    () => props.history.find(entry => entry.id === props.activeHistoryId) || props.history[0] || null,
    [props.activeHistoryId, props.history],
  );
  const baselineOptions = useMemo(
    () => props.history.filter(entry => entry.id !== selectedHistory?.id),
    [props.history, selectedHistory?.id],
  );
  const selectedHistoryFrames = selectedHistory ? (historyFramesById.get(selectedHistory.id) || []) : [];
  const baselineEntry = useMemo(
    () => baselineOptions.find(entry => entry.id === baselineHistoryId) || baselineOptions[0] || null,
    [baselineHistoryId, baselineOptions],
  );
  const baselineFrames = baselineEntry ? (historyFramesById.get(baselineEntry.id) || []) : [];
  const subtitle = getServiceSubtitle(props.service, props.activeEndpoint, props.invokeSupport);
  const lastRunLabel = latestHistory ? formatRelativeTimeFromNow(latestHistory.updatedAt) : 'never';
  const currentResponse = props.summary.textOutput.trim();
  const effectiveStatus = props.loading && props.summary.status === 'idle' ? 'running' : props.summary.status;

  useEffect(() => {
    const nextBaselineId = pickDefaultBaseline(selectedHistory, props.history);
    if (!nextBaselineId) {
      if (baselineHistoryId) {
        setBaselineHistoryId('');
      }
      return;
    }

    if (baselineOptions.some(entry => entry.id === baselineHistoryId)) {
      return;
    }

    setBaselineHistoryId(nextBaselineId);
  }, [baselineHistoryId, baselineOptions, props.history, selectedHistory]);

  return (
    <div className="flex-1 min-h-0 overflow-auto" style={workbenchTokens}>
      <div className="mx-auto max-w-[1500px] px-5 py-5" style={{ minHeight: '100%', background: 'var(--wb-paper-1)' }}>
        <div style={{ ...cardStyle(), padding: 24 }}>
          <div className="flex flex-wrap items-start justify-between gap-6">
            <div className="min-w-0 flex-1">
              <div className="flex flex-wrap items-center gap-2 text-[12px] font-semibold tracking-[0.14em]" style={{ color: 'var(--wb-ink-3)' }}>
                <span>{getServiceTypeLabel(props.service)}</span>
                <span>·</span>
                <span className="font-mono">{props.service.id}</span>
              </div>
              <div className="mt-3 flex flex-wrap items-center gap-3">
                <div style={{ fontSize: 26, fontWeight: 800, color: 'var(--wb-ink-0)', lineHeight: 1.1 }}>{props.service.label}</div>
                <span style={headerPillStyle('var(--wb-paper-2)', 'var(--wb-ink-2)')}>{props.activeEndpoint?.endpointId || props.endpointId}</span>
                <span style={headerPillStyle(props.invokeSupport.supported ? 'var(--wb-ok-wash)' : 'var(--wb-warn-wash)', props.invokeSupport.supported ? 'var(--wb-ok)' : 'var(--wb-warn)')}>
                  {props.invokeSupport.supported ? 'serving' : 'needs setup'}
                </span>
              </div>
              <div className="mt-4 max-w-[880px] text-[15px] leading-7" style={{ color: 'var(--wb-ink-2)' }}>
                {subtitle}
              </div>
            </div>
            <div className="flex min-w-[280px] flex-col items-end gap-3">
              <div className="flex flex-wrap justify-end gap-2">
                <button
                  type="button"
                  onClick={() => {
                    setCompareOpen(open => !open);
                  }}
                  disabled={props.history.length < 2}
                  style={{
                    borderRadius: 12,
                    border: '1px solid var(--wb-hairline)',
                    background: 'var(--wb-paper-0)',
                    color: 'var(--wb-ink-1)',
                    padding: '10px 14px',
                    fontSize: 13,
                    fontWeight: 700,
                    opacity: props.history.length < 2 ? 0.45 : 1,
                  }}
                >
                  Compare runs
                </button>
                <button
                  type="button"
                  onClick={() => { void props.copyText(`${props.transportLabel}\n\n${props.requestPreview}`); }}
                  style={{
                    borderRadius: 12,
                    border: '1px solid var(--wb-hairline)',
                    background: 'var(--wb-paper-0)',
                    color: 'var(--wb-ink-1)',
                    padding: '10px 14px',
                    fontSize: 13,
                    fontWeight: 700,
                  }}
                >
                  Share
                </button>
                <button
                  type="button"
                  onClick={props.onInvoke}
                  disabled={!props.invokeSupport.supported || props.loading || !props.prompt.trim() || !!props.headerError}
                  style={{
                    display: 'inline-flex',
                    alignItems: 'center',
                    gap: 8,
                    borderRadius: 12,
                    border: '1px solid var(--wb-accent)',
                    background: 'var(--wb-accent)',
                    color: 'white',
                    padding: '10px 16px',
                    fontSize: 14,
                    fontWeight: 800,
                    opacity: !props.invokeSupport.supported || props.loading || !props.prompt.trim() || !!props.headerError ? 0.45 : 1,
                  }}
                >
                  <Play size={16} />
                  Invoke
                </button>
              </div>
              <div className="flex flex-wrap gap-3 text-[12px]" style={{ color: 'var(--wb-ink-3)' }}>
                <span>scope · <b style={{ color: 'var(--wb-ink-1)', fontWeight: 700 }}>{props.scopeId}</b></span>
                <span>last run · <b style={{ color: 'var(--wb-ink-1)', fontWeight: 700 }}>{lastRunLabel}</b></span>
              </div>
            </div>
          </div>
          <div className="mt-5">
            <WorkbenchStepper invokeDone={props.history.length > 0} observeDone={frames.length > 0} />
          </div>
        </div>

        {compareOpen ? (
          <div className="mt-4">
            <CompareRunsCard
              currentEntry={selectedHistory}
              baselineEntry={baselineEntry}
              baselineOptions={baselineOptions}
              baselineId={baselineHistoryId}
              onBaselineChange={setBaselineHistoryId}
              currentFrames={selectedHistoryFrames}
              baselineFrames={baselineFrames}
            />
          </div>
        ) : null}

        <div className="mt-4 grid gap-4 xl:grid-cols-[420px_minmax(0,1fr)]">
          <div className="flex min-h-0 flex-col gap-4">
            <div style={{ ...cardStyle(), overflow: 'hidden' }}>
              <div className="flex flex-wrap items-center gap-3 border-b px-4 py-4" style={{ borderColor: 'var(--wb-hairline)' }}>
                <div style={{ fontSize: 14, fontWeight: 800, color: 'var(--wb-ink-0)' }}>Playground</div>
                <div className="flex-1" />
                <span style={headerPillStyle('var(--wb-paper-2)', 'var(--wb-ink-2)')}>POST {props.transportLabel}</span>
              </div>
              <div className="space-y-4 p-4">
                <div className="flex flex-wrap items-center gap-2">
                  <span style={headerPillStyle('var(--wb-paper-2)', 'var(--wb-ink-2)')}>auth · nyxid session</span>
                  <span style={headerPillStyle('var(--wb-paper-2)', 'var(--wb-ink-2)')}>stream · SSE</span>
                  <span style={headerPillStyle('var(--wb-paper-2)', 'var(--wb-ink-2)')}>accept · AGUI frames</span>
                </div>

                {!props.invokeSupport.supported ? (
                  <div style={{ borderRadius: 14, border: '1px solid rgba(138, 90, 0, 0.24)', background: 'var(--wb-warn-wash)', padding: 16 }}>
                    <div style={{ fontSize: 14, fontWeight: 700, color: 'var(--wb-warn)' }}>This service is not invokable from this surface</div>
                    <div className="mt-2 text-[13px] leading-6" style={{ color: 'var(--wb-ink-2)' }}>{props.invokeSupport.reason}</div>
                    <div className="mt-3 flex flex-wrap gap-2">
                      {props.invokeSupport.suggestedTab === 'chat' ? (
                        <button
                          type="button"
                          onClick={props.onGoToChat}
                          style={{
                            borderRadius: 12,
                            border: '1px solid var(--wb-ink-0)',
                            background: 'var(--wb-ink-0)',
                            color: 'var(--wb-paper-0)',
                            padding: '8px 12px',
                            fontSize: 13,
                            fontWeight: 700,
                          }}
                        >
                          Go to Chat
                        </button>
                      ) : null}
                      {props.invokeSupport.suggestedTab === 'raw' ? (
                        <button
                          type="button"
                          onClick={props.onGoToRaw}
                          style={{
                            borderRadius: 12,
                            border: '1px solid var(--wb-ink-0)',
                            background: 'var(--wb-ink-0)',
                            color: 'var(--wb-paper-0)',
                            padding: '8px 12px',
                            fontSize: 13,
                            fontWeight: 700,
                          }}
                        >
                          Open Raw
                        </button>
                      ) : null}
                    </div>
                  </div>
                ) : (
                  <>
                    <div className="grid gap-3">
                      <div className="grid gap-3 md:grid-cols-2">
                        <div>
                          <div className="mb-2 text-[11px] font-semibold uppercase tracking-[0.14em]" style={{ color: 'var(--wb-ink-3)' }}>endpoint</div>
                          {props.invokeableEndpoints.length > 1 ? (
                            <select
                              value={props.endpointId}
                              onChange={event => props.onEndpointChange(event.target.value)}
                              className="w-full rounded-[12px] border px-3 py-2.5 text-[13px] font-mono"
                              style={{ borderColor: 'var(--wb-hairline)', background: 'var(--wb-paper-2)', color: 'var(--wb-ink-1)' }}
                            >
                              {props.invokeableEndpoints.map(endpoint => (
                                <option key={endpoint.endpointId} value={endpoint.endpointId}>{endpoint.endpointId}</option>
                              ))}
                            </select>
                          ) : (
                            <div className="rounded-[12px] border px-3 py-2.5 text-[13px] font-mono" style={{ borderColor: 'var(--wb-hairline)', background: 'var(--wb-paper-2)', color: 'var(--wb-ink-1)' }}>
                              {props.activeEndpoint?.endpointId || props.endpointId}
                            </div>
                          )}
                        </div>
                        <div>
                          <div className="mb-2 text-[11px] font-semibold uppercase tracking-[0.14em]" style={{ color: 'var(--wb-ink-3)' }}>transport</div>
                          <div className="rounded-[12px] border px-3 py-2.5 text-[12px] font-mono break-all" style={{ borderColor: 'var(--wb-hairline)', background: 'var(--wb-paper-2)', color: 'var(--wb-ink-2)' }}>
                            {props.transportLabel}
                          </div>
                        </div>
                      </div>

                      <div>
                        <div className="mb-2 text-[11px] font-semibold uppercase tracking-[0.14em]" style={{ color: 'var(--wb-ink-3)' }}>prompt</div>
                        <textarea
                          rows={6}
                          value={props.prompt}
                          onChange={event => props.onPromptChange(event.target.value)}
                          placeholder="Describe the request you want this service to handle..."
                          className="w-full rounded-[16px] border px-3 py-3 text-[13px] leading-6"
                          style={{ borderColor: 'var(--wb-hairline)', background: 'var(--wb-paper-2)', color: 'var(--wb-ink-1)', resize: 'vertical' }}
                        />
                      </div>

                      <div style={{ borderRadius: 16, border: '1px solid var(--wb-hairline)', background: 'var(--wb-paper-2)', overflow: 'hidden' }}>
                        <button
                          type="button"
                          onClick={() => props.onAdvancedOpenChange(!props.advancedOpen)}
                          className="flex w-full items-center justify-between px-4 py-3 text-left"
                        >
                          <div>
                            <div style={{ fontSize: 13, fontWeight: 700, color: 'var(--wb-ink-1)' }}>Advanced options</div>
                            <div style={{ marginTop: 2, fontSize: 12, color: 'var(--wb-ink-3)' }}>Actor reuse and header overrides live here, not in a fake generic body bag.</div>
                          </div>
                          <ChevronDown size={18} style={{ color: 'var(--wb-ink-3)', transform: props.advancedOpen ? 'rotate(180deg)' : 'rotate(0deg)', transition: 'transform 120ms ease' }} />
                        </button>
                        {props.advancedOpen ? (
                          <div className="grid gap-3 border-t p-4" style={{ borderColor: 'var(--wb-hairline)', background: 'var(--wb-paper-0)' }}>
                            <div>
                              <div className="mb-2 text-[11px] font-semibold uppercase tracking-[0.14em]" style={{ color: 'var(--wb-ink-3)' }}>actor id</div>
                              <input
                                value={props.actorId}
                                onChange={event => props.onActorIdChange(event.target.value)}
                                placeholder="Optional: send this invoke to an existing actor"
                                className="w-full rounded-[12px] border px-3 py-2.5 text-[13px] font-mono"
                                style={{ borderColor: 'var(--wb-hairline)', background: 'var(--wb-paper-2)', color: 'var(--wb-ink-1)' }}
                              />
                            </div>
                            <div>
                              <div className="mb-2 text-[11px] font-semibold uppercase tracking-[0.14em]" style={{ color: 'var(--wb-ink-3)' }}>headers</div>
                              <textarea
                                rows={4}
                                value={props.headersText}
                                onChange={event => props.onHeadersTextChange(event.target.value)}
                                placeholder={'nyxid.route_preference: /api/v1/proxy/s/demo\naevatar.model_override: gpt-5'}
                                className="w-full rounded-[12px] border px-3 py-2.5 text-[12px] font-mono leading-6"
                                style={{ borderColor: 'var(--wb-hairline)', background: 'var(--wb-paper-2)', color: 'var(--wb-ink-1)', resize: 'vertical' }}
                                spellCheck={false}
                              />
                            </div>
                          </div>
                        ) : null}
                      </div>

                      <div>
                        <div className="mb-2 flex items-center justify-between gap-3">
                          <div className="text-[11px] font-semibold uppercase tracking-[0.14em]" style={{ color: 'var(--wb-ink-3)' }}>actual request</div>
                          <button
                            type="button"
                            onClick={props.onSaveRequest}
                            className="inline-flex items-center gap-1 text-[11px] font-semibold"
                            style={{ color: 'var(--wb-ink-3)' }}
                          >
                            <Copy size={14} />
                            Copy
                          </button>
                        </div>
                        <pre
                          style={{
                            minHeight: 176,
                            margin: 0,
                            borderRadius: 16,
                            border: '1px solid var(--wb-hairline)',
                            background: 'var(--wb-paper-2)',
                            padding: '14px 16px',
                            color: 'var(--wb-ink-1)',
                            fontSize: 13,
                            lineHeight: 1.65,
                            whiteSpace: 'pre-wrap',
                            fontFamily: 'JetBrains Mono, SFMono-Regular, Menlo, monospace',
                          }}
                        >
                          {props.requestPreview}
                        </pre>
                      </div>

                      {props.hiddenInvokeEndpoints.length > 0 ? (
                        <div style={{ fontSize: 12, lineHeight: 1.7, color: 'var(--wb-ink-3)' }}>
                          Hidden command endpoints: {props.hiddenInvokeEndpoints.length}. Use Raw if you need typed command payloads instead of this streaming surface.
                        </div>
                      ) : null}

                      {props.formError || props.headerError ? (
                        <div style={{ borderRadius: 14, border: '1px solid rgba(162, 37, 28, 0.18)', background: 'var(--wb-err-wash)', padding: '12px 14px', color: 'var(--wb-err)', fontSize: 13, lineHeight: 1.6 }}>
                          {props.formError || props.headerError}
                        </div>
                      ) : null}

                      <div className="flex flex-wrap items-center gap-2">
                        <button
                          type="button"
                          onClick={props.onInvoke}
                          disabled={props.loading || !props.prompt.trim() || !!props.headerError}
                          style={{
                            display: 'inline-flex',
                            alignItems: 'center',
                            gap: 8,
                            borderRadius: 12,
                            border: '1px solid var(--wb-accent)',
                            background: 'var(--wb-accent)',
                            color: 'white',
                            padding: '10px 14px',
                            fontSize: 14,
                            fontWeight: 800,
                            opacity: props.loading || !props.prompt.trim() || !!props.headerError ? 0.45 : 1,
                          }}
                        >
                          <Play size={16} />
                          {props.loading ? 'Streaming…' : 'Run'}
                        </button>
                        {props.loading ? (
                          <button
                            type="button"
                            onClick={props.onStop}
                            style={{
                              borderRadius: 12,
                              border: '1px solid rgba(162, 37, 28, 0.18)',
                              background: 'var(--wb-err-wash)',
                              color: 'var(--wb-err)',
                              padding: '10px 14px',
                              fontSize: 13,
                              fontWeight: 700,
                            }}
                          >
                            Stop
                          </button>
                        ) : null}
                        <button
                          type="button"
                          onClick={props.onLoadFixture}
                          style={{
                            borderRadius: 12,
                            border: '1px solid var(--wb-hairline)',
                            background: 'var(--wb-paper-0)',
                            color: 'var(--wb-ink-1)',
                            padding: '10px 14px',
                            fontSize: 13,
                            fontWeight: 700,
                          }}
                        >
                          Load fixture
                        </button>
                        <button
                          type="button"
                          onClick={props.onReplayLast}
                          disabled={props.history.length === 0 || props.loading}
                          style={{
                            borderRadius: 12,
                            border: '1px solid var(--wb-hairline)',
                            background: 'var(--wb-paper-0)',
                            color: 'var(--wb-ink-1)',
                            padding: '10px 14px',
                            fontSize: 13,
                            fontWeight: 700,
                            opacity: props.history.length === 0 || props.loading ? 0.45 : 1,
                          }}
                        >
                          Replay last
                        </button>
                        <div className="flex-1" />
                        <span className="text-[12px]" style={{ color: 'var(--wb-ink-3)' }}>
                          Streams AGUI frames into the panel →
                        </span>
                      </div>
                    </div>
                  </>
                )}
              </div>
            </div>

            <HumanInputCard
              pendingHumanInput={props.pendingHumanInput}
              resumeLoading={props.resumeLoading}
              resumeError={props.resumeError}
              onResumeHumanInput={props.onResumeHumanInput}
              onGoToChat={props.onGoToChat}
            />

            <div ref={historyRef} style={{ ...cardStyle(), overflow: 'hidden', minHeight: 240 }}>
              <div className="flex flex-wrap items-center gap-3 border-b px-4 py-4" style={{ borderColor: 'var(--wb-hairline)' }}>
                <div style={{ fontSize: 14, fontWeight: 800, color: 'var(--wb-ink-0)' }}>Request history</div>
                <div className="flex-1" />
                <span style={{ fontSize: 12, fontWeight: 700, color: 'var(--wb-ok)' }}>SESSION · LOCAL</span>
              </div>
              <div className="max-h-[420px] overflow-auto p-3">
                {props.history.length === 0 ? (
                  <div className="flex min-h-[180px] items-center justify-center text-[13px]" style={{ color: 'var(--wb-ink-3)' }}>
                    The history rail fills as soon as you run the first invoke.
                  </div>
                ) : (
                  <div className="space-y-2">
                    {props.history.map(entry => {
                      const statusTone = getStatusTone(entry.summary.status);
                      const active = entry.id === props.activeHistoryId;
                      return (
                        <button
                          key={entry.id}
                          type="button"
                          onClick={() => props.onSelectHistory(entry.id)}
                          className="w-full rounded-[16px] border px-3 py-3 text-left"
                          style={{
                            borderColor: active ? 'rgba(31, 79, 214, 0.24)' : 'transparent',
                            background: active ? 'var(--wb-accent-wash)' : 'transparent',
                          }}
                        >
                          <div className="flex items-start gap-3">
                            <span style={{ width: 10, height: 10, marginTop: 6, borderRadius: 3, background: statusTone.foreground }} />
                            <div className="min-w-0 flex-1">
                              <div className="flex items-center gap-2">
                                <span className="truncate font-mono text-[12px]" style={{ color: 'var(--wb-ink-0)', fontWeight: 700 }}>
                                  {entry.summary.runId || entry.id.slice(0, 8)}
                                </span>
                                <div className="flex-1" />
                                <span className="text-[11px]" style={{ color: 'var(--wb-ink-3)' }}>{formatRelativeTimeFromNow(entry.updatedAt)}</span>
                              </div>
                              <div className="mt-1 text-[14px] leading-6" style={{ color: 'var(--wb-ink-1)' }}>
                                {truncateText(entry.request.prompt || '(empty prompt)', 88)}
                              </div>
                              <div className="mt-1 text-[12px] leading-5" style={{ color: 'var(--wb-ink-3)' }}>
                                {getHistoryNote(entry.summary)}
                              </div>
                            </div>
                          </div>
                        </button>
                      );
                    })}
                  </div>
                )}
              </div>
            </div>
          </div>

          <div className="min-h-0" style={{ ...cardStyle(), overflow: 'hidden' }}>
            <div className="flex flex-wrap items-center gap-3 border-b px-4 py-4" style={{ borderColor: 'var(--wb-hairline)' }}>
              <div style={{ fontSize: 14, fontWeight: 800, color: 'var(--wb-ink-0)' }}>AGUI events</div>
              <span style={headerPillStyle('var(--wb-accent-wash)', 'var(--wb-accent-ink)')}>{frames.length} frames</span>
              <span style={{ fontSize: 12, fontWeight: 700, color: props.loading ? 'var(--wb-ok)' : 'var(--wb-ink-2)' }}>SSE · {props.loading ? 'LIVE' : 'IDLE'}</span>
              <div className="flex-1" />
              <div className="flex flex-wrap gap-2">
                {(['timeline', 'trace', 'tabs', 'bubbles', 'raw'] as InvokeWorkbenchMode[]).map(value => (
                  <button
                    key={value}
                    type="button"
                    onClick={() => setMode(value)}
                    style={{
                      borderRadius: 10,
                      border: `1px solid ${mode === value ? 'var(--wb-ink-0)' : 'var(--wb-hairline)'}`,
                      background: mode === value ? 'var(--wb-ink-0)' : 'transparent',
                      color: mode === value ? 'var(--wb-paper-0)' : 'var(--wb-ink-1)',
                      padding: '7px 12px',
                      fontSize: 13,
                      fontWeight: 700,
                    }}
                  >
                    {value}
                  </button>
                ))}
              </div>
            </div>

            <div className="border-b p-4" style={{ borderColor: 'var(--wb-hairline)', background: 'var(--wb-paper-0)' }}>
              <MetricsBar summary={props.summary} frames={frames} loading={props.loading} />
            </div>

            <div className="grid min-h-0 gap-4 p-4 xl:grid-cols-[minmax(0,1fr)_320px]">
              <div className="min-h-[420px] overflow-auto">
                {mode === 'timeline' ? (
                  <TimelineView frames={frames} focusedId={focusedId || frames[0]?.id || null} onFocus={setFocusedId} />
                ) : null}
                {mode === 'trace' ? <TraceView frames={frames} /> : null}
                {mode === 'tabs' ? <TabsView frames={frames} /> : null}
                {mode === 'bubbles' ? <BubblesView frames={frames} /> : null}
                {mode === 'raw' ? <RawView events={props.events} /> : null}
              </div>

              <div className="space-y-4">
                <div style={{ ...cardStyle(), padding: 18, borderRadius: 18 }}>
                  <div className="flex items-center justify-between gap-3">
                    <div>
                      <div style={{ fontSize: 13, fontWeight: 800, color: 'var(--wb-ink-0)' }}>Run summary</div>
                      <div className="mt-1 text-[12px]" style={{ color: 'var(--wb-ink-3)' }}>User-facing outcome first, raw frames second.</div>
                    </div>
                    <span style={headerPillStyle(getStatusTone(effectiveStatus).background, getStatusTone(effectiveStatus).foreground)}>
                      {getStatusTone(effectiveStatus).label}
                    </span>
                  </div>
                  <div className="mt-4 grid grid-cols-2 gap-3">
                    <div style={{ borderRadius: 14, border: '1px solid var(--wb-hairline)', background: 'var(--wb-paper-2)', padding: '12px 14px' }}>
                      <div style={{ fontSize: 11, letterSpacing: '0.12em', textTransform: 'uppercase', color: 'var(--wb-ink-3)', fontWeight: 700 }}>actor</div>
                      <div className="mt-2 break-all font-mono text-[12px]" style={{ color: 'var(--wb-ink-1)' }}>{props.summary.actorId || '—'}</div>
                    </div>
                    <div style={{ borderRadius: 14, border: '1px solid var(--wb-hairline)', background: 'var(--wb-paper-2)', padding: '12px 14px' }}>
                      <div style={{ fontSize: 11, letterSpacing: '0.12em', textTransform: 'uppercase', color: 'var(--wb-ink-3)', fontWeight: 700 }}>run</div>
                      <div className="mt-2 break-all font-mono text-[12px]" style={{ color: 'var(--wb-ink-1)' }}>{props.summary.runId || '—'}</div>
                    </div>
                    <div style={{ borderRadius: 14, border: '1px solid var(--wb-hairline)', background: 'var(--wb-paper-2)', padding: '12px 14px' }}>
                      <div style={{ fontSize: 11, letterSpacing: '0.12em', textTransform: 'uppercase', color: 'var(--wb-ink-3)', fontWeight: 700 }}>steps</div>
                      <div className="mt-2 text-[26px] font-extrabold" style={{ color: 'var(--wb-ink-0)' }}>{props.summary.stepCount}</div>
                    </div>
                    <div style={{ borderRadius: 14, border: '1px solid var(--wb-hairline)', background: 'var(--wb-paper-2)', padding: '12px 14px' }}>
                      <div style={{ fontSize: 11, letterSpacing: '0.12em', textTransform: 'uppercase', color: 'var(--wb-ink-3)', fontWeight: 700 }}>tool calls</div>
                      <div className="mt-2 text-[26px] font-extrabold" style={{ color: 'var(--wb-ink-0)' }}>{props.summary.toolCallCount}</div>
                    </div>
                  </div>
                </div>

                <div style={{ ...cardStyle(), padding: 18, borderRadius: 18 }}>
                  <div className="flex items-center justify-between gap-3">
                    <div>
                      <div style={{ fontSize: 13, fontWeight: 800, color: 'var(--wb-ink-0)' }}>Response</div>
                      <div className="mt-1 text-[12px]" style={{ color: 'var(--wb-ink-3)' }}>The answer users care about before they inspect raw transport.</div>
                    </div>
                    <button
                      type="button"
                      onClick={() => {
                        void props.copyText(
                          currentResponse
                          || props.summary.errorMessage
                          || (props.summary.status === 'submitted' ? 'Resume was accepted. Waiting for subsequent observation frames.' : '')
                          || props.summary.humanInputPrompt,
                        );
                      }}
                      className="inline-flex items-center gap-1 text-[12px] font-semibold"
                      style={{ color: 'var(--wb-ink-3)' }}
                    >
                      <Share2 size={14} />
                      Copy
                    </button>
                  </div>
                  <div className="mt-4 min-h-[180px] rounded-[16px] border p-4" style={{ borderColor: 'var(--wb-hairline)', background: 'var(--wb-paper-2)' }}>
                    {currentResponse ? (
                      <div className="text-[14px] leading-7" style={{ color: 'var(--wb-ink-1)' }}>
                        {props.renderResponse(currentResponse)}
                      </div>
                    ) : props.summary.errorMessage ? (
                      <div style={{ color: 'var(--wb-err)', fontSize: 14, lineHeight: 1.7 }}>{props.summary.errorMessage}</div>
                    ) : props.summary.status === 'submitted' ? (
                      <div style={{ color: 'var(--wb-accent-ink)', fontSize: 14, lineHeight: 1.7 }}>
                        Resume was accepted. If the backend does not continue this SSE stream, switch to Chat to keep following the run.
                      </div>
                    ) : props.summary.humanInputPrompt ? (
                      <div style={{ color: 'var(--wb-warn)', fontSize: 14, lineHeight: 1.7 }}>{props.summary.humanInputPrompt}</div>
                    ) : props.loading ? (
                      <div style={{ color: 'var(--wb-ink-3)', fontSize: 13 }}>Waiting for assistant output…</div>
                    ) : (
                      <div style={{ color: 'var(--wb-ink-3)', fontSize: 13 }}>No assistant text was captured for this invoke.</div>
                    )}
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
