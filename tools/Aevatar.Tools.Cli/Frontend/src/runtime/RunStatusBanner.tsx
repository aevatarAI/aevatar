import { useEffect, useState } from 'react';
import {
  AlertTriangle,
  CheckCircle2,
  Clock3,
  LoaderCircle,
  OctagonX,
  PanelRightClose,
  PanelRightOpen,
  PauseCircle,
  PlayCircle,
  Workflow,
} from 'lucide-react';

import type { ActiveRunState } from './runState';

type Props = {
  run: ActiveRunState | null;
  sidebarOpen: boolean;
  onToggleSidebar: () => void;
};

function formatDuration(ms: number) {
  const totalSeconds = Math.max(Math.floor(ms / 1000), 0);
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;

  if (hours > 0) {
    return `${hours}h ${minutes}m ${seconds}s`;
  }

  if (minutes > 0) {
    return `${minutes}m ${seconds}s`;
  }

  return `${seconds}s`;
}

function formatRelative(timestamp?: number) {
  if (!timestamp) {
    return 'Just now';
  }

  const diff = Math.max(Date.now() - timestamp, 0);
  if (diff < 15_000) {
    return 'Just now';
  }

  const seconds = Math.floor(diff / 1000);
  if (seconds < 60) {
    return `${seconds}s ago`;
  }

  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) {
    return `${minutes}m ago`;
  }

  const hours = Math.floor(minutes / 60);
  if (hours < 24) {
    return `${hours}h ago`;
  }

  return new Date(timestamp).toLocaleString();
}

function shortId(value?: string) {
  const normalized = String(value || '').trim();
  if (!normalized) {
    return 'Pending';
  }

  if (normalized.length <= 20) {
    return normalized;
  }

  return `${normalized.slice(0, 10)}...${normalized.slice(-6)}`;
}

function getStatusMeta(status: ActiveRunState['status']) {
  switch (status) {
    case 'completed':
      return {
        label: 'Completed',
        icon: CheckCircle2,
        badgeClass: 'bg-emerald-50 text-emerald-700 border-emerald-200',
        panelClass: 'border-emerald-200 bg-emerald-50/60',
      };
    case 'error':
      return {
        label: 'Error',
        icon: OctagonX,
        badgeClass: 'bg-rose-50 text-rose-700 border-rose-200',
        panelClass: 'border-rose-200 bg-rose-50/60',
      };
    case 'waiting':
      return {
        label: 'Waiting',
        icon: PauseCircle,
        badgeClass: 'bg-amber-50 text-amber-700 border-amber-200',
        panelClass: 'border-amber-200 bg-amber-50/70',
      };
    case 'stopped':
      return {
        label: 'Stopped',
        icon: AlertTriangle,
        badgeClass: 'bg-slate-100 text-slate-700 border-slate-200',
        panelClass: 'border-slate-200 bg-slate-50/90',
      };
    case 'accepted':
      return {
        label: 'Accepted',
        icon: PlayCircle,
        badgeClass: 'bg-sky-50 text-sky-700 border-sky-200',
        panelClass: 'border-sky-200 bg-sky-50/70',
      };
    case 'running':
      return {
        label: 'Running',
        icon: LoaderCircle,
        badgeClass: 'bg-violet-50 text-violet-700 border-violet-200',
        panelClass: 'border-violet-200 bg-violet-50/70',
      };
    default:
      return {
        label: 'Starting',
        icon: Clock3,
        badgeClass: 'bg-stone-100 text-stone-700 border-stone-200',
        panelClass: 'border-stone-200 bg-stone-50/90',
      };
  }
}

function describeWaiting(run: ActiveRunState) {
  if (run.waitingKind === 'human-input') {
    return run.waitingPrompt || 'Waiting for your input to continue this workflow.';
  }

  if (run.waitingKind === 'signal') {
    return run.waitingSignalName
      ? `Waiting for signal "${run.waitingSignalName}".`
      : (run.waitingPrompt || 'Waiting for an external signal.');
  }

  if (run.waitingKind === 'tool-approval') {
    return run.currentToolName
      ? `Waiting for approval to continue ${run.currentToolName}.`
      : 'Waiting for tool approval.';
  }

  if (run.status === 'error') {
    return run.error || 'Workflow execution failed.';
  }

  return run.currentStepLabel
    ? `Currently focused on ${run.currentStepLabel}.`
    : 'Waiting for the first workflow event.';
}

export default function RunStatusBanner({ run, sidebarOpen, onToggleSidebar }: Props) {
  const [now, setNow] = useState(Date.now());

  useEffect(() => {
    if (!run || !['starting', 'accepted', 'running', 'waiting'].includes(run.status)) {
      return;
    }

    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [run]);

  if (!run) {
    return null;
  }

  const meta = getStatusMeta(run.status);
  const StatusIcon = meta.icon;
  const elapsedMs = (run.completedAt || now) - run.startedAt;
  const waitingCopy = describeWaiting(run);

  return (
    <section className={`mb-4 rounded-[24px] border p-4 shadow-sm shadow-stone-200/70 ${meta.panelClass}`}>
      <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <span className={`inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-[11px] font-semibold uppercase tracking-[0.14em] ${meta.badgeClass}`}>
              <StatusIcon size={14} className={run.status === 'running' ? 'animate-spin' : ''} />
              {meta.label}
            </span>
            {run.serviceLabel && (
              <span className="rounded-full border border-stone-200 bg-white/80 px-2.5 py-1 text-[11px] font-medium text-stone-500">
                {run.serviceLabel}
              </span>
            )}
          </div>

          <div className="mt-3 flex items-start gap-3">
            <div className="mt-0.5 flex h-10 w-10 shrink-0 items-center justify-center rounded-2xl bg-white text-violet-600 shadow-sm">
              <Workflow size={20} />
            </div>
            <div className="min-w-0">
              <div className="text-[17px] font-semibold text-stone-900">
                {run.workflowName || 'Workflow run in progress'}
              </div>
              <div className="mt-1 text-[13px] text-stone-600">
                {run.currentStepLabel
                  ? `Current step: ${run.currentStepLabel}`
                  : 'Waiting for the workflow to reveal its first step.'}
              </div>
            </div>
          </div>
        </div>

        <button
          type="button"
          onClick={onToggleSidebar}
          className="inline-flex items-center justify-center gap-1.5 self-start rounded-xl border border-stone-200 bg-white/90 px-3 py-2 text-[12px] font-medium text-stone-600 transition-colors hover:bg-white hover:text-stone-900"
        >
          {sidebarOpen ? <PanelRightClose size={15} /> : <PanelRightOpen size={15} />}
          {sidebarOpen ? 'Hide Run Panel' : 'Show Run Panel'}
        </button>
      </div>

      <div className="mt-4 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <div className="rounded-2xl border border-white/80 bg-white/85 px-3 py-3">
          <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-stone-400">Run</div>
          <div className="mt-1 text-[13px] font-medium text-stone-800">{shortId(run.runId)}</div>
        </div>
        <div className="rounded-2xl border border-white/80 bg-white/85 px-3 py-3">
          <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-stone-400">Actor</div>
          <div className="mt-1 text-[13px] font-medium text-stone-800">{shortId(run.actorId)}</div>
        </div>
        <div className="rounded-2xl border border-white/80 bg-white/85 px-3 py-3">
          <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-stone-400">Elapsed</div>
          <div className="mt-1 text-[13px] font-medium text-stone-800">{formatDuration(elapsedMs)}</div>
        </div>
        <div className="rounded-2xl border border-white/80 bg-white/85 px-3 py-3">
          <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-stone-400">Last Event</div>
          <div className="mt-1 text-[13px] font-medium text-stone-800">{formatRelative(run.lastEventAt)}</div>
        </div>
      </div>

      <div className={`mt-4 rounded-2xl border px-3.5 py-3 text-[13px] ${
        run.status === 'error'
          ? 'border-rose-200 bg-white/90 text-rose-700'
          : run.status === 'waiting'
          ? 'border-amber-200 bg-white/90 text-amber-800'
          : 'border-stone-200 bg-white/90 text-stone-600'
      }`}>
        {waitingCopy}
      </div>
    </section>
  );
}

